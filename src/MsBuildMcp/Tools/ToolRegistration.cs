using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

/// <summary>
/// Coordinates registration of all MCP tools.
/// </summary>
public static class ToolRegistration
{
    public static void RegisterAll(McpServer server, SolutionEngine solutionEngine,
        ProjectEngine projectEngine, BuildManager buildManager)
    {
        SolutionTools.Register(server, solutionEngine);
        ProjectTools.Register(server, projectEngine, solutionEngine);
        DependencyTools.Register(server, solutionEngine, projectEngine);
        BuildTools.Register(server, buildManager);
        QueryTools.Register(server, solutionEngine, projectEngine);
    }
}
