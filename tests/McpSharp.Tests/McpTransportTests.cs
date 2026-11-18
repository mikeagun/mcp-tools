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
    public void ReadMessage_UnframedGarbageThenEof_ReturnsNull()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("not json at all\n"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // First read detects NDJSON (starts with 'n', not '{'), falls to Content-Length
        // The unframeable header block is skipped; end of stream then yields null
        Assert.Null(transport.ReadMessage());
    }

    [Fact]
    public void ContentLength_HeaderBlockWithoutLength_SkippedUntilEof()
    {
        // Header that doesn't contain Content-Length
        var input = new MemoryStream(Encoding.UTF8.GetBytes("X-Custom: foo\r\n\r\n{}\r\n"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // 'X' is not '{' so Content-Length framing is selected
        // The header block has no Content-Length, so it is skipped; end of stream then yields null
        Assert.Null(transport.ReadMessage());
    }

    // ── Robustness: encoding and malformed input ─────────────────

    private static readonly Func<string, JsonNode?, JsonNode?> EchoText =
        (_, p) => new JsonObject { ["echo"] = p?["text"]?.DeepClone() };

    private static JsonNode ResponseFor(List<JsonNode> messages, int id)
        => messages.Single(m => m["id"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.GetValue<int>() == id);

    private static JsonNode ErrorWithCode(List<JsonNode> messages, int code)
        => messages.Single(m => m["error"]?["code"]?.GetValue<int>() == code);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAsciiText_RoundTripsIntact(bool contentLength)
    {
        const string text = "caf\u00e9 \u65e5\u672c \U0001F600";
        // Clients send raw (unescaped) UTF-8, so build the requests by hand;
        // JsonNode.ToJsonString() would escape non-ASCII as \uXXXX.
        var requests = new[]
        {
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"echo\",\"params\":{{\"text\":\"{text}\"}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"echo\",\"params\":{{\"text\":\"{text}\"}}}}",
        };
        var input = contentLength ? MakeContentLengthInput(requests) : MakeNdjsonInput(requests);
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run(EchoText);

        var responses = contentLength ? ParseContentLengthOutput(output) : ParseNdjsonOutput(output);
        Assert.Equal(2, responses.Count);
        // The first message exercises the framing-detection path; the second the steady-state path.
        Assert.All(responses, r => Assert.Equal(text, r["result"]!["echo"]!.GetValue<string>()));
    }

    [Fact]
    public void Ndjson_LeadingUtf8Bom_IsIgnored()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(MakeJsonRpcRequest(1, "echo", new JsonObject { ["text"] = "hi" }) + "\n"))
            .ToArray();
        var output = new MemoryStream();
        var transport = new McpTransport(new MemoryStream(bytes), output, "test");

        transport.Run(EchoText);

        var response = Assert.Single(ParseNdjsonOutput(output));
        Assert.Equal("hi", response["result"]!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void Ndjson_InvalidUtf8_IsReplacedNotFatal()
    {
        var line = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"echo\",\"params\":{\"text\":\"a")
            .Concat(new byte[] { 0xFF })
            .Concat(Encoding.UTF8.GetBytes("b\"}}\n"))
            .ToArray();
        var output = new MemoryStream();
        var transport = new McpTransport(new MemoryStream(line), output, "test");

        transport.Run(EchoText);

        var response = Assert.Single(ParseNdjsonOutput(output));
        Assert.Equal("a\uFFFDb", response["result"]!["echo"]!.GetValue<string>());
    }

    [Fact]
    public void Ndjson_ParseError_RespondsAndKeepsServing()
    {
        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "tools/list"),
            "{this is not json",
            MakeJsonRpcRequest(2, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var messages = ParseNdjsonOutput(output);
        Assert.Equal(3, messages.Count);
        var parseError = ErrorWithCode(messages, -32700);
        Assert.Null(parseError["id"]);
        Assert.True(parseError.AsObject().ContainsKey("id"));
        Assert.NotNull(ResponseFor(messages, 1)["result"]);
        Assert.NotNull(ResponseFor(messages, 2)["result"]);
    }

    [Fact]
    public void ContentLength_ParseError_RespondsAndKeepsServing()
    {
        var input = MakeContentLengthInput(
            MakeJsonRpcRequest(1, "tools/list"),
            "{not json}",
            MakeJsonRpcRequest(2, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var messages = ParseContentLengthOutput(output);
        Assert.Equal(3, messages.Count);
        Assert.Null(ErrorWithCode(messages, -32700)["id"]);
        Assert.NotNull(ResponseFor(messages, 1)["result"]);
        Assert.NotNull(ResponseFor(messages, 2)["result"]);
    }

    [Fact]
    public void ContentLength_HeaderBlockWithoutLength_SkipsAndKeepsServing()
    {
        var request = MakeJsonRpcRequest(1, "tools/list");
        var raw = "X-Custom: foo\r\n\r\n"
            + $"Content-Length: {Encoding.UTF8.GetByteCount(request)}\r\n\r\n{request}";
        var output = new MemoryStream();
        var transport = new McpTransport(new MemoryStream(Encoding.UTF8.GetBytes(raw)), output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var response = Assert.Single(ParseContentLengthOutput(output));
        Assert.Equal(1, response["id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[1,2]", null)]
    [InlineData("\"str\"", null)]
    [InlineData("42", null)]
    [InlineData("null", null)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":7}", 5)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"x\"}", null)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":9}", 9)]
    public void Ndjson_InvalidRequest_RespondsAndKeepsServing(string invalid, int? expectedId)
    {
        // A valid first line establishes NDJSON framing (a scalar first line
        // would select Content-Length framing by design).
        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "tools/list"), invalid, MakeJsonRpcRequest(2, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var messages = ParseNdjsonOutput(output);
        Assert.Equal(3, messages.Count);
        var error = ErrorWithCode(messages, -32600);
        if (expectedId is null)
            Assert.Null(error["id"]);
        else
            Assert.Equal(expectedId, error["id"]!.GetValue<int>());
        Assert.NotNull(ResponseFor(messages, 1)["result"]);
        Assert.NotNull(ResponseFor(messages, 2)["result"]);
    }

    [Fact]
    public void Ndjson_NotificationWithNonStringMethod_DroppedSilently()
    {
        var input = MakeNdjsonInput(
            "{\"jsonrpc\":\"2.0\",\"method\":5}",
            MakeJsonRpcRequest(1, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var response = Assert.Single(ParseNdjsonOutput(output));
        Assert.Equal(1, response["id"]!.GetValue<int>());
    }

    [Fact]
    public void Ndjson_UnroutableResponses_DoNotStopReader()
    {
        var input = MakeNdjsonInput(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":{\"x\":1},\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-1,\"message\":\"m\"}}",
            MakeJsonRpcRequest(1, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var response = Assert.Single(ParseNdjsonOutput(output));
        Assert.Equal(1, response["id"]!.GetValue<int>());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonObjectParams_GetsInvalidParamsAndKeepsServing(bool concurrent)
    {
        var input = MakeNdjsonInput(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":[]}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"subscriptions/listen\",\"params\":[]}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":5}",
            MakeJsonRpcRequest(3, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m }, concurrent);
        WaitForOutputCount(output, 3, concurrent);

        var messages = ParseNdjsonOutput(output);
        Assert.Equal(3, messages.Count);
        Assert.Equal(-32602, ResponseFor(messages, 1)["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, ResponseFor(messages, 2)["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(ResponseFor(messages, 3)["result"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonObjectMeta_IsIgnoredAndKeepsServing(bool concurrent)
    {
        var input = MakeNdjsonInput(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"x\",\"_meta\":7}}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"subscriptions/listen\",\"params\":{\"_meta\":7}}",
            MakeJsonRpcRequest(3, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m }, concurrent);
        WaitForOutputCount(output, 3, concurrent);

        var messages = ParseNdjsonOutput(output);
        Assert.Equal(3, messages.Count);
        Assert.Equal("tools/call", ResponseFor(messages, 1)["result"]!["ok"]!.GetValue<string>());
        Assert.Equal("subscriptions/listen", ResponseFor(messages, 2)["result"]!["ok"]!.GetValue<string>());
        Assert.NotNull(ResponseFor(messages, 3)["result"]);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"method\":\"y\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"a\",\"name\":\"b\"}}")]
    public void DuplicateProperty_GetsInvalidRequestAndKeepsServing(string duplicate)
    {
        var input = MakeNdjsonInput(duplicate, MakeJsonRpcRequest(2, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        transport.Run((m, _) => new JsonObject { ["ok"] = m });

        var messages = ParseNdjsonOutput(output);
        Assert.Equal(2, messages.Count);
        Assert.Null(ErrorWithCode(messages, -32600)["id"]);
        Assert.NotNull(ResponseFor(messages, 2)["result"]);
    }

    /// <summary>
    /// With concurrent dispatch, Run() returns once the queue drains while
    /// ThreadPool handlers may still be writing; wait for the expected output.
    /// </summary>
    private static void WaitForOutputCount(MemoryStream output, int count, bool concurrent)
    {
        if (!concurrent) return;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                var text = Encoding.UTF8.GetString(output.ToArray());
                if (text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= count) return;
            }
            Thread.Sleep(20);
        }
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
                Thread.Sleep(3_000); // >1s initial delay triggers a keepalive tick
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
            Thread.Sleep(3_000); // triggers a keepalive tick (1s initial delay)
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

    // ── Rich progress: push (Report) + pull (status provider) ───────

    [Fact]
    public void Progress_Current_DefaultsToNoOp()
    {
        // Outside any tools/call, the ambient reporter is a non-null no-op.
        Assert.NotNull(McpProgress.Current);
        McpProgress.Current.Report("ignored");
        McpProgress.Current.SetStatusProvider(() => "ignored");
    }

    [Fact]
    public void Progress_Push_EmitsImmediatelyWithMonotonicCounter()
    {
        // Handler pushes two explicit reports and returns fast (no 10s timer wait).
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "push-1" },
                ["name"] = "pushy_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "pushy_tool",
            Description = "Pushes explicit progress",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                McpProgress.Current.Report("step one");
                McpProgress.Current.Report("step two");
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        var progressMsgs = messages.Where(m =>
            m["method"]?.GetValue<string>() == "notifications/progress").ToList();

        Assert.Equal(2, progressMsgs.Count);
        Assert.Equal("push-1", progressMsgs[0]["params"]!["progressToken"]!.GetValue<string>());
        Assert.Equal(1, progressMsgs[0]["params"]!["progress"]!.GetValue<long>());
        Assert.Equal("step one", progressMsgs[0]["params"]!["message"]!.GetValue<string>());
        Assert.Equal(2, progressMsgs[1]["params"]!["progress"]!.GetValue<long>());
        Assert.Equal("step two", progressMsgs[1]["params"]!["message"]!.GetValue<string>());

        // Response follows the progress notifications.
        var response = messages.Last(m => m.AsObject().ContainsKey("id"));
        Assert.NotNull(response["result"]);
    }

    [Fact]
    public void Progress_Push_WithoutProgressTokenIsNoOp()
    {
        // No _meta.progressToken → the ambient reporter is a no-op; Report emits nothing.
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "pushy_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "pushy_tool",
            Description = "Pushes explicit progress",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                McpProgress.Current.Report("should be dropped");
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        Assert.DoesNotContain(messages, m => m["method"]?.GetValue<string>() == "notifications/progress");
        Assert.Single(messages); // just the response
    }

    [Fact]
    public void Progress_Pull_TimerEmitsProviderMessageInsteadOfHeartbeat()
    {
        // Slow handler registers a status provider; the keepalive tick should emit
        // the provider's message instead of the static heartbeat text.
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "pull-1" },
                ["name"] = "slow_pull_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "slow_pull_tool",
            Description = "Registers a status provider then waits",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                McpProgress.Current.SetStatusProvider(() => "42 lines output. Last: 'compiling'");
                Thread.Sleep(3_000); // >1s initial delay → a timer tick fires
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        var progressMsgs = messages.Where(m =>
            m["method"]?.GetValue<string>() == "notifications/progress").ToList();

        Assert.True(progressMsgs.Count >= 1, "Expected at least one progress notification");
        Assert.Equal("42 lines output. Last: 'compiling'",
            progressMsgs[0]["params"]!["message"]!.GetValue<string>());
        Assert.Equal(1, progressMsgs[0]["params"]!["progress"]!.GetValue<long>());
    }

    [Fact]
    public void Progress_Push_AfterHandlerReturns_IsDropped()
    {
        // Capture the live reporter during the call, then push after it returns.
        // The session is stopped by then, so the late push must not be emitted
        // (and must never land after the response).
        IProgressReporter? captured = null;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "late-1" },
                ["name"] = "capturing_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        server.RegisterTool(new ToolInfo
        {
            Name = "capturing_tool",
            Description = "Captures the reporter",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                captured = McpProgress.Current;
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        // The call has completed and the session is stopped — this push is dropped.
        captured!.Report("late push");

        var messages = ParseNdjsonOutput(output);
        Assert.DoesNotContain(messages, m =>
            m["method"]?.GetValue<string>() == "notifications/progress" &&
            m["params"]?["message"]?.GetValue<string>() == "late push");
    }

    // ── Adaptive dedupe-pull ────────────────────────────────────────

    [Theory]
    // force always emits, even when unchanged and within backstop.
    [InlineData("x", "x", 0L, 15000L, true, true)]
    // first emission (nothing emitted yet).
    [InlineData("x", null, 0L, 15000L, false, true)]
    // content changed → emit.
    [InlineData("y", "x", 0L, 15000L, false, true)]
    // unchanged within the backstop window → skip.
    [InlineData("x", "x", 5000L, 15000L, false, false)]
    // unchanged but backstop elapsed → emit to refresh the client timeout.
    [InlineData("x", "x", 15000L, 15000L, false, true)]
    [InlineData("x", "x", 20000L, 15000L, false, true)]
    public void ShouldEmit_DedupeAndBackstop(string message, string? lastEmitted,
        long msSinceLastEmit, long backstopMs, bool force, bool expected)
    {
        Assert.Equal(expected,
            ProgressSession.ShouldEmit(message, lastEmitted, msSinceLastEmit, backstopMs, force));
    }

    [Fact]
    public void Progress_Pull_ChangingProvider_EmitsMultipleDistinctUpdates()
    {
        // A provider whose text changes each tick should produce several distinct,
        // monotonically-counted progress updates on the fast cadence — not a single
        // sample. Exercises the adaptive dedupe-pull path end to end.
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["progressToken"] = "adapt-1" },
                ["name"] = "changing_tool",
                ["arguments"] = new JsonObject(),
            },
        };
        var input = MakeNdjsonInput(request.ToJsonString());
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var server = new McpServer("test");
        var lines = 0;
        server.RegisterTool(new ToolInfo
        {
            Name = "changing_tool",
            Description = "Provider text changes over time",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                McpProgress.Current.SetStatusProvider(() => $"{Volatile.Read(ref lines)} lines");
                // Advance the reported line count while the timer ticks (~1s, 3s, 5s)
                // so at least two ticks observe distinct values.
                for (var i = 0; i < 5; i++)
                {
                    Thread.Sleep(1_000);
                    Interlocked.Increment(ref lines);
                }
                return new JsonObject { ["done"] = true };
            },
        });
        server.Transport = transport;

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        var messages = ParseNdjsonOutput(output);
        var progressMsgs = messages.Where(m =>
            m["method"]?.GetValue<string>() == "notifications/progress").ToList();

        // At least two distinct updates over ~5s (vs. exactly one under the old
        // fixed 15s cadence).
        var distinct = progressMsgs
            .Select(m => m["params"]!["message"]!.GetValue<string>())
            .Distinct()
            .ToList();
        Assert.True(distinct.Count >= 2,
            $"Expected >=2 distinct progress messages, got {distinct.Count}: [{string.Join(", ", distinct)}]");

        // Counter is monotonically increasing across emissions.
        var counts = progressMsgs.Select(m => m["params"]!["progress"]!.GetValue<long>()).ToList();
        for (var i = 1; i < counts.Count; i++)
            Assert.True(counts[i] > counts[i - 1], "progress counter must be monotonic");
    }

    // ── server/discover integration ─────────────────────────────

    [Fact]
    public void Ndjson_DiscoverThenInitializeThenToolsList()
    {
        var server = new McpServer("disco-test", "1.0.0");
        server.RegisterTool(new ToolInfo
        {
            Name = "ping",
            Description = "Ping tool",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => new JsonObject { ["pong"] = true },
        });

        var input = MakeNdjsonInput(
            MakeJsonRpcRequest(1, "server/discover"),
            MakeJsonRpcRequest(2, "initialize"),
            MakeJsonRpcNotification("notifications/initialized"),
            MakeJsonRpcRequest(3, "tools/list"));
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");
        server.Transport = transport;

        transport.Run(server.Dispatch);

        var responses = ParseNdjsonOutput(output);
        Assert.Equal(3, responses.Count);

        // discover response
        var discover = responses[0]["result"]!;
        Assert.Equal("complete", discover["resultType"]!.GetValue<string>());
        Assert.Equal("2025-06-18", discover["supportedVersions"]![0]!.GetValue<string>());

        // initialize response
        var init = responses[1]["result"]!;
        Assert.Equal("2025-06-18", init["protocolVersion"]!.GetValue<string>());

        // tools/list response
        var tools = responses[2]["result"]!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("ping", tools[0]!["name"]!.GetValue<string>());
    }
}
