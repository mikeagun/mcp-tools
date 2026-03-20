using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MsBuildMcp.Tests;

/// <summary>
/// End-to-end tests that launch the actual msbuild-mcp server process
/// and communicate via stdio JSON-RPC 2.0 (NDJSON framing).
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly Process _server;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private int _requestId;

    public EndToEndTests()
    {
        var projectDir = FindProjectDir();
        _server = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectDir}\" --no-build",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        _server.Start();
        _writer = _server.StandardInput;
        _reader = _server.StandardOutput;
    }

    public void Dispose()
    {
        try
        {
            _writer.Close();
            if (!_server.WaitForExit(3000))
                _server.Kill(entireProcessTree: true);
        }
        catch { }
        _server.Dispose();
    }

    private JsonNode? SendRequest(string method, JsonNode? parameters = null)
    {
        var id = ++_requestId;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters != null)
            request["params"] = JsonNode.Parse(parameters.ToJsonString());

        var line = request.ToJsonString();
        _writer.WriteLine(line);
        _writer.Flush();

        // Read response line
        var responseLine = _reader.ReadLine();
        if (responseLine == null) return null;

        var response = JsonNode.Parse(responseLine);
        Assert.Equal(id, response!["id"]!.GetValue<int>());
        return response["result"];
    }

    private void SendNotification(string method)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        _writer.WriteLine(notification.ToJsonString());
        _writer.Flush();
    }

    [Fact]
    public void InitializeAndListTools()
    {
        // Initialize
        var initResult = SendRequest("initialize", new JsonObject());
        Assert.NotNull(initResult);
        Assert.Equal("2025-06-18", initResult!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("msbuild-mcp", initResult["serverInfo"]!["name"]!.GetValue<string>());

        // Send initialized notification (no response expected)
        SendNotification("notifications/initialized");

        // List tools
        var toolsResult = SendRequest("tools/list");
        Assert.NotNull(toolsResult);
        var tools = toolsResult!["tools"]!.AsArray();
        Assert.Equal(13, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(x => x).ToList();
        Assert.Contains("list_projects", names);
        Assert.Contains("build", names);
        Assert.Contains("get_dependency_graph", names);
    }

    [Fact]
    public void ListProjectsOnOwnSolution()
    {
        SendRequest("initialize", new JsonObject());
        SendNotification("notifications/initialized");

        var slnPath = FindSolutionPath();
        if (slnPath == null) return;

        var result = SendRequest("tools/call", new JsonObject
        {
            ["name"] = "list_projects",
            ["arguments"] = new JsonObject { ["sln_path"] = slnPath },
        });

        Assert.NotNull(result);
        Assert.Null(result!["isError"]);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        Assert.True(parsed["project_count"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public void ParseBuildOutputViaTool()
    {
        SendRequest("initialize", new JsonObject());
        SendNotification("notifications/initialized");

        var result = SendRequest("tools/call", new JsonObject
        {
            ["name"] = "parse_build_output",
            ["arguments"] = new JsonObject
            {
                ["output"] = @"C:\src\a.cpp(10,5): error C2039: 'foo': not a member [C:\proj.vcxproj]
C:\src\b.cpp(20): warning C4996: deprecated [C:\proj.vcxproj]",
            },
        });

        Assert.NotNull(result);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        Assert.Equal(1, parsed["error_count"]!.GetValue<int>());
        Assert.Equal(1, parsed["warning_count"]!.GetValue<int>());
    }

    [Fact]
    public void ListPromptsViaProtocol()
    {
        SendRequest("initialize", new JsonObject());

        var result = SendRequest("prompts/list");
        Assert.NotNull(result);
        var prompts = result!["prompts"]!.AsArray();
        Assert.Equal(5, prompts.Count);
        var names = prompts.Select(p => p!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("diagnose-build-failure", names);
        Assert.Contains("what-to-build", names);
    }

    [Fact]
    public void ToolErrorReturnsIsError()
    {
        SendRequest("initialize", new JsonObject());

        // Call a tool with a missing required argument
        var result = SendRequest("tools/call", new JsonObject
        {
            ["name"] = "list_projects",
            ["arguments"] = new JsonObject(), // missing sln_path
        });

        Assert.NotNull(result);
        Assert.True(result!["isError"]!.GetValue<bool>());
    }

    private static string FindProjectDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var projDir = Path.Combine(dir, "src", "MsBuildMcp");
            if (Directory.Exists(projDir) && File.Exists(Path.Combine(projDir, "MsBuildMcp.csproj")))
                return projDir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot find MsBuildMcp project directory");
    }

    private static string? FindSolutionPath()
    {
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
