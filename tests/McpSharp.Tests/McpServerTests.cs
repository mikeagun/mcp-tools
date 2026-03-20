// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using Xunit;

namespace McpSharp.Tests;

public class McpServerTests
{
    private static McpServer CreateServer(string name = "test-server") => new(name);

    private static ToolInfo CreateTool(string name = "echo", Func<JsonObject, JsonNode?>? handler = null)
    {
        return new ToolInfo
        {
            Name = name,
            Description = $"Test tool: {name}",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["input"] = new JsonObject { ["type"] = "string" },
                },
            },
            Handler = handler ?? (args => new JsonObject { ["echoed"] = args["input"]?.GetValue<string>() }),
        };
    }

    private static ResourceInfo CreateResource(string uri = "test://data", Func<JsonNode?>? reader = null)
    {
        return new ResourceInfo
        {
            Uri = uri,
            Name = "test-resource",
            Description = "A test resource",
            MimeType = "application/json",
            Reader = reader ?? (() => new JsonObject { ["value"] = 42 }),
        };
    }

    private static PromptInfo CreatePrompt(string name = "greet", List<PromptArgument>? args = null)
    {
        return new PromptInfo
        {
            Name = name,
            Description = $"Test prompt: {name}",
            Arguments = args ?? [],
            Handler = a =>
            {
                var who = a["name"]?.GetValue<string>() ?? "world";
                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = $"Hello, {who}!" },
                    }
                };
            },
        };
    }

    // ── Initialize ──────────────────────────────────────────────

    [Fact]
    public void Initialize_ReturnsProtocolVersion()
    {
        var server = CreateServer("my-server");
        var result = server.Dispatch("initialize", null)!;

        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ReturnsServerName()
    {
        var server = CreateServer("custom-name");
        var result = server.Dispatch("initialize", null)!;

        Assert.Equal("custom-name", result["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ReturnsVersion()
    {
        var server = new McpServer("test", "1.2.3");
        var result = server.Dispatch("initialize", null)!;

        Assert.Equal("1.2.3", result["serverInfo"]!["version"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ReturnsCapabilities()
    {
        var result = CreateServer().Dispatch("initialize", null)!;
        var caps = result["capabilities"]!;

        Assert.NotNull(caps["tools"]);
        Assert.NotNull(caps["resources"]);
        Assert.NotNull(caps["prompts"]);
    }

    // ── Notifications ───────────────────────────────────────────

    [Fact]
    public void Notifications_ReturnNull()
    {
        var server = CreateServer();
        Assert.Null(server.Dispatch("notifications/initialized", null));
        Assert.Null(server.Dispatch("notifications/cancelled", null));
    }

    // ── Unknown method ──────────────────────────────────────────

    [Fact]
    public void UnknownMethod_Throws()
    {
        var server = CreateServer();
        Assert.Throws<InvalidOperationException>(() => server.Dispatch("bogus/method", null));
    }

    // ── Tools ───────────────────────────────────────────────────

    [Fact]
    public void ToolsList_EmptyByDefault()
    {
        var result = CreateServer().Dispatch("tools/list", null)!;
        Assert.Empty(result["tools"]!.AsArray());
    }

    [Fact]
    public void ToolsList_ReturnsRegisteredTools()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("alpha"));
        server.RegisterTool(CreateTool("beta"));

        var result = server.Dispatch("tools/list", null)!;
        var tools = result["tools"]!.AsArray();

        Assert.Equal(2, tools.Count);
        Assert.Equal("alpha", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("beta", tools[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_IncludesSchemaAndDescription()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("echo"));

        var result = server.Dispatch("tools/list", null)!;
        var tool = result["tools"]!.AsArray()[0]!;

        Assert.Equal("Test tool: echo", tool["description"]!.GetValue<string>());
        Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_InvokesHandler()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("echo"));

        var args = new JsonObject
        {
            ["name"] = "echo",
            ["arguments"] = new JsonObject { ["input"] = "hello" },
        };
        var result = server.Dispatch("tools/call", args)!;
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;

        Assert.Equal("hello", parsed["echoed"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_WithNullArguments_PassesEmptyObject()
    {
        bool called = false;
        var server = CreateServer();
        server.RegisterTool(CreateTool("noargs", args =>
        {
            called = true;
            Assert.Empty(args);
            return new JsonObject { ["ok"] = true };
        }));

        server.Dispatch("tools/call", new JsonObject { ["name"] = "noargs" });
        Assert.True(called);
    }

    [Fact]
    public void ToolsCall_HandlerReturnsNull_ReturnsNullText()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("nulltool", _ => null));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "nulltool" })!;
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();

        Assert.Equal("null", text);
    }

    [Fact]
    public void ToolsCall_HandlerThrows_ReturnsError()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("boom", _ => throw new InvalidOperationException("kaboom")));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "boom" })!;

        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("kaboom", text);
    }

    [Fact]
    public void ToolsCall_UnknownTool_Throws()
    {
        var server = CreateServer();
        Assert.Throws<InvalidOperationException>(() =>
            server.Dispatch("tools/call", new JsonObject { ["name"] = "nonexistent" }));
    }

    [Fact]
    public void ToolsCall_MissingName_Throws()
    {
        var server = CreateServer();
        Assert.Throws<ArgumentException>(() =>
            server.Dispatch("tools/call", new JsonObject()));
    }

    [Fact]
    public void RegisterTool_OverwritesByName()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("dup", _ => new JsonObject { ["v"] = 1 }));
        server.RegisterTool(CreateTool("dup", _ => new JsonObject { ["v"] = 2 }));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "dup" })!;
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("2", text);

        var list = server.Dispatch("tools/list", null)!["tools"]!.AsArray();
        Assert.Single(list);
    }

    // ── Resources ───────────────────────────────────────────────

    [Fact]
    public void ResourcesList_EmptyByDefault()
    {
        var result = CreateServer().Dispatch("resources/list", null)!;
        Assert.Empty(result["resources"]!.AsArray());
    }

    [Fact]
    public void ResourcesList_ReturnsRegisteredResources()
    {
        var server = CreateServer();
        server.RegisterResource(CreateResource("test://a"));
        server.RegisterResource(CreateResource("test://b"));

        var result = server.Dispatch("resources/list", null)!;
        var resources = result["resources"]!.AsArray();

        Assert.Equal(2, resources.Count);
        Assert.Equal("test://a", resources[0]!["uri"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesList_IncludesMimeType()
    {
        var server = CreateServer();
        server.RegisterResource(CreateResource());

        var result = server.Dispatch("resources/list", null)!;
        Assert.Equal("application/json", result["resources"]!.AsArray()[0]!["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_InvokesReader()
    {
        var server = CreateServer();
        server.RegisterResource(CreateResource("test://data", () => new JsonObject { ["answer"] = 42 }));

        var result = server.Dispatch("resources/read", new JsonObject { ["uri"] = "test://data" })!;
        var text = result["contents"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;

        Assert.Equal(42, parsed["answer"]!.GetValue<int>());
    }

    [Fact]
    public void ResourcesRead_ReaderReturnsNull_ReturnsNullText()
    {
        var server = CreateServer();
        server.RegisterResource(CreateResource("test://null", () => null));

        var result = server.Dispatch("resources/read", new JsonObject { ["uri"] = "test://null" })!;
        var text = result["contents"]!.AsArray()[0]!["text"]!.GetValue<string>();

        Assert.Equal("null", text);
    }

    [Fact]
    public void ResourcesRead_UnknownUri_Throws()
    {
        var server = CreateServer();
        Assert.Throws<InvalidOperationException>(() =>
            server.Dispatch("resources/read", new JsonObject { ["uri"] = "test://missing" }));
    }

    [Fact]
    public void ResourcesRead_MissingUri_Throws()
    {
        var server = CreateServer();
        Assert.Throws<ArgumentException>(() =>
            server.Dispatch("resources/read", new JsonObject()));
    }

    // ── Prompts ─────────────────────────────────────────────────

    [Fact]
    public void PromptsList_EmptyByDefault()
    {
        var result = CreateServer().Dispatch("prompts/list", null)!;
        Assert.Empty(result["prompts"]!.AsArray());
    }

    [Fact]
    public void PromptsList_ReturnsRegisteredPrompts()
    {
        var server = CreateServer();
        server.RegisterPrompt(CreatePrompt("greet"));

        var result = server.Dispatch("prompts/list", null)!;
        var prompts = result["prompts"]!.AsArray();

        Assert.Single(prompts);
        Assert.Equal("greet", prompts[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsList_WithArguments_IncludesArguments()
    {
        var server = CreateServer();
        server.RegisterPrompt(CreatePrompt("greet", [
            new PromptArgument { Name = "name", Description = "Who to greet", Required = true },
            new PromptArgument { Name = "style", Description = "Greeting style", Required = false },
        ]));

        var result = server.Dispatch("prompts/list", null)!;
        var prompt = result["prompts"]!.AsArray()[0]!;
        var args = prompt["arguments"]!.AsArray();

        Assert.Equal(2, args.Count);
        Assert.Equal("name", args[0]!["name"]!.GetValue<string>());
        Assert.True(args[0]!["required"]!.GetValue<bool>());
        Assert.False(args[1]!["required"]!.GetValue<bool>());
    }

    [Fact]
    public void PromptsList_WithoutArguments_OmitsArgumentsKey()
    {
        var server = CreateServer();
        server.RegisterPrompt(CreatePrompt("simple"));

        var result = server.Dispatch("prompts/list", null)!;
        var prompt = result["prompts"]!.AsArray()[0]!;

        Assert.Null(prompt["arguments"]);
    }

    [Fact]
    public void PromptsGet_InvokesHandler()
    {
        var server = CreateServer();
        server.RegisterPrompt(CreatePrompt("greet"));

        var result = server.Dispatch("prompts/get", new JsonObject
        {
            ["name"] = "greet",
            ["arguments"] = new JsonObject { ["name"] = "Alice" },
        })!;

        var messages = result["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Contains("Alice", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsGet_WithoutArguments_PassesEmptyObject()
    {
        var server = CreateServer();
        server.RegisterPrompt(CreatePrompt("greet"));

        var result = server.Dispatch("prompts/get", new JsonObject { ["name"] = "greet" })!;
        var text = result["messages"]!.AsArray()[0]!["content"]!["text"]!.GetValue<string>();

        Assert.Contains("world", text);
    }

    [Fact]
    public void PromptsGet_UnknownPrompt_Throws()
    {
        var server = CreateServer();
        Assert.Throws<InvalidOperationException>(() =>
            server.Dispatch("prompts/get", new JsonObject { ["name"] = "missing" }));
    }

    [Fact]
    public void PromptsGet_MissingName_Throws()
    {
        var server = CreateServer();
        Assert.Throws<ArgumentException>(() =>
            server.Dispatch("prompts/get", new JsonObject()));
    }

    // ── Client capabilities ─────────────────────────────────────

    [Fact]
    public void Initialize_WithElicitationCapability_SetsFlag()
    {
        var server = CreateServer();
        Assert.False(server.ClientSupportsElicitation);

        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["elicitation"] = new JsonObject(),
            },
        });

        Assert.True(server.ClientSupportsElicitation);
    }

    [Fact]
    public void Initialize_WithoutElicitationCapability_FlagIsFalse()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
        });

        Assert.False(server.ClientSupportsElicitation);
    }

    [Fact]
    public void Initialize_NullParams_FlagIsFalse()
    {
        var server = CreateServer();
        server.Dispatch("initialize", null);
        Assert.False(server.ClientSupportsElicitation);
    }

    // ── Elicitation ─────────────────────────────────────────────

    [Fact]
    public void Elicit_WithoutTransport_ReturnsNull()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        Assert.Null(server.Elicit("test", new JsonObject()));
    }

    [Fact]
    public void Elicit_WithoutElicitationCapability_ReturnsNull()
    {
        var server = CreateServer();
        server.Transport = new McpTransport(new MemoryStream(), new MemoryStream());
        server.Dispatch("initialize", null);

        Assert.Null(server.Elicit("test", new JsonObject()));
    }

    [Fact]
    public void Elicit_AcceptResponse_ReturnsContent()
    {
        var (server, input, output) = CreateServerWithTransport();

        // Pre-load the elicitation response into the input stream.
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["action"] = "accept",
                ["content"] = new JsonObject { ["choice"] = "approve" },
            },
        };
        WriteNdjsonToStream(input, response.ToJsonString());

        var result = server.Elicit("Pick one", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["choice"] = new JsonObject { ["type"] = "string" },
            },
        });

        Assert.NotNull(result);
        Assert.Equal(ElicitationAction.Accept, result.Action);
        Assert.Equal("approve", result.Content?["choice"]?.GetValue<string>());

        // Verify the elicitation request was written to output.
        var sent = ReadNdjsonFromStream(output);
        Assert.Equal("elicitation/create", sent["method"]?.GetValue<string>());
        Assert.Equal("s-1", sent["id"]?.GetValue<string>());
        Assert.Contains("Pick one", sent["params"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Elicit_DeclineResponse_ReturnsDecline()
    {
        var (server, input, _) = CreateServerWithTransport();

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject { ["action"] = "decline" },
        };
        WriteNdjsonToStream(input, response.ToJsonString());

        var result = server.Elicit("Confirm?", new JsonObject());

        Assert.NotNull(result);
        Assert.Equal(ElicitationAction.Decline, result.Action);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Elicit_CancelResponse_ReturnsCancel()
    {
        var (server, input, _) = CreateServerWithTransport();

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject { ["action"] = "cancel" },
        };
        WriteNdjsonToStream(input, response.ToJsonString());

        var result = server.Elicit("Confirm?", new JsonObject());

        Assert.NotNull(result);
        Assert.Equal(ElicitationAction.Cancel, result.Action);
    }

    [Fact]
    public void Elicit_ErrorResponse_ReturnsCancel()
    {
        var (server, input, _) = CreateServerWithTransport();

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "not supported" },
        };
        WriteNdjsonToStream(input, response.ToJsonString());

        var result = server.Elicit("Confirm?", new JsonObject());

        Assert.NotNull(result);
        Assert.Equal(ElicitationAction.Cancel, result.Action);
    }

    [Fact]
    public void Elicit_SkipsNotifications_ReadsResponse()
    {
        var (server, input, _) = CreateServerWithTransport();

        // Write a notification first, then the real response.
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
        };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["action"] = "accept",
                ["content"] = new JsonObject { ["value"] = "ok" },
            },
        };
        WriteNdjsonToStream(input, notification.ToJsonString(), response.ToJsonString());

        var result = server.Elicit("Test", new JsonObject());

        Assert.NotNull(result);
        Assert.Equal(ElicitationAction.Accept, result.Action);
        Assert.Equal("ok", result.Content?["value"]?.GetValue<string>());
    }

    [Fact]
    public void Elicit_StreamClosed_ReturnsNull()
    {
        var (server, _, _) = CreateServerWithTransport();
        // Input stream is empty — ReadMessage returns null.
        Assert.Null(server.Elicit("Test", new JsonObject()));
    }

    [Fact]
    public void Elicit_IncrementingIds_AreUnique()
    {
        // First elicitation uses s-1, second uses s-2.
        var (server, input, output) = CreateServerWithTransport();

        WriteNdjsonToStream(input,
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s-1",
                ["result"] = new JsonObject { ["action"] = "accept",
                    ["content"] = new JsonObject { ["n"] = 1 } } }.ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s-2",
                ["result"] = new JsonObject { ["action"] = "accept",
                    ["content"] = new JsonObject { ["n"] = 2 } } }.ToJsonString());

        var r1 = server.Elicit("First", new JsonObject());
        var r2 = server.Elicit("Second", new JsonObject());

        Assert.Equal(1, r1!.Content!["n"]!.GetValue<int>());
        Assert.Equal(2, r2!.Content!["n"]!.GetValue<int>());

        // Verify both requests were sent with different IDs.
        output.Position = 0;
        var raw = System.Text.Encoding.UTF8.GetString(output.ToArray());
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var req1 = JsonNode.Parse(lines[0])!;
        var req2 = JsonNode.Parse(lines[1])!;
        Assert.Equal("s-1", req1["id"]?.GetValue<string>());
        Assert.Equal("s-2", req2["id"]?.GetValue<string>());
    }

    // ── Elicitation helpers ─────────────────────────────────────

    private static (McpServer server, MemoryStream input, MemoryStream output) CreateServerWithTransport()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // Trigger NDJSON framing detection by reading a dummy message.
        var dummy = System.Text.Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();

        // Clear streams for test data.
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("test-server");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        return (server, input, output);
    }

    private static void WriteNdjsonToStream(MemoryStream stream, params string[] jsonLines)
    {
        stream.SetLength(0);
        stream.Position = 0;
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", jsonLines) + "\n");
        stream.Write(bytes);
        stream.Position = 0;
    }

    private static JsonNode ReadNdjsonFromStream(MemoryStream stream)
    {
        stream.Position = 0;
        var raw = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        return JsonNode.Parse(firstLine)!;
    }
}
