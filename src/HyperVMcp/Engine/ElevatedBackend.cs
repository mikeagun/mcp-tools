// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyperVMcp.Engine;

/// <summary>
/// Manages a lazily-started elevated PowerShell backend process.
/// The backend is launched via Verb="runas" on first use, triggering a UAC prompt
/// that displays "Hyper-V MCP Server". Communication uses named pipes with PID
/// verification for security.
///
/// Supports two execution modes:
/// - Sync (Execute): sends a script, blocks until result. Used for session management and short ops.
/// - Async (StartAsync): sends a script, streams output/error lines via registered handlers.
///   Used for long-running VM commands that need real-time output streaming.
///
/// Protocol (frontend → backend):
///   Sync exec:  {"id":"r-1","script":"..."}
///   Async start: {"type":"start","id":"r-1","script":"...","timeout":N}
///   Cancel:      {"type":"cancel","id":"r-1"}
///
/// Protocol (backend → frontend):
///   Sync result:   {"id":"r-1","status":"ok","output":[...],"errors":[...]}
///   Stream output:  {"id":"r-1","type":"output","line":"..."}
///   Stream error:   {"id":"r-1","type":"error","line":"..."}
///   Async done:     {"id":"r-1","type":"done","had_errors":bool}
///   Cancelled:      {"id":"r-1","type":"cancelled"}
///   Timed out:      {"id":"r-1","type":"timed_out"}
///   Diagnostics:    {"type":"diag","line":"..."}
/// </summary>
public sealed class ElevatedBackend : IDisposable
{
    private readonly object _startLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, StreamHandler> _streamHandlers = new();
    private Process? _process;
    private PipeTransport? _pipeTransport;
    private Task? _readerTask;
    private int _requestCounter;
    private int _disposed;
    private bool _started;

    /// <summary>
    /// Default timeout for sync Execute() calls, in milliseconds.
    /// Should be less than the MCP protocol timeout to ensure responses arrive in time.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 50_000;

    private sealed record StreamHandler(Action<string, string> OnLine, Action<string, bool> OnComplete);

    public bool IsStarted => _started;

    /// <summary>
    /// Generate a unique request ID for use with StartAsync/RegisterStreamHandler.
    /// </summary>
    public string NextRequestId() => $"r-{Interlocked.Increment(ref _requestCounter)}";

    public void EnsureStarted()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            StartBackend();
            _started = true;
        }
    }

    /// <summary>
    /// Execute a PowerShell script synchronously. Returns a JSON result object.
    /// On timeout, returns a structured error instead of throwing, ensuring the
    /// MCP response arrives before the protocol timeout expires.
    /// </summary>
    public JsonObject Execute(string script, int? timeoutMs = null)
    {
        EnsureStarted();

        var effectiveTimeout = timeoutMs ?? DefaultTimeoutMs;
        var reqId = NextRequestId();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;

        try
        {
            SendToBackend(new JsonObject
            {
                ["id"] = reqId,
                ["script"] = script,
            });

            if (!tcs.Task.Wait(effectiveTimeout))
            {
                _pending.TryRemove(reqId, out _);
                return new JsonObject
                {
                    ["status"] = "error",
                    ["error"] = $"Backend did not respond within {effectiveTimeout / 1000}s. " +
                        "A long-running command may be occupying the runspace pool. " +
                        "Use get_command_status to check active commands, or cancel_command to free a slot.",
                    ["output"] = new JsonArray(),
                    ["errors"] = new JsonArray(),
                };
            }

            return tcs.Task.Result;
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    /// <summary>
    /// Start an async command in the backend. Returns immediately.
    /// Output is streamed to the handler registered via RegisterStreamHandler.
    /// </summary>
    public void StartAsync(string id, string script, int? hardTimeoutSecs)
    {
        EnsureStarted();

        var request = new JsonObject
        {
            ["type"] = "start",
            ["id"] = id,
            ["script"] = script,
        };
        if (hardTimeoutSecs.HasValue)
            request["timeout"] = hardTimeoutSecs.Value;

        SendToBackend(request);
    }

    /// <summary>
    /// Cancel a running async command.
    /// </summary>
    public void Cancel(string id)
    {
        SendToBackend(new JsonObject
        {
            ["type"] = "cancel",
            ["id"] = id,
        });
    }

    /// <summary>
    /// Register callbacks for streamed output from an async command.
    /// Must be called BEFORE StartAsync to avoid missing early output.
    /// </summary>
    public void RegisterStreamHandler(string id, Action<string, string> onLine, Action<string, bool> onComplete)
    {
        _streamHandlers[id] = new StreamHandler(onLine, onComplete);
    }

    /// <summary>
    /// Unregister a stream handler (e.g., on cleanup).
    /// </summary>
    public void UnregisterStreamHandler(string id)
    {
        _streamHandlers.TryRemove(id, out _);
    }

    private void SendToBackend(JsonObject request)
    {
        lock (_startLock)
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("Elevated backend process is not running.");

            try
            {
                var line = request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                _pipeTransport!.Writer.WriteLine(line);
                _pipeTransport.Writer.Flush();
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Failed to communicate with elevated backend: {ex.Message}", ex);
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("Elevated backend process has been disposed.");
            }
        }
    }

    private void StartBackend()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine own executable path.");

        _pipeTransport = new PipeTransport();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--elevated-backend");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(_pipeTransport.PipeName);

        Log("starting elevated backend via runas (UAC prompt may appear)...");

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start elevated backend.");

        Log($"elevated backend started (PID {_process.Id})");

        // Wait for backend to connect to our pipe and verify its PID.
        try
        {
            _pipeTransport.WaitForConnectionAsync(_process.Id, 30_000).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _pipeTransport.Dispose();
            _pipeTransport = null;
            throw new TimeoutException("Elevated backend did not connect within 30s.");
        }

        Log("backend connected to pipe, PID verified");

        // Read responses from named pipe.
        _readerTask = Task.Run(ReadResponses);

        // Wait for ready signal.
        _pending["ready"] = new TaskCompletionSource<JsonObject>();

        if (!_pending["ready"].Task.Wait(30_000))
        {
            _pending.TryRemove("ready", out _);
            throw new TimeoutException("Elevated backend did not send ready signal within 30s.");
        }
        _pending.TryRemove("ready", out _);
    }

    private void ReadResponses()
    {
        try
        {
            while (_pipeTransport?.Reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var response = JsonNode.Parse(line)?.AsObject();
                    if (response == null) continue;

                    // Handle diagnostics messages from the backend.
                    var type = response["type"]?.GetValue<string>();
                    if (type == "diag")
                    {
                        var diagLine = response["line"]?.GetValue<string>() ?? "";
                        Console.Error.WriteLine($"hyperv-mcp[backend]: {diagLine}");
                        continue;
                    }

                    var id = response["id"]?.GetValue<string>();
                    if (id == null) continue;

                    if (type == "output" || type == "error")
                    {
                        // Streaming line from an async command.
                        if (_streamHandlers.TryGetValue(id, out var handler))
                        {
                            var lineText = response["line"]?.GetValue<string>() ?? "";
                            handler.OnLine(type, lineText);
                        }
                    }
                    else if (type == "done" || type == "cancelled" || type == "timed_out")
                    {
                        // Async command completed/cancelled/timed out.
                        if (_streamHandlers.TryRemove(id, out var handler))
                        {
                            var hadErrors = response["had_errors"]?.GetValue<bool>() ?? false;
                            handler.OnComplete(type, hadErrors);
                        }
                    }
                    else
                    {
                        // Sync response (no type field).
                        if (_pending.TryGetValue(id, out var tcs))
                            tcs.TrySetResult(response);
                    }
                }
                catch
                {
                    // Malformed JSON — skip.
                }
            }
        }
        catch { /* process exited */ }

        // Signal all pending sync requests that the backend died.
        foreach (var kvp in _pending.ToArray())
            kvp.Value.TrySetException(new InvalidOperationException("Elevated backend process exited unexpectedly."));

        // Signal all stream handlers that the backend died.
        foreach (var kvp in _streamHandlers.ToArray())
        {
            try { kvp.Value.OnComplete("error", true); }
            catch { /* handler may throw */ }
        }
        _streamHandlers.Clear();
    }

    private static void Log(string message) => Console.Error.WriteLine($"hyperv-mcp: {message}");

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        if (_process is { HasExited: false })
        {
            // Try graceful shutdown via the pipe.
            try
            {
                _pipeTransport?.Writer.WriteLine("{\"id\":\"shutdown\",\"script\":\"__shutdown__\"}");
                _pipeTransport?.Writer.Flush();
                _process.WaitForExit(3000);
            }
            catch { /* pipe may already be broken — best effort */ }

            // Force-kill if graceful shutdown didn't work.
            if (!_process.HasExited)
            {
                try { _process.Kill(true); } catch { }
            }
        }

        _pipeTransport?.Dispose();
        _process?.Dispose();
    }
}

/// <summary>
/// The elevated backend entry point. Reads JSON-line commands from a named pipe,
/// executes PowerShell scripts, and writes JSON-line responses back.
///
/// Uses a single shared Runspace. PSSessions are stored in $global:__sessions and
/// accessed directly by all commands. Async commands use BeginInvoke; sync commands
/// use Invoke. Since a Runspace allows only one pipeline at a time, sync requests
/// that arrive while an async command is running get an immediate "busy" error
/// instead of blocking the main loop.
///
/// When the frontend process exits (clean or crash), the pipe closes and ReadLine()
/// returns null, causing the main loop to exit and cleanup to run.
/// </summary>
public static class ElevatedBackendHost
{
    // Shared runspace for all operations. PSSessions live here in $global:__sessions.
    private static readonly Runspace _runspace;
    // Tracks running async commands for cancellation.
    private static readonly ConcurrentDictionary<string, PowerShell> _runningCommands = new();
    // Synchronized writer — multiple async commands may stream concurrently.
    private static readonly object _writeLock = new();

    // Named pipe I/O streams.
    private static TextReader _input = null!;
    private static TextWriter _output = null!;
    // Set when the pipe is broken (frontend exited) — suppresses further write attempts.
    private static volatile bool _pipeBroken;

    static ElevatedBackendHost()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();

        // Initialize session storage hashtable.
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("$global:__sessions = @{}");
        ps.Invoke();
    }

    /// <summary>
    /// Run the backend loop. Connects to the frontend's named pipe server.
    /// </summary>
    public static void Run(string pipeName)
    {
        using var pipeClient = new PipeClient(pipeName);
        try
        {
            pipeClient.Connect(10_000);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"backend: failed to connect to pipe: {ex.Message}");
            Environment.Exit(1);
            return;
        }
        _input = pipeClient.Reader;
        _output = pipeClient.Writer;
        WriteDiag("backend started");

        // Send ready signal.
        WriteLineSync(new JsonObject { ["id"] = "ready", ["status"] = "ok" });

        // Main command loop — exits when the pipe closes (frontend exited or clean shutdown).
        while (_input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonNode.Parse(line)?.AsObject();
                if (request == null) continue;

                var id = request["id"]?.GetValue<string>() ?? "unknown";
                var script = request["script"]?.GetValue<string>() ?? "";
                var type = request["type"]?.GetValue<string>();

                if (script == "__shutdown__")
                {
                    WriteDiag("backend shutting down");
                    break;
                }

                switch (type)
                {
                    case "start":
                        HandleStart(id, script, request["timeout"]?.GetValue<int>());
                        break;
                    case "cancel":
                        HandleCancel(id);
                        break;
                    default:
                        // Sync exec — check if runspace is available first.
                        if (_runspace.RunspaceAvailability != RunspaceAvailability.Available)
                        {
                            WriteLineSync(new JsonObject
                            {
                                ["id"] = id,
                                ["status"] = "error",
                                ["error"] = "Session is busy (command running). " +
                                    "Poll with get_command_status or abort with cancel_command.",
                                ["output"] = new JsonArray(),
                                ["errors"] = new JsonArray(),
                            });
                            break;
                        }
                        var result = ExecuteScript(script);
                        result["id"] = id;
                        WriteLineSync(result);
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteDiag($"backend error: {ex.Message}");
            }
        }

        // Cancel all running async commands.
        foreach (var kvp in _runningCommands)
        {
            try { kvp.Value.Stop(); } catch { }
        }
        _runningCommands.Clear();

        // Cleanup all sessions.
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript("$global:__sessions.Values | Remove-PSSession -ErrorAction SilentlyContinue");
            ps.Invoke();
        }
        catch { }

        _runspace.Dispose();
        WriteDiag("backend stopped");
    }

    private static void HandleStart(string id, string script, int? timeoutSecs)
    {
        var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(script);

        _runningCommands[id] = ps;
        var hadErrors = false;

        // Stream output lines as they arrive.
        var output = new PSDataCollection<PSObject>();
        output.DataAdded += (sender, e) =>
        {
            var collection = (PSDataCollection<PSObject>)sender!;
            var item = collection[e.Index];
            WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "output", ["line"] = item?.ToString() ?? "" });
        };

        ps.Streams.Error.DataAdded += (sender, e) =>
        {
            hadErrors = true;
            var collection = (PSDataCollection<ErrorRecord>)sender!;
            var item = collection[e.Index];
            WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "error", ["line"] = item?.ToString() ?? "" });
        };

        // Hard timeout: kill the pipeline after N seconds.
        Timer? hardTimer = null;
        if (timeoutSecs.HasValue && timeoutSecs.Value > 0)
        {
            hardTimer = new Timer(_ =>
            {
                if (_runningCommands.TryRemove(id, out var timedPs))
                {
                    try { timedPs.Stop(); } catch { }
                    WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "timed_out" });
                }
            }, null, timeoutSecs.Value * 1000, Timeout.Infinite);
        }

        IAsyncResult asyncResult;
        try
        {
            var input = new PSDataCollection<PSObject>();
            input.Complete();
            asyncResult = ps.BeginInvoke(input, output);
        }
        catch (Exception ex)
        {
            _runningCommands.TryRemove(id, out _);
            hardTimer?.Dispose();
            WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "error", ["line"] = ex.Message });
            WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "done", ["had_errors"] = true });
            ps.Dispose();
            return;
        }

        // Monitor completion in background.
        Task.Run(() =>
        {
            try
            {
                ps.EndInvoke(asyncResult);
            }
            catch (PipelineStoppedException) { /* cancel/timeout */ }
            catch (Exception ex)
            {
                hadErrors = true;
                // Terminating errors from -ErrorAction Stop may throw here
                // instead of (or in addition to) firing Streams.Error.DataAdded.
                // Surface the message so agents see WHY the command failed.
                WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "error", ["line"] = ex.Message });
            }

            hardTimer?.Dispose();

            // Only send "done" if we weren't already cancelled/timed out.
            if (_runningCommands.TryRemove(id, out _))
            {
                WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "done", ["had_errors"] = hadErrors });
            }

            ps.Dispose();
        });
    }

    private static void HandleCancel(string id)
    {
        if (_runningCommands.TryRemove(id, out var ps))
        {
            try { ps.Stop(); } catch { }
            WriteLineSync(new JsonObject { ["id"] = id, ["type"] = "cancelled" });
        }
    }

    private static JsonObject ExecuteScript(string script)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(script);

        var output = new List<string>();
        try
        {
            var results = ps.Invoke();
            foreach (var r in results)
                output.Add(r?.ToString() ?? "");
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = ex.Message,
                ["output"] = new JsonArray(),
            };
        }

        var errors = ps.Streams.Error.Select(e => e.ToString()).ToList();

        var outputArr = new JsonArray();
        foreach (var o in output) outputArr.Add(o);
        var errorArr = new JsonArray();
        foreach (var e in errors) errorArr.Add(e);

        return new JsonObject
        {
            ["status"] = errors.Count > 0 ? "error" : "ok",
            ["output"] = outputArr,
            ["errors"] = errorArr,
        };
    }

    /// <summary>
    /// Synchronized writer — multiple async commands may stream concurrently.
    /// Silently drops messages if the pipe is broken (frontend exited).
    /// </summary>
    private static void WriteLineSync(JsonObject obj)
    {
        if (_pipeBroken) return;

        var line = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        lock (_writeLock)
        {
            if (_pipeBroken) return;
            try
            {
                _output.WriteLine(line);
                _output.Flush();
            }
            catch (IOException)
            {
                _pipeBroken = true;
            }
            catch (ObjectDisposedException)
            {
                _pipeBroken = true;
            }
        }
    }

    /// <summary>
    /// Write a diagnostic message via the pipe protocol.
    /// </summary>
    private static void WriteDiag(string message)
    {
        WriteLineSync(new JsonObject { ["type"] = "diag", ["line"] = message });
    }
}
