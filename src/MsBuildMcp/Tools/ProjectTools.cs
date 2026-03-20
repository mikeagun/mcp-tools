using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

public static class ProjectTools
{
    public static void Register(McpServer server, ProjectEngine engine, SolutionEngine slnEngine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_project_details",
            Description = "Evaluate a .vcxproj/.csproj file and return its properties, source files, " +
                          "project references, and NuGet packages for a specific Configuration|Platform. " +
                          "Uses Microsoft.Build evaluation — correctly handles imports, conditions, and property inheritance.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .vcxproj or .csproj file"
                    },
                    ["configuration"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build configuration (default: Debug)",
                        ["default"] = "Debug",
                    },
                    ["platform"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build platform (default: x64)",
                        ["default"] = "x64",
                    },
                    ["sln_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .sln file (for SolutionDir resolution). Auto-inferred if omitted.",
                    },
                    ["additional_properties"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Additional MSBuild global properties to set during evaluation",
                    },
                    ["properties"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Specific property names to include (looked up from all evaluated properties). " +
                                          "Omit to get the default curated summary. " +
                                          "Example: [\"SpectreMitigation\", \"WDKVersion\", \"DriverType\"]",
                    },
                },
                ["required"] = new JsonArray("project_path"),
            },
            Handler = args =>
            {
                var path = args["project_path"]!.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                var slnPath = args["sln_path"]?.GetValue<string>();
                var solutionDir = slnPath != null ? Path.GetDirectoryName(Path.GetFullPath(slnPath)) + Path.DirectorySeparatorChar : null;
                var addlProps = ParseAdditionalProperties(args["additional_properties"]);
                var propsFilter = args["properties"]?.AsArray()
                    .Select(n => n!.GetValue<string>()).ToList();
                var snapshot = engine.Evaluate(path, config, platform, solutionDir, addlProps);
                return SnapshotToJson(snapshot, propsFilter);
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "find_project_for_file",
            Description = "Given a source file path, find which project(s) in a solution compile it. " +
                          "Uses evaluated ItemGroups (ClCompile, ClInclude, etc.) — not XML grep.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .sln file" },
                    ["file_path"] = new JsonObject { ["type"] = "string", ["description"] = "Source file path to search for" },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                },
                ["required"] = new JsonArray("sln_path", "file_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var fileArg = args["file_path"]!.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";

                // Support both full paths and bare filenames
                var isFullPath = Path.IsPathRooted(fileArg);
                var filePath = isFullPath ? Path.GetFullPath(fileArg) : null;
                var fileName = Path.GetFileName(fileArg);

                var solution = slnEngine.Parse(slnPath);
                var solutionDir = solution.Directory + Path.DirectorySeparatorChar;
                var results = new JsonArray();
                var evalErrors = new JsonArray();
                int evaluated = 0;

                foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
                {
                    var projFullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
                    try
                    {
                        var snapshot = engine.Evaluate(projFullPath, config, platform, solutionDir);
                        evaluated++;
                        var projDir = Path.GetDirectoryName(projFullPath)!;

                        foreach (var (itemType, items) in snapshot.Items)
                        {
                            if (itemType.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                                continue; // Skip project refs for file search

                            foreach (var item in items)
                            {
                                var itemFullPath = Path.GetFullPath(Path.Combine(projDir, item));
                                var match = filePath != null
                                    ? itemFullPath.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                                    : Path.GetFileName(itemFullPath).Equals(fileName, StringComparison.OrdinalIgnoreCase);

                                if (match)
                                {
                                    results.Add(new JsonObject
                                    {
                                        ["project"] = proj.Name,
                                        ["project_path"] = proj.RelativePath,
                                        ["item_type"] = itemType,
                                        ["resolved_path"] = itemFullPath,
                                        ["solution_folder"] = proj.SolutionFolder,
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        evalErrors.Add(new JsonObject { ["project"] = proj.Name, ["error"] = ex.Message });
                    }
                }

                var result = new JsonObject
                {
                    ["file"] = fileArg,
                    ["matches"] = results,
                    ["match_count"] = results.Count,
                    ["projects_evaluated"] = evaluated,
                };
                if (evalErrors.Count > 0)
                {
                    result["projects_failed"] = evalErrors.Count;
                    result["evaluation_errors"] = evalErrors;
                }
                return result;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_project_items",
            Description = "Get source files and compiler/linker settings from a project. Returns file lists " +
                          "by item type (ClCompile, ClInclude, etc.) and ItemDefinitionGroup metadata " +
                          "(PreprocessorDefinitions, Optimization, etc.). Filter with item_types parameter.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project_path"] = new JsonObject { ["type"] = "string" },
                    ["item_types"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Item types to retrieve (e.g. ClCompile, ClInclude). Omit for all.",
                    },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["sln_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .sln file (for SolutionDir). Auto-inferred if omitted.",
                    },
                },
                ["required"] = new JsonArray("project_path"),
            },
            Handler = args =>
            {
                var path = args["project_path"]!.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                var slnPath = args["sln_path"]?.GetValue<string>();
                var solutionDir = slnPath != null ? Path.GetDirectoryName(Path.GetFullPath(slnPath)) + Path.DirectorySeparatorChar : null;
                var snapshot = engine.Evaluate(path, config, platform, solutionDir);

                var filterTypes = args["item_types"]?.AsArray()
                    .Select(n => n!.GetValue<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var items = new JsonObject();
                foreach (var (itemType, itemList) in snapshot.Items)
                {
                    if (filterTypes != null && !filterTypes.Contains(itemType)) continue;
                    var arr = new JsonArray();
                    foreach (var item in itemList) arr.Add(item);
                    items[itemType] = arr;
                }

                // Include ProjectReference if explicitly requested
                if (filterTypes != null && filterTypes.Contains("ProjectReference") && snapshot.ProjectReferences.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var r in snapshot.ProjectReferences) arr.Add(r.Include);
                    items["ProjectReference"] = arr;
                }

                var itemDefs = new JsonObject();
                foreach (var (itemType, metadata) in snapshot.ItemDefinitions)
                {
                    if (filterTypes != null && !filterTypes.Contains(itemType)) continue;
                    var md = new JsonObject();
                    foreach (var (k, v) in metadata) md[k] = v;
                    itemDefs[itemType] = md;
                }

                var result = new JsonObject
                {
                    ["project"] = Path.GetFileName(path),
                    ["configuration"] = config,
                    ["platform"] = platform,
                    ["items"] = items,
                };
                if (itemDefs.Count > 0)
                    result["item_definitions"] = itemDefs;
                return result;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_project_imports",
            Description = "Show the .props/.targets import chain for a project. Returns each imported file " +
                          "with its importing parent and condition. Essential for understanding why a property " +
                          "has a particular value (e.g. Directory.Build.props → wdk.props → SDK .props chain). " +
                          "Use 'filter' to search for specific imports by path pattern.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .vcxproj or .csproj" },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["sln_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .sln file (for SolutionDir). Auto-inferred if omitted.",
                    },
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter imports by path substring (case-insensitive). " +
                                          "E.g. 'wdk' to find WDK-related imports, 'Directory.Build' for repo-level props.",
                    },
                },
                ["required"] = new JsonArray("project_path"),
            },
            Handler = args =>
            {
                var path = args["project_path"]!.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                var slnPath = args["sln_path"]?.GetValue<string>();
                var solutionDir = slnPath != null ? Path.GetDirectoryName(Path.GetFullPath(slnPath)) + Path.DirectorySeparatorChar : null;
                var filter = args["filter"]?.GetValue<string>();

                var snapshot = engine.Evaluate(path, config, platform, solutionDir);

                var imports = new JsonArray();
                foreach (var imp in snapshot.Imports)
                {
                    if (filter != null && !imp.ImportedProject.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var entry = new JsonObject
                    {
                        ["imported"] = imp.ImportedProject,
                        ["imported_by"] = imp.ImportingElement,
                    };
                    if (!string.IsNullOrEmpty(imp.Condition))
                        entry["condition"] = imp.Condition;
                    imports.Add(entry);
                }

                return new JsonObject
                {
                    ["project"] = Path.GetFileName(path),
                    ["import_count"] = imports.Count,
                    ["imports"] = imports,
                };
            },
        });
    }

    internal static JsonObject SnapshotToJson(ProjectSnapshot snapshot, List<string>? propertiesFilter = null)
    {
        var props = new JsonObject();
        if (propertiesFilter != null)
        {
            // Return only the requested properties (from the full set)
            foreach (var name in propertiesFilter)
            {
                if (snapshot.AllProperties.TryGetValue(name, out var value))
                    props[name] = value;
            }
        }
        else
        {
            // Default: curated summary
            foreach (var (key, value) in snapshot.Properties)
                props[key] = value;
        }

        var items = new JsonObject();
        foreach (var (itemType, itemList) in snapshot.Items)
        {
            items[itemType] = new JsonObject
            {
                ["count"] = itemList.Count,
                ["files"] = new JsonArray(itemList.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            };
        }

        var itemDefs = new JsonObject();
        foreach (var (itemType, metadata) in snapshot.ItemDefinitions)
        {
            var md = new JsonObject();
            foreach (var (k, v) in metadata) md[k] = v;
            itemDefs[itemType] = md;
        }

        var refs = new JsonArray();
        foreach (var r in snapshot.ProjectReferences)
            refs.Add(new JsonObject { ["name"] = r.Name, ["path"] = r.Include });

        var packages = new JsonArray();
        foreach (var p in snapshot.PackageReferences)
            packages.Add(new JsonObject { ["name"] = p.Name, ["version"] = p.Version });

        var result = new JsonObject
        {
            ["project"] = Path.GetFileName(snapshot.FullPath),
            ["full_path"] = snapshot.FullPath,
            ["configuration"] = snapshot.Configuration,
            ["platform"] = snapshot.Platform,
            ["properties"] = props,
            ["items"] = items,
            ["project_references"] = refs,
            ["package_references"] = packages,
        };
        if (itemDefs.Count > 0)
            result["item_definitions"] = itemDefs;
        return result;
    }

    internal static Dictionary<string, string>? ParseAdditionalProperties(JsonNode? node)
    {
        if (node is not JsonObject obj || obj.Count == 0) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in obj)
            result[key] = value?.GetValue<string>() ?? "";
        return result;
    }
}
