using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;
using MsBuildMcp.Tools;
using MsBuildMcp.Prompts;
using MsBuildMcp.Resources;

namespace MsBuildMcp.Tests;

/// <summary>
/// Integration test: registers all tools/prompts/resources and validates via dispatch.
/// </summary>
public class IntegrationTests
{
    private readonly McpServer _server;

    public IntegrationTests()
    {
        ProjectEngine.EnsureMsBuildRegistered();
        var slnEngine = new SolutionEngine();
        var projEngine = new ProjectEngine();
        var buildManager = new BuildManager();
        _server = new McpServer("test");
        ToolRegistration.RegisterAll(_server, slnEngine, projEngine, buildManager);
        ResourceRegistration.RegisterAll(_server, slnEngine);
        PromptRegistration.RegisterAll(_server, slnEngine, projEngine);
    }

    [Fact]
    public void AllToolsRegistered()
    {
        var result = _server.Dispatch("tools/list", null);
        var tools = result!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

        // Phase 1 tools
        Assert.Contains("list_projects", names);
        Assert.Contains("get_project_details", names);
        Assert.Contains("find_project_for_file", names);
        Assert.Contains("get_dependency_graph", names);
        Assert.Contains("build", names);
        Assert.Contains("parse_build_output", names);
        Assert.Contains("get_project_items", names);

        // Phase 2 tools
        Assert.Contains("find_packages", names);
        Assert.Contains("find_property", names);
        Assert.Contains("diff_configurations", names);

        // Build management tools
        Assert.Contains("get_build_status", names);
        Assert.Contains("cancel_build", names);

        // Import chain tool
        Assert.Contains("get_project_imports", names);

        // Verify consolidated tools are removed
        Assert.DoesNotContain("get_build_order", names);   // merged into get_dependency_graph
        Assert.DoesNotContain("get_build_targets", names);  // merged into list_projects
    }

    [Fact]
    public void AllPromptsRegistered()
    {
        var result = _server.Dispatch("prompts/list", null);
        var prompts = result!["prompts"]!.AsArray();
        var names = prompts.Select(p => p!["name"]!.GetValue<string>()).ToHashSet();

        Assert.Contains("diagnose-build-failure", names);
        Assert.Contains("what-to-build", names);
        Assert.Contains("impact-analysis", names);
        Assert.Contains("resolve-nuget-issue", names);
        Assert.Contains("explain-build-config", names);
    }

    [Fact]
    public void ParseBuildOutputToolWorks()
    {
        var args = new JsonObject
        {
            ["name"] = "parse_build_output",
            ["arguments"] = new JsonObject
            {
                ["output"] = @"C:\src\a.cpp(10,5): error C2039: 'foo': not a member [C:\proj.vcxproj]
C:\src\b.cpp(20): warning C4996: deprecated [C:\proj.vcxproj]"
            },
        };

        var result = _server.Dispatch("tools/call", args);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;

        Assert.Equal(1, parsed["error_count"]!.GetValue<int>());
        Assert.Equal(1, parsed["warning_count"]!.GetValue<int>());
    }

    [Fact]
    public void DiagnoseBuildFailurePromptWorks()
    {
        var args = new JsonObject
        {
            ["name"] = "diagnose-build-failure",
            ["arguments"] = new JsonObject
            {
                ["build_output"] = @"C:\src\a.cpp(1): error C1234: msg [C:\proj.vcxproj]"
            },
        };

        var result = _server.Dispatch("prompts/get", args);
        var messages = result!["messages"]!.AsArray();
        Assert.Single(messages);
        var text = messages[0]!["content"]!["text"]!.GetValue<string>();
        Assert.Contains("1 errors", text);
        Assert.Contains("C1234", text);
    }

    [Fact]
    public void ResolveNugetPromptWorks()
    {
        var args = new JsonObject
        {
            ["name"] = "resolve-nuget-issue",
            ["arguments"] = new JsonObject
            {
                ["error_message"] = "NU1605: Detected package downgrade"
            },
        };

        var result = _server.Dispatch("prompts/get", args);
        var messages = result!["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Contains("NU1605", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ListProjectsOnRealSolution()
    {
        // Test against the msbuild-mcp solution itself
        var slnPath = FindSolutionPath();
        if (slnPath == null) return; // Skip if we can't find it

        var args = new JsonObject
        {
            ["name"] = "list_projects",
            ["arguments"] = new JsonObject { ["sln_path"] = slnPath },
        };

        var result = _server.Dispatch("tools/call", args);
        Assert.Null(result!["isError"]);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        Assert.True(parsed["project_count"]!.GetValue<int>() >= 2); // MsBuildMcp + MsBuildMcp.Tests
    }

    private static string? FindSolutionPath()
    {
        // Walk up from the test assembly location to find the .sln
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var slnPath = Path.Combine(dir, "mcp-tools.sln");
            if (File.Exists(slnPath)) return slnPath;
            // Also check old name for compatibility
            slnPath = Path.Combine(dir, "msbuild-mcp.sln");
            if (File.Exists(slnPath)) return slnPath;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
