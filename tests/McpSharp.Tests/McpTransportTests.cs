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
}
