using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MsBuildMcp.Engine;

/// <summary>
/// A running or completed MSBuild build. Streams stdout line-by-line,
/// incrementally parses errors/warnings, and tracks project progress.
/// </summary>
public sealed class BuildJob : IDisposable
{
    private static readonly Regex ProjectDoneRegex = new(
        @"^\s*\d+>Done Building Project ""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ProjectStartRegex = new(
        @"^\s*\d+>Project ""([^""]+)"" on node \d+",
        RegexOptions.Compiled);

    private const int MaxErrors = 500;
    private const int MaxWarnings = 200;

    public string BuildId { get; }
    public string Command { get; }
    public string SolutionPath { get; }
    public string? Targets { get; }
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public OutputBuffer Output { get; }

    private readonly Process _process;
    private readonly Task _readerTask;
    private readonly object _lock = new();

    // Incremental state (guarded by _lock)
    private readonly List<BuildDiagnostic> _errors = [];
    private readonly List<BuildDiagnostic> _warnings = [];
    private int _lineCounter;
    private int _projectsStarted;
    private int _projectsCompleted;
    private string? _currentProject;
    private int _lastReportedErrorCount;
    private bool _completed;
    private int? _exitCode;

    // Event fired on new error/warning or process exit
    private readonly ManualResetEventSlim _newsEvent = new(false);

    public BuildJob(ProcessStartInfo psi, string buildId, string command,
        string solutionPath, string? targets, string retention = "full")
    {
        BuildId = buildId;
        Command = command;
        SolutionPath = solutionPath;
        Targets = targets;
        Output = new OutputBuffer(retention, buildId);
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MSBuild");
        _readerTask = Task.Run(ReadOutputLoop);
    }

    private async Task ReadOutputLoop()
    {
        try
        {
            // Read stderr on a separate task (don't block, just collect)
            var stderrTask = Task.Run(async () =>
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                {
                    // Parse stderr for errors too (linker errors often go here)
                    ProcessLine(line);
                }
            });

            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                ProcessLine(line);
            }

            await stderrTask;
            await _process.WaitForExitAsync();

            lock (_lock)
            {
                _completed = true;
                _exitCode = _process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _completed = true;
                _exitCode = -1;
                if (_errors.Count < MaxErrors)
                    _errors.Add(new BuildDiagnostic
                    {
                        File = "msbuild-mcp",
                        Severity = DiagnosticSeverity.Error,
                        Code = "MCP0001",
                        Message = $"Build process error: {ex.Message}",
                    });
            }
        }
        finally
        {
            _newsEvent.Set();
        }
    }

    private void ProcessLine(string line)
    {
        lock (_lock)
        {
            _lineCounter++;
            Output.AddLine(line);

            // Parse errors/warnings
            var diagnostics = ErrorParser.Parse(line);
            foreach (var d in diagnostics)
            {
                // Create a copy with OutputLine set
                var withLine = new BuildDiagnostic
                {
                    File = d.File,
                    Line = d.Line,
                    Column = d.Column,
                    Severity = d.Severity,
                    Code = d.Code,
                    Message = d.Message,
                    Project = d.Project,
                    RawLine = d.RawLine,
                    OutputLine = _lineCounter,
                };

                if (withLine.Severity == DiagnosticSeverity.Error && _errors.Count < MaxErrors)
                    _errors.Add(withLine);
                else if (withLine.Severity == DiagnosticSeverity.Warning && _warnings.Count < MaxWarnings)
                    _warnings.Add(withLine);
            }

            // Track project progress
            var startMatch = ProjectStartRegex.Match(line);
            if (startMatch.Success)
            {
                _projectsStarted++;
                _currentProject = Path.GetFileNameWithoutExtension(startMatch.Groups[1].Value);
            }

            var doneMatch = ProjectDoneRegex.Match(line);
            if (doneMatch.Success)
                _projectsCompleted++;

            // Signal news only on new ERRORS (not warnings — warnings rarely change the agent's next action)
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                _newsEvent.Set();
        }
    }

    /// <summary>
    /// Wait up to timeoutMs for the build to complete or new errors to appear.
    /// Returns immediately if the build is already done.
    /// </summary>
    public void WaitForNews(int timeoutMs)
    {
        if (timeoutMs <= 0) return;

        lock (_lock)
        {
            if (_completed) return;
            // Track baseline so we only wake for NEW errors
            _lastReportedErrorCount = _errors.Count;
            _newsEvent.Reset();
        }

        // Wait with periodic checks (100ms granularity)
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) break;
            if (_newsEvent.Wait(Math.Min(remaining, 100)))
            {
                lock (_lock)
                {
                    if (_completed) return;
                    if (_errors.Count > _lastReportedErrorCount)
                        return;
                    // Spurious wake — reset and keep waiting
                    _newsEvent.Reset();
                }
            }
        }
    }

    /// <summary>
    /// Snapshot of current build state.
    /// </summary>
    public BuildStatus GetStatus(bool includeOutput = false, int outputLines = 50)
    {
        lock (_lock)
        {
            var elapsed = (long)(DateTime.UtcNow - StartTime).TotalMilliseconds;

            string status;
            if (!_completed) status = "running";
            else if (_exitCode == 0) status = "succeeded";
            else status = "failed";

            var result = new BuildStatus
            {
                BuildId = BuildId,
                Status = status,
                ElapsedMs = elapsed,
                ExitCode = _exitCode,
                ProjectsStarted = _projectsStarted,
                ProjectsCompleted = _projectsCompleted,
                CurrentProject = _currentProject,
                ErrorCount = _errors.Count,
                WarningCount = _warnings.Count,
                Errors = _errors.ToList(),
                Warnings = _warnings.ToList(),
                Command = Command,
                IsCompleted = _completed,
            };

            if (includeOutput)
            {
                var tail = Output.GetTail(outputLines);
                result.OutputTail = tail.Lines;
            }

            return result;
        }
    }

    public void Cancel()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { /* process may have already exited */ }
    }

    public bool IsCompleted
    {
        get { lock (_lock) { return _completed; } }
    }

    public void Dispose()
    {
        Cancel();
        _newsEvent.Dispose();
        _process.Dispose();
        Output.Dispose();
    }
}

/// <summary>
/// Manages build lifecycle: start, poll, cancel. At most one build at a time.
/// </summary>
public sealed class BuildManager : IDisposable
{
    private static readonly Lazy<VsToolchain> _toolchain = new(DiscoverVsToolchain);

    private BuildJob? _currentBuild;
    private readonly Queue<BuildStatus> _recentBuilds = new(); // last 5 completed
    private readonly object _lock = new();
    private int _buildCounter;

    /// <summary>
    /// Start a new build or return the current running build's status.
    /// Waits up to timeoutSeconds for the build to complete or for new errors.
    /// </summary>
    public BuildStatus StartOrPoll(
        string solutionPath,
        string? targets,
        string configuration,
        string platform,
        bool restore,
        string? additionalArgs,
        int timeoutSeconds,
        int? projectsTotal = null,
        string retention = "full")
    {
        var toolchain = _toolchain.Value;
        var normalizedSln = Path.GetFullPath(solutionPath);

        lock (_lock)
        {
            if (_currentBuild != null && !_currentBuild.IsCompleted)
            {
                var sameTargets = string.Equals(_currentBuild.Targets, targets,
                    StringComparison.OrdinalIgnoreCase);
                var sameSln = string.Equals(_currentBuild.SolutionPath, normalizedSln,
                    StringComparison.OrdinalIgnoreCase);

                // Release lock during wait
                var job = _currentBuild;
                Monitor.Exit(_lock);
                try
                {
                    job.WaitForNews(timeoutSeconds * 1000);
                }
                finally
                {
                    Monitor.Enter(_lock);
                }

                var status = _currentBuild!.GetStatus();
                status.ProjectsTotal = projectsTotal;

                // If the running build is for different targets, tell the agent
                if (!sameTargets || !sameSln)
                {
                    status.Collision = new BuildCollision
                    {
                        RequestedSolution = normalizedSln,
                        RequestedTargets = targets,
                        RunningSolution = _currentBuild.SolutionPath,
                        RunningTargets = _currentBuild.Targets,
                    };
                }

                ArchiveIfCompletedLocked();
                return status;
            }
        }

        // Start a new build
        var args = BuildArgs(solutionPath, targets, configuration, platform, restore, additionalArgs);
        var psi = CreateProcessStartInfo(toolchain, solutionPath, args);
        var buildId = $"b-{Interlocked.Increment(ref _buildCounter)}";
        var command = $"{toolchain.MsBuildPath} {string.Join(" ", args)}";

        var newJob = new BuildJob(psi, buildId, command, normalizedSln, targets, retention);

        lock (_lock)
        {
            _currentBuild?.Dispose();
            _currentBuild = newJob;
        }

        // Wait for initial results
        newJob.WaitForNews(timeoutSeconds * 1000);
        var result = newJob.GetStatus();
        result.ProjectsTotal = projectsTotal;
        ArchiveIfCompleted();
        return result;
    }

    /// <summary>
    /// Poll the current or most recent build. Waits up to timeoutSeconds for news.
    /// </summary>
    public BuildStatus? GetStatus(string? buildId, int timeoutSeconds, bool includeOutput, int outputLines)
    {
        lock (_lock)
        {
            if (_currentBuild != null)
            {
                if (buildId == null || _currentBuild.BuildId == buildId)
                {
                    if (!_currentBuild.IsCompleted && timeoutSeconds > 0)
                    {
                        // Release lock during wait
                        var job = _currentBuild;
                        Monitor.Exit(_lock);
                        try
                        {
                            job.WaitForNews(timeoutSeconds * 1000);
                        }
                        finally
                        {
                            Monitor.Enter(_lock);
                        }
                    }
                    var status = _currentBuild.GetStatus(includeOutput, outputLines);
                    ArchiveIfCompletedLocked();
                    return status;
                }
            }

            // Check recent completed builds
            if (buildId != null)
            {
                var found = _recentBuilds.FirstOrDefault(b => b.BuildId == buildId);
                return found;
            }

            // Return most recent completed if no current build
            return _recentBuilds.LastOrDefault();
        }
    }

    /// <summary>
    /// Cancel the current build.
    /// </summary>
    public BuildStatus? Cancel(string? buildId)
    {
        lock (_lock)
        {
            if (_currentBuild == null) return null;
            if (buildId != null && _currentBuild.BuildId != buildId) return null;

            _currentBuild.Cancel();
            // Give it a moment to exit
            Thread.Sleep(200);
            var status = _currentBuild.GetStatus();
            if (!status.IsCompleted)
                status = status with { Status = "cancelled" };
            ArchiveIfCompletedLocked();
            return status;
        }
    }

    public bool HasRunningBuild
    {
        get { lock (_lock) { return _currentBuild != null && !_currentBuild.IsCompleted; } }
    }

    /// <summary>
    /// Get the OutputBuffer for the current or specified build.
    /// Returns null if no build found.
    /// </summary>
    public OutputBuffer? GetBuildOutput(string? buildId = null)
    {
        lock (_lock)
        {
            if (_currentBuild != null &&
                (buildId == null || _currentBuild.BuildId == buildId))
                return _currentBuild.Output;
            return null;
        }
    }

    /// <summary>
    /// Get the build ID of the current or most recent build.
    /// </summary>
    public string? GetCurrentBuildId()
    {
        lock (_lock) { return _currentBuild?.BuildId; }
    }

    /// <summary>
    /// Get the solution path of the current or most recent build.
    /// Used by the policy system to scope cancel_build approvals by solution.
    /// </summary>
    public string? GetCurrentBuildSolution(string? buildId = null)
    {
        lock (_lock)
        {
            if (_currentBuild != null &&
                (buildId == null || _currentBuild.BuildId == buildId))
                return _currentBuild.SolutionPath;
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _currentBuild?.Dispose();
            _currentBuild = null;
        }
    }

    private void ArchiveIfCompleted()
    {
        lock (_lock) { ArchiveIfCompletedLocked(); }
    }

    private void ArchiveIfCompletedLocked()
    {
        if (_currentBuild != null && _currentBuild.IsCompleted)
        {
            _recentBuilds.Enqueue(_currentBuild.GetStatus());
            while (_recentBuilds.Count > 5) _recentBuilds.Dequeue();
        }
    }

    internal static List<string> BuildArgs(string solutionPath, string? targets,
        string configuration, string platform, bool restore, string? additionalArgs)
    {
        var args = new List<string>
        {
            $"\"{solutionPath}\"",
            "/m",
            "/nologo",
            $"/p:Configuration=\"{configuration}\"",
            $"/p:Platform=\"{platform}\"",
        };

        if (!string.IsNullOrEmpty(targets))
            args.Add($"/t:{targets}");
        if (restore)
            args.Add("-Restore");
        if (!string.IsNullOrEmpty(additionalArgs))
            args.Add(additionalArgs);
        args.Add("/v:minimal");

        return args;
    }

    private static ProcessStartInfo CreateProcessStartInfo(VsToolchain toolchain,
        string solutionPath, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolchain.MsBuildPath,
            Arguments = string.Join(" ", args),
            WorkingDirectory = Path.GetDirectoryName(solutionPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var (key, value) in toolchain.Environment)
            psi.Environment[key] = value;

        // Strip dotnet host vars that poison VS MSBuild's NuGet SDK resolver.
        foreach (var key in new[] { "MSBUILD_EXE_PATH", "MSBuildSDKsPath",
            "MSBuildExtensionsPath", "DOTNET_HOST_PATH" })
        {
            psi.Environment.Remove(key);
        }

        return psi;
    }

    private static VsToolchain DiscoverVsToolchain()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
            throw new FileNotFoundException(
                "vswhere.exe not found — is Visual Studio installed?", vswhere);

        var psi = new ProcessStartInfo
        {
            FileName = vswhere,
            Arguments = "-latest -requires Microsoft.Component.MSBuild -property installationPath",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var vsProc = Process.Start(psi)!;
        var vsInstallPath = vsProc.StandardOutput.ReadLine()?.Trim();
        vsProc.WaitForExit();
        if (string.IsNullOrEmpty(vsInstallPath))
            throw new FileNotFoundException("VS installation not found via vswhere");

        Console.Error.WriteLine($"msbuild-mcp: VS install: {vsInstallPath}");

        var msbuildPath = Path.Combine(vsInstallPath, @"MSBuild\Current\Bin\amd64\MSBuild.exe");
        if (!File.Exists(msbuildPath))
        {
            msbuildPath = Path.Combine(vsInstallPath, @"MSBuild\Current\Bin\MSBuild.exe");
            if (!File.Exists(msbuildPath))
                throw new FileNotFoundException($"MSBuild.exe not found under {vsInstallPath}");
        }

        Console.Error.WriteLine($"msbuild-mcp: MSBuild: {msbuildPath}");

        var vcvarsall = Path.Combine(vsInstallPath, @"VC\Auxiliary\Build\vcvarsall.bat");
        if (!File.Exists(vcvarsall))
            throw new FileNotFoundException(
                "vcvarsall.bat not found — VC++ build tools may not be installed", vcvarsall);

        psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{vcvarsall}\" amd64 && set\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var envProc = Process.Start(psi)!;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (envProc.StandardOutput.ReadLine() is { } line)
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                env[line[..eq]] = line[(eq + 1)..];
        }
        envProc.WaitForExit();

        Console.Error.WriteLine($"msbuild-mcp: captured {env.Count} environment variables from vcvarsall");

        return new VsToolchain(msbuildPath, env);
    }

    private sealed record VsToolchain(string MsBuildPath, Dictionary<string, string> Environment);
}

