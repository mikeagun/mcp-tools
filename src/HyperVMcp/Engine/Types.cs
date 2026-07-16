// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

namespace HyperVMcp.Engine;

/// <summary>
/// Represents a managed PowerShell session to a VM.
/// </summary>
public sealed class ManagedSession
{
    public required string SessionId { get; init; }
    public required string VmName { get; init; }
    public required ConnectionMode Mode { get; init; }
    public required string CredentialTarget { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Connected;
    public DateTime ConnectedSince { get; init; } = DateTime.UtcNow;
    public int CommandCount;

    public void IncrementCommandCount() => Interlocked.Increment(ref CommandCount);

    /// <summary>
    /// InstanceId of the PSSession, used by async commands to retrieve the session
    /// from any runspace via Get-PSSession -InstanceId.
    /// </summary>
    public Guid? SessionInstanceId { get; set; }

    /// <summary>
    /// Environment variables injected into all commands on this session.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();
}

public enum ConnectionMode
{
    PSDirect,
    WinRM,
}

public enum SessionStatus
{
    Connected,
    Disconnected,
    Reconnecting,
    Broken,
}

/// <summary>
/// Tracks a long-running command executed on a VM.
/// </summary>
public sealed class CommandJob : IDisposable
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _newsEvent = new(false);
    private long _version;
    private bool _disposed;

    public string CommandId { get; }
    public string SessionId { get; }
    public string Command { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public CommandStatus Status { get; private set; } = CommandStatus.Running;
    public int? ExitCode { get; private set; }
    public Task? ExecutionTask { get; set; }

    /// <summary>Backend request ID for cancellation.</summary>
    public string? BackendRequestId { get; set; }

    /// <summary>Output buffer (stdout). Agent controls mode via output_mode param.</summary>
    public OutputBuffer Output { get; }

    /// <summary>Error buffer (stderr). Always full mode, typically small.</summary>
    public OutputBuffer Errors { get; }

    public CommandJob(string commandId, string sessionId, string command,
        string outputMode = "full", string? saveTo = null)
    {
        CommandId = commandId;
        SessionId = sessionId;
        Command = command;
        Output = new OutputBuffer(outputMode, saveTo);
        Errors = new OutputBuffer("full"); // errors are always small, keep all
    }

    public void AddOutput(string line)
    {
        Output.AddLine(line);
        lock (_lock) { _version++; }
        _newsEvent.Set();
    }

    public void AddError(string line)
    {
        Errors.AddLine(line);
        lock (_lock) { _version++; }
        _newsEvent.Set();
    }

    public void Complete(int exitCode)
    {
        lock (_lock)
        {
            ExitCode = exitCode;
            Status = exitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
            _version++;
        }
        _newsEvent.Set();
    }

    public void Fail(string error)
    {
        Errors.AddLine(error);
        lock (_lock)
        {
            Status = CommandStatus.Failed;
            ExitCode = -1;
            _version++;
        }
        _newsEvent.Set();
    }

    public void TimedOut()
    {
        lock (_lock)
        {
            Status = CommandStatus.TimedOut;
            _version++;
        }
        _newsEvent.Set();
    }

    public void Cancel()
    {
        lock (_lock)
        {
            Status = CommandStatus.Cancelled;
            _version++;
        }
        _newsEvent.Set();
    }

    /// <summary>
    /// Waits for new output, completion, or timeout — whichever comes first.
    /// </summary>
    public bool WaitForNews(int timeoutMs)
    {
        long versionBefore;
        lock (_lock)
        {
            versionBefore = _version;
            _newsEvent.Reset();
        }
        lock (_lock)
        {
            if (_version != versionBefore)
                return true;
        }
        return _newsEvent.Wait(timeoutMs);
    }

    /// <summary>
    /// Drain-style wait for polling: blocks until the command completes or the
    /// timeout elapses, ignoring intermediate output lines (which only update the
    /// progress provider). Unlike <see cref="WaitForNews"/> — which returns on the
    /// first new output line for initial-output latency — this gives a poll a full
    /// window so progress can stream. Mirrors BuildJob's poll semantics.
    /// Future: accept early-return triggers (output filter/regex or parsed error)
    /// analogous to BuildJob's return-on-new-error.
    /// </summary>
    public void WaitForCompletion(int timeoutMs)
    {
        if (timeoutMs <= 0) return;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            // Reset BEFORE re-checking the terminal condition. Because
            // Complete/Fail/TimedOut/Cancel all Set() the event AFTER updating
            // Status under _lock, any completion landing after this Reset() leaves
            // the event signaled, so the Wait() below returns immediately — no
            // lost wakeup. (Do not delegate to WaitForNews here: its version
            // capture + Reset would consume a completion that raced the check.)
            lock (_lock)
            {
                if (Status != CommandStatus.Running) return;
                _newsEvent.Reset();
            }
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) return;
            _newsEvent.Wait(remaining);
        }
    }

    /// <summary>
    /// Short, thread-safe progress summary for progress notifications: stdout line
    /// count, stderr line count (when any), and a truncated last-line preview.
    /// Returns null until any output arrives so the caller can fall back to the
    /// generic keepalive text.
    /// </summary>
    public string? ProgressSummary()
    {
        var outLines = Output.TotalLinesReceived;
        var errLines = Errors.TotalLinesReceived;
        if (outLines == 0 && errLines == 0) return null;
        var tail = Output.GetTail(1);
        var lastLine = tail.Lines.Count > 0 ? tail.Lines[0] : null;
        return FormatProgress(outLines, errLines, lastLine);
    }

    /// <summary>Pure formatter for the progress summary message (testable).</summary>
    internal static string? FormatProgress(int totalLines, int stderrLines, string? lastLine)
    {
        if (totalLines == 0 && stderrLines == 0) return null;
        var msg = $"{totalLines} {(totalLines == 1 ? "line" : "lines")} output";
        if (stderrLines > 0)
            msg += $", {stderrLines} stderr";
        if (!string.IsNullOrEmpty(lastLine))
        {
            var preview = lastLine.Length > 100 ? lastLine[..100] + "…" : lastLine;
            msg += $". Last: '{preview}'";
        }
        return msg;
    }

    public CommandSnapshot GetSnapshot(int tailLines = 100, bool includeOutput = true, int? sinceLine = null)
    {
        lock (_lock)
        {
            OutputSlice? outputSlice = null;
            if (includeOutput)
            {
                outputSlice = sinceLine.HasValue
                    ? Output.GetTailSince(sinceLine.Value, tailLines)
                    : Output.GetTail(tailLines);
            }

            return new CommandSnapshot
            {
                CommandId = CommandId,
                SessionId = SessionId,
                Command = Command,
                Status = Status,
                ExitCode = ExitCode,
                Output = outputSlice != null ? string.Join("\n", outputSlice.Lines) : null,
                Errors = string.Join("\n", Errors.GetTail(100).Lines),
                TotalOutputLines = Output.TotalLinesReceived,
                RetainedOutputLines = Output.RetainedLineCount,
                FirstAvailableLine = Output.FirstAvailableLine,
                OutputMode = Output.Mode,
                SavedTo = Output.SavedTo,
                ElapsedSeconds = (DateTime.UtcNow - StartedAt).TotalSeconds,
                FromLine = outputSlice?.FromLine ?? 0,
                ToLine = outputSlice?.ToLine ?? 0,
                SkippedLines = outputSlice?.SkippedLines ?? 0,
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ExecutionTask is { IsCompleted: false })
        {
            try { ExecutionTask.Wait(3000); } catch { /* observed */ }
        }
        else if (ExecutionTask is { IsFaulted: true })
        {
            _ = ExecutionTask.Exception;
        }
        Output.Dispose();
        Errors.Dispose();
        _newsEvent.Dispose();
    }
}

public enum CommandStatus
{
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed class CommandSnapshot
{
    public required string CommandId { get; init; }
    public required string SessionId { get; init; }
    public required string Command { get; init; }
    public required CommandStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public string? Output { get; init; }
    public string Errors { get; init; } = "";
    public int TotalOutputLines { get; init; }
    public int RetainedOutputLines { get; init; }
    public int FirstAvailableLine { get; init; }
    public string OutputMode { get; init; } = "full";
    public string? SavedTo { get; init; }
    public double ElapsedSeconds { get; init; }
    public int FromLine { get; init; }
    public int ToLine { get; init; }
    public int SkippedLines { get; init; }
}

/// <summary>
/// Compact file entry for VM directory listings.
/// </summary>
public sealed class VmFileEntry
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool Dir { get; init; }
}

/// <summary>
/// Result of a VM directory listing with summary stats.
/// </summary>
public sealed class VmFileListResult
{
    public string Path { get; init; } = "";
    public List<VmFileEntry> Entries { get; } = [];
    public int TotalFiles { get; set; }
    public int TotalDirectories { get; set; }
    public long TotalSizeBytes { get; set; }
    public int TotalCount { get; set; }
    public bool Truncated { get; set; }
}

/// <summary>
/// Hyper-V VM information.
/// </summary>
public sealed class VmInfo
{
    public required string Name { get; init; }
    public required string State { get; init; }
    public int CpuCount { get; init; }
    public long MemoryMb { get; init; }
    public TimeSpan? Uptime { get; init; }
    public List<string> Checkpoints { get; init; } = [];
}
