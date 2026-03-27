// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp;

/// <summary>
/// MCP server: tool/resource/prompt registry, JSON-RPC dispatch, and elicitation.
/// Protocol version: 2025-06-18.
/// </summary>
public sealed class McpServer
{
    private readonly Dictionary<string, ToolInfo> _tools = new();
    private readonly Dictionary<string, ResourceInfo> _resources = new();
    private readonly Dictionary<string, PromptInfo> _prompts = new();
    private readonly string _name;
    private readonly string _version;
    private long _nextServerRequestId;

    /// <summary>
    /// Transport for bidirectional communication. Set before calling transport.Run()
    /// to enable server-initiated requests (elicitation). Optional for test usage
    /// where Dispatch() is called directly.
    /// </summary>
    public McpTransport? Transport { get; set; }

    /// <summary>
    /// Whether the connected client advertised the elicitation capability.
    /// Populated after the initialize handshake.
    /// </summary>
    public bool ClientSupportsElicitation { get; private set; }

    public McpServer(string name, string? version = null)
    {
        _name = name;
        _version = version ?? typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    public void RegisterTool(ToolInfo tool) => _tools[tool.Name] = tool;
    public void RegisterResource(ResourceInfo resource) => _resources[resource.Uri] = resource;
    public void RegisterPrompt(PromptInfo prompt) => _prompts[prompt.Name] = prompt;

    public JsonNode? Dispatch(string method, JsonNode? parameters)
    {
        return method switch
        {
            "initialize" => HandleInitialize(parameters),
            "tools/list" => HandleToolsList(),
            "tools/call" => HandleToolsCall(parameters),
            "resources/list" => HandleResourcesList(),
            "resources/read" => HandleResourcesRead(parameters),
            "prompts/list" => HandlePromptsList(),
            "prompts/get" => HandlePromptsGet(parameters),
            "notifications/initialized" or "notifications/cancelled" => null,
            _ => throw new InvalidOperationException($"Unknown method: {method}")
        };
    }

    // ── Elicitation ─────────────────────────────────────────────

    /// <summary>
    /// Send an elicitation request to the client, prompting the user for input.
    /// Blocks until the user responds or the timeout expires. On timeout, sends
    /// a notifications/cancelled to dismiss the client's prompt.
    /// Returns null if transport is not set or the client does not support elicitation.
    /// </summary>
    /// <param name="message">The message to display to the user.</param>
    /// <param name="requestedSchema">JSON Schema for the requested input.</param>
    /// <param name="timeoutSeconds">Timeout in seconds. 0 = no timeout (blocks indefinitely).</param>
    public ElicitationResult? Elicit(string message, JsonObject requestedSchema, int timeoutSeconds = 0)
    {
        if (Transport == null || !ClientSupportsElicitation)
            return null;

        var id = $"s-{Interlocked.Increment(ref _nextServerRequestId)}";

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "elicitation/create",
            ["params"] = new JsonObject
            {
                ["message"] = message,
                ["requestedSchema"] = JsonNode.Parse(requestedSchema.ToJsonString()),
            },
        };

        // Register a response waiter BEFORE sending the request to avoid races.
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        Transport.RegisterResponseWaiter(id, tcs);

        Transport.WriteMessage(request);
        Transport.StartReader();

        // Wait for the reader thread to route the matching response to our TCS.
        try
        {
            if (timeoutSeconds > 0)
            {
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    Transport.UnregisterResponseWaiter(id);
                    return CancelElicitation(id);
                }
            }
            else
            {
                tcs.Task.Wait();
            }
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
            // Reader thread shut down (connection closed).
            return null;
        }

        return ParseElicitationResult(tcs.Task.Result);
    }

    /// <summary>
    /// Cancel a pending elicitation by sending notifications/cancelled to the client.
    /// The client should dismiss the prompt and not send a response.
    /// </summary>
    private ElicitationResult CancelElicitation(string elicitationId)
    {
        Transport!.WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject
            {
                ["requestId"] = elicitationId,
                ["reason"] = "Timeout waiting for user response",
            },
        });
        return new ElicitationResult { Action = ElicitationAction.Timeout };
    }

    private static ElicitationResult ParseElicitationResult(JsonNode msg)
    {
        if (msg["error"] != null)
            return new ElicitationResult { Action = ElicitationAction.Cancel };

        var result = msg["result"]?.AsObject();
        if (result == null)
            return new ElicitationResult { Action = ElicitationAction.Cancel };

        var actionStr = result["action"]?.GetValue<string>();
        var action = actionStr switch
        {
            "accept" => ElicitationAction.Accept,
            "decline" => ElicitationAction.Decline,
            "cancel" => ElicitationAction.Cancel,
            _ => ElicitationAction.Cancel,
        };

        // Clone content to avoid parent-already-set issues.
        var content = result["content"] is JsonObject c
            ? JsonNode.Parse(c.ToJsonString())?.AsObject()
            : null;

        return new ElicitationResult { Action = action, Content = content };
    }

    // ── Initialize ──────────────────────────────────────────────

    private JsonNode HandleInitialize(JsonNode? parameters)
    {
        // Extract client capabilities.
        var clientCaps = parameters?["capabilities"];
        ClientSupportsElicitation = clientCaps?["elicitation"] != null;

        return new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject(),
                ["prompts"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _name,
                ["version"] = _version,
            },
        };
    }

    private JsonNode HandleToolsList()
    {
        var arr = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            arr.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchema.ToJsonString()),
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private JsonNode HandleToolsCall(JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing tool name");
        var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        if (!_tools.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Unknown tool: {toolName}");

        try
        {
            var result = tool.Handler(arguments);
            var text = result?.ToJsonString() ?? "null";
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text }
                }
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"Error: {ex.Message}" }
                },
                ["isError"] = true
            };
        }
    }

    private JsonNode HandleResourcesList()
    {
        var arr = new JsonArray();
        foreach (var res in _resources.Values)
        {
            arr.Add(new JsonObject
            {
                ["uri"] = res.Uri,
                ["name"] = res.Name,
                ["description"] = res.Description,
                ["mimeType"] = res.MimeType,
            });
        }
        return new JsonObject { ["resources"] = arr };
    }

    private JsonNode HandleResourcesRead(JsonNode? parameters)
    {
        var uri = parameters?["uri"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing resource URI");

        if (!_resources.TryGetValue(uri, out var resource))
            throw new InvalidOperationException($"Unknown resource: {uri}");

        var content = resource.Reader();
        var text = content?.ToJsonString() ?? "null";
        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = resource.MimeType,
                    ["text"] = text,
                }
            }
        };
    }

    private JsonNode HandlePromptsList()
    {
        var arr = new JsonArray();
        foreach (var prompt in _prompts.Values)
        {
            var p = new JsonObject
            {
                ["name"] = prompt.Name,
                ["description"] = prompt.Description,
            };
            if (prompt.Arguments.Count > 0)
            {
                var args = new JsonArray();
                foreach (var a in prompt.Arguments)
                {
                    args.Add(new JsonObject
                    {
                        ["name"] = a.Name,
                        ["description"] = a.Description,
                        ["required"] = a.Required,
                    });
                }
                p["arguments"] = args;
            }
            arr.Add(p);
        }
        return new JsonObject { ["prompts"] = arr };
    }

    private JsonNode HandlePromptsGet(JsonNode? parameters)
    {
        var promptName = parameters?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing prompt name");
        var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        if (!_prompts.TryGetValue(promptName, out var prompt))
            throw new InvalidOperationException($"Unknown prompt: {promptName}");

        var messages = prompt.Handler(arguments);
        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = messages,
        };
    }
}
