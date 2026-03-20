using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

public static class DependencyTools
{
    public static void Register(McpServer server, SolutionEngine slnEngine, ProjectEngine projEngine)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_dependency_graph",
            Description = "Get the project reference dependency graph for a solution. Returns nodes, edges, " +
                          "and topological build order. By default excludes infrastructure projects " +
                          "(ZERO_CHECK, setup_build, ALL_BUILD). Use 'include' to show only specific projects, " +
                          "or 'exclude' to remove specific ones.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .sln file" },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("json", "mermaid"),
                        ["default"] = "json",
                        ["description"] = "Output format",
                    },
                    ["exclude"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Project names to exclude from the graph. " +
                                          "Default: [\"ZERO_CHECK\", \"setup_build\", \"ALL_BUILD\"]. " +
                                          "Set to [] to include everything.",
                    },
                    ["include"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "If provided, show ONLY these projects and their dependencies. " +
                                          "Useful for focused subgraph queries (e.g. [\"EbpfApi\"]).",
                    },
                },
                ["required"] = new JsonArray("sln_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                var format = args["format"]?.GetValue<string>() ?? "json";

                var defaultExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "ZERO_CHECK", "setup_build", "ALL_BUILD" };
                var exclude = args["exclude"]?.AsArray()
                    .Select(n => n!.GetValue<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? defaultExclude;
                var include = args["include"]?.AsArray()
                    .Select(n => n!.GetValue<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var solution = slnEngine.Parse(slnPath);
                var graph = DependencyGraph.Build(solution, projEngine, config, platform);

                // Apply include filter: expand to include all transitive dependencies
                HashSet<string>? visibleNodes = null;
                if (include != null && include.Count > 0)
                {
                    visibleNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in include)
                    {
                        visibleNodes.Add(name);
                        foreach (var dep in graph.TransitiveDependenciesOf(name))
                            visibleNodes.Add(dep);
                    }
                }

                bool IsVisible(string name) =>
                    !exclude.Contains(name) && (visibleNodes == null || visibleNodes.Contains(name));

                if (format == "mermaid")
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("graph TD");
                    foreach (var (from, to) in graph.Edges)
                    {
                        if (!IsVisible(from) || !IsVisible(to)) continue;
                        var fromId = from.Replace(" ", "_").Replace(".", "_");
                        var toId = to.Replace(" ", "_").Replace(".", "_");
                        sb.AppendLine($"    {fromId}[\"{from}\"] --> {toId}[\"{to}\"]");
                    }
                    return new JsonObject { ["mermaid"] = sb.ToString() };
                }

                var nodes = new JsonArray();
                foreach (var n in graph.Nodes.OrderBy(x => x))
                    if (IsVisible(n)) nodes.Add(n);

                var edges = new JsonArray();
                foreach (var (from, to) in graph.Edges)
                    if (IsVisible(from) && IsVisible(to))
                        edges.Add(new JsonObject { ["from"] = from, ["to"] = to });

                var buildOrder = new JsonArray();
                foreach (var n in graph.TopologicalSort())
                    if (IsVisible(n)) buildOrder.Add(n);

                return new JsonObject
                {
                    ["node_count"] = nodes.Count,
                    ["edge_count"] = edges.Count,
                    ["nodes"] = nodes,
                    ["edges"] = edges,
                    ["build_order"] = buildOrder,
                };
            },
        });
    }
}
