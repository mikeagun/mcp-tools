using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

public static class SolutionTools
{
    public static void Register(McpServer server, SolutionEngine engine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "list_projects",
            Description = "List all projects in a .sln file with solution folders, configurations, and MSBuild " +
                          "target syntax. Each project includes its /t: target path for use with the build tool.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to .sln file"
                    },
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter projects by name (substring match, case-insensitive)",
                    },
                },
                ["required"] = new JsonArray("sln_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var filter = args["filter"]?.GetValue<string>();
                var info = engine.Parse(slnPath);

                var projects = new JsonArray();
                foreach (var proj in info.Projects.Where(p => !p.IsSolutionFolder))
                {
                    if (filter != null && !proj.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fullPath = Path.Combine(info.Directory, proj.RelativePath);
                    // MSBuild /t: target syntax uses solution folder path with backslashes
                    var target = string.IsNullOrEmpty(proj.SolutionFolder)
                        ? proj.Name
                        : $"{proj.SolutionFolder}\\{proj.Name}";

                    var p = new JsonObject
                    {
                        ["name"] = proj.Name,
                        ["path"] = proj.RelativePath,
                        ["target"] = target,
                    };
                    if (!string.IsNullOrEmpty(proj.SolutionFolder))
                        p["solution_folder"] = proj.SolutionFolder;
                    if (!File.Exists(fullPath))
                        p["exists"] = false;
                    if (proj.SolutionDependencies.Count > 0)
                    {
                        var deps = new JsonArray();
                        foreach (var d in proj.SolutionDependencies) deps.Add(d);
                        p["solution_dependencies"] = deps;
                    }
                    projects.Add(p);
                }

                var configs = new JsonArray();
                foreach (var c in info.Configurations)
                    configs.Add(new JsonObject
                    {
                        ["configuration"] = c.Configuration,
                        ["platform"] = c.Platform,
                    });

                var folders = new JsonArray();
                foreach (var f in info.Projects.Where(p => p.IsSolutionFolder))
                    folders.Add(f.Name);

                return new JsonObject
                {
                    ["solution"] = info.Path,
                    ["project_count"] = projects.Count,
                    ["configurations"] = configs,
                    ["solution_folders"] = folders,
                    ["projects"] = projects,
                };
            },
        });
    }
}
