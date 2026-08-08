// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace McpSharp;

/// <summary>
/// MCP server: tool/resource/prompt registry, JSON-RPC dispatch, and elicitation.
/// Protocol version: 2025-06-18.
/// </summary>
public sealed class McpServer
{
    private readonly ConcurrentDictionary<string, ToolInfo> _tools = new();
    private readonly ConcurrentDictionary<string, ResourceInfo> _resources = new();
    private readonly ConcurrentDictionary<string, PromptInfo> _prompts = new();
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
    /// Populated after the initialize handshake. Equivalent to
    /// <see cref="ElicitationCaps"/>.<see cref="ElicitationCapabilities.Supported"/>;
    /// retained for backwards compatibility.
    /// </summary>
    public bool ClientSupportsElicitation { get; private set; }

    /// <summary>
    /// Parsed client elicitation capabilities (form/url modes), populated after
    /// the initialize handshake. Used to gate features and to refuse sending a
    /// mode the client did not advertise.
    /// </summary>
    public ElicitationCapabilities ElicitationCaps { get; private set; } = ElicitationCapabilities.None;

    /// <summary>
    /// Parsed client sampling capabilities, populated after the initialize handshake.
    /// Used to gate <see cref="Sample"/> calls — the server must not send
    /// sampling requests to clients that did not advertise the capability.
    /// </summary>
    public SamplingCapabilities SamplingCaps { get; private set; } = SamplingCapabilities.None;

    public McpServer(string name, string? version = null)
    {
        _name = name;
        _version = version ?? typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    /// <summary>
    /// Optional filter invoked during <c>tools/list</c> and <c>tools/call</c>.
    /// Return true to include the tool for the requesting client.
    /// Receives the tool and the parsed per-request context (client caps, protocol version).
    /// When null, all registered tools are visible to all clients.
    /// Must be thread-safe if the transport uses concurrent dispatch.
    /// </summary>
    public Func<ToolInfo, RequestContext, bool>? ToolFilter { get; set; }

    // ── Subscription state ──────────────────────────────────────

    private readonly Dictionary<string, Subscription> _subscriptions = new();
    private readonly Lock _subscriptionLock = new();

    /// <summary>
    /// Register a tool dynamically after server startup. Fires
    /// <c>notifications/tools/list_changed</c> to all subscribed clients.
    /// </summary>
    public void AddTool(ToolInfo tool)
    {
        _tools[tool.Name] = tool;
        NotifyToolsChanged();
    }

    /// <summary>
    /// Remove a tool dynamically. Fires <c>notifications/tools/list_changed</c>
    /// to all subscribed clients. Returns true if the tool existed.
    /// </summary>
    public bool RemoveTool(string name)
    {
        if (!_tools.TryRemove(name, out _))
            return false;
        NotifyToolsChanged();
        return true;
    }

    /// <summary>
    /// Register a resource dynamically after server startup. Fires
    /// <c>notifications/resources/list_changed</c> to all subscribed clients.
    /// </summary>
    public void AddResource(ResourceInfo resource)
    {
        _resources[resource.Uri] = resource;
        NotifyResourcesChanged();
    }

    /// <summary>
    /// Remove a resource dynamically. Fires <c>notifications/resources/list_changed</c>
    /// to all subscribed clients. Returns true if the resource existed.
    /// </summary>
    public bool RemoveResource(string uri)
    {
        if (!_resources.TryRemove(uri, out _))
            return false;
        NotifyResourcesChanged();
        return true;
    }

    /// <summary>
    /// Register a prompt dynamically after server startup. Fires
    /// <c>notifications/prompts/list_changed</c> to all subscribed clients.
    /// </summary>
    public void AddPrompt(PromptInfo prompt)
    {
        _prompts[prompt.Name] = prompt;
        NotifyPromptsChanged();
    }

    /// <summary>
    /// Remove a prompt dynamically. Fires <c>notifications/prompts/list_changed</c>
    /// to all subscribed clients. Returns true if the prompt existed.
    /// </summary>
    public bool RemovePrompt(string name)
    {
        if (!_prompts.TryRemove(name, out _))
            return false;
        NotifyPromptsChanged();
        return true;
    }

    public void RegisterTool(ToolInfo tool) => _tools[tool.Name] = tool;
    public void RegisterResource(ResourceInfo resource) => _resources[resource.Uri] = resource;
    public void RegisterPrompt(PromptInfo prompt) => _prompts[prompt.Name] = prompt;

    public JsonNode? Dispatch(string method, JsonNode? parameters)
    {
        return method switch
        {
            "initialize" => HandleInitialize(parameters),
            "tools/list" => HandleToolsList(parameters),
            "tools/call" => HandleToolsCall(parameters),
            "resources/list" => HandleResourcesList(),
            "resources/read" => HandleResourcesRead(parameters),
            "prompts/list" => HandlePromptsList(),
            "prompts/get" => HandlePromptsGet(parameters),
            "server/discover" => HandleDiscover(),
            "subscriptions/listen" => HandleSubscriptionsListen(parameters),
            "notifications/initialized" => null,
            "notifications/cancelled" => HandleNotificationsCancelled(parameters),
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
        // Form mode requires the client to advertise form support.
        // The request omits `mode` (form is the default mode); we never emit a
        // mode the client did not declare.
        if (Transport == null || !ElicitationCaps.Supports(ElicitationMode.Form))
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

    // ── Sampling ────────────────────────────────────────────────

    /// <summary>
    /// Send a <c>sampling/createMessage</c> request to the client, asking it to
    /// generate an LLM completion. Blocks until the client responds or the timeout
    /// expires. Returns null if transport is not set or the client does not support
    /// sampling.
    /// </summary>
    /// <param name="samplingParams">
    /// The full params object for <c>sampling/createMessage</c>. Must include at
    /// least <c>messages</c> (array) and <c>maxTokens</c> (integer). May include
    /// <c>modelPreferences</c>, <c>systemPrompt</c>, <c>temperature</c>,
    /// <c>tools</c>, <c>toolChoice</c>, and <c>includeContext</c>.
    /// </param>
    /// <param name="timeoutSeconds">Timeout in seconds. 0 = no timeout.</param>
    public SamplingResult? Sample(JsonObject samplingParams, int timeoutSeconds = 0)
    {
        if (Transport == null || !SamplingCaps.Supported)
            return null;

        // If the request includes tools or toolChoice, verify the client advertised tool support.
        if ((samplingParams.ContainsKey("tools") || samplingParams.ContainsKey("toolChoice"))
            && !SamplingCaps.Tools)
            return null;

        var id = $"s-{Interlocked.Increment(ref _nextServerRequestId)}";

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "sampling/createMessage",
            ["params"] = JsonNode.Parse(samplingParams.ToJsonString()),
        };

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        Transport.RegisterResponseWaiter(id, tcs);

        Transport.WriteMessage(request);
        Transport.StartReader();

        try
        {
            if (timeoutSeconds > 0)
            {
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    Transport.UnregisterResponseWaiter(id);
                    CancelSampling(id);
                    return null;
                }
            }
            else
            {
                tcs.Task.Wait();
            }
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
            return null;
        }

        return ParseSamplingResult(tcs.Task.Result);
    }

    private void CancelSampling(string requestId)
    {
        Transport!.WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject
            {
                ["requestId"] = requestId,
                ["reason"] = "Timeout waiting for sampling response",
            },
        });
    }

    private static SamplingResult? ParseSamplingResult(JsonNode msg)
    {
        try
        {
            if (msg["error"] != null)
                return null;

            var result = msg["result"] as JsonObject;
            if (result == null)
                return null;

            // role must be a string
            if (result["role"] is not JsonValue roleVal || !roleVal.TryGetValue<string>(out var role))
                return null;

            var content = result["content"];
            if (content == null)
                return null;

            // model and stopReason are optional strings — read defensively
            string? model = null;
            if (result["model"] is JsonValue modelVal && modelVal.TryGetValue<string>(out var m))
                model = m;

            string? stopReason = null;
            if (result["stopReason"] is JsonValue stopVal && stopVal.TryGetValue<string>(out var s))
                stopReason = s;

            return new SamplingResult
            {
                Role = role,
                Content = JsonNode.Parse(content.ToJsonString())!,
                Model = model,
                StopReason = stopReason,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ── Discover ─────────────────────────────────────────────────

    private JsonNode HandleDiscover()
    {
        return new JsonObject
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = new JsonArray("2025-06-18"),
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject(),
                ["prompts"] = new JsonObject(),
            },
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/serverInfo"] = new JsonObject
                {
                    ["name"] = _name,
                    ["version"] = _version,
                },
            },
        };
    }

    // ── Initialize ──────────────────────────────────────────────

    private JsonNode HandleInitialize(JsonNode? parameters)
    {
        // Extract client capabilities.
        var clientCaps = parameters?["capabilities"];
        ElicitationCaps = ElicitationCapabilities.Parse(clientCaps?["elicitation"]);
        ClientSupportsElicitation = ElicitationCaps.Supported;
        SamplingCaps = SamplingCapabilities.Parse(clientCaps?["sampling"]);

        return new JsonObject
        {
            ["resultType"] = "complete",
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
                ["resources"] = new JsonObject { ["listChanged"] = true },
                ["prompts"] = new JsonObject { ["listChanged"] = true },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _name,
                ["version"] = _version,
            },
        };
    }

    private JsonNode HandleToolsList(JsonNode? parameters)
    {
        var ctx = RequestContext.Parse(parameters);
        var arr = new JsonArray();
        foreach (var tool in _tools.Values.OrderBy(t => t.Name))
        {
            if (ToolFilter != null && !ToolFilter(tool, ctx))
                continue;

            var obj = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchema.ToJsonString()),
            };
            if (tool.Title != null)
                obj["title"] = tool.Title;
            if (tool.OutputSchema != null)
                obj["outputSchema"] = JsonNode.Parse(tool.OutputSchema.ToJsonString());
            if (tool.Icons != null)
                obj["icons"] = JsonNode.Parse(tool.Icons.ToJsonString());
            arr.Add(obj);
        }
        return new JsonObject { ["resultType"] = "complete", ["tools"] = arr };
    }

    private JsonNode HandleToolsCall(JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing tool name");
        var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        if (!_tools.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Unknown tool: {toolName}");

        // Enforce the same filter applied to tools/list — a client cannot call
        // a tool that would not appear in their listing.
        if (ToolFilter != null)
        {
            var ctx = RequestContext.Parse(parameters);
            if (!ToolFilter(tool, ctx))
                throw new InvalidOperationException($"Unknown tool: {toolName}");
        }

        const int maxAuthRetries = 2;

        for (int attempt = 0; attempt <= maxAuthRetries; attempt++)
        {
            try
            {
                var result = tool.Handler(arguments);

                // RawResult: handler returned a pre-formed MCP tool result — pass through.
                if (tool.RawResult && result is JsonObject rawObj)
                {
                    var cloned = JsonNode.Parse(rawObj.ToJsonString())!.AsObject();
                    cloned["resultType"] = "complete";
                    return cloned;
                }

                var text = result?.ToJsonString() ?? "null";
                return new JsonObject
                {
                    ["resultType"] = "complete",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = text }
                    }
                };
            }
            catch (AuthenticationException authEx)
            {
                // Try elicitation if the client advertised form support and we haven't exhausted retries
                if (attempt < maxAuthRetries && ElicitationCaps.Supports(ElicitationMode.Form))
                {
                    var elicitResult = Elicit(
                        $"Authentication failed ({authEx.Provider}): {authEx.Message}\n\n" +
                        $"{authEx.Remediation}\n\n" +
                        "After re-authenticating, choose Retry to continue.",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["action"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "Choose 'retry' after re-authenticating, or 'abort' to stop",
                                    ["enum"] = new JsonArray("retry", "abort"),
                                },
                            },
                        },
                        timeoutSeconds: 0);

                    if (elicitResult?.Action == ElicitationAction.Accept)
                    {
                        var action = elicitResult.Content?["action"]?.GetValue<string>();
                        if (action == "retry")
                        {
                            authEx.ResetAuth?.Invoke();
                            // Re-parse arguments since JsonObject can only have one parent
                            arguments = parameters?["arguments"]?.AsObject() != null
                                ? JsonNode.Parse(parameters!["arguments"]!.ToJsonString())!.AsObject()
                                : new JsonObject();
                            continue; // retry the tool handler
                        }
                    }
                    // User declined, cancelled, timed out, or chose abort — fall through to STOP
                }

                return new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"AUTHENTICATION FAILED ({authEx.Provider}): {authEx.Message}\n\n" +
                                       $"STOP — do not retry or work around this error. " +
                                       $"This requires human action:\n{authEx.Remediation}\n\n" +
                                       $"Present this message to the user and wait for them to resolve it.",
                        }
                    },
                    ["isError"] = true
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

        // Should not reach here, but safety net
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "Error: authentication retry limit exceeded" }
            },
            ["isError"] = true
        };
    }

    private JsonNode HandleResourcesList()
    {
        var arr = new JsonArray();
        foreach (var res in _resources.Values.OrderBy(r => r.Uri))
        {
            arr.Add(new JsonObject
            {
                ["uri"] = res.Uri,
                ["name"] = res.Name,
                ["description"] = res.Description,
                ["mimeType"] = res.MimeType,
            });
        }
        return new JsonObject { ["resultType"] = "complete", ["resources"] = arr };
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
            ["resultType"] = "complete",
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
        foreach (var prompt in _prompts.Values.OrderBy(p => p.Name))
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
        return new JsonObject { ["resultType"] = "complete", ["prompts"] = arr };
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
            ["resultType"] = "complete",
            ["description"] = prompt.Description,
            ["messages"] = messages,
        };
    }

    // ── Subscriptions ───────────────────────────────────────────

    /// <summary>
    /// Sentinel value returned by HandleSubscriptionsListen to signal the transport
    /// that the response was already handled (the request is long-lived).
    /// </summary>
    internal static readonly JsonObject DeferredResponseSentinel = new() { ["__deferred"] = true };

    private JsonNode? HandleSubscriptionsListen(JsonNode? parameters)
    {
        if (Transport == null)
            throw new InvalidOperationException("subscriptions/listen requires an active transport");

        // The request ID is needed to correlate this subscription. We pass it
        // via a well-known _meta key injected by the transport.
        var requestIdNode = parameters?["_meta"]?["__requestId"]
            ?? throw new ArgumentException("Missing request ID for subscription");
        var requestId = requestIdNode.ToString();
        var requestIdJson = requestIdNode.ToJsonString();

        var notifications = parameters?["notifications"]?.AsObject();
        var sub = new Subscription
        {
            RequestId = requestId,
            RequestIdJson = requestIdJson,
            ToolsListChanged = notifications?["toolsListChanged"]?.GetValue<bool>() == true,
            PromptsListChanged = notifications?["promptsListChanged"]?.GetValue<bool>() == true,
            ResourcesListChanged = notifications?["resourcesListChanged"]?.GetValue<bool>() == true,
        };

        lock (_subscriptionLock)
        {
            _subscriptions[requestId] = sub;
        }

        // Send the acknowledgment notification with the agreed filter.
        var ackedNotifications = new JsonObject();
        if (sub.ToolsListChanged) ackedNotifications["toolsListChanged"] = true;
        if (sub.PromptsListChanged) ackedNotifications["promptsListChanged"] = true;
        if (sub.ResourcesListChanged) ackedNotifications["resourcesListChanged"] = true;

        Transport.WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/subscriptions/acknowledged",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/subscriptionId"] = JsonNode.Parse(requestIdJson),
                },
                ["notifications"] = ackedNotifications,
            },
        });

        // Return sentinel — the transport must NOT send a response for this request.
        // The response is sent later when the subscription closes (graceful closure).
        return DeferredResponseSentinel;
    }

    private JsonNode? HandleNotificationsCancelled(JsonNode? parameters)
    {
        var cancelledId = parameters?["requestId"]?.ToString();
        if (cancelledId != null)
        {
            Subscription? sub;
            lock (_subscriptionLock)
            {
                _subscriptions.Remove(cancelledId, out sub);
            }

            // Send the graceful closure response for the subscription.
            if (sub != null && Transport != null)
            {
                Transport.WriteMessage(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(sub.RequestIdJson),
                    ["result"] = new JsonObject
                    {
                        ["resultType"] = "complete",
                        ["_meta"] = new JsonObject
                        {
                            ["io.modelcontextprotocol/subscriptionId"] = JsonNode.Parse(sub.RequestIdJson),
                        },
                    },
                });
            }
        }
        return null;
    }

    private void NotifyToolsChanged()
    {
        NotifyListChanged("notifications/tools/list_changed", s => s.ToolsListChanged);
    }

    private void NotifyResourcesChanged()
    {
        NotifyListChanged("notifications/resources/list_changed", s => s.ResourcesListChanged);
    }

    private void NotifyPromptsChanged()
    {
        NotifyListChanged("notifications/prompts/list_changed", s => s.PromptsListChanged);
    }

    private void NotifyListChanged(string method, Func<Subscription, bool> filter)
    {
        if (Transport == null) return;

        List<Subscription> subs;
        lock (_subscriptionLock)
        {
            subs = _subscriptions.Values.Where(filter).ToList();
        }

        foreach (var sub in subs)
        {
            Transport.WriteMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = new JsonObject
                {
                    ["_meta"] = new JsonObject
                    {
                        ["io.modelcontextprotocol/subscriptionId"] = JsonNode.Parse(sub.RequestIdJson),
                    },
                },
            });
        }
    }

    /// <summary>
    /// Close all active subscriptions (sends graceful closure responses).
    /// Call during server shutdown before stopping the transport.
    /// </summary>
    public void CloseAllSubscriptions()
    {
        if (Transport == null) return;

        List<Subscription> subs;
        lock (_subscriptionLock)
        {
            subs = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }

        foreach (var sub in subs)
        {
            Transport.WriteMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(sub.RequestIdJson),
                ["result"] = new JsonObject
                {
                    ["resultType"] = "complete",
                    ["_meta"] = new JsonObject
                    {
                        ["io.modelcontextprotocol/subscriptionId"] = JsonNode.Parse(sub.RequestIdJson),
                    },
                },
            });
        }
    }

    private sealed class Subscription
    {
        public required string RequestId { get; init; }
        /// <summary>The request ID as a JSON literal for embedding in responses.</summary>
        public required string RequestIdJson { get; init; }
        public bool ToolsListChanged { get; init; }
        public bool PromptsListChanged { get; init; }
        public bool ResourcesListChanged { get; init; }
    }
}
