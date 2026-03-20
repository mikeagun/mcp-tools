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
/// A dedicated reader thread owns the input stream; consumers use TakeMessage().
/// </summary>
public sealed class McpTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _logPrefix;
    private Framing _framing = Framing.Unknown;

    private enum Framing { Unknown, ContentLength, Ndjson }

    // Single reader thread feeds this collection. All consumers read from here.
    private readonly BlockingCollection<JsonNode> _messageQueue = new();
    private Thread? _readerThread;

    public McpTransport(Stream input, Stream output, string? logPrefix = null)
    {
        _input = input;
        _output = output;
        _logPrefix = logPrefix ?? "mcp-server";
    }

    /// <summary>
    /// Start the background reader thread that owns the input stream.
    /// Must be called before Run() or TakeMessage(). Called automatically by Run().
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
                    if (msg == null) break; // EOF / connection closed.
                    _messageQueue.Add(msg);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{_logPrefix}: reader thread error: {ex.Message}");
            }
            finally
            {
                _messageQueue.CompleteAdding();
            }
        }) { IsBackground = true, Name = $"{_logPrefix}-reader" };
        _readerThread.Start();
    }

    /// <summary>
    /// Take the next message from the queue. Blocks until a message is available
    /// or the reader thread has finished (returns null).
    /// </summary>
    public JsonNode? TakeMessage()
    {
        try { return _messageQueue.Take(); }
        catch (InvalidOperationException) { return null; } // CompleteAdding was called.
    }

    /// <summary>
    /// Try to take a message with a timeout and cancellation token.
    /// Returns null if the timeout expires or cancellation is requested.
    /// </summary>
    public JsonNode? TakeMessage(int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            return _messageQueue.TryTake(out var msg, timeoutMs, ct) ? msg : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // Messages returned by Elicit() that need to be re-consumed by Run().
    private readonly ConcurrentQueue<JsonNode> _returnedMessages = new();

    /// <summary>
    /// Return a message to the front of the queue (for messages consumed by
    /// Elicit() that belong to Run() — e.g., parallel tool calls).
    /// </summary>
    internal void ReturnMessage(JsonNode msg) => _returnedMessages.Enqueue(msg);

    /// <summary>
    /// Main event loop. Reads requests, dispatches to handler, writes responses.
    /// </summary>
    public void Run(Func<string, JsonNode?, JsonNode?> handler)
    {
        StartReader();

        while (true)
        {
            // Check returned messages first (from Elicit() consuming non-response messages).
            if (!_returnedMessages.TryDequeue(out var request))
                request = TakeMessage();

            if (request == null) break;

            var method = request["method"]?.GetValue<string>() ?? "";
            var parameters = request["params"];
            var isNotification = !request.AsObject().ContainsKey("id") || request["id"] is null;

            try
            {
                var result = handler(method, parameters);
                if (!isNotification)
                {
                    var response = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = JsonNode.Parse(request["id"]!.ToJsonString()),
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
                        ["id"] = JsonNode.Parse(request["id"]!.ToJsonString()),
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
    }

    // ── Stream reading (used by reader thread; also public for test setups) ──

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

    public void WriteMessage(JsonNode msg)
    {
        var body = msg.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var bodyBytes = Encoding.UTF8.GetBytes(body);

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
