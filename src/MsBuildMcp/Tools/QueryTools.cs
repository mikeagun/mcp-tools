using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

/// <summary>
/// Query tools for cross-solution package, property, and configuration analysis.
/// </summary>
public static class QueryTools
{
    public static void Register(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        RegisterFindPackages(server, slnEngine, projEngine);
        RegisterFindProperty(server, slnEngine, projEngine);
        RegisterDiffConfigurations(server, projEngine);
    }

    private static void RegisterFindPackages(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "find_packages",
            Description = "Find NuGet packages across all projects in a solution. " +
                          "Shows which projects use each package and what version. " +
                          "Detects both PackageReference and packages.config styles.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .sln file" },
                    ["package_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter by package name (substring match, case-insensitive). Omit for all packages.",
                    },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                },
                ["required"] = new JsonArray("sln_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var filter = args["package_name"]?.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";

                var solution = slnEngine.Parse(slnPath);
                var solutionDir = solution.Directory + Path.DirectorySeparatorChar;
                var packageMap = new Dictionary<string, List<(string Project, string Version)>>(StringComparer.OrdinalIgnoreCase);
                var evalErrors = new JsonArray();
                int evaluated = 0;

                foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
                {
                    var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
                    try
                    {
                        var snapshot = projEngine.Evaluate(fullPath, config, platform, solutionDir);
                        evaluated++;
                        foreach (var pkg in snapshot.PackageReferences)
                        {
                            if (filter != null && !pkg.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!packageMap.TryGetValue(pkg.Name, out var list))
                            {
                                list = [];
                                packageMap[pkg.Name] = list;
                            }
                            list.Add((proj.Name, pkg.Version));
                        }
                    }
                    catch (Exception ex)
                    {
                        evalErrors.Add(new JsonObject { ["project"] = proj.Name, ["error"] = ex.Message });
                    }
                }

                // Also check packages.config files
                foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
                {
                    var projDir = Path.GetDirectoryName(Path.Combine(solution.Directory, proj.RelativePath))!;
                    var pkgConfig = Path.Combine(projDir, "packages.config");
                    if (File.Exists(pkgConfig))
                    {
                        try
                        {
                            var doc = System.Xml.Linq.XDocument.Load(pkgConfig);
                            foreach (var pkg in doc.Descendants("package"))
                            {
                                var name = pkg.Attribute("id")?.Value ?? "";
                                var version = pkg.Attribute("version")?.Value ?? "";
                                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (!packageMap.TryGetValue(name, out var list))
                                {
                                    list = [];
                                    packageMap[name] = list;
                                }
                                list.Add((proj.Name, version));
                            }
                        }
                        catch { /* skip malformed packages.config */ }
                    }
                }

                var packages = new JsonArray();
                foreach (var (name, usages) in packageMap.OrderBy(kv => kv.Key))
                {
                    var versions = usages.Select(u => u.Version).Distinct().ToList();
                    var projects = new JsonArray();
                    foreach (var (projName, ver) in usages)
                        projects.Add(new JsonObject { ["project"] = projName, ["version"] = ver });

                    var pkg = new JsonObject
                    {
                        ["name"] = name,
                        ["versions"] = new JsonArray(versions.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                        ["project_count"] = usages.Count,
                        ["projects"] = projects,
                    };
                    if (versions.Count > 1)
                        pkg["version_conflict"] = true;

                    packages.Add(pkg);
                }

                var result = new JsonObject
                {
                    ["total_packages"] = packageMap.Count,
                    ["projects_evaluated"] = evaluated,
                    ["packages"] = packages,
                };
                if (evalErrors.Count > 0)
                {
                    result["projects_failed"] = evalErrors.Count;
                    result["evaluation_errors"] = evalErrors;
                }
                return result;
            },
        });
    }

    private static void RegisterFindProperty(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "find_property",
            Description = "Search for MSBuild properties across all projects in a solution. " +
                          "Searches project-level properties (e.g. ConfigurationType, SpectreMitigation, PlatformToolset). " +
                          "For compiler/linker settings (PreprocessorDefinitions, WarningLevel, Optimization), " +
                          "use get_project_details or diff_configurations instead — those are ItemDefinitionGroup metadata, not properties. " +
                          "Use property_name for exact lookup, property_filter for regex discovery. " +
                          "Use max_results to limit output for broad regex queries.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject { ["type"] = "string" },
                    ["property_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact property name (case-insensitive). Mutually exclusive with property_filter.",
                    },
                    ["property_filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex pattern to match property names (e.g. '(?i)spectre|wdk'). " +
                                          "Returns summary by default — add include_details=true for full breakdown.",
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum number of results. Default: unlimited for exact name, 100 for regex. " +
                                          "When truncated, response includes truncated=true and total_matches.",
                    },
                    ["include_details"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include per-project details array. Default: true for exact name, false for regex.",
                    },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                },
                ["required"] = new JsonArray("sln_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var propName = args["property_name"]?.GetValue<string>();
                var propFilter = args["property_filter"]?.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                bool isRegex = propFilter != null;
                var maxResults = args["max_results"]?.GetValue<int>() ?? (isRegex ? 100 : int.MaxValue);
                var includeDetails = args["include_details"]?.GetValue<bool>() ?? !isRegex;

                if (propName == null && propFilter == null)
                    throw new ArgumentException("Provide either property_name or property_filter");

                System.Text.RegularExpressions.Regex? regex = propFilter != null
                    ? new System.Text.RegularExpressions.Regex(propFilter) : null;

                var solution = slnEngine.Parse(slnPath);
                var solutionDir = solution.Directory + Path.DirectorySeparatorChar;
                var allMatches = new List<(string Project, string Property, string Value)>();
                var evalErrors = new JsonArray();
                int evaluated = 0;
                bool hitLimit = false;

                foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
                {
                    var fullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
                    try
                    {
                        var snapshot = projEngine.Evaluate(fullPath, config, platform, solutionDir);
                        evaluated++;

                        if (propName != null)
                        {
                            if (snapshot.AllProperties.TryGetValue(propName, out var value) && !string.IsNullOrEmpty(value))
                                allMatches.Add((proj.Name, propName, value));
                        }
                        else if (regex != null)
                        {
                            foreach (var (name, value) in snapshot.AllProperties)
                            {
                                if (regex.IsMatch(name))
                                    allMatches.Add((proj.Name, name, value));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        evalErrors.Add(new JsonObject { ["project"] = proj.Name, ["error"] = ex.Message });
                    }
                }

                int totalMatches = allMatches.Count;
                if (allMatches.Count > maxResults)
                {
                    allMatches = allMatches.Take(maxResults).ToList();
                    hitLimit = true;
                }

                // Group by value — for exact name, key is just the value; for regex, key is Property=Value
                var byValue = new JsonObject();
                foreach (var group in allMatches.GroupBy(x => isRegex ? $"{x.Property}={x.Value}" : x.Value))
                {
                    var projs = new JsonArray();
                    foreach (var p in group) projs.Add(p.Project);
                    byValue[group.Key] = projs;
                }

                var result = new JsonObject
                {
                    ["query"] = propName ?? $"/{propFilter}/",
                    ["configuration"] = config,
                    ["platform"] = platform,
                    ["match_count"] = allMatches.Count,
                    ["projects_evaluated"] = evaluated,
                    ["by_value"] = byValue,
                };

                if (hitLimit)
                {
                    result["truncated"] = true;
                    result["total_matches"] = totalMatches;
                }

                if (includeDetails)
                {
                    var details = new JsonArray();
                    foreach (var (proj, prop, val) in allMatches)
                        details.Add(new JsonObject { ["project"] = proj, ["property"] = prop, ["value"] = val });
                    result["details"] = details;
                }
                if (evalErrors.Count > 0)
                {
                    result["projects_failed"] = evalErrors.Count;
                    result["evaluation_errors"] = evalErrors;
                }
                return result;
            },
        });
    }

    private static void RegisterDiffConfigurations(McpServer server, ProjectEngine projEngine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "diff_configurations",
            Description = "Compare two build configurations for a project. Shows properties, compiler/linker " +
                          "settings (ItemDefinitionGroup), and item counts that differ. Useful for understanding " +
                          "Debug vs Release, or x64 vs ARM64.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project_path"] = new JsonObject { ["type"] = "string" },
                    ["config_a"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "First configuration (e.g. 'Debug')",
                    },
                    ["config_b"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Second configuration (e.g. 'Release')",
                    },
                    ["platform_a"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["platform_b"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["sln_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .sln file (for SolutionDir resolution). Auto-inferred if omitted.",
                    },
                },
                ["required"] = new JsonArray("project_path", "config_a", "config_b"),
            },
            Handler = args =>
            {
                var path = args["project_path"]!.GetValue<string>();
                var configA = args["config_a"]!.GetValue<string>();
                var configB = args["config_b"]!.GetValue<string>();
                var platformA = args["platform_a"]?.GetValue<string>() ?? "x64";
                var platformB = args["platform_b"]?.GetValue<string>() ?? "x64";
                var slnPath = args["sln_path"]?.GetValue<string>();
                var solutionDir = slnPath != null ? Path.GetDirectoryName(Path.GetFullPath(slnPath)) + Path.DirectorySeparatorChar : null;

                var snapA = projEngine.Evaluate(path, configA, platformA, solutionDir);
                var snapB = projEngine.Evaluate(path, configB, platformB, solutionDir);

                var diffs = new JsonArray();

                // Compare properties
                var allPropKeys = snapA.Properties.Keys.Union(snapB.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var key in allPropKeys.OrderBy(k => k))
                {
                    var valA = snapA.Properties.GetValueOrDefault(key, "");
                    var valB = snapB.Properties.GetValueOrDefault(key, "");
                    if (valA != valB)
                    {
                        diffs.Add(new JsonObject
                        {
                            ["type"] = "property",
                            ["name"] = key,
                            [$"{configA}|{platformA}"] = valA,
                            [$"{configB}|{platformB}"] = valB,
                        });
                    }
                }

                // Compare item counts
                var allItemTypes = snapA.Items.Keys.Union(snapB.Items.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var itemType in allItemTypes.OrderBy(k => k))
                {
                    var countA = snapA.Items.GetValueOrDefault(itemType)?.Count ?? 0;
                    var countB = snapB.Items.GetValueOrDefault(itemType)?.Count ?? 0;
                    if (countA != countB)
                    {
                        diffs.Add(new JsonObject
                        {
                            ["type"] = "item_count",
                            ["name"] = itemType,
                            [$"{configA}|{platformA}"] = countA,
                            [$"{configB}|{platformB}"] = countB,
                        });
                    }
                }

                // Compare ItemDefinitionGroup metadata (compiler/linker settings)
                var allDefTypes = snapA.ItemDefinitions.Keys.Union(snapB.ItemDefinitions.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var itemType in allDefTypes.OrderBy(k => k))
                {
                    var defsA = snapA.ItemDefinitions.GetValueOrDefault(itemType) ?? new Dictionary<string, string>();
                    var defsB = snapB.ItemDefinitions.GetValueOrDefault(itemType) ?? new Dictionary<string, string>();
                    var allKeys = defsA.Keys.Union(defsB.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in allKeys.OrderBy(k => k))
                    {
                        var valA = defsA.GetValueOrDefault(key, "");
                        var valB = defsB.GetValueOrDefault(key, "");
                        if (valA != valB)
                        {
                            diffs.Add(new JsonObject
                            {
                                ["type"] = "item_definition",
                                ["item_type"] = itemType,
                                ["name"] = key,
                                [$"{configA}|{platformA}"] = valA,
                                [$"{configB}|{platformB}"] = valB,
                            });
                        }
                    }
                }

                return new JsonObject
                {
                    ["project"] = Path.GetFileName(path),
                    ["comparison"] = $"{configA}|{platformA} vs {configB}|{platformB}",
                    ["diff_count"] = diffs.Count,
                    ["diffs"] = diffs,
                };
            },
        });
    }
}
