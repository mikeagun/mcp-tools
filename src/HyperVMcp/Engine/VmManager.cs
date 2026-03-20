// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace HyperVMcp.Engine;

/// <summary>
/// Manages Hyper-V VM lifecycle operations via the elevated backend.
/// Supports bulk operations on multiple VMs in parallel.
/// </summary>
public sealed class VmManager
{
    private readonly ElevatedBackend _backend;

    public VmManager(ElevatedBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// List Hyper-V VMs on the local host, optionally filtered by name.
    /// </summary>
    public List<VmInfo> ListVms(string? nameFilter = null)
    {
        var filter = string.IsNullOrEmpty(nameFilter) ? "" : $" -Name '*{PsUtils.PsEscape(nameFilter)}*'";
        var result = _backend.Execute($@"
            Get-VM{filter} | ForEach-Object {{
                [PSCustomObject]@{{
                    Name = $_.Name
                    State = $_.State.ToString()
                    CpuCount = $_.ProcessorCount
                    MemoryMb = [math]::Round($_.MemoryAssigned / 1MB)
                    Uptime = $_.Uptime.ToString()
                    Checkpoints = @(Get-VMSnapshot -VMName $_.Name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join '|'
                }}
            }} | ForEach-Object {{ ""$($_.Name)`t$($_.State)`t$($_.CpuCount)`t$($_.MemoryMb)`t$($_.Uptime)`t$($_.Checkpoints)"" }}
        ");

        ThrowOnError(result, "List VMs");

        var vms = new List<VmInfo>();
        foreach (var line in GetOutputLines(result))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 6) continue;
            vms.Add(new VmInfo
            {
                Name = parts[0],
                State = parts[1],
                CpuCount = int.TryParse(parts[2], out var cpu) ? cpu : 0,
                MemoryMb = long.TryParse(parts[3], out var mem) ? mem : 0,
                Uptime = TimeSpan.TryParse(parts[4], out var up) ? up : null,
                Checkpoints = string.IsNullOrEmpty(parts[5]) ? [] : parts[5].Split('|').ToList(),
            });
        }
        return vms;
    }

    public async Task<List<VmOperationResult>> StartVmsAsync(
        IReadOnlyList<string> vmNames, bool waitForReady = true, int timeoutSeconds = 300)
    {
        var tasks = vmNames.Select(name => Task.Run(() => StartSingleVm(name, waitForReady, timeoutSeconds)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<List<VmOperationResult>> StopVmsAsync(IReadOnlyList<string> vmNames, bool force = true)
    {
        var tasks = vmNames.Select(name => Task.Run(() => StopSingleVm(name, force)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<List<VmOperationResult>> RestartVmsAsync(
        IReadOnlyList<string> vmNames, bool waitForReady = true, int timeoutSeconds = 300)
    {
        var tasks = vmNames.Select(name => Task.Run(() => RestartSingleVm(name, waitForReady, timeoutSeconds)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<List<VmOperationResult>> CheckpointVmsAsync(
        IReadOnlyList<string> vmNames, string checkpointName)
    {
        var tasks = vmNames.Select(name => Task.Run(() => CheckpointSingleVm(name, checkpointName)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    public async Task<List<VmOperationResult>> RestoreVmsAsync(
        IReadOnlyList<string> vmNames, string checkpointName = "baseline",
        bool waitForReady = true, int timeoutSeconds = 300)
    {
        var tasks = vmNames.Select(name => Task.Run(() =>
            RestoreSingleVm(name, checkpointName, waitForReady, timeoutSeconds)));
        return (await Task.WhenAll(tasks)).ToList();
    }

    private VmOperationResult StartSingleVm(string vmName, bool waitForReady, int timeoutSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RunBackendScript($"Start-VM -Name '{PsUtils.PsEscape(vmName)}' -ErrorAction Stop");

            if (waitForReady)
                WaitForVmReady(vmName, timeoutSeconds);

            return new VmOperationResult
            {
                VmName = vmName, Success = true,
                State = GetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new VmOperationResult
            {
                VmName = vmName, Success = false, Error = ex.Message,
                State = TryGetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    private VmOperationResult StopSingleVm(string vmName, bool force)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var forceFlag = force ? " -Force -TurnOff" : "";
            RunBackendScript($"Stop-VM -Name '{PsUtils.PsEscape(vmName)}'{forceFlag} -ErrorAction Stop");
            return new VmOperationResult
            {
                VmName = vmName, Success = true, State = "Off", ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new VmOperationResult
            {
                VmName = vmName, Success = false, Error = ex.Message,
                State = TryGetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    private VmOperationResult RestartSingleVm(string vmName, bool waitForReady, int timeoutSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RunBackendScript($"Restart-VM -Name '{PsUtils.PsEscape(vmName)}' -Force -ErrorAction Stop");
            if (waitForReady) WaitForVmReady(vmName, timeoutSeconds);
            return new VmOperationResult
            {
                VmName = vmName, Success = true,
                State = GetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new VmOperationResult
            {
                VmName = vmName, Success = false, Error = ex.Message,
                State = TryGetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    private VmOperationResult CheckpointSingleVm(string vmName, string checkpointName)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RunBackendScript($"Checkpoint-VM -Name '{PsUtils.PsEscape(vmName)}' -SnapshotName '{PsUtils.PsEscape(checkpointName)}' -ErrorAction Stop");
            return new VmOperationResult
            {
                VmName = vmName, Success = true,
                State = GetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new VmOperationResult
            {
                VmName = vmName, Success = false, Error = ex.Message,
                State = TryGetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    private VmOperationResult RestoreSingleVm(
        string vmName, string checkpointName, bool waitForReady, int timeoutSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RunBackendScript($"Restore-VMSnapshot -Name '{PsUtils.PsEscape(checkpointName)}' -VMName '{PsUtils.PsEscape(vmName)}' -Confirm:$false -ErrorAction Stop");
            if (waitForReady)
            {
                var state = GetVmState(vmName);
                if (state != "Running")
                    RunBackendScript($"Start-VM -Name '{PsUtils.PsEscape(vmName)}' -ErrorAction Stop");
                WaitForVmReady(vmName, timeoutSeconds);
            }
            return new VmOperationResult
            {
                VmName = vmName, Success = true,
                State = GetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new VmOperationResult
            {
                VmName = vmName, Success = false, Error = ex.Message,
                State = TryGetVmState(vmName), ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    private void WaitForVmReady(string vmName, int timeoutSeconds)
    {
        var effectiveTimeout = Math.Min(timeoutSeconds, 60);
        var deadline = DateTime.UtcNow.AddSeconds(effectiveTimeout);

        while (DateTime.UtcNow < deadline)
        {
            var state = GetVmState(vmName);
            if (state == "Running") break;
            Thread.Sleep(1000);
        }

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = _backend.Execute($@"
                    (Get-VMIntegrationService -VMName '{PsUtils.PsEscape(vmName)}' |
                     Where-Object {{ $_.Name -eq 'Heartbeat' }}).PrimaryStatusDescription
                ");
                var status = GetOutputLines(result).FirstOrDefault();
                if (status == "OK") break;
            }
            catch { }
            Thread.Sleep(2000);
        }

        // Enable Guest Service Interface.
        try
        {
            _backend.Execute($@"
                $gs = Get-VMIntegrationService -VMName '{PsUtils.PsEscape(vmName)}' |
                      Where-Object {{ $_.Name -eq 'Guest Service Interface' }}
                if ($gs -and -not $gs.Enabled) {{
                    Enable-VMIntegrationService -Name 'Guest Service Interface' -VMName '{PsUtils.PsEscape(vmName)}'
                }}
            ");
        }
        catch { }

        // Verify PowerShell Direct readiness.
        // Heartbeat OK doesn't guarantee PSDirect is available — there's a lag.
        // Check that the PowerShell Direct VM integration service is enabled and responding.
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = _backend.Execute($@"
                    $ps = Get-VMIntegrationService -VMName '{PsUtils.PsEscape(vmName)}' |
                          Where-Object {{ $_.Name -eq 'PowerShell Direct' }}
                    if ($ps -and $ps.Enabled -and $ps.PrimaryStatusDescription -eq 'OK') {{ 'ready' }}
                ");
                var status = GetOutputLines(result).FirstOrDefault();
                if (status == "ready") break;
            }
            catch { }
            Thread.Sleep(2000);
        }

        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException($"VM '{vmName}' did not become ready within {effectiveTimeout} seconds.");
    }

    private string GetVmState(string vmName)
    {
        var result = _backend.Execute($"(Get-VM -Name '{PsUtils.PsEscape(vmName)}' -ErrorAction Stop).State.ToString()");
        return GetOutputLines(result).FirstOrDefault() ?? "Unknown";
    }

    private string TryGetVmState(string vmName)
    {
        try { return GetVmState(vmName); } catch { return "Unknown"; }
    }

    private void RunBackendScript(string script)
    {
        var result = _backend.Execute(script);
        ThrowOnError(result, script);
    }

    private static void ThrowOnError(JsonObject result, string context)
    {
        var status = result["status"]?.GetValue<string>();
        if (status == "error")
        {
            var error = result["error"]?.GetValue<string>();
            var errors = result["errors"]?.AsArray();
            var errorMsg = error ?? (errors != null ? string.Join("; ", errors.Select(e => e?.GetValue<string>())) : "Unknown error");
            if (!string.IsNullOrEmpty(errorMsg))
                throw new InvalidOperationException(errorMsg);
        }
    }

    private static List<string> GetOutputLines(JsonObject result)
    {
        var output = result["output"]?.AsArray();
        if (output == null) return [];
        return output.Select(o => o?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
    }
}

/// <summary>
/// Result of a VM lifecycle operation.
/// </summary>
public sealed class VmOperationResult
{
    public required string VmName { get; init; }
    public bool Success { get; init; }
    public string? State { get; init; }
    public string? Error { get; init; }
    public double ElapsedSeconds { get; init; }
}
