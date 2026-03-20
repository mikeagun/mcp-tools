using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpSharp;

namespace MsBuildMcp.Tests;

public class McpProtocolTests
{
    [Fact]
    public void InitializeReturnsCapabilities()
    {
        var server = new McpServer("test-server", "0.0.1");
        var result = server.Dispatch("initialize", null);

        Assert.NotNull(result);
        Assert.Equal("2025-06-18", result!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.NotNull(result["capabilities"]!["resources"]);
        Assert.NotNull(result["capabilities"]!["prompts"]);
        Assert.Equal("test-server", result["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsListReturnsRegisteredTools()
    {
        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "test_tool",
            Description = "A test tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonObject { ["result"] = "ok" },
        });

        var result = server.Dispatch("tools/list", null);
        var tools = result!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("test_tool", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCallInvokesHandler()
    {
        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "echo",
            Description = "Echo input",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = args =>
            {
                var msg = args["message"]?.GetValue<string>() ?? "none";
                return new JsonObject { ["echo"] = msg };
            },
        });

        var callParams = new JsonObject
        {
            ["name"] = "echo",
            ["arguments"] = new JsonObject { ["message"] = "hello" },
        };

        var result = server.Dispatch("tools/call", callParams);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text);
        Assert.Equal("hello", parsed!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCallReturnsErrorOnException()
    {
        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "fail",
            Description = "Always fails",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => throw new InvalidOperationException("test error"),
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "fail" });
        Assert.True(result!["isError"]!.GetValue<bool>());
        Assert.Contains("test error", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesListAndRead()
    {
        var server = new McpServer("test");
        server.RegisterResource(new ResourceInfo
        {
            Uri = "test://resource1",
            Name = "Test Resource",
            Description = "A test resource",
            Reader = () => new JsonObject { ["data"] = "test_data" },
        });

        var list = server.Dispatch("resources/list", null);
        Assert.Single(list!["resources"]!.AsArray());

        var read = server.Dispatch("resources/read", new JsonObject { ["uri"] = "test://resource1" });
        var text = read!["contents"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("test_data", text);
    }

    [Fact]
    public void PromptsListAndGet()
    {
        var server = new McpServer("test");
        server.RegisterPrompt(new PromptInfo
        {
            Name = "test_prompt",
            Description = "A test prompt",
            Arguments =
            [
                new PromptArgument { Name = "input", Description = "User input", Required = true }
            ],
            Handler = args =>
            {
                var input = args["input"]?.GetValue<string>() ?? "";
                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = $"Analyze: {input}" },
                    }
                };
            },
        });

        var list = server.Dispatch("prompts/list", null);
        var prompts = list!["prompts"]!.AsArray();
        Assert.Single(prompts);
        Assert.Equal("test_prompt", prompts[0]!["name"]!.GetValue<string>());

        var get = server.Dispatch("prompts/get", new JsonObject
        {
            ["name"] = "test_prompt",
            ["arguments"] = new JsonObject { ["input"] = "build errors" },
        });
        var messages = get!["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Contains("build errors", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void NotificationsReturnNull()
    {
        var server = new McpServer("test");
        Assert.Null(server.Dispatch("notifications/initialized", null));
        Assert.Null(server.Dispatch("notifications/cancelled", null));
    }

    [Fact]
    public void UnknownMethodThrows()
    {
        var server = new McpServer("test");
        Assert.Throws<InvalidOperationException>(() => server.Dispatch("foo/bar", null));
    }

    [Fact]
    public void NdjsonTransportRoundtrip()
    {
        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "ping",
            Description = "Ping",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonObject { ["pong"] = true },
        });

        // Build NDJSON input: initialize + tools/list + tools/call
        var requests = new[]
        {
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "ping" } },
        };

        var inputSb = new StringBuilder();
        foreach (var r in requests)
            inputSb.AppendLine(r.ToJsonString());

        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(inputSb.ToString()));
        var outputStream = new MemoryStream();

        var transport = new McpTransport(inputStream, outputStream);
        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        // Parse responses
        outputStream.Position = 0;
        var outputText = Encoding.UTF8.GetString(outputStream.ToArray());
        var responseLines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, responseLines.Length);

        var resp1 = JsonNode.Parse(responseLines[0]);
        Assert.Equal(1, resp1!["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", resp1["result"]!["protocolVersion"]!.GetValue<string>());

        var resp2 = JsonNode.Parse(responseLines[1]);
        Assert.Equal(2, resp2!["id"]!.GetValue<int>());
        Assert.Contains("pong", resp2["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ContentLengthTransportRoundtrip()
    {
        var server = new McpServer("test");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject(),
        };
        var body = request.ToJsonString();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var inputBytes = Encoding.UTF8.GetBytes(header).Concat(bodyBytes).ToArray();

        var inputStream = new MemoryStream(inputBytes);
        var outputStream = new MemoryStream();

        var transport = new McpTransport(inputStream, outputStream);
        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        outputStream.Position = 0;
        var outputText = Encoding.UTF8.GetString(outputStream.ToArray());
        Assert.Contains("Content-Length:", outputText);
        var bodyStart = outputText.IndexOf("\r\n\r\n") + 4;
        var responseBody = outputText[bodyStart..];
        var resp = JsonNode.Parse(responseBody);
        Assert.Equal(1, resp!["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", resp["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void TransportHandlerExceptionReturnsJsonRpcError()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "unknown/method",
            ["params"] = new JsonObject(),
        };
        var inputSb = new StringBuilder();
        inputSb.AppendLine(request.ToJsonString());

        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(inputSb.ToString()));
        var outputStream = new MemoryStream();

        var server = new McpServer("test");
        var transport = new McpTransport(inputStream, outputStream);
        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        outputStream.Position = 0;
        var outputText = Encoding.UTF8.GetString(outputStream.ToArray());
        var resp = JsonNode.Parse(outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.NotNull(resp!["error"]);
        Assert.Equal(-32603, resp["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Unknown method", resp["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void NotificationDoesNotSendResponse()
    {
        var server = new McpServer("test");
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        };
        var inputSb = new StringBuilder();
        inputSb.AppendLine(notification.ToJsonString());

        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(inputSb.ToString()));
        var outputStream = new MemoryStream();

        var transport = new McpTransport(inputStream, outputStream);
        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        Assert.Equal(0, outputStream.Length);
    }
}
