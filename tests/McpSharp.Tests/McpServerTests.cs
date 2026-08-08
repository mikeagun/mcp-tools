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

    // ── Discover ────────────────────────────────────────────────

    [Fact]
    public void Discover_ReturnsSupportedVersions()
    {
        var server = CreateServer("my-server");
        var result = server.Dispatch("server/discover", null)!;

        var versions = result["supportedVersions"]!.AsArray();
        Assert.Single(versions);
        Assert.Equal("2025-06-18", versions[0]!.GetValue<string>());
    }

    [Fact]
    public void Discover_ReturnsResultTypeComplete()
    {
        var result = CreateServer().Dispatch("server/discover", null)!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void Discover_ReturnsCapabilities()
    {
        var result = CreateServer().Dispatch("server/discover", null)!;
        var caps = result["capabilities"]!;

        Assert.NotNull(caps["tools"]);
        Assert.NotNull(caps["resources"]);
        Assert.NotNull(caps["prompts"]);
    }

    [Fact]
    public void Discover_ReturnsServerInfoInMeta()
    {
        var server = new McpServer("discover-test", "2.0.1");
        var result = server.Dispatch("server/discover", null)!;

        var meta = result["_meta"]!["io.modelcontextprotocol/serverInfo"]!;
        Assert.Equal("discover-test", meta["name"]!.GetValue<string>());
        Assert.Equal("2.0.1", meta["version"]!.GetValue<string>());
    }

    // ── resultType ────────────────────────────────────────────────

    [Theory]
    [InlineData("server/discover")]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    [InlineData("resources/list")]
    [InlineData("prompts/list")]
    public void AllListMethods_ReturnResultTypeComplete(string method)
    {
        var server = CreateServer();
        var result = server.Dispatch(method, null)!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ReturnsResultTypeComplete()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool());
        var result = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = "echo",
            ["arguments"] = new JsonObject { ["input"] = "hi" },
        })!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_ReturnsResultTypeComplete()
    {
        var server = CreateServer();
        server.RegisterResource(new ResourceInfo
        {
            Uri = "test://r",
            Name = "r",
            Description = "d",
            MimeType = "text/plain",
            Reader = () => JsonNode.Parse("\"hello\""),
        });
        var result = server.Dispatch("resources/read", new JsonObject { ["uri"] = "test://r" })!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsGet_ReturnsResultTypeComplete()
    {
        var server = CreateServer();
        server.RegisterPrompt(new PromptInfo
        {
            Name = "test",
            Description = "d",
            Handler = _ => new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        });
        var result = server.Dispatch("prompts/get", new JsonObject { ["name"] = "test" })!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
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
    public void ToolsList_IncludesMetadataFields()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "rich",
            Description = "A rich tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => null,
            Title = "Rich Tool Display Name",
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" } } },
            Icons = new JsonArray(new JsonObject { ["src"] = "https://example.com/icon.png", ["mimeType"] = "image/png" }),
        });

        var result = server.Dispatch("tools/list", null)!;
        var tool = result["tools"]!.AsArray()[0]!;

        Assert.Equal("Rich Tool Display Name", tool["title"]!.GetValue<string>());
        Assert.Equal("object", tool["outputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/icon.png", tool["icons"]![0]!["src"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_OmitsNullMetadataFields()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("plain"));

        var result = server.Dispatch("tools/list", null)!;
        var tool = result["tools"]!.AsArray()[0]!;

        Assert.Null(tool["title"]);
        Assert.Null(tool["outputSchema"]);
        Assert.Null(tool["icons"]);
    }

    [Fact]
    public void ToolsList_AppliesToolFilter()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("public_tool"));
        server.RegisterTool(CreateTool("admin_tool"));
        server.ToolFilter = (tool, ctx) => !tool.Name.StartsWith("admin");

        var result = server.Dispatch("tools/list", null)!;
        var tools = result["tools"]!.AsArray();

        Assert.Single(tools);
        Assert.Equal("public_tool", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_FilterReceivesRequestContext()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("gated"));
        RequestContext? captured = null;
        server.ToolFilter = (tool, ctx) => { captured = ctx; return true; };

        var parameters = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "TestClient", ["version"] = "1.0" },
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
            },
        };
        server.Dispatch("tools/list", parameters);

        Assert.NotNull(captured);
        Assert.Equal("2026-07-28", captured!.ProtocolVersion);
        Assert.Equal("TestClient", captured.ClientInfo!["name"]!.GetValue<string>());
        Assert.NotNull(captured.ClientCapabilities);
    }

    [Fact]
    public void ToolsCall_RejectsFilteredOutTool()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("secret"));
        server.ToolFilter = (tool, ctx) => false;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            server.Dispatch("tools/call", new JsonObject { ["name"] = "secret" }));
        Assert.Contains("Unknown tool", ex.Message);
    }

    [Fact]
    public void ToolsCall_AllowsToolPassingFilter()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("allowed"));
        server.ToolFilter = (tool, ctx) => tool.Name == "allowed";

        var result = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = "allowed",
            ["arguments"] = new JsonObject { ["input"] = "test" },
        })!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void RequestContext_ParsesMetaFields()
    {
        var parameters = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "Agent", ["version"] = "2.0" },
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
                ["progressToken"] = "tok-123",
            },
        };

        var ctx = RequestContext.Parse(parameters);

        Assert.Equal("2026-07-28", ctx.ProtocolVersion);
        Assert.Equal("Agent", ctx.ClientInfo!["name"]!.GetValue<string>());
        Assert.NotNull(ctx.ClientCapabilities);
        Assert.Equal("tok-123", ctx.ProgressToken);
        Assert.NotNull(ctx.RawMeta);
    }

    [Fact]
    public void RequestContext_ReturnsEmptyWhenNoMeta()
    {
        var ctx = RequestContext.Parse(null);
        Assert.Same(RequestContext.Empty, ctx);
        Assert.Null(ctx.ProtocolVersion);

        var ctx2 = RequestContext.Parse(new JsonObject { ["name"] = "test" });
        Assert.Same(RequestContext.Empty, ctx2);
    }

    [Theory]
    [InlineData("\"tok-str\"", "tok-str")]
    [InlineData("42", "42")]
    public void RequestContext_HandlesStringAndNumericProgressToken(string tokenJson, string expected)
    {
        var parameters = JsonNode.Parse($"{{\"_meta\":{{\"progressToken\":{tokenJson}}}}}");
        var ctx = RequestContext.Parse(parameters);
        Assert.Equal(expected, ctx.ProgressToken);
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
    public void ToolsCall_RawResult_PassesThroughContentDirectly()
    {
        var server = CreateServer();
        var upstreamResult = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "raw output from upstream" }
            }
        };
        server.RegisterTool(new ToolInfo
        {
            Name = "proxy_tool",
            Description = "Forwarding tool",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => JsonNode.Parse(upstreamResult.ToJsonString())!.AsObject(),
            RawResult = true,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "proxy_tool" })!;

        // Content should be the upstream content directly, not double-wrapped.
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("raw output from upstream", text);
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_RawResult_PreservesIsErrorFlag()
    {
        var server = CreateServer();
        var errorResult = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "something went wrong" }
            },
            ["isError"] = true,
        };
        server.RegisterTool(new ToolInfo
        {
            Name = "proxy_error",
            Description = "Forwarding tool that returns error",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => JsonNode.Parse(errorResult.ToJsonString())!.AsObject(),
            RawResult = true,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "proxy_error" })!;

        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("something went wrong", text);
    }

    [Fact]
    public void ToolsCall_RawResult_PreservesMultipleContentBlocks()
    {
        var server = CreateServer();
        var multiContent = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "first block" },
                new JsonObject { ["type"] = "text", ["text"] = "second block" },
            }
        };
        server.RegisterTool(new ToolInfo
        {
            Name = "proxy_multi",
            Description = "Forwarding tool with multiple content blocks",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => JsonNode.Parse(multiContent.ToJsonString())!.AsObject(),
            RawResult = true,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "proxy_multi" })!;

        var content = result["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("first block", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("second block", content[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_RawResult_FalseByDefault_WrapsResult()
    {
        // Verify that the default (RawResult = false) behavior is unchanged.
        var server = CreateServer();
        var contentResult = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "should be wrapped" }
            }
        };
        server.RegisterTool(new ToolInfo
        {
            Name = "normal_tool",
            Description = "Normal tool returning content-like object",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => JsonNode.Parse(contentResult.ToJsonString())!.AsObject(),
            // RawResult defaults to false — should wrap the result.
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "normal_tool" })!;

        // The entire handler result should be serialized into a text content block.
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        // The inner "content" is part of the serialized JSON, not a top-level MCP content block.
        Assert.NotNull(parsed["content"]);
    }

    [Fact]
    public void ToolsCall_RawResult_HandlerReturnsNull_FallsBackToWrapping()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "proxy_null",
            Description = "Forwarding tool returning null",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => null,
            RawResult = true,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "proxy_null" })!;

        // Null result falls back to normal wrapping even with RawResult = true.
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("null", text);
    }

    [Fact]
    public void ToolsCall_RawResult_HandlerReturnsNonObject_FallsBackToWrapping()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "proxy_string",
            Description = "Forwarding tool returning a string value",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => JsonValue.Create("plain string"),
            RawResult = true,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "proxy_string" })!;

        // Non-JsonObject falls back to normal wrapping even with RawResult = true.
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("\"plain string\"", text);
    }

    [Fact]
    public void ToolsCall_WithOutputSchema_IncludesStructuredContent()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "structured",
            Description = "Returns structured data",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" } } },
            Handler = _ => new JsonObject { ["count"] = 42 },
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "structured" })!;

        // content block contains JSON text
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("42", text);
        // structuredContent contains the raw object
        var sc = result["structuredContent"]!;
        Assert.Equal(42, sc["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_WithOutputSchema_AndTextRenderer_UsesRenderer()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "rendered",
            Description = "Uses text renderer",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object" },
            TextRenderer = node => $"Count is {node!["count"]!.GetValue<int>()}",
            Handler = _ => new JsonObject { ["count"] = 7 },
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "rendered" })!;

        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("Count is 7", text);
        Assert.Equal(7, result["structuredContent"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_WithOutputSchema_NullResult_NoStructuredContent()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "null_out",
            Description = "Returns null",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => null,
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "null_out" })!;

        // No structuredContent when result is null
        Assert.Null(result["structuredContent"]);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("null", text);
    }

    [Fact]
    public void ToolsCall_WithOutputSchema_NonJsonObject_NoStructuredContent()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "array_out",
            Description = "Returns an array",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonArray(JsonValue.Create(1), JsonValue.Create(2)),
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "array_out" })!;

        Assert.Null(result["structuredContent"]);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("[1,2]", text);
    }

    [Fact]
    public void ToolsCall_RawResult_IgnoresOutputSchema()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "raw_with_schema",
            Description = "RawResult takes precedence",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object" },
            RawResult = true,
            Handler = _ => new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "raw" } }
            },
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "raw_with_schema" })!;

        // RawResult wins — no structuredContent auto-added
        Assert.Null(result["structuredContent"]);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("raw", text);
    }

    [Fact]
    public void ToolsCall_TextRenderer_WithoutOutputSchema_OnlyAffectsContent()
    {
        var server = CreateServer();
        server.RegisterTool(new ToolInfo
        {
            Name = "renderer_only",
            Description = "TextRenderer without OutputSchema",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            TextRenderer = node => "custom text",
            Handler = _ => new JsonObject { ["data"] = "ignored" },
        });

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "renderer_only" })!;

        Assert.Null(result["structuredContent"]);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("custom text", text);
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
    public void ToolsCall_AuthenticationException_ReturnsStructuredAuthError()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("authtool", _ =>
            throw new AuthenticationException("GitHub", "Token expired", "Run: gh auth login")));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "authtool" })!;

        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("AUTHENTICATION FAILED", text);
        Assert.Contains("GitHub", text);
        Assert.Contains("Token expired", text);
        Assert.Contains("STOP", text);
        Assert.Contains("gh auth login", text);
    }

    [Fact]
    public void ToolsCall_AuthenticationException_IncludesRemediation()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("adoauth", _ =>
            throw new AuthenticationException("ADO", "Auth failed",
                "1. Run: az login\n2. Set AZURE_DEVOPS_PAT")));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "adoauth" })!;

        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("ADO", text);
        Assert.Contains("az login", text);
        Assert.Contains("AZURE_DEVOPS_PAT", text);
        Assert.Contains("do not retry", text);
    }

    [Fact]
    public void ToolsCall_AuthException_WithoutElicitation_FallsBackToStopError()
    {
        // Server without transport → ClientSupportsElicitation is false → no elicitation
        var server = CreateServer();
        var resetCalled = false;
        server.RegisterTool(CreateTool("authtool", _ =>
            throw new AuthenticationException("GitHub", "Token expired", "Fix it")
            { ResetAuth = () => resetCalled = true }));

        var result = server.Dispatch("tools/call", new JsonObject { ["name"] = "authtool" })!;

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("AUTHENTICATION FAILED", result["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.False(resetCalled); // ResetAuth not called without elicitation
    }

    [Fact]
    public void ToolsCall_AuthException_ResetAuthCallbackOnRetry()
    {
        // Verify that ResetAuth callback is correctly wired — test the property itself
        var resetCount = 0;
        var authEx = new AuthenticationException("ADO", "Auth failed", "Fix it")
        { ResetAuth = () => resetCount++ };

        authEx.ResetAuth();
        authEx.ResetAuth();

        Assert.Equal(2, resetCount);
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

    // ── Dynamic Tools & Subscriptions ───────────────────────────

    [Fact]
    public void AddTool_AddsToRegistry()
    {
        var server = CreateServer();
        Assert.Empty(server.Dispatch("tools/list", null)!["tools"]!.AsArray());

        server.AddTool(CreateTool("dynamic"));
        var tools = server.Dispatch("tools/list", null)!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("dynamic", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveTool_RemovesFromRegistry()
    {
        var server = CreateServer();
        server.RegisterTool(CreateTool("removable"));
        Assert.Single(server.Dispatch("tools/list", null)!["tools"]!.AsArray());

        Assert.True(server.RemoveTool("removable"));
        Assert.Empty(server.Dispatch("tools/list", null)!["tools"]!.AsArray());
    }

    [Fact]
    public void RemoveTool_ReturnsFalseForUnknown()
    {
        var server = CreateServer();
        Assert.False(server.RemoveTool("nonexistent"));
    }

    [Fact]
    public void AddTool_NotifiesSubscribers()
    {
        var (server, output) = CreateSubscriptionTestServer();
        SubscribeForTools(server, output, "1");

        // Clear the output so we only see the notification
        output.SetLength(0);
        server.AddTool(CreateTool("new_tool"));

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/tools/list_changed", notification["method"]!.GetValue<string>());
        Assert.Equal("1", notification["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.ToString());
    }

    [Fact]
    public void RemoveTool_NotifiesSubscribers()
    {
        var (server, output) = CreateSubscriptionTestServer();
        server.RegisterTool(CreateTool("old_tool"));
        SubscribeForTools(server, output, "2");
        output.SetLength(0);

        server.RemoveTool("old_tool");

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/tools/list_changed", notification["method"]!.GetValue<string>());
    }

    [Fact]
    public void SubscriptionsListen_SendsAcknowledgment()
    {
        var (server, output) = CreateSubscriptionTestServer();

        var result = server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "sub-1" },
            ["notifications"] = new JsonObject { ["toolsListChanged"] = true },
        });

        // Handler returns the deferred sentinel
        Assert.True(result is JsonObject obj && obj.ContainsKey("__deferred"));

        // Acknowledgment was sent
        var ack = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/subscriptions/acknowledged", ack["method"]!.GetValue<string>());
        Assert.Equal("sub-1", ack["params"]!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.ToString());
        Assert.True(ack["params"]!["notifications"]!["toolsListChanged"]!.GetValue<bool>());
    }

    [Fact]
    public void SubscriptionsListen_OnlyAcksRequestedNotifications()
    {
        var (server, output) = CreateSubscriptionTestServer();

        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "sub-2" },
            ["notifications"] = new JsonObject { ["resourcesListChanged"] = true },
        });

        var ack = ReadAllNdjsonMessages(output).First();
        Assert.Null(ack["params"]!["notifications"]!["toolsListChanged"]);
        Assert.True(ack["params"]!["notifications"]!["resourcesListChanged"]!.GetValue<bool>());

        // Adding a tool should NOT notify this subscriber (only subscribed to resources)
        output.SetLength(0);
        server.AddTool(CreateTool("ignored"));
        Assert.Empty(ReadAllNdjsonMessages(output));
    }

    [Fact]
    public void NotificationsCancelled_ClosesSubscription()
    {
        var (server, output) = CreateSubscriptionTestServer();
        SubscribeForTools(server, output, "3");
        output.SetLength(0);

        // Cancel the subscription
        server.Dispatch("notifications/cancelled", new JsonObject { ["requestId"] = "3" });

        // Should get a graceful closure response
        var closure = ReadAllNdjsonMessages(output).First();
        Assert.Equal("3", closure["id"]!.ToString());
        Assert.Equal("complete", closure["result"]!["resultType"]!.GetValue<string>());

        // No more notifications after cancellation
        output.SetLength(0);
        server.AddTool(CreateTool("post_cancel"));
        Assert.Empty(ReadAllNdjsonMessages(output));
    }

    [Fact]
    public void CloseAllSubscriptions_ClosesAll()
    {
        var (server, output) = CreateSubscriptionTestServer();
        SubscribeForTools(server, output, "10");
        SubscribeForTools(server, output, "11");
        output.SetLength(0);

        server.CloseAllSubscriptions();

        var messages = ReadAllNdjsonMessages(output);
        Assert.Equal(2, messages.Count);
        var ids = messages.Select(m => m["id"]!.ToString()).OrderBy(x => x).ToList();
        Assert.Equal("10", ids[0]);
        Assert.Equal("11", ids[1]);

        // No more notifications
        output.SetLength(0);
        server.AddTool(CreateTool("after_close"));
        Assert.Empty(ReadAllNdjsonMessages(output));
    }

    [Fact]
    public void Initialize_AdvertisesListChanged()
    {
        var server = CreateServer();
        var result = server.Dispatch("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        })!;

        var toolsCap = result["capabilities"]!["tools"]!;
        Assert.True(toolsCap["listChanged"]!.GetValue<bool>());
        var resourcesCap = result["capabilities"]!["resources"]!;
        Assert.True(resourcesCap["listChanged"]!.GetValue<bool>());
        var promptsCap = result["capabilities"]!["prompts"]!;
        Assert.True(promptsCap["listChanged"]!.GetValue<bool>());
    }

    [Fact]
    public void AddPrompt_AddsToRegistryAndNotifies()
    {
        var (server, output) = CreateSubscriptionTestServer();
        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "p1" },
            ["notifications"] = new JsonObject { ["promptsListChanged"] = true },
        });
        output.SetLength(0);

        Assert.Empty(server.Dispatch("prompts/list", null)!["prompts"]!.AsArray());
        server.AddPrompt(new PromptInfo
        {
            Name = "test_prompt",
            Description = "A test prompt",
            Handler = _ => new JsonArray(),
        });

        var prompts = server.Dispatch("prompts/list", null)!["prompts"]!.AsArray();
        Assert.Single(prompts);
        Assert.Equal("test_prompt", prompts[0]!["name"]!.GetValue<string>());

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/prompts/list_changed", notification["method"]!.GetValue<string>());
    }

    [Fact]
    public void RemovePrompt_RemovesAndNotifies()
    {
        var (server, output) = CreateSubscriptionTestServer();
        server.RegisterPrompt(new PromptInfo
        {
            Name = "removable",
            Description = "Will be removed",
            Handler = _ => new JsonArray(),
        });
        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "p2" },
            ["notifications"] = new JsonObject { ["promptsListChanged"] = true },
        });
        output.SetLength(0);

        Assert.True(server.RemovePrompt("removable"));
        Assert.False(server.RemovePrompt("nonexistent"));
        Assert.Empty(server.Dispatch("prompts/list", null)!["prompts"]!.AsArray());

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/prompts/list_changed", notification["method"]!.GetValue<string>());
    }

    [Fact]
    public void AddResource_AddsToRegistryAndNotifies()
    {
        var (server, output) = CreateSubscriptionTestServer();
        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "r1" },
            ["notifications"] = new JsonObject { ["resourcesListChanged"] = true },
        });
        output.SetLength(0);

        server.AddResource(new ResourceInfo
        {
            Uri = "test://res",
            Name = "test_res",
            Description = "A test resource",
            Reader = () => new JsonObject { ["data"] = "hello" },
        });

        var resources = server.Dispatch("resources/list", null)!["resources"]!.AsArray();
        Assert.Single(resources);

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/resources/list_changed", notification["method"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveResource_RemovesAndNotifies()
    {
        var (server, output) = CreateSubscriptionTestServer();
        server.RegisterResource(new ResourceInfo
        {
            Uri = "test://rem",
            Name = "rem",
            Description = "Will be removed",
            Reader = () => null,
        });
        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = "r2" },
            ["notifications"] = new JsonObject { ["resourcesListChanged"] = true },
        });
        output.SetLength(0);

        Assert.True(server.RemoveResource("test://rem"));
        Assert.False(server.RemoveResource("nonexistent"));

        var notification = ReadAllNdjsonMessages(output).First();
        Assert.Equal("notifications/resources/list_changed", notification["method"]!.GetValue<string>());
    }

    // ── Subscription test helpers ───────────────────────────────

    private static (McpServer server, MemoryStream output) CreateSubscriptionTestServer()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        // Trigger NDJSON framing detection by reading a dummy message.
        var dummy = System.Text.Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();

        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = CreateServer();
        server.Transport = transport;
        return (server, output);
    }

    private static void SubscribeForTools(McpServer server, MemoryStream output, string requestId)
    {
        server.Dispatch("subscriptions/listen", new JsonObject
        {
            ["_meta"] = new JsonObject { ["__requestId"] = requestId },
            ["notifications"] = new JsonObject { ["toolsListChanged"] = true },
        });
    }

    private static List<JsonNode> ReadAllNdjsonMessages(MemoryStream stream)
    {
        stream.Position = 0;
        var raw = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line.TrimEnd('\r'))!)
            .ToList();
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

    // ── Elicitation capabilities ────────────────────────────────

    [Fact]
    public void Parse_Absent_Unsupported()
    {
        var caps = ElicitationCapabilities.Parse(null);
        Assert.False(caps.Supported);
        Assert.False(caps.Form);
        Assert.False(caps.Url);
    }

    [Fact]
    public void Parse_EmptyObject_FormOnly()
    {
        // An empty object => form mode only (backwards compatibility).
        var caps = ElicitationCapabilities.Parse(new JsonObject());
        Assert.True(caps.Supported);
        Assert.True(caps.Form);
        Assert.False(caps.Url);
    }

    [Fact]
    public void Parse_ExplicitFormAndUrl_BothModes()
    {
        var caps = ElicitationCapabilities.Parse(new JsonObject
        {
            ["form"] = new JsonObject(),
            ["url"] = new JsonObject(),
        });
        Assert.True(caps.Supported);
        Assert.True(caps.Form);
        Assert.True(caps.Url);
    }

    [Fact]
    public void Parse_FormOnly_FormTrueUrlFalse()
    {
        var caps = ElicitationCapabilities.Parse(new JsonObject { ["form"] = new JsonObject() });
        Assert.True(caps.Supported);
        Assert.True(caps.Form);
        Assert.False(caps.Url);
    }

    [Fact]
    public void Parse_UrlOnly_UrlTrueFormFalse()
    {
        // Explicit url-only client: form mode is NOT advertised.
        var caps = ElicitationCapabilities.Parse(new JsonObject { ["url"] = new JsonObject() });
        Assert.True(caps.Supported);
        Assert.False(caps.Form);
        Assert.True(caps.Url);
    }

    [Fact]
    public void Supports_MapsModeToCapability()
    {
        var caps = ElicitationCapabilities.Parse(new JsonObject { ["url"] = new JsonObject() });
        Assert.False(caps.Supports(ElicitationMode.Form));
        Assert.True(caps.Supports(ElicitationMode.Url));
    }

    [Fact]
    public void Initialize_PopulatesElicitationCaps_FormOnlyFromEmptyObject()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        Assert.True(server.ClientSupportsElicitation);
        Assert.True(server.ElicitationCaps.Form);
        Assert.False(server.ElicitationCaps.Url);
    }

    [Fact]
    public void Initialize_PopulatesElicitationCaps_UrlOnly()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["elicitation"] = new JsonObject { ["url"] = new JsonObject() },
            },
        });

        Assert.True(server.ClientSupportsElicitation);
        Assert.False(server.ElicitationCaps.Form);
        Assert.True(server.ElicitationCaps.Url);
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

    // ── Mode guard ──────────────────────────────────────────────

    [Fact]
    public void Elicit_FormClient_NeverEmitsModeField()
    {
        // Form mode omits `mode` — and must never emit mode:"url".
        var (server, input, output) = CreateServerWithTransport();
        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject { ["action"] = "accept", ["content"] = new JsonObject() },
        }.ToJsonString());

        server.Elicit("Pick one", new JsonObject());

        var sent = ReadNdjsonFromStream(output);
        Assert.Equal("elicitation/create", sent["method"]?.GetValue<string>());
        Assert.False(sent["params"]!.AsObject().ContainsKey("mode"),
            "form-mode requests must omit the mode field, never send mode:\"url\"");
    }

    [Fact]
    public void Elicit_UrlOnlyClient_RefusesFormRequest_WritesNothing()
    {
        // A url-only client did not advertise form mode — Elicit (form) must refuse
        // and send nothing: never provoke a -32602 from the client.
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var dummy = System.Text.Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("test-server");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["elicitation"] = new JsonObject { ["url"] = new JsonObject() },
            },
        });

        var result = server.Elicit("Pick one", new JsonObject());

        Assert.Null(result);
        Assert.Equal(0, output.Length); // nothing was written to the wire
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

    // ── Sampling capability parsing ─────────────────────────────

    [Fact]
    public void SamplingCapabilities_Parse_Null_ReturnsNone()
    {
        var caps = SamplingCapabilities.Parse(null);
        Assert.False(caps.Supported);
        Assert.False(caps.Tools);
    }

    [Fact]
    public void SamplingCapabilities_Parse_EmptyObject_BasicSupport()
    {
        var caps = SamplingCapabilities.Parse(new JsonObject());
        Assert.True(caps.Supported);
        Assert.False(caps.Tools);
    }

    [Fact]
    public void SamplingCapabilities_Parse_WithTools()
    {
        var caps = SamplingCapabilities.Parse(new JsonObject
        {
            ["tools"] = new JsonObject(),
        });
        Assert.True(caps.Supported);
        Assert.True(caps.Tools);
        Assert.False(caps.Context);
    }

    [Fact]
    public void SamplingCapabilities_Parse_WithContext()
    {
        var caps = SamplingCapabilities.Parse(new JsonObject
        {
            ["context"] = new JsonObject(),
        });
        Assert.True(caps.Supported);
        Assert.False(caps.Tools);
        Assert.True(caps.Context);
    }

    [Fact]
    public void Initialize_PopulatesSamplingCaps()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["sampling"] = new JsonObject { ["tools"] = new JsonObject() },
            },
        });
        Assert.True(server.SamplingCaps.Supported);
        Assert.True(server.SamplingCaps.Tools);
    }

    [Fact]
    public void Initialize_NoSampling_CapsUnsupported()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject(),
        });
        Assert.False(server.SamplingCaps.Supported);
    }

    // ── Sampling ────────────────────────────────────────────────

    [Fact]
    public void Sample_WithoutTransport_ReturnsNull()
    {
        var server = CreateServer();
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["sampling"] = new JsonObject() },
        });

        Assert.Null(server.Sample(CreateSamplingParams()));
    }

    [Fact]
    public void Sample_WithoutSamplingCapability_ReturnsNull()
    {
        var (server, _, _) = CreateSamplingServerWithTransport();
        // Re-initialize without sampling capability
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject(),
        });

        Assert.Null(server.Sample(CreateSamplingParams()));
    }

    [Fact]
    public void Sample_ToolsRequestWithoutToolsCap_ReturnsNull()
    {
        var (server, _, _) = CreateSamplingServerWithTransport(withTools: false);

        var paramsWithTools = CreateSamplingParams();
        paramsWithTools["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "test_tool",
            ["description"] = "A tool",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        });

        Assert.Null(server.Sample(paramsWithTools));
    }

    [Fact]
    public void Sample_ToolChoiceWithoutToolsCap_ReturnsNull()
    {
        var (server, _, _) = CreateSamplingServerWithTransport(withTools: false);

        var paramsWithChoice = CreateSamplingParams();
        paramsWithChoice["toolChoice"] = new JsonObject { ["mode"] = "auto" };

        Assert.Null(server.Sample(paramsWithChoice));
    }

    [Fact]
    public void Sample_IncludeContextWithoutContextCap_StillAllowed()
    {
        // includeContext "thisServer"/"allServers" is SHOULD NOT without context cap, not MUST NOT.
        var (server, input, _) = CreateSamplingServerWithTransport(withTools: false);

        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "ok" },
                ["model"] = "m",
                ["stopReason"] = "endTurn",
            },
        }.ToJsonString());

        var paramsWithContext = CreateSamplingParams();
        paramsWithContext["includeContext"] = "thisServer";

        var result = server.Sample(paramsWithContext);
        Assert.NotNull(result);
    }

    [Fact]
    public void Sample_IncludeContextNone_AllowedWithoutContextCap()
    {
        var (server, input, _) = CreateSamplingServerWithTransport(withTools: false);

        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "ok" },
                ["model"] = "m",
                ["stopReason"] = "endTurn",
            },
        }.ToJsonString());

        var paramsNoneContext = CreateSamplingParams();
        paramsNoneContext["includeContext"] = "none";

        var result = server.Sample(paramsNoneContext);
        Assert.NotNull(result);
    }

    [Fact]
    public void Sample_MalformedResponse_ReturnsNull()
    {
        var (server, input, _) = CreateSamplingServerWithTransport();

        // Response with role as a number instead of string
        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["role"] = 42,
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "ok" },
            },
        }.ToJsonString());

        Assert.Null(server.Sample(CreateSamplingParams()));
    }

    [Fact]
    public void Sample_BasicTextResponse()
    {
        var (server, input, output) = CreateSamplingServerWithTransport();

        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Paris" },
                ["model"] = "test-model",
                ["stopReason"] = "endTurn",
            },
        }.ToJsonString());

        var result = server.Sample(CreateSamplingParams());

        Assert.NotNull(result);
        Assert.Equal("assistant", result.Role);
        Assert.Equal("Paris", result.Content["text"]!.GetValue<string>());
        Assert.Equal("test-model", result.Model);
        Assert.Equal("endTurn", result.StopReason);
        Assert.False(result.IsToolUse);

        // Verify the request was sent correctly
        var sent = ReadNdjsonFromStream(output);
        Assert.Equal("sampling/createMessage", sent["method"]!.GetValue<string>());
        Assert.Equal("s-1", sent["id"]!.GetValue<string>());
        Assert.NotNull(sent["params"]!["messages"]);
    }

    [Fact]
    public void Sample_ToolUseResponse()
    {
        var (server, input, _) = CreateSamplingServerWithTransport(withTools: true);

        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "call_123",
                    ["name"] = "get_weather",
                    ["input"] = new JsonObject { ["city"] = "Paris" },
                }),
                ["model"] = "test-model",
                ["stopReason"] = "toolUse",
            },
        }.ToJsonString());

        var paramsWithTools = CreateSamplingParams();
        paramsWithTools["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "get_weather",
            ["description"] = "Get weather",
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        });

        var result = server.Sample(paramsWithTools);

        Assert.NotNull(result);
        Assert.True(result.IsToolUse);
        Assert.Equal("toolUse", result.StopReason);
        // Content is an array with tool_use items
        Assert.Equal("tool_use", result.Content[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Sample_ErrorResponse_ReturnsNull()
    {
        var (server, input, _) = CreateSamplingServerWithTransport();

        WriteNdjsonToStream(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "not supported" },
        }.ToJsonString());

        Assert.Null(server.Sample(CreateSamplingParams()));
    }

    [Fact]
    public void Sample_IncrementingIds_ShareCounterWithElicit()
    {
        // Sampling and elicitation share the same ID counter.
        var (server, input, output) = CreateSamplingServerWithTransport();
        // Also enable elicitation
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["sampling"] = new JsonObject(),
                ["elicitation"] = new JsonObject(),
            },
        });

        // Queue two responses
        WriteNdjsonToStream(input,
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s-1",
                ["result"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hello" },
                    ["model"] = "m", ["stopReason"] = "endTurn",
                } }.ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s-2",
                ["result"] = new JsonObject { ["action"] = "accept",
                    ["content"] = new JsonObject { ["v"] = 1 } } }.ToJsonString());

        var samplingResult = server.Sample(CreateSamplingParams());
        var elicitResult = server.Elicit("Test", new JsonObject());

        Assert.NotNull(samplingResult);
        Assert.NotNull(elicitResult);

        // Verify IDs are sequential
        output.Position = 0;
        var raw = System.Text.Encoding.UTF8.GetString(output.ToArray());
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("s-1", JsonNode.Parse(lines[0])!["id"]!.GetValue<string>());
        Assert.Equal("s-2", JsonNode.Parse(lines[1])!["id"]!.GetValue<string>());
    }

    // ── Sampling helpers ────────────────────────────────────────

    private static JsonObject CreateSamplingParams() => new()
    {
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "What is the capital of France?" },
        }),
        ["maxTokens"] = 100,
    };

    private static (McpServer server, MemoryStream input, MemoryStream output) CreateSamplingServerWithTransport(
        bool withTools = false)
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var dummy = System.Text.Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();

        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("test-server");
        server.Transport = transport;

        var samplingCap = new JsonObject();
        if (withTools)
            samplingCap["tools"] = new JsonObject();

        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["sampling"] = samplingCap },
        });

        return (server, input, output);
    }
}
