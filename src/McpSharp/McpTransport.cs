// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpSharp;

/// <summary>
/// MCP JSON-RPC 2.0 transport over stdio.
/// Supports Content-Length framing and NDJSON, auto-detected from first byte.
///
/// Architecture:
///   - A single reader thread reads from the input stream and classifies each message:
///     • Requests/notifications (has "method") → _requestQueue → consumed by Run()
///     • Responses (has "result"/"error", no "method") → routed to a waiting
///       Elicit() call via _responseWaiters
///   - This split eliminates competing readers between Run() and Elicit().
///   - WriteMessage is thread-safe (locked) for concurrent handler dispatch.
/// </summary>
public sealed class McpTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _logPrefix;
    private Framing _framing = Framing.Unknown;

    private enum Framing { Unknown, ContentLength, Ndjson }

    // Inbound requests and notifications — consumed only by Run().
    private readonly BlockingCollection<JsonNode> _requestQueue = new();

    // Inbound responses — routed to waiting Elicit() calls by request ID.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _responseWaiters = new();

    // Responses that arrived before their waiter was registered.
    private readonly ConcurrentDictionary<string, JsonNode> _earlyResponses = new();

    // Synchronizes waiter registration with early response buffering.
    private readonly object _responseRoutingLock = new();

    private Thread? _readerThread;

    public McpTransport(Stream input, Stream output, string? logPrefix = null)
    {
        _input = input;
        _output = output;
        _logPrefix = logPrefix ?? "mcp-server";
    }

    /// <summary>
    /// Start the background reader thread that owns the input stream.
    /// Classifies messages and routes them to the appropriate queue.
    /// Called automatically by Run().
    /// </summary>
    public void StartReader()
    {
        if (_readerThread != null) return;
        _readerThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var msg = ReadMessage();
                    if (msg == null) break;

                    if (IsResponse(msg))
                    {
                        var id = msg["id"]?.GetValue<string>();
                        if (id != null)
                        {
                            lock (_responseRoutingLock)
                            {
                                if (_responseWaiters.TryRemove(id, out var tcs))
                                    tcs.TrySetResult(msg);
                                else
                                    _earlyResponses[id] = msg;
                            }
                        }
                    }
                    else
                    {
                        _requestQueue.Add(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{_logPrefix}: reader thread error: {ex.Message}");
            }
            finally
            {
                _requestQueue.CompleteAdding();
                // Complete any waiting elicitations.
                foreach (var kvp in _responseWaiters)
                {
                    if (_responseWaiters.TryRemove(kvp.Key, out var tcs))
                        tcs.TrySetCanceled();
                }
            }
        }) { IsBackground = true, Name = $"{_logPrefix}-reader" };
        _readerThread.Start();
    }

    /// <summary>
    /// Classify a message as a response (has "result" or "error", no "method").
    /// </summary>
    private static bool IsResponse(JsonNode msg)
    {
        var obj = msg.AsObject();
        return !obj.ContainsKey("method")
            && (obj.ContainsKey("result") || obj.ContainsKey("error"));
    }

    // ── Response waiters (used by Elicit()) ─────────────────────

    /// <summary>
    /// Register a waiter for a server-initiated request response.
    /// The reader thread will complete the TCS when a response with the matching ID arrives.
    /// </summary>
    internal void RegisterResponseWaiter(string requestId, TaskCompletionSource<JsonNode> tcs)
    {
        lock (_responseRoutingLock)
        {
            if (_earlyResponses.TryRemove(requestId, out var earlyResponse))
            {
                tcs.TrySetResult(earlyResponse);
                return;
            }
            _responseWaiters[requestId] = tcs;
        }
    }

    /// <summary>
    /// Remove a response waiter (e.g., on timeout before the response arrives).
    /// </summary>
    internal void UnregisterResponseWaiter(string requestId)
    {
        _responseWaiters.TryRemove(requestId, out _);
    }

    // ── Request queue (used by Run()) ───────────────────────────

    /// <summary>
    /// Take the next request from the queue. Blocks until available or reader finishes (returns null).
    /// </summary>
    public JsonNode? TakeMessage()
    {
        try { return _requestQueue.Take(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Try to take a request with a timeout and cancellation token.
    /// </summary>
    public JsonNode? TakeMessage(int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            return _requestQueue.TryTake(out var msg, timeoutMs, ct) ? msg : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // ── Main event loop ────────────────────────────────────────

    /// <summary>
    /// Main event loop. Reads requests from the queue and dispatches to the handler.
    /// When concurrent is true, handlers run on ThreadPool threads, allowing
    /// blocking tools (like poll_messages) without starving other requests.
    /// Elicitation works correctly in both modes because responses are routed
    /// to Elicit() via _responseWaiters, not through this queue.
    /// </summary>
    public void Run(Func<string, JsonNode?, JsonNode?> handler, bool concurrent = false)
    {
        StartReader();

        while (true)
        {
            var request = TakeMessage();
            if (request == null) break;

            var method = request["method"]?.GetValue<string>() ?? "";
            var parameters = request["params"];
            var isNotification = !request.AsObject().ContainsKey("id") || request["id"] is null;

            if (concurrent)
            {
                var idJson = isNotification ? null : request["id"]!.ToJsonString();
                ThreadPool.QueueUserWorkItem(_ =>
                    DispatchAndRespond(handler, method, parameters, isNotification, idJson));
            }
            else
            {
                DispatchAndRespond(handler, method, parameters, isNotification,
                    isNotification ? null : request["id"]!.ToJsonString());
            }
        }
    }

    private void DispatchAndRespond(Func<string, JsonNode?, JsonNode?> handler,
        string method, JsonNode? parameters, bool isNotification, string? idJson)
    {
        try
        {
            var result = handler(method, parameters);
            if (!isNotification)
            {
                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(idJson!),
                    ["result"] = result is not null
                        ? JsonNode.Parse(result.ToJsonString())
                        : null,
                };
                WriteMessage(response);
            }
        }
        catch (Exception ex)
        {
            if (!isNotification)
            {
                var errorResponse = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(idJson!),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32603,
                        ["message"] = ex.Message,
                    }
                };
                WriteMessage(errorResponse);
            }
            else
            {
                Console.Error.WriteLine($"{_logPrefix}: notification error: {ex.Message}");
            }
        }
    }

    // ── Stream reading (used by reader thread) ──────────────────

    /// <summary>
    /// Read a single message directly from the input stream (blocking).
    /// In production, the reader thread calls this. Tests may call it directly.
    /// </summary>
    public JsonNode? ReadMessage()
    {
        if (_framing == Framing.Unknown)
        {
            int ch;
            while ((ch = _input.ReadByte()) != -1)
            {
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') continue;
                if (ch == '{')
                {
                    _framing = Framing.Ndjson;
                    Console.Error.WriteLine($"{_logPrefix}: using NDJSON framing");
                    var sb = new StringBuilder();
                    sb.Append((char)ch);
                    int c;
                    while ((c = _input.ReadByte()) != -1 && c != '\n')
                    {
                        sb.Append((char)c);
                    }
                    var line = sb.ToString().TrimEnd('\r');
                    try { return JsonNode.Parse(line); }
                    catch { return null; }
                }
                else
                {
                    _framing = Framing.ContentLength;
                    Console.Error.WriteLine($"{_logPrefix}: using Content-Length framing");
                    var sb = new StringBuilder();
                    sb.Append((char)ch);
                    return ReadContentLength(sb);
                }
            }
            return null;
        }

        return _framing == Framing.Ndjson ? ReadNdjson() : ReadContentLength(null);
    }

    private JsonNode? ReadContentLength(StringBuilder? prefixBuilder)
    {
        int contentLength = 0;
        bool found = false;

        string? firstLine;
        if (prefixBuilder != null)
        {
            int c;
            while ((c = _input.ReadByte()) != -1 && c != '\n')
                prefixBuilder.Append((char)c);
            firstLine = prefixBuilder.ToString().TrimEnd('\r');
        }
        else
        {
            firstLine = ReadLine();
        }

        if (firstLine == null) return null;

        var line = firstLine;
        while (!string.IsNullOrEmpty(line))
        {
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan(16), out var len))
                {
                    contentLength = len;
                    found = true;
                }
            }
            line = ReadLine();
            if (line == null) return null;
        }

        if (!found) return null;

        var buf = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = _input.Read(buf, read, contentLength - read);
            if (n == 0) return null;
            read += n;
        }

        try { return JsonNode.Parse(Encoding.UTF8.GetString(buf)); }
        catch { return null; }
    }

    private JsonNode? ReadNdjson()
    {
        while (true)
        {
            var line = ReadLine();
            if (line == null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { return JsonNode.Parse(line); }
            catch { return null; }
        }
    }

    private string? ReadLine()
    {
        var sb = new StringBuilder();
        int ch;
        while ((ch = _input.ReadByte()) != -1)
        {
            if (ch == '\n')
            {
                var s = sb.ToString();
                if (s.EndsWith('\r')) s = s[..^1];
                return s;
            }
            sb.Append((char)ch);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private readonly Lock _writeLock = new();

    public void WriteMessage(JsonNode msg)
    {
        var body = msg.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        lock (_writeLock)
        {
            if (_framing == Framing.Ndjson)
            {
                _output.Write(bodyBytes);
                _output.WriteByte((byte)'\n');
            }
            else
            {
                var header = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
                _output.Write(Encoding.UTF8.GetBytes(header));
                _output.Write(bodyBytes);
            }
            _output.Flush();
        }
    }
}
