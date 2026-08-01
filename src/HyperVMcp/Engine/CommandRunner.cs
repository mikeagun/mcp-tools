// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Management.Automation;
using McpSharp;

namespace HyperVMcp.Engine;

/// <summary>
/// Manages asynchronous VM command and host-side transfer jobs.
/// Supports multiple concurrent jobs per session and across sessions.
/// Follows the BuildManager pattern from msbuild-mcp.
/// </summary>
public sealed class CommandRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, CommandJob> _commands = new();
    private readonly SessionManager _sessionManager;
    private int _commandCounter;
    private bool _disposed;

    /// <summary>
    /// Maximum concurrent commands per session. PSDirect/WinRM sessions are
    /// single-pipeline — only one Invoke-Command can execute at a time.
    /// </summary>
    public int MaxCommandsPerSession { get; set; } = 1;

    public CommandRunner(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Start a command on a VM session. Returns immediately with a CommandJob
    /// that can be polled for status. The command runs asynchronously.
    /// </summary>
    public CommandJob StartCommand(string sessionId, string command, string? workingDirectory = null,
        string outputFormat = "text", int? hardTimeoutSecs = null,
        string outputMode = "full", string? saveTo = null)
    {
        var vmName = _sessionManager.GetSession(sessionId).VmName;

        var activeCount = _commands.Values.Count(c =>
            c.SessionId == sessionId && c.Status == CommandStatus.Running);
        if (activeCount >= MaxCommandsPerSession)
            throw new InvalidOperationException(
                $"Session '{sessionId}' has {activeCount} active commands (limit: {MaxCommandsPerSession}). " +
                $"Wait for a command to complete or increase the limit.");

        var commandId = $"cmd-{Interlocked.Increment(ref _commandCounter)}";
        var job = new CommandJob(commandId, sessionId, vmName, command, outputMode, saveTo);

        _commands[commandId] = job;

        // Launch async execution.
        job.ExecutionTask = Task.Run(() => ExecuteCommandAsync(job, sessionId, command, workingDirectory, outputFormat, hardTimeoutSecs));

        return job;
    }

    /// <summary>
    /// Execute a short command synchronously, waiting up to the specified timeout.
    /// If the command completes within the timeout, returns the result inline.
    /// If it's still running, returns the job for polling.
    /// </summary>
    public CommandJob ExecuteWithTimeout(string sessionId, string command, int timeoutSeconds = 30,
        string? workingDirectory = null, string outputFormat = "text", int? hardTimeoutSecs = null,
        string outputMode = "full", string? saveTo = null)
    {
        var job = StartCommand(sessionId, command, workingDirectory, outputFormat, hardTimeoutSecs, outputMode, saveTo);

        // Wait for completion or timeout.
        McpProgress.Current.SetStatusProvider(job.ProgressSummary);
        var completed = job.WaitForNews(timeoutSeconds * 1000);

        // If still running after initial wait, keep waiting briefly for any final output.
        if (job.Status == CommandStatus.Running && completed)
            job.WaitForNews(500);

        return job;
    }

    /// <summary>
    /// Check if a session has any running commands. Used by sync tools to fail fast
    /// when the PSSession is busy (PSDirect sessions are single-pipeline).
    /// </summary>
    public (bool IsBusy, string? ActiveCommandId) GetActiveCommand(string sessionId)
    {
        var active = _commands.Values.FirstOrDefault(c =>
            c.SessionId == sessionId && c.Status == CommandStatus.Running);
        return (active != null, active?.CommandId);
    }

    /// <summary>
    /// Get a retained command or transfer job by ID.
    /// </summary>
    public CommandJob? GetCommand(string commandId)
    {
        _commands.TryGetValue(commandId, out var job);
        return job;
    }

    /// <summary>
    /// Resolve immutable policy identity for a retained command or transfer job.
    /// </summary>
    /// <param name="commandId">The process-local command or transfer job identifier.</param>
    /// <returns>The retained policy context, or <see langword="null"/> if the job is absent.</returns>
    public CommandPolicyContext? GetPolicyContext(string commandId)
    {
        var job = GetCommand(commandId);
        return job == null ? null : new CommandPolicyContext(job.SessionId, job.VmName);
    }

    internal static string JobNotFoundMessage(string commandId)
        => $"Command or transfer job '{commandId}' was not found in this server process. " +
           "Use an ID returned by invoke_command, run_script, copy_to_vm, or copy_from_vm, " +
           "or rerun the originating operation.";

    /// <summary>
    /// List all retained command and transfer jobs, optionally filtered by session.
    /// </summary>
    public IReadOnlyList<CommandSnapshot> ListCommands(string? sessionId = null)
    {
        var commands = _commands.Values.AsEnumerable();
        if (sessionId != null)
            commands = commands.Where(c => c.SessionId == sessionId);
        return commands.Select(c => c.GetSnapshot()).ToList();
    }

    /// <summary>
    /// Cancel a running command or transfer job. Returns the current snapshot.
    /// </summary>
    public CommandSnapshot CancelCommand(string commandId)
    {
        var job = GetCommand(commandId)
            ?? throw new InvalidOperationException(JobNotFoundMessage(commandId));

        if (job.Status == CommandStatus.Running && job.BackendRequestId != null)
        {
            _sessionManager.CancelCommand(job.BackendRequestId);
        }

        return job.GetSnapshot();
    }

    private async Task ExecuteCommandAsync(
        CommandJob job, string sessionId, string command, string? workingDirectory,
        string outputFormat, int? hardTimeoutSecs)
    {
        try
        {
            var (requestId, completionTask) = _sessionManager.ExecuteOnVmStreaming(
                sessionId, command, job, workingDirectory, outputFormat, hardTimeoutSecs);
            job.BackendRequestId = requestId;
            await completionTask;
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Start a host-side transfer job (runs on elevated backend, not wrapped in Invoke-Command).
    /// Returns immediately with a CommandJob that can be polled.
    /// </summary>
    public CommandJob StartTransfer(string script, string description, string sessionId,
        int? hardTimeoutSecs = null, string outputMode = "full", string? saveTo = null)
    {
        var vmName = _sessionManager.GetSession(sessionId).VmName;
        var commandId = $"xfer-{Interlocked.Increment(ref _commandCounter)}";
        var job = new CommandJob(commandId, sessionId, vmName, description, outputMode, saveTo);
        _commands[commandId] = job;

        job.ExecutionTask = Task.Run(async () =>
        {
            try
            {
                var (requestId, completionTask) = _sessionManager.ExecuteOnHostStreaming(
                    script, job, hardTimeoutSecs);
                job.BackendRequestId = requestId;
                await completionTask;
            }
            catch (Exception ex)
            {
                job.Fail(ex.Message);
            }
        });

        return job;
    }

    /// <summary>
    /// Start a transfer and wait up to timeoutSeconds for completion.
    /// If the transfer completes within the timeout, returns the result inline.
    /// If still running, returns the job for polling.
    /// </summary>
    public CommandJob ExecuteTransferWithTimeout(string script, string description, string sessionId,
        int timeoutSeconds = 30, int? hardTimeoutSecs = null,
        string outputMode = "full", string? saveTo = null)
    {
        var job = StartTransfer(script, description, sessionId, hardTimeoutSecs, outputMode, saveTo);

        var completed = job.WaitForNews(timeoutSeconds * 1000);
        if (job.Status == CommandStatus.Running && completed)
            job.WaitForNews(500);

        return job;
    }

    /// <summary>
    /// Free a command or transfer job's output buffer to release memory.
    /// </summary>
    public int FreeCommandOutput(string commandId)
    {
        var job = GetCommand(commandId)
            ?? throw new InvalidOperationException(JobNotFoundMessage(commandId));
        return job.Output.Free();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel all running commands before waiting.
        foreach (var job in _commands.Values)
        {
            if (job.Status == CommandStatus.Running && job.BackendRequestId != null)
            {
                try { _sessionManager.CancelCommand(job.BackendRequestId); }
                catch { /* best effort during shutdown */ }
            }
        }

        // Wait briefly for commands to complete after cancellation.
        var runningTasks = _commands.Values
            .Where(j => j.ExecutionTask is { IsCompleted: false })
            .Select(j => j.ExecutionTask!)
            .ToArray();
        if (runningTasks.Length > 0)
        {
            try { Task.WaitAll(runningTasks, 5000); }
            catch { /* observed — shutting down */ }
        }

        foreach (var job in _commands.Values)
            job.Dispose();
        _commands.Clear();
    }
}
