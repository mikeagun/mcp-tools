// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;

namespace HyperVMcp.Engine;

/// <summary>
/// Manages persistent PowerShell sessions to Hyper-V VMs.
/// Supports sync execution (for short ops) and async streaming (for long-running commands).
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CredentialStore _credentialStore;
    private readonly ElevatedBackend _backend;
    private int _sessionCounter;
    private bool _disposed;

    /// <summary>
    /// Callback to check if a session has an active running command.
    /// Set by Program.cs after CommandRunner is created (avoids circular dependency).
    /// Returns (isBusy, activeCommandId).
    /// </summary>
    public Func<string, (bool IsBusy, string? ActiveCommandId)>? ActiveCommandChecker { get; set; }

    public SessionManager(CredentialStore credentialStore, ElevatedBackend backend)
    {
        _credentialStore = credentialStore;
        _backend = backend;
    }

    /// <summary>
    /// Throws if the session has an active running command (PSSession is single-pipeline).
    /// </summary>
    private void ThrowIfSessionBusy(string sessionId)
    {
        if (ActiveCommandChecker == null) return;
        var (isBusy, activeCommandId) = ActiveCommandChecker(sessionId);
        if (isBusy)
            throw new InvalidOperationException(
                $"Session '{sessionId}' is busy ({activeCommandId} running). " +
                $"Poll with get_command_status(command_id='{activeCommandId}') or abort with cancel_command(command_id='{activeCommandId}').");
    }

    /// <summary>
    /// Connect to a VM and establish a persistent PSSession.
    /// Stores the PSSession InstanceId for use by async commands.
    /// </summary>
    public ManagedSession Connect(
        string? vmName = null,
        string? computerName = null,
        string? credentialTarget = null,
        string? username = null,
        string? password = null,
        string? sessionId = null,
        int? maxRetries = null)
    {
        if (string.IsNullOrEmpty(vmName) && string.IsNullOrEmpty(computerName))
            throw new ArgumentException("Either vm_name (for PSDirect) or computer_name (for WinRM) is required.");

        var mode = !string.IsNullOrEmpty(vmName) ? ConnectionMode.PSDirect : ConnectionMode.WinRM;
        var target = vmName ?? computerName!;
        var credTarget = credentialTarget ?? "TEST_VM";
        var id = sessionId ?? $"s-{Interlocked.Increment(ref _sessionCounter)}";

        // Default retries: PSDirect=10 (VM boot window), WinRM=2 (network failures rarely transient).
        var maxAttempts = maxRetries ?? (mode == ConnectionMode.PSDirect ? 10 : 2);

        // Build credential in the backend.
        var cred = _credentialStore.GetCredential(credTarget, username, password);
        var credUsername = PsUtils.PsEscape(cred.UserName);
        var credPassword = PsUtils.PsEscape(cred.GetNetworkCredential().Password);

        // Create session and store in $global:__sessions on the shared runspace.
        var sessionScript = mode switch
        {
            ConnectionMode.PSDirect => $@"
                $secPass = ConvertTo-SecureString '{credPassword}' -AsPlainText -Force
                $cred = [PSCredential]::new('{credUsername}', $secPass)
                $__s = New-PSSession -VMName '{PsUtils.PsEscape(target)}' -Credential $cred -ErrorAction Stop
                $global:__sessions['{PsUtils.PsEscape(id)}'] = $__s
                $__s.InstanceId.ToString()
            ",
            ConnectionMode.WinRM => $@"
                $secPass = ConvertTo-SecureString '{credPassword}' -AsPlainText -Force
                $cred = [PSCredential]::new('{credUsername}', $secPass)
                $__s = New-PSSession -ComputerName '{PsUtils.PsEscape(target)}' -Credential $cred -ErrorAction Stop
                $global:__sessions['{PsUtils.PsEscape(id)}'] = $__s
                $__s.InstanceId.ToString()
            ",
            _ => throw new ArgumentException($"Unknown connection mode: {mode}"),
        };

        // Retry for transient PSDirect errors.
        const int retryDelayMs = 3000;
        const int maxCredentialRetries = 3;
        int credentialRetryCount = 0;

        Guid? instanceId = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = _backend.Execute(sessionScript);
                var status = result["status"]?.GetValue<string>();
                if (status != "error")
                {
                    // Parse InstanceId from output.
                    var output = (result["output"]?.AsArray() ?? []).Select(o => o?.GetValue<string>() ?? "").ToList();
                    var instanceIdStr = output.LastOrDefault(s => Guid.TryParse(s, out _));
                    if (instanceIdStr != null && Guid.TryParse(instanceIdStr, out var parsed))
                        instanceId = parsed;
                    break;
                }

                var errorText = result["error"]?.GetValue<string>()
                    ?? string.Join("; ", (result["errors"]?.AsArray() ?? []).Select(e => e?.GetValue<string>()));

                if (string.IsNullOrEmpty(errorText))
                    errorText = "PSSession was not created (no error details available)";

                // Limit credential retries — bad passwords don't fix themselves.
                if (IsCredentialError(errorText))
                    credentialRetryCount++;

                if (!IsTransientError(errorText) || attempt == maxAttempts
                    || credentialRetryCount > maxCredentialRetries)
                    throw new InvalidOperationException(
                        $"Failed to create PSSession to '{target}' via {mode} (attempt {attempt}/{maxAttempts}): {errorText}");

                Log($"Transient error on attempt {attempt}/{maxAttempts}, retrying in {retryDelayMs}ms: {errorText}");
                Thread.Sleep(retryDelayMs);
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                if (IsCredentialError(ex.Message))
                    credentialRetryCount++;

                if (!IsTransientError(ex.Message) || attempt == maxAttempts
                    || credentialRetryCount > maxCredentialRetries)
                    throw new InvalidOperationException(
                        $"Failed to create PSSession to '{target}' via {mode} (attempt {attempt}/{maxAttempts}): {ex.Message}", ex);

                Log($"Transient error on attempt {attempt}/{maxAttempts}, retrying in {retryDelayMs}ms: {ex.Message}");
                Thread.Sleep(retryDelayMs);
            }
        }

        var session = new ManagedSession
        {
            SessionId = id,
            VmName = target,
            Mode = mode,
            CredentialTarget = credTarget,
            Status = SessionStatus.Connected,
            SessionInstanceId = instanceId,
        };

        _sessions[id] = session;
        Log($"Connected to {target} via {mode} as session {id} (InstanceId: {instanceId})");
        return session;
    }

    /// <summary>
    /// Disconnect and clean up a session.
    /// </summary>
    public void Disconnect(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            throw new InvalidOperationException(
                $"Session '{sessionId}' not found. Use connect_vm to create a session first.");

        try
        {
            _backend.Execute($"if ($global:__sessions['{PsUtils.PsEscape(sessionId)}']) {{ Remove-PSSession $global:__sessions['{PsUtils.PsEscape(sessionId)}'] -ErrorAction SilentlyContinue; $global:__sessions.Remove('{PsUtils.PsEscape(sessionId)}') }}");
        }
        catch { /* best-effort cleanup */ }

        session.Status = SessionStatus.Disconnected;
        Log($"Disconnected session {sessionId} ({session.VmName})");
    }

    /// <summary>
    /// Reconnect a broken or stale session (same ID, fresh PSSession).
    /// </summary>
    public ManagedSession Reconnect(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var existing))
            throw new InvalidOperationException(
                $"Session '{sessionId}' not found. Use connect_vm to create a new session.");

        // For PSDirect, check if VM is running before wasting retries.
        if (existing.Mode == ConnectionMode.PSDirect)
        {
            try
            {
                var stateResult = _backend.Execute(
                    $"(Get-VM -Name '{PsUtils.PsEscape(existing.VmName)}').State.ToString()");
                var state = (stateResult["output"]?.AsArray() ?? [])
                    .Select(o => o?.GetValue<string>()).FirstOrDefault();
                if (state != null && state != "Running")
                    throw new InvalidOperationException(
                        $"VM '{existing.VmName}' is {state}. " +
                        $"Use start_vm(vm_name='{existing.VmName}') then connect_vm(vm_name='{existing.VmName}').");
            }
            catch (InvalidOperationException) { throw; }
            catch { /* backend unavailable — fall through to reconnect attempt */ }
        }

        // Remove old session in backend (best-effort).
        try
        {
            _backend.Execute($"if ($global:__sessions['{PsUtils.PsEscape(sessionId)}']) {{ Remove-PSSession $global:__sessions['{PsUtils.PsEscape(sessionId)}'] -ErrorAction SilentlyContinue; $global:__sessions.Remove('{PsUtils.PsEscape(sessionId)}') }}");
        }
        catch { }

        // Reconnect using same parameters. Keep old session in map until new one succeeds.
        var newSession = Connect(
            vmName: existing.Mode == ConnectionMode.PSDirect ? existing.VmName : null,
            computerName: existing.Mode == ConnectionMode.WinRM ? existing.VmName : null,
            credentialTarget: existing.CredentialTarget,
            sessionId: sessionId);

        // Connect succeeded — it already overwrote _sessions[sessionId].
        return newSession;
    }

    /// <summary>
    /// Get a session by ID. Throws if not found.
    /// </summary>
    public ManagedSession GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException(
                $"Session '{sessionId}' not found. Use connect_vm to create a session first.");
        return session;
    }

    /// <summary>
    /// List all active sessions.
    /// </summary>
    public IReadOnlyList<ManagedSession> ListSessions()
    {
        return _sessions.Values.ToList();
    }

    /// <summary>
    /// Execute a command on a VM via the async backend with streaming output.
    /// Returns the backend request ID and a Task that completes when the command finishes.
    /// Output/error/completion events are wired directly to the CommandJob.
    /// </summary>
    public (string RequestId, Task CompletionTask) ExecuteOnVmStreaming(
        string sessionId, string command, CommandJob job,
        string? workingDirectory = null, string outputFormat = "text", int? hardTimeoutSecs = null)
    {
        var session = GetSession(sessionId);
        var requestId = _backend.NextRequestId();

        var envPrefix = BuildEnvPrefix(session);
        var cdPrefix = string.IsNullOrEmpty(workingDirectory)
            ? ""
            : $"Set-Location '{PsUtils.PsEscape(workingDirectory)}'; ";
        var outputSuffix = BuildOutputSuffix(outputFormat, command);

        // Build script. PSSessions live in $global:__sessions on the shared runspace.
        // BeginInvoke runs asynchronously on the same runspace.
        var script = $@"
            Invoke-Command -Session $global:__sessions['{PsUtils.PsEscape(sessionId)}'] -ScriptBlock {{
                $ErrorActionPreference = 'Continue'
                {envPrefix}{cdPrefix}{command}{outputSuffix}
            }}
        ";

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _backend.RegisterStreamHandler(requestId,
            onLine: (type, line) =>
            {
                if (type == "output") job.AddOutput(line);
                else if (type == "error") job.AddError(line);
            },
            onComplete: (completionType, hadErrors) =>
            {
                switch (completionType)
                {
                    case "done":
                        job.Complete(hadErrors ? 1 : 0);
                        break;
                    case "cancelled":
                        job.Cancel();
                        break;
                    case "timed_out":
                        job.TimedOut();
                        break;
                    default:
                        job.Fail("Backend connection lost");
                        break;
                }
                tcs.TrySetResult();
            }
        );

        _backend.StartAsync(requestId, script, hardTimeoutSecs);
        session.IncrementCommandCount();

        return (requestId, tcs.Task);
    }

    /// <summary>
    /// Execute a short command synchronously via a pool runspace.
    /// Fails fast if the session has an active command (PSSession is single-pipeline).
    /// Used by get_services, get_vm_info, list_vm_files, and other quick queries.
    /// </summary>
    public (List<string> Output, List<string> Errors) ExecuteOnVmSync(
        string sessionId, string command, string outputFormat = "none")
    {
        ThrowIfSessionBusy(sessionId);
        var session = GetSession(sessionId);
        var envPrefix = BuildEnvPrefix(session);
        var outputSuffix = BuildOutputSuffix(outputFormat, command);

        var script = $@"
            Invoke-Command -Session $global:__sessions['{PsUtils.PsEscape(sessionId)}'] -ScriptBlock {{
                {envPrefix}{command}{outputSuffix}
            }}
        ";

        var result = _backend.Execute(script);
        session.IncrementCommandCount();

        var output = (result["output"]?.AsArray() ?? []).Select(o => o?.GetValue<string>() ?? "").ToList();
        var errors = (result["errors"]?.AsArray() ?? []).Select(e => e?.GetValue<string>() ?? "").ToList();

        if (result["status"]?.GetValue<string>() == "error" && result["error"] != null)
            errors.Insert(0, result["error"]!.GetValue<string>());

        return (output, errors);
    }

    /// <summary>
    /// Execute a PowerShell command on a VM session via the elevated backend (sync).
    /// Fails fast if the session has an active command (PSSession is single-pipeline).
    /// Returns raw output lines and error lines. Used internally by FileTransferManager.
    /// </summary>
    public (List<string> Output, List<string> Errors) ExecuteOnVm(string sessionId, string command)
    {
        ThrowIfSessionBusy(sessionId);
        var session = GetSession(sessionId);

        var script = $@"
            Invoke-Command -Session $global:__sessions['{PsUtils.PsEscape(sessionId)}'] -ScriptBlock {{
                {command}
            }}
        ";

        var result = _backend.Execute(script);
        session.IncrementCommandCount();

        var output = (result["output"]?.AsArray() ?? []).Select(o => o?.GetValue<string>() ?? "").ToList();
        var errors = (result["errors"]?.AsArray() ?? []).Select(e => e?.GetValue<string>() ?? "").ToList();

        if (result["status"]?.GetValue<string>() == "error" && result["error"] != null)
            errors.Insert(0, result["error"]!.GetValue<string>());

        return (output, errors);
    }

    /// <summary>
    /// Execute a script on the elevated backend with async streaming output.
    /// Unlike ExecuteOnVmStreaming, this does NOT wrap the script in Invoke-Command.
    /// Used for host-side operations like file transfers (Copy-Item -ToSession runs on host).
    /// </summary>
    public (string RequestId, Task CompletionTask) ExecuteOnHostStreaming(
        string script, CommandJob job, int? hardTimeoutSecs = null)
    {
        var requestId = _backend.NextRequestId();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _backend.RegisterStreamHandler(requestId,
            onLine: (type, line) =>
            {
                if (type == "output") job.AddOutput(line);
                else if (type == "error") job.AddError(line);
            },
            onComplete: (completionType, hadErrors) =>
            {
                switch (completionType)
                {
                    case "done":
                        job.Complete(hadErrors ? 1 : 0);
                        break;
                    case "cancelled":
                        job.Cancel();
                        break;
                    case "timed_out":
                        job.TimedOut();
                        break;
                    default:
                        job.Fail("Backend connection lost");
                        break;
                }
                tcs.TrySetResult();
            }
        );

        _backend.StartAsync(requestId, script, hardTimeoutSecs);
        return (requestId, tcs.Task);
    }

    /// <summary>
    /// Cancel a running async command in the backend.
    /// </summary>
    public void CancelCommand(string backendRequestId)
    {
        _backend.Cancel(backendRequestId);
    }

    private static string BuildEnvPrefix(ManagedSession session)
    {
        if (session.EnvironmentVariables.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (key, value) in session.EnvironmentVariables)
            sb.Append($"$env:{key} = '{PsUtils.PsEscape(value)}'; ");
        return sb.ToString();
    }

    private static string BuildOutputSuffix(string outputFormat, string command)
    {
        return outputFormat switch
        {
            "text" => " | Out-String -Width 500",
            "json" => " | ConvertTo-Json -Depth 5 -Compress",
            _ => NeedsOutString(command) ? " | Out-String -Width 500" : "",
        };
    }

    /// <summary>
    /// Detect Format-* cmdlets that produce raw format objects when captured without Out-String.
    /// These cmdlets ALWAYS need Out-String to render properly in non-host contexts.
    /// </summary>
    private static bool NeedsOutString(string command)
    {
        // Check for Format-* cmdlets and common aliases.
        // Only match when not already piped to Out-String.
        if (command.Contains("Out-String", StringComparison.OrdinalIgnoreCase))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            command,
            @"(?i)\b(Format-Table|Format-List|Format-Wide|Format-Custom|ft|fl|fw|fc)\b");
    }

    private static bool IsTransientError(string errorText)
    {
        return errorText.Contains("PSDirectException", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("remote session might have ended", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("PSSessionStateBroken", StringComparison.OrdinalIgnoreCase)
            || IsCredentialError(errorText)
            || errorText.Contains("PowerShell cannot handle", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Windows PowerShell cannot handle", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("The background process reported an error", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("transport error", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("no error details available", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("PSSession was not created", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCredentialError(string errorText)
    {
        return errorText.Contains("The credential is invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string message) => Console.Error.WriteLine($"hyperv-mcp: {message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Clean up sessions in backend.
        foreach (var session in _sessions.Values)
        {
            try
            {
                _backend.Execute($"if ($global:__sessions['{PsUtils.PsEscape(session.SessionId)}']) {{ Remove-PSSession $global:__sessions['{PsUtils.PsEscape(session.SessionId)}'] -ErrorAction SilentlyContinue }}");
            }
            catch { }
        }

        _sessions.Clear();
    }
}
