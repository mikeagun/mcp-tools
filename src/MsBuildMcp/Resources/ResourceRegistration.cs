using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Resources;

public static class ResourceRegistration
{
    public static void RegisterAll(McpServer server, SolutionEngine solutionEngine)
    {
        // Solution resource is registered dynamically when a solution is first loaded.
        // For now, we register a static resource that lists known patterns.
    }

    /// <summary>
    /// Register a solution resource for a specific .sln path.
    /// Called when the server first sees a solution path in a tool call.
    /// </summary>
    public static void RegisterSolutionResource(McpServer server, SolutionEngine engine, string slnPath)
    {
        var uri = $"msbuild://solution/{Path.GetFileName(slnPath)}";
        server.RegisterResource(new ResourceInfo
        {
            Uri = uri,
            Name = $"Solution: {Path.GetFileName(slnPath)}",
            Description = $"Overview of {Path.GetFileName(slnPath)} — project count, folders, configurations.",
            Reader = () =>
            {
                var info = engine.Parse(slnPath);
                var projects = info.Projects.Where(p => !p.IsSolutionFolder).ToList();

                var byFolder = new JsonObject();
                foreach (var group in projects.GroupBy(p => string.IsNullOrEmpty(p.SolutionFolder) ? "(root)" : p.SolutionFolder))
                {
                    var arr = new JsonArray();
                    foreach (var p in group.OrderBy(p => p.Name))
                        arr.Add(p.Name);
                    byFolder[group.Key] = arr;
                }

                var configs = new JsonArray();
                foreach (var c in info.Configurations)
                    configs.Add($"{c.Configuration}|{c.Platform}");

                return new JsonObject
                {
                    ["solution"] = info.Path,
                    ["project_count"] = projects.Count,
                    ["folder_count"] = info.Projects.Count(p => p.IsSolutionFolder),
                    ["configurations"] = configs,
                    ["projects_by_folder"] = byFolder,
                };
            },
        });
    }
}
