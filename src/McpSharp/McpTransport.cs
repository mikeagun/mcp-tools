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
                    var status = ReadNext(out var msg);
                    if (status == ReadStatus.Eof) break;

                    if (status == ReadStatus.ParseError)
                    {
                        WriteError(null, -32700, "Parse error");
                        continue;
                    }

                    if (status == ReadStatus.InvalidRequest)
                    {
                        WriteError(null, -32600, "Invalid Request: duplicate or malformed property name");
                        continue;
                    }

                    if (status == ReadStatus.Skipped) continue;

                    try
                    {
                        RouteMessage(msg);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
                    {
                        // Unexpected message shape: answer it and keep reading.
                        Log($"failed to route message: {ex.Message}");
                        WriteError(null, -32600, "Invalid Request");
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
    /// Validate an inbound message's JSON-RPC shape and route it: requests and
    /// notifications go to the request queue, responses go to a waiting
    /// server-initiated request. Invalid requests get a <c>-32600</c> error;
    /// malformed notifications and unroutable responses are logged and dropped.
    /// </summary>
    private void RouteMessage(JsonNode? msg)
    {
        if (msg is not JsonObject obj)
        {
            Log("ignoring message that is not a JSON object");
            WriteError(null, -32600, "Invalid Request: expected a JSON object");
            return;
        }

        var idNode = obj["id"];
        var idValid = IsValidId(idNode);

        if (obj.ContainsKey("method"))
        {
            var isNotification = idNode is null;
            if (!idValid)
            {
                // Never echo an id that cannot be decoded or serialized.
                Log("ignoring request with an invalid id");
                WriteError(null, -32600, "Invalid Request: id must be a string or number");
                return;
            }
            if (obj["method"] is not JsonValue methodValue || !TryGetString(methodValue, out _))
            {
                Log("ignoring message with a missing or undecodable method");
                if (!isNotification)
                    WriteError(idNode, -32600, "Invalid Request: method must be a string");
                return;
            }

            // MCP request params are always objects (or omitted).
            if (obj["params"] is { } p && p is not JsonObject)
            {
                Log("ignoring message with non-object params");
                if (!isNotification)
                    WriteError(idNode, -32602, "Invalid params: params must be an object");
                return;
            }

            _requestQueue.Add(obj);
            return;
        }

        if (obj.ContainsKey("result") || obj.ContainsKey("error"))
        {
            var key = idValid ? IdKey(idNode) : null;
            if (key == null)
            {
                Log("ignoring response without a usable id");
                return;
            }

            lock (_responseRoutingLock)
            {
                if (_responseWaiters.TryRemove(key, out var tcs))
                    tcs.TrySetResult(obj);
                else
                    _earlyResponses[key] = obj;
            }
            return;
        }

        Log("ignoring message that is neither a request nor a response");
        WriteError(idValid ? idNode : null, -32600, "Invalid Request: missing method");
    }

    /// <summary>
    /// JSON-RPC ids are strings or numbers; null/absent marks a notification.
    /// A string id must also decode (e.g. no lone surrogate escapes), since it
    /// is echoed in the response.
    /// </summary>
    private static bool IsValidId(JsonNode? id)
        => id is null
            || (id is JsonValue v
                && (v.GetValueKind() == JsonValueKind.Number || TryGetString(v, out _)));

    /// <summary>
    /// Read a JSON string value. Returns false for non-strings and for strings
    /// whose escapes do not decode (e.g. a lone surrogate), which
    /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// accepts but decodes lazily.
    /// </summary>
    internal static bool TryGetString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            return false;
        try
        {
            value = v.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Routing key for a response id (string value, or the number's JSON text).</summary>
    private static string? IdKey(JsonNode? id) => id switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    private void WriteError(JsonNode? id, int code, string message)
    {
        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });
    }

    private void Log(string message) => Console.Error.WriteLine($"{_logPrefix}: {message}");

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
        ProgressSession? progressSession = null;
        try
        {
            // Progress: if this is a tools/call with a progressToken, create a session
            // that (a) emits a periodic keepalive to reset the client's timeout clock
            // and (b) lets the handler emit meaningful status via McpProgress.Current.
            // The MCP spec defines progressToken as string | integer. A _meta that is
            // not an object carries no progress token.
            if (method == "tools/call"
                && ((parameters as JsonObject)?["_meta"] as JsonObject)?["progressToken"] is { } progressToken)
            {
                progressSession = new ProgressSession(this, progressToken);
            }
            McpProgress.SetCurrent(progressSession);

            // For subscriptions/listen, inject the request ID into _meta so the
            // handler can correlate it with the subscription without parsing the
            // outer JSON-RPC envelope (which is already consumed at this point).
            if (method == "subscriptions/listen" && idJson != null)
            {
                var paramsObj = parameters as JsonObject ?? new JsonObject();
                if (paramsObj["_meta"] is not JsonObject meta)
                {
                    meta = new JsonObject();
                    paramsObj["_meta"] = meta;
                }
                meta["__requestId"] = JsonNode.Parse(idJson);
                parameters = paramsObj;
            }

            var result = handler(method, parameters);

            // Stop progress and drain any in-flight callback before sending the
            // response — ensures no progress arrives after the result.
            progressSession?.Stop();

            if (!isNotification)
            {
                // Deferred responses (e.g., subscriptions/listen) handle their
                // own response lifecycle — do not send an automatic response.
                if (result is JsonObject obj && obj.ContainsKey("__deferred"))
                    return;

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
            progressSession?.Stop();

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
        finally
        {
            McpProgress.SetCurrent(null);
            if (progressSession != null && progressSession.NotificationsSent > 0)
                Console.Error.WriteLine($"{_logPrefix}: progress sent {progressSession.NotificationsSent} notifications");
        }
    }

    // ── Progress notifications ──────────────────────────────────

    /// <summary>
    /// Send a progress notification to the client. The progress value must
    /// increase monotonically across calls for the same progressToken.
    /// The token is echoed back in its original JSON type (string or integer).
    /// </summary>
    public void SendProgress(JsonNode progressToken, long progress, long? total = null, string? message = null)
    {
        var param = new JsonObject
        {
            ["progressToken"] = JsonNode.Parse(progressToken.ToJsonString()),
            ["progress"] = progress,
        };
        if (total.HasValue)
            param["total"] = total.Value;
        if (message != null)
            param["message"] = message;

        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = param,
        });
    }

    // ── Stream reading (used by reader thread) ──────────────────

    private enum ReadStatus { Message, ParseError, InvalidRequest, Skipped, Eof }

    /// <summary>
    /// Read the next parseable message directly from the input stream (blocking).
    /// Unparseable or unframeable input is logged and skipped; returns null only
    /// at end of stream. In production the reader thread reads the stream (and
    /// additionally answers unparseable input with a <c>-32700</c> error).
    /// Tests may call this directly, e.g. to prime framing detection.
    /// </summary>
    public JsonNode? ReadMessage()
    {
        while (true)
        {
            var status = ReadNext(out var msg);
            if (status == ReadStatus.Eof) return null;
            if (status == ReadStatus.Message && msg != null) return msg;
        }
    }

    /// <summary>
    /// Read one framed unit from the stream. Framing is auto-detected from the
    /// first non-whitespace byte (after an optional UTF-8 BOM): <c>{</c> or
    /// <c>[</c> selects NDJSON, anything else selects Content-Length headers.
    /// </summary>
    private ReadStatus ReadNext(out JsonNode? message)
    {
        message = null;
        List<byte>? prefix = null;

        if (_framing == Framing.Unknown)
        {
            prefix = DetectFraming();
            if (prefix == null) return ReadStatus.Eof;
        }

        return _framing == Framing.Ndjson
            ? ReadNdjson(prefix, out message)
            : ReadContentLength(prefix, out message);
    }

    /// <summary>
    /// Consume leading whitespace and an optional UTF-8 BOM, select the framing,
    /// and return the bytes already consumed from the first message (null at EOF).
    /// </summary>
    private List<byte>? DetectFraming()
    {
        bool first = true;
        int ch;
        while ((ch = ReadByteBuffered()) != -1)
        {
            if (first && ch == 0xEF)
            {
                first = false;
                int b1 = ReadByteBuffered();
                int b2 = b1 == 0xBB ? ReadByteBuffered() : -1;
                if (b1 == 0xBB && b2 == 0xBF) continue;

                var consumed = new List<byte> { 0xEF };
                if (b1 != -1) consumed.Add((byte)b1);
                if (b2 != -1) consumed.Add((byte)b2);
                return SelectFraming(Framing.ContentLength, consumed);
            }

            first = false;
            if (ch is ' ' or '\t' or '\r' or '\n') continue;

            return SelectFraming(
                ch is '{' or '[' ? Framing.Ndjson : Framing.ContentLength,
                new List<byte> { (byte)ch });
        }
        return null;
    }

    private List<byte> SelectFraming(Framing framing, List<byte> consumed)
    {
        _framing = framing;
        Log(framing == Framing.Ndjson ? "using NDJSON framing" : "using Content-Length framing");
        return consumed;
    }

    private ReadStatus ReadNdjson(List<byte>? prefix, out JsonNode? message)
    {
        message = null;
        while (true)
        {
            var line = ReadLineBytes(prefix);
            prefix = null;
            if (line == null) return ReadStatus.Eof;

            var text = Encoding.UTF8.GetString(line).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text)) continue;
            return TryParse(text, out message);
        }
    }

    private ReadStatus ReadContentLength(List<byte>? prefix, out JsonNode? message)
    {
        message = null;
        int? contentLength = null;
        bool inHeaders = false;

        while (true)
        {
            var lineBytes = ReadLineBytes(prefix);
            prefix = null;
            if (lineBytes == null) return ReadStatus.Eof;

            var line = Encoding.UTF8.GetString(lineBytes);
            if (line.Length == 0)
            {
                // Blank lines before a header block are tolerated; one after
                // headers terminates the block.
                if (inHeaders) break;
                continue;
            }

            inHeaders = true;
            const string header = "Content-Length:";
            if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(header.Length).Trim(), out var len)
                && len >= 0)
            {
                contentLength = len;
            }
        }

        if (contentLength == null)
        {
            Log("skipping header block without a valid Content-Length");
            return ReadStatus.Skipped;
        }

        var buf = new byte[contentLength.Value];
        int read = 0;
        while (read < buf.Length)
        {
            int n = ReadBuffered(buf, read, buf.Length - read);
            if (n == 0) return ReadStatus.Eof;
            read += n;
        }

        return TryParse(Encoding.UTF8.GetString(buf), out message);
    }

    private ReadStatus TryParse(string text, out JsonNode? message)
    {
        try
        {
            message = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            message = null;
            Log($"parse error: {ex.Message}");
            return ReadStatus.ParseError;
        }

        // JsonNode.Parse accepts duplicate property names and undecodable escapes
        // in property names, but JsonObject builds its dictionary lazily and throws
        // on first access. Materialize the whole tree now so such a message is
        // rejected here rather than wherever it is first read.
        try
        {
            Materialize(message);
            return ReadStatus.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // ArgumentException: duplicate property name.
            // InvalidOperationException: undecodable property name (lone surrogate).
            message = null;
            Log($"invalid message: {ex.Message}");
            return ReadStatus.InvalidRequest;
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                    Materialize(property.Value);
                break;
            case JsonArray array:
                foreach (var item in array)
                    Materialize(item);
                break;
        }
    }
    // Read-ahead buffer over the input stream. Reading byte-at-a-time from an
    // unbuffered stdin pipe costs a system call per byte, so all reads go
    // through this buffer. Owned by the single reading thread.
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private int _readPos;
    private int _readLen;

    /// <summary>Refill the read-ahead buffer; false at end of stream.</summary>
    private bool FillReadBuffer()
    {
        _readPos = 0;
        _readLen = _input.Read(_readBuffer, 0, _readBuffer.Length);
        return _readLen > 0;
    }

    private int ReadByteBuffered()
    {
        if (_readPos >= _readLen && !FillReadBuffer())
            return -1;
        return _readBuffer[_readPos++];
    }

    private int ReadBuffered(byte[] destination, int offset, int count)
    {
        if (_readPos >= _readLen)
        {
            // Large reads bypass the buffer once it is drained.
            if (count >= _readBuffer.Length)
                return _input.Read(destination, offset, count);
            if (!FillReadBuffer())
                return 0;
        }

        int n = Math.Min(count, _readLen - _readPos);
        Buffer.BlockCopy(_readBuffer, _readPos, destination, offset, n);
        _readPos += n;
        return n;
    }

    /// <summary>
    /// Read raw bytes up to (not including) the next <c>\n</c>, dropping a
    /// trailing <c>\r</c>. Bytes are decoded by the caller as UTF-8. Returns
    /// null at end of stream when no bytes were read.
    /// </summary>
    private byte[]? ReadLineBytes(List<byte>? prefix)
    {
        var line = new MemoryStream();
        if (prefix != null)
            line.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prefix));

        while (true)
        {
            if (_readPos >= _readLen && !FillReadBuffer())
                return line.Length > 0 ? TrimCr(line) : null;

            var available = _readBuffer.AsSpan(_readPos, _readLen - _readPos);
            int newline = available.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                line.Write(available[..newline]);
                _readPos += newline + 1;
                return TrimCr(line);
            }

            line.Write(available);
            _readPos = _readLen;
        }
    }

    private static byte[] TrimCr(MemoryStream line)
    {
        var bytes = line.ToArray();
        return bytes.Length > 0 && bytes[^1] == '\r' ? bytes[..^1] : bytes;
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
