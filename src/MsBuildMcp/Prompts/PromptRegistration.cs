using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Prompts;

/// <summary>
/// MCP prompt templates for guided MSBuild workflows.
/// </summary>
public static class PromptRegistration
{
    public static void RegisterAll(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        RegisterDiagnoseBuildFailure(server);
        RegisterWhatToBuild(server, slnEngine, projEngine);
        RegisterImpactAnalysis(server, slnEngine, projEngine);
        RegisterResolveNugetIssue(server);
        RegisterExplainBuildConfig(server, projEngine);
    }

    private static void RegisterDiagnoseBuildFailure(McpServer server)
    {
        server.RegisterPrompt(new PromptInfo
        {
            Name = "diagnose-build-failure",
            Description = "Analyze MSBuild output to identify root cause errors, group by category, " +
                          "suggest fix order, and distinguish cascade errors from real errors.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "build_output",
                    Description = "Raw MSBuild output text containing errors",
                    Required = true,
                }
            ],
            Handler = args =>
            {
                var output = args["build_output"]?.GetValue<string>() ?? "";
                var diagnostics = ErrorParser.Parse(output);
                var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

                var errorSummary = string.Join("\n", errors.Select(e =>
                    $"  - {e.File}({e.Line}): {e.Code}: {e.Message}" +
                    (e.Project != null ? $" [{Path.GetFileName(e.Project)}]" : "")));

                var byProject = string.Join("\n", errors.GroupBy(e => e.Project ?? "unknown")
                    .Select(g => $"  {Path.GetFileName(g.Key ?? "unknown")}: {g.Count()} errors"));

                var byCode = string.Join("\n", errors.GroupBy(e => e.Code)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"  {g.Key}: {g.Count()} occurrences"));

                var prompt = $"""
                    Diagnose this MSBuild failure. I found {errors.Count} errors and {warnings.Count} warnings.

                    ## Parsed Errors
                    {errorSummary}

                    ## Errors by Project
                    {byProject}

                    ## Error Codes (frequency)
                    {byCode}

                    ## Instructions
                    1. Identify ROOT CAUSE errors vs CASCADE errors. Cascade errors are caused by earlier errors
                       (e.g., a missing header causes dozens of "undeclared identifier" errors in the same file).
                       Fix root causes first.
                    2. Group errors by category: missing includes, linker errors, type mismatches, missing dependencies.
                    3. Suggest a fix order: fix errors in dependency order (headers → implementations → tests).
                    4. For each root cause error, explain what likely caused it and how to fix it.
                    5. If errors mention a specific project, that project's dependencies may need rebuilding first.
                    """;

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
                    }
                };
            },
        });
    }

    private static void RegisterWhatToBuild(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        server.RegisterPrompt(new PromptInfo
        {
            Name = "what-to-build",
            Description = "Given a list of changed files, determine which MSBuild targets to build. " +
                          "Returns the minimal build command.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "sln_path",
                    Description = "Path to .sln file",
                    Required = true,
                },
                new PromptArgument
                {
                    Name = "changed_files",
                    Description = "Comma-separated list of changed file paths",
                    Required = true,
                }
            ],
            Handler = args =>
            {
                var slnPath = args["sln_path"]?.GetValue<string>() ?? "";
                var filesStr = args["changed_files"]?.GetValue<string>() ?? "";
                var files = filesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var solution = slnEngine.Parse(slnPath);
                var affectedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    var fullPath = Path.GetFullPath(file);
                    foreach (var proj in solution.Projects.Where(p => !p.IsSolutionFolder))
                    {
                        var projFullPath = Path.GetFullPath(Path.Combine(solution.Directory, proj.RelativePath));
                        try
                        {
                            var snapshot = projEngine.Evaluate(projFullPath, "Debug", "x64");
                            var projDir = Path.GetDirectoryName(projFullPath)!;
                            foreach (var (_, items) in snapshot.Items)
                            {
                                if (items.Any(item =>
                                    Path.GetFullPath(Path.Combine(projDir, item))
                                        .Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var target = string.IsNullOrEmpty(proj.SolutionFolder)
                                        ? proj.Name
                                        : $"{proj.SolutionFolder}\\{proj.Name}";
                                    affectedProjects.Add(target);
                                }
                            }
                        }
                        catch { /* skip */ }
                    }
                }

                var targets = string.Join(",", affectedProjects.OrderBy(x => x));
                var fileList = string.Join("\n", files.Select(f => $"  - {f}"));

                var prompt = $"""
                    The following files were changed:
                    {fileList}

                    These files belong to the following MSBuild targets:
                      {targets}

                    Suggested build command:
                      msbuild "{slnPath}" /m /p:Configuration=Debug /p:Platform=x64 /t:"{targets}"

                    If any changed files are headers (.h/.hpp), consider that dependent projects may also need rebuilding.
                    Use the get_dependency_graph tool to check for transitive dependents if needed.
                    """;

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
                    }
                };
            },
        });
    }

    private static void RegisterImpactAnalysis(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        server.RegisterPrompt(new PromptInfo
        {
            Name = "impact-analysis",
            Description = "Analyze the impact of changing a file: which projects include it, " +
                          "what depends on those projects, and what tests cover them.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "sln_path",
                    Description = "Path to .sln file",
                    Required = true,
                },
                new PromptArgument
                {
                    Name = "file_path",
                    Description = "Path to the file being changed",
                    Required = true,
                }
            ],
            Handler = args =>
            {
                var slnPath = args["sln_path"]?.GetValue<string>() ?? "";
                var filePath = args["file_path"]?.GetValue<string>() ?? "";

                var prompt = $"""
                    Analyze the impact of changing this file: {filePath}

                    Use the following tools to build a complete picture:
                    1. Call find_project_for_file to find which project(s) compile this file
                    2. Call get_dependency_graph to find the full dependency tree
                    3. Identify all transitive dependents of the affected project(s)
                    4. Check which of those dependents are test projects (name contains "test")
                    5. Suggest which tests to run to validate the change

                    Solution: {slnPath}
                    """;

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
                    }
                };
            },
        });
    }

    private static void RegisterResolveNugetIssue(McpServer server)
    {
        server.RegisterPrompt(new PromptInfo
        {
            Name = "resolve-nuget-issue",
            Description = "Diagnose and resolve NuGet package restore errors.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "error_message",
                    Description = "NuGet error message or MSBuild output containing NuGet errors",
                    Required = true,
                }
            ],
            Handler = args =>
            {
                var error = args["error_message"]?.GetValue<string>() ?? "";

                var prompt = $"""
                    Diagnose this NuGet issue:

                    {error}

                    Common NuGet problems and fixes:
                    1. "Unable to find version X of package Y"
                       → Check if the package source is configured correctly
                       → Check Directory.Packages.props for centralized version management
                       → Try running: msbuild <sln> -Restore

                    2. "Version conflict" / "NU1605"
                       → Multiple projects reference different versions of the same package
                       → Use find_packages tool to see all version references
                       → Align versions in Directory.Packages.props

                    3. "packages.config and PackageReference" mixed
                       → A project uses old-style packages.config while others use PackageReference
                       → Consider migrating to PackageReference

                    4. Missing .g.props / .g.targets
                       → NuGet restore hasn't run or was interrupted
                       → Delete obj/ directories and re-run: msbuild <sln> -Restore

                    Analyze the specific error and provide a targeted fix.
                    """;

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
                    }
                };
            },
        });
    }

    private static void RegisterExplainBuildConfig(McpServer server, ProjectEngine projEngine)
    {
        server.RegisterPrompt(new PromptInfo
        {
            Name = "explain-build-config",
            Description = "Evaluate and explain key build properties for a project in a specific configuration.",
            Arguments =
            [
                new PromptArgument
                {
                    Name = "project_path",
                    Description = "Path to .vcxproj or .csproj",
                    Required = true,
                },
                new PromptArgument
                {
                    Name = "configuration",
                    Description = "Build configuration (e.g. Debug, Release, NativeOnlyDebug)",
                    Required = true,
                },
                new PromptArgument
                {
                    Name = "platform",
                    Description = "Build platform (default: x64)",
                    Required = false,
                }
            ],
            Handler = args =>
            {
                var path = args["project_path"]?.GetValue<string>() ?? "";
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";

                ProjectSnapshot? snapshot = null;
                string evalResult;
                try
                {
                    snapshot = projEngine.Evaluate(path, config, platform);
                    var propList = string.Join("\n", snapshot.Properties
                        .OrderBy(kv => kv.Key)
                        .Select(kv => $"  {kv.Key} = {kv.Value}"));
                    evalResult = $"Evaluated properties:\n{propList}";
                }
                catch (Exception ex)
                {
                    evalResult = $"Failed to evaluate: {ex.Message}";
                }

                var prompt = $"""
                    Explain the build configuration for:
                      Project: {Path.GetFileName(path)}
                      Configuration: {config}
                      Platform: {platform}

                    {evalResult}

                    Explain:
                    1. What type of output does this produce? (exe, dll, lib, sys driver)
                    2. Are optimizations enabled?
                    3. What preprocessor defines are set?
                    4. What include paths are configured?
                    5. Is static analysis enabled?
                    6. Any notable configuration-specific settings?
                    """;

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
                    }
                };
            },
        });
    }
}
