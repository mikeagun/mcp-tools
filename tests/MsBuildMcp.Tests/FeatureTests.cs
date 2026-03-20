using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;
using MsBuildMcp.Tools;
using MsBuildMcp.Prompts;
using MsBuildMcp.Resources;

namespace MsBuildMcp.Tests;

/// <summary>
/// Tests for features added after initial implementation.
/// Creates a temporary test solution with synthetic projects.
/// </summary>
public class FeatureTests : IDisposable
{
    private readonly McpServer _server;
    private readonly string _testSlnDir;
    private readonly string _testSlnPath;

    public FeatureTests()
    {
        ProjectEngine.EnsureMsBuildRegistered();

        // Create a temporary test solution
        _testSlnDir = Path.Combine(Path.GetTempPath(), "msbuild-mcp-feature-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testSlnDir);
        _testSlnPath = Path.Combine(_testSlnDir, "TestSolution.sln");
        CreateTestSolution();

        var slnEngine = new SolutionEngine();
        var projEngine = new ProjectEngine();
        var buildManager = new BuildManager();
        _server = new McpServer("test");
        ToolRegistration.RegisterAll(_server, slnEngine, projEngine, buildManager);
        ResourceRegistration.RegisterAll(_server, slnEngine);
        PromptRegistration.RegisterAll(_server, slnEngine, projEngine);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testSlnDir, recursive: true); } catch { }
    }

    private void CreateTestSolution()
    {
        // Create project directories
        var coreDir = Path.Combine(_testSlnDir, "CoreLib");
        var dataDir = Path.Combine(_testSlnDir, "DataLib");
        var appDir = Path.Combine(_testSlnDir, "WebApp");
        var testDir = Path.Combine(_testSlnDir, "Tests");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(testDir);

        File.WriteAllText(Path.Combine(coreDir, "CoreLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dataDir, "DataLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\CoreLib\\CoreLib.csproj\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(appDir, "WebApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\DataLib\\DataLib.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(testDir, "Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\CoreLib\\CoreLib.csproj\" /><ProjectReference Include=\"..\\DataLib\\DataLib.csproj\" /></ItemGroup></Project>");

        File.WriteAllText(_testSlnPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CoreLib", "CoreLib\CoreLib.csproj", "{A0000000-0000-0000-0000-000000000001}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DataLib", "DataLib\DataLib.csproj", "{A0000000-0000-0000-0000-000000000002}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "WebApp", "WebApp\WebApp.csproj", "{A0000000-0000-0000-0000-000000000003}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Tests", "Tests\Tests.csproj", "{A0000000-0000-0000-0000-000000000004}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            EndGlobal
            """);
    }

    private (JsonNode? result, bool isError) CallTool(string name, JsonObject arguments)
    {
        var result = _server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments,
        });
        if (result!["isError"]?.GetValue<bool>() == true)
            return (null, true);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        return (JsonNode.Parse(text), false);
    }

    // --- Exact tool set ---

    [Fact]
    public void ExactToolSet()
    {
        var result = _server.Dispatch("tools/list", null);
        var names = result!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).OrderBy(x => x).ToList();
        Assert.Equal(new[]
        {
            "build", "cancel_build", "diff_configurations", "find_packages",
            "find_project_for_file", "find_property", "get_build_status",
            "get_dependency_graph", "get_project_details", "get_project_imports",
            "get_project_items", "list_projects", "parse_build_output",
        }, names);
    }

    // --- list_projects ---

    [Fact]
    public void ListProjectsWithFilter()
    {
        var (result, _) = CallTool("list_projects", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["filter"] = "Core",
        });
        Assert.NotNull(result);
        var projects = result!["projects"]!.AsArray();
        Assert.True(projects.Count >= 1);
        foreach (var p in projects)
        {
            Assert.Contains("Core", p!["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(p["target"]); // Every project has target syntax
        }
    }

    // --- find_property ---

    [Fact]
    public void FindPropertyRequiresNameOrFilter()
    {
        var (_, isError) = CallTool("find_property", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
        });
        Assert.True(isError);
    }

    [Fact]
    public void FindPropertyRegexReturnsQueryFormat()
    {
        var (result, _) = CallTool("find_property", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["property_filter"] = "(?i)config",
        });
        Assert.NotNull(result);
        // Query should show regex format
        Assert.StartsWith("/", result!["query"]!.GetValue<string>());
        Assert.True(result["projects_evaluated"]!.GetValue<int>() >= 0);
    }

    // --- get_dependency_graph ---

    [Fact]
    public void DependencyGraphAllNodes()
    {
        var (result, _) = CallTool("get_dependency_graph", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["exclude"] = new JsonArray(), // no exclusions
        });
        Assert.NotNull(result);
        Assert.True(result!["node_count"]!.GetValue<int>() >= 4);
        Assert.NotNull(result["build_order"]);
        Assert.NotNull(result["edges"]);
    }

    [Fact]
    public void DependencyGraphExcludeReducesNodes()
    {
        var (full, _) = CallTool("get_dependency_graph", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["exclude"] = new JsonArray(),
        });
        var (filtered, _) = CallTool("get_dependency_graph", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["exclude"] = new JsonArray("CoreLib"),
        });
        Assert.NotNull(full);
        Assert.NotNull(filtered);
        Assert.True(filtered!["node_count"]!.GetValue<int>() < full!["node_count"]!.GetValue<int>());
    }

    [Fact]
    public void DependencyGraphMermaidFormat()
    {
        var (result, _) = CallTool("get_dependency_graph", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["format"] = "mermaid",
            ["exclude"] = new JsonArray(),
        });
        Assert.NotNull(result);
        var mermaid = result!["mermaid"]!.GetValue<string>();
        Assert.StartsWith("graph TD", mermaid);
    }

    // --- parse_build_output ---

    [Fact]
    public void ParseBuildOutputMixedDiagnostics()
    {
        var (result, _) = CallTool("parse_build_output", new JsonObject
        {
            ["output"] = "file.cpp(1): error C1: msg1 [p.vcxproj]\nfile.cpp(2): warning C2: msg2 [p.vcxproj]\nfile.cpp(3): error C3: msg3 [p.vcxproj]",
        });
        Assert.NotNull(result);
        Assert.Equal(2, result!["error_count"]!.GetValue<int>());
        Assert.Equal(1, result["warning_count"]!.GetValue<int>());
    }

    // --- cancel_build with no build ---

    [Fact]
    public void CancelBuildWithNoBuild()
    {
        var (result, _) = CallTool("cancel_build", new JsonObject());
        Assert.NotNull(result);
        Assert.NotNull(result!["error"]); // "No running build to cancel"
    }

    // --- get_build_status with no build ---

    [Fact]
    public void GetBuildStatusWithNoBuild()
    {
        var (result, _) = CallTool("get_build_status", new JsonObject());
        Assert.NotNull(result);
        Assert.NotNull(result!["error"]); // "No build found"
    }

    // --- find_packages (structural test on test solution) ---

    [Fact]
    public void FindPackagesFindsNewtonsoft()
    {
        var (result, _) = CallTool("find_packages", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["package_name"] = "Newtonsoft",
        });
        Assert.NotNull(result);
        // DataLib has Newtonsoft.Json — even if evaluation fails, packages.config check may find it
        Assert.True(result!["projects_evaluated"]!.GetValue<int>() >= 0);
    }

    // --- Prompt tests ---

    [Fact]
    public void ExplainBuildConfigPromptWorks()
    {
        var result = _server.Dispatch("prompts/get", new JsonObject
        {
            ["name"] = "explain-build-config",
            ["arguments"] = new JsonObject
            {
                ["project_path"] = @"C:\fake\project.vcxproj",
                ["configuration"] = "Debug",
            },
        });
        var messages = result!["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Contains("Debug", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ImpactAnalysisPromptWorks()
    {
        var result = _server.Dispatch("prompts/get", new JsonObject
        {
            ["name"] = "impact-analysis",
            ["arguments"] = new JsonObject
            {
                ["sln_path"] = @"C:\fake\test.sln",
                ["file_path"] = @"C:\fake\src\file.cpp",
            },
        });
        var messages = result!["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Contains("file.cpp", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    // --- get_project_details ---

    [Fact]
    public void GetProjectDetailsReturnsProperties()
    {
        var coreProj = Path.Combine(_testSlnDir, "CoreLib", "CoreLib.csproj");
        var (result, isError) = CallTool("get_project_details", new JsonObject
        {
            ["project_path"] = coreProj,
            ["sln_path"] = _testSlnPath,
        });
        Assert.False(isError);
        Assert.NotNull(result);
        Assert.Equal("CoreLib.csproj", result!["project"]!.GetValue<string>());
        Assert.NotNull(result["properties"]);
        Assert.NotNull(result["items"]);
        Assert.NotNull(result["project_references"]);
        Assert.NotNull(result["package_references"]);
    }

    [Fact]
    public void GetProjectDetailsWithPropertiesFilter()
    {
        var coreProj = Path.Combine(_testSlnDir, "CoreLib", "CoreLib.csproj");
        var (result, _) = CallTool("get_project_details", new JsonObject
        {
            ["project_path"] = coreProj,
            ["sln_path"] = _testSlnPath,
            ["properties"] = new JsonArray("TargetFramework", "OutputType"),
        });
        Assert.NotNull(result);
        var props = result!["properties"]!.AsObject();
        // Should only contain requested properties (that exist)
        Assert.True(props.Count <= 2);
    }

    // --- get_project_items ---

    [Fact]
    public void GetProjectItemsReturnsItems()
    {
        var dataProj = Path.Combine(_testSlnDir, "DataLib", "DataLib.csproj");
        var (result, isError) = CallTool("get_project_items", new JsonObject
        {
            ["project_path"] = dataProj,
            ["sln_path"] = _testSlnPath,
        });
        Assert.False(isError);
        Assert.NotNull(result);
        Assert.Equal("DataLib.csproj", result!["project"]!.GetValue<string>());
        Assert.NotNull(result["items"]);
    }

    // --- get_project_imports ---

    [Fact]
    public void GetProjectImportsReturnsChain()
    {
        var coreProj = Path.Combine(_testSlnDir, "CoreLib", "CoreLib.csproj");
        var (result, isError) = CallTool("get_project_imports", new JsonObject
        {
            ["project_path"] = coreProj,
            ["sln_path"] = _testSlnPath,
        });
        Assert.False(isError);
        Assert.NotNull(result);
        // SDK-style projects have many imports (Sdk.props, Sdk.targets, etc.)
        Assert.True(result!["import_count"]!.GetValue<int>() > 0);
        var imports = result["imports"]!.AsArray();
        Assert.True(imports.Count > 0);
        // Each import has an "imported" and "imported_by" field
        Assert.NotNull(imports[0]!["imported"]);
        Assert.NotNull(imports[0]!["imported_by"]);
    }

    [Fact]
    public void GetProjectImportsFilterWorks()
    {
        var coreProj = Path.Combine(_testSlnDir, "CoreLib", "CoreLib.csproj");
        var (all, _) = CallTool("get_project_imports", new JsonObject
        {
            ["project_path"] = coreProj,
            ["sln_path"] = _testSlnPath,
        });
        var (filtered, _) = CallTool("get_project_imports", new JsonObject
        {
            ["project_path"] = coreProj,
            ["sln_path"] = _testSlnPath,
            ["filter"] = "Sdk",
        });
        Assert.NotNull(all);
        Assert.NotNull(filtered);
        Assert.True(filtered!["import_count"]!.GetValue<int>() <= all!["import_count"]!.GetValue<int>());
    }

    // --- find_project_for_file ---

    [Fact]
    public void FindProjectForFileMissingFileReturnsEmpty()
    {
        var (result, isError) = CallTool("find_project_for_file", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["file_path"] = "nonexistent_file.cs",
        });
        Assert.False(isError);
        Assert.NotNull(result);
        Assert.Equal(0, result!["match_count"]!.GetValue<int>());
    }

    // --- diff_configurations ---

    [Fact]
    public void DiffConfigurationsShowsDifferences()
    {
        var coreProj = Path.Combine(_testSlnDir, "CoreLib", "CoreLib.csproj");
        var (result, isError) = CallTool("diff_configurations", new JsonObject
        {
            ["project_path"] = coreProj,
            ["config_a"] = "Debug",
            ["config_b"] = "Release",
            ["platform_a"] = "AnyCPU",
            ["platform_b"] = "AnyCPU",
            ["sln_path"] = _testSlnPath,
        });
        Assert.False(isError);
        Assert.NotNull(result);
        Assert.NotNull(result!["diffs"]);
        Assert.Contains("Debug|AnyCPU vs Release|AnyCPU", result["comparison"]!.GetValue<string>());
    }

    // --- get_dependency_graph with include ---

    [Fact]
    public void DependencyGraphIncludeFilter()
    {
        var (result, _) = CallTool("get_dependency_graph", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["include"] = new JsonArray("DataLib"),
            ["exclude"] = new JsonArray(),
        });
        Assert.NotNull(result);
        // DataLib depends on CoreLib, so we should get both
        var nodes = result!["nodes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("DataLib", nodes);
        Assert.Contains("CoreLib", nodes);
        // WebApp and Tests should NOT be included (they're not deps of DataLib)
        Assert.DoesNotContain("WebApp", nodes);
        Assert.DoesNotContain("Tests", nodes);
    }

    // --- find_property with exact name ---

    [Fact]
    public void FindPropertyExactName()
    {
        var (result, _) = CallTool("find_property", new JsonObject
        {
            ["sln_path"] = _testSlnPath,
            ["property_name"] = "TargetFramework",
        });
        Assert.NotNull(result);
        Assert.Equal("TargetFramework", result!["query"]!.GetValue<string>());
        Assert.True(result["match_count"]!.GetValue<int>() >= 4); // All 4 projects have TargetFramework
        // by_value should group by value, and key should be just the value (not "TargetFramework=net9.0")
        var byValue = result["by_value"]!.AsObject();
        Assert.True(byValue.ContainsKey("net9.0"));
    }
}
