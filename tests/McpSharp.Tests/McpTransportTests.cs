// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace McpSharp.Tests;

public class McpTransportTests
{
    // ── Helpers ─────────────────────────────────────────────────

    private static MemoryStream MakeNdjsonInput(params string[] jsonLines)
    {
        var sb = new StringBuilder();
        foreach (var line in jsonLines)
            sb.AppendLine(line);
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static MemoryStream MakeContentLengthInput(params string[] jsonBodies)
    {
        var sb = new StringBuilder();
        foreach (var body in jsonBodies)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            sb.Append(body);
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string MakeJsonRpcRequest(int id, string method, JsonNode? @params = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null)
            obj["params"] = @params;
        return obj.ToJsonString();
    }

    private static string MakeJsonRpcNotification(string method)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        }.ToJsonString();
    }

    private static List<JsonNode> ParseNdjsonOutput(MemoryStream output)
    {
        output.Position = 0;
        var text = Encoding.UTF8.GetString(output.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private static List<JsonNode> ParseContentLengthOutput(MemoryStream output)
    {
        output.Position = 0;
        var results = new List<JsonNode>();
        var reader = new StreamReader(output, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            var headerLine = reader.ReadLine();
            if (headerLine == null) break;
            if (!headerLine.StartsWith("Content-Length: ")) continue;

            var length = int.Parse(headerLine.AsSpan(16));
            reader.ReadLine(); // empty line after headers

            var buf = new char[length];
            int read = 0;
            while (read < length)
            {
                int n = reader.Read(buf, read, length - read);
                if (n == 0) break;
                read += n;
            }

            results.Add(JsonNode.Parse(new string(buf, 0, read))!);
        }

        return results;
    }

    // ── NDJSON framing ──────────────────────────────────────────

    [Fact]
    public void Ndjson_AutoDetectsFromOpenBrace()
    {
        var request = MakeJsonRpcRequest(1, "initialize");
        var input = MakeNdjsonInput(request);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((method, _) =>
            new JsonObject { ["method"] = method });

        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
        Assert.Equal("2.0", responses[0]["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
    }

    [Fact]
    public void Ndjson_MultipleRequests()
    {
        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "initialize"),
            MakeJsonRpcRequest(2, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((method, _) =>
            new JsonObject { ["method"] = method });

        var responses = ParseNdjsonOutput(output);
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
        Assert.Equal(2, responses[1]["id"]!.GetValue<int>());
    }

    [Fact]
    public void Ndjson_NotificationProducesNoResponse()
    {
        var input = MakeNdjsonInput(
            MakeJsonRpcNotification("notifications/initialized"),
            MakeJsonRpcRequest(1, "initialize"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((_, _) => new JsonObject { ["ok"] = true });

        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
    }

    [Fact]
    public void Ndjson_HandlerThrows_ReturnsJsonRpcError()
    {
        var input = MakeNdjsonInput(MakeJsonRpcRequest(1, "fail"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((_, _) => throw new Exception("test error"));

        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
        Assert.Equal(-32603, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.Contains("test error", responses[0]["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Ndjson_NullResult_ResponseHasNullResult()
    {
        var input = MakeNdjsonInput(MakeJsonRpcRequest(1, "test"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((_, _) => null);

        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
        Assert.Null(responses[0]["result"]);
    }

    [Fact]
    public void Ndjson_SkipsLeadingWhitespace()
    {
        // Input with leading whitespace/newlines before JSON
        var raw = "\n  \r\n" + MakeJsonRpcRequest(1, "test") + "\n";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((_, _) => new JsonObject { ["ok"] = true });

        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
    }

    // ── Content-Length framing ───────────────────────────────────

    [Fact]
    public void ContentLength_AutoDetectsFromHeader()
    {
        var request = MakeJsonRpcRequest(1, "initialize");
        var input = MakeContentLengthInput(request);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((method, _) =>
            new JsonObject { ["method"] = method });

        var responses = ParseContentLengthOutput(output);
        Assert.Single(responses);
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
    }

    [Fact]
    public void ContentLength_MultipleRequests()
    {
        var input = MakeContentLengthInput(
            MakeJsonRpcRequest(1, "first"),
            MakeJsonRpcRequest(2, "second"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((method, _) =>
            new JsonObject { ["method"] = method });

        var responses = ParseContentLengthOutput(output);
        Assert.Equal(2, responses.Count);
    }

    [Fact]
    public void ContentLength_ResponseHasContentLengthHeader()
    {
        var input = MakeContentLengthInput(MakeJsonRpcRequest(1, "test"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((_, _) => new JsonObject { ["ok"] = true });

        output.Position = 0;
        var raw = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("Content-Length: ", raw);
        Assert.Contains("\r\n\r\n", raw);
    }

    // ── ReadMessage / WriteMessage direct ────────────────────────

    [Fact]
    public void ReadMessage_EmptyStream_ReturnsNull()
    {
        var input = new MemoryStream([]);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        Assert.Null(transport.ReadMessage());
    }

    [Fact]
    public void WriteMessage_Ndjson_AppendsNewline()
    {
        // Force NDJSON by reading a NDJSON message first
        var jsonReq = MakeJsonRpcRequest(1, "test");
        var input = MakeNdjsonInput(jsonReq);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.ReadMessage(); // triggers NDJSON auto-detect
        transport.WriteMessage(new JsonObject { ["test"] = true });

        output.Position = 0;
        var raw = Encoding.UTF8.GetString(output.ToArray());
        Assert.EndsWith("\n", raw);
        Assert.DoesNotContain("Content-Length", raw);
    }

    [Fact]
    public void ReadMessage_InvalidJson_ReturnsNull()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("not json at all\n"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // First read detects NDJSON (starts with 'n', not '{'), falls to Content-Length
        // Content-Length expects "Content-Length: N" but gets "not json...", returns null
        Assert.Null(transport.ReadMessage());
    }

    [Fact]
    public void ContentLength_NoContentLengthHeader_ReturnsNull()
    {
        // Header that doesn't contain Content-Length
        var input = new MemoryStream(Encoding.UTF8.GetBytes("X-Custom: foo\r\n\r\n{}\r\n"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // 'X' is not '{' so Content-Length framing is selected
        // But "X-Custom: foo" is not a Content-Length header, returns null
        Assert.Null(transport.ReadMessage());
    }

    // ── Integration: full round-trip with McpServer ──────────────

    [Fact]
    public void Integration_NdjsonRoundTrip()
    {
        var server = new McpServer("integration-test", "1.0.0");
        server.RegisterTool(new ToolInfo
        {
            Name = "add",
            Description = "Add two numbers",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["a"] = new JsonObject { ["type"] = "number" },
                    ["b"] = new JsonObject { ["type"] = "number" },
                },
            },
            Handler = args =>
            {
                var a = args["a"]!.GetValue<int>();
                var b = args["b"]!.GetValue<int>();
                return new JsonObject { ["sum"] = a + b };
            },
        });

        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "initialize"),
            MakeJsonRpcRequest(2, "tools/list"),
            MakeJsonRpcRequest(3, "tools/call", new JsonObject
            {
                ["name"] = "add",
                ["arguments"] = new JsonObject { ["a"] = 3, ["b"] = 4 },
            }));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "integration");

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var responses = ParseNdjsonOutput(output);
        Assert.Equal(3, responses.Count);

        // Initialize
        Assert.Equal("integration-test", responses[0]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("2025-06-18", responses[0]["result"]!["protocolVersion"]!.GetValue<string>());

        // Tools list
        var tools = responses[1]["result"]!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("add", tools[0]!["name"]!.GetValue<string>());

        // Tools call
        var content = responses[2]["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var resultObj = JsonNode.Parse(content)!;
        Assert.Equal(7, resultObj["sum"]!.GetValue<int>());
    }

    [Fact]
    public void Integration_ContentLengthRoundTrip()
    {
        var server = new McpServer("cl-test");
        server.RegisterResource(new ResourceInfo
        {
            Uri = "config://main",
            Name = "config",
            Description = "Main config",
            Reader = () => new JsonObject { ["debug"] = false },
        });

        var input = MakeContentLengthInput(
            MakeJsonRpcRequest(1, "resources/list"),
            MakeJsonRpcRequest(2, "resources/read", new JsonObject { ["uri"] = "config://main" }));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "cl");

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var responses = ParseContentLengthOutput(output);
        Assert.Equal(2, responses.Count);

        // Resources list
        var resources = responses[0]["result"]!["resources"]!.AsArray();
        Assert.Single(resources);

        // Resources read
        var text = responses[1]["result"]!["contents"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("false", text);
    }

    [Fact]
    public void Integration_PromptRoundTrip()
    {
        var server = new McpServer("prompt-test");
        server.RegisterPrompt(new PromptInfo
        {
            Name = "summarize",
            Description = "Summarize text",
            Arguments =
            [
                new PromptArgument { Name = "text", Description = "Text to summarize", Required = true },
            ],
            Handler = args =>
            {
                var text = args["text"]!.GetValue<string>();
                return new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = $"Summarize: {text}" },
                    }
                };
            },
        });

        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "prompts/list"),
            MakeJsonRpcRequest(2, "prompts/get", new JsonObject
            {
                ["name"] = "summarize",
                ["arguments"] = new JsonObject { ["text"] = "Long document here" },
            }));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "prompt");

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var responses = ParseNdjsonOutput(output);
        Assert.Equal(2, responses.Count);

        // Prompts list includes arguments
        var prompt = responses[0]["result"]!["prompts"]!.AsArray()[0]!;
        Assert.Equal("summarize", prompt["name"]!.GetValue<string>());
        Assert.Single(prompt["arguments"]!.AsArray());

        // Prompts get
        var messages = responses[1]["result"]!["messages"]!.AsArray();
        Assert.Contains("Long document here", messages[0]!["content"]!["text"]!.GetValue<string>());
    }

    // ── Server-initiated elicitation round-trip ──

    [Fact]
    public void ServerInitiatedElicitation_RoundTrip_Ndjson()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "elic");

        // Lock NDJSON framing.
        input.Write(Encoding.UTF8.GetBytes("{\"_\":0}\n"));
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("elic");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        // Pre-load the client's accept response (routed via the reader thread /
        // _responseWaiters path).
        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["action"] = "accept",
                ["content"] = new JsonObject { ["v"] = "ok" },
            },
        };
        input.Write(Encoding.UTF8.GetBytes(resp.ToJsonString() + "\n"));
        input.Position = 0;

        var result = server.Elicit("Pick", new JsonObject());

        Assert.Equal(ElicitationAction.Accept, result!.Action);
        Assert.Equal("ok", result.Content!["v"]!.GetValue<string>());

        var sent = ParseNdjsonOutput(output);
        Assert.Single(sent);
        Assert.Equal("elicitation/create", sent[0]["method"]!.GetValue<string>());
        Assert.Equal("s-1", sent[0]["id"]!.GetValue<string>());
    }

    [Fact]
    public void ServerInitiatedElicitation_RoundTrip_ContentLength()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "elic");

        // Lock Content-Length framing via a framed dummy.
        var dummy = "{\"_\":0}";
        var dummyBytes = Encoding.UTF8.GetBytes(dummy);
        input.Write(Encoding.UTF8.GetBytes($"Content-Length: {dummyBytes.Length}\r\n\r\n"));
        input.Write(dummyBytes);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("elic");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        var respBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject { ["action"] = "accept", ["content"] = new JsonObject { ["v"] = "ok" } },
        }.ToJsonString();
        var rb = Encoding.UTF8.GetBytes(respBody);
        input.Write(Encoding.UTF8.GetBytes($"Content-Length: {rb.Length}\r\n\r\n"));
        input.Write(rb);
        input.Position = 0;

        var result = server.Elicit("Pick", new JsonObject());

        Assert.Equal(ElicitationAction.Accept, result!.Action);
        Assert.Equal("ok", result.Content!["v"]!.GetValue<string>());

        var sent = ParseContentLengthOutput(output);
        Assert.Single(sent);
        Assert.Equal("elicitation/create", sent[0]["method"]!.GetValue<string>());
    }

    // ── LogPrefix ───────────────────────────────────────────────

    [Fact]
    public void LogPrefix_DefaultsToMcpServer()
    {
        // Just verify construction doesn't throw with null prefix
        var transport = new McpTransport(new MemoryStream(), new MemoryStream());
        Assert.NotNull(transport);
    }

    [Fact]
    public void LogPrefix_CustomPrefixUsedInErrorLog()
    {
        // Handler throws on notification → logged to stderr with prefix
        // We can't easily capture Console.Error in a test, but verify it doesn't crash
        var input = MakeNdjsonInput(
            MakeJsonRpcNotification("notifications/initialized"),
            MakeJsonRpcRequest(1, "test"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "custom-prefix");

        transport.Run((method, _) =>
        {
            if (method == "notifications/initialized")
                throw new Exception("notification handler error");
            return new JsonObject { ["ok"] = true };
        });

        // Should still process the second request despite notification error
        var responses = ParseNdjsonOutput(output);
        Assert.Single(responses);
    }

    // ── Progress keepalive ──────────────────────────────────────

    [Fact]
    public void SendProgress_EmitsValidNotification()
    {
        var input = new MemoryStream(); // empty — no reading needed
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.SendProgress(JsonValue.Create("tok-1"), 1);

        var messages = ParseContentLengthOutput(output);
        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal("2.0", msg["jsonrpc"]!.GetValue<string>());
        Assert.Equal("notifications/progress", msg["method"]!.GetValue<string>());
        Assert.False(msg.AsObject().ContainsKey("id")); // notification — no id
        Assert.Equal("tok-1", msg["params"]!["progressToken"]!.GetValue<string>());
        Assert.Equal(1, msg["params"]!["progress"]!.GetValue<long>());
    }

    [Fact]
    public void SendProgress_IncludesOptionalFields()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.SendProgress(JsonValue.Create("tok-2"), 5, total: 10, message: "halfway");

        var messages = ParseContentLengthOutput(output);
        Assert.Single(messages);
        var p = messages[0]["params"]!;
        Assert.Equal(5, p["progress"]!.GetValue<long>());
        Assert.Equal(10, p["total"]!.GetValue<long>());
        Assert.Equal("halfway", p["message"]!.GetValue<string>());
    }

    [Fact]
    public void SendProgress_OmitsNullOptionalFields()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.SendProgress(JsonValue.Create("tok-3"), 2);

        var messages = ParseContentLengthOutput(output);
        var p = messages[0]["params"]!.AsObject();
        Assert.False(p.ContainsKey("total"));
        Assert.False(p.ContainsKey("message"));
    }

    [Fact]
    public void SendProgress_PreservesNumericToken()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.SendProgress(JsonValue.Create(42), 1);

        var messages = ParseContentLengthOutput(output);
        var p = messages[0]["params"]!;
        Assert.Equal(42, p["progressToken"]!.GetValue<int>());
    }

    [Fact]
    public void Keepalive_EmitsProgressForSlowToolCall()
    {
        // Simulate a tools/call with _meta.progressToken and a slow handler.
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "keep-1" },
                ["name"] = "slow_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "slow_tool",
            Description = "A slow tool for testing keepalive",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                Thread.Sleep(12_000); // >10s triggers keepalive
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        // Should have at least 1 progress notification + 1 final response
        Assert.True(messages.Count >= 2,
            $"Expected at least 2 messages (progress + response), got {messages.Count}");

        // Find progress notifications
        var progressMsgs = messages.Where(m =>
            m["method"]?.GetValue<string>() == "notifications/progress").ToList();
        Assert.True(progressMsgs.Count >= 1, "Expected at least one progress notification");

        var firstProgress = progressMsgs[0]["params"]!;
        Assert.Equal("keep-1", firstProgress["progressToken"]!.GetValue<string>());
        Assert.Equal(1, firstProgress["progress"]!.GetValue<long>());

        // Final response should be a successful tool result
        var response = messages.Last(m => m.AsObject().ContainsKey("id"));
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
    }

    [Fact]
    public void Keepalive_DoesNotEmitWithoutProgressToken()
    {
        // tools/call without _meta.progressToken — no keepalive
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "slow_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "slow_tool",
            Description = "A slow tool",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                Thread.Sleep(100); // short — no keepalive should fire
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        // Only the response, no progress notifications
        Assert.Single(messages);
        Assert.Equal(1, messages[0]["id"]!.GetValue<int>());
    }

    [Fact]
    public void Keepalive_StopsOnHandlerError()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "err-1" },
                ["name"] = "failing_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((method, parameters) =>
        {
            Thread.Sleep(11_000); // triggers one keepalive tick
            throw new Exception("handler failed");
        });

        var messages = ParseNdjsonOutput(output);
        // Should have progress notification(s) + error response
        var errorResponse = messages.Last(m => m.AsObject().ContainsKey("error"));
        Assert.NotNull(errorResponse);
        Assert.Equal(-32603, errorResponse["error"]!["code"]!.GetValue<int>());

        // Timer should have been disposed — no further messages after error
        // (can't easily test absence of future messages, but at least verify it didn't crash)
    }
}
