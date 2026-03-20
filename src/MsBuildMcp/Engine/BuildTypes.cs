namespace MsBuildMcp.Engine;

/// <summary>
/// Snapshot of a build's current state.
/// </summary>
public sealed record BuildStatus
{
    public required string BuildId { get; init; }
    public required string Status { get; init; } // running, succeeded, failed, cancelled
    public required long ElapsedMs { get; init; }
    public int? ExitCode { get; init; }
    public int ProjectsStarted { get; init; }
    public int ProjectsCompleted { get; init; }
    public int? ProjectsTotal { get; set; }
    public string? CurrentProject { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required List<BuildDiagnostic> Errors { get; init; }
    public required List<BuildDiagnostic> Warnings { get; init; }
    public required string Command { get; init; }
    public bool IsCompleted { get; init; }
    public List<string>? OutputTail { get; set; }
    public BuildCollision? Collision { get; set; }
}

/// <summary>
/// Included in BuildStatus when the agent requested a build for different targets
/// than what's currently running.
/// </summary>
public sealed class BuildCollision
{
    public required string RequestedSolution { get; init; }
    public string? RequestedTargets { get; init; }
    public required string RunningSolution { get; init; }
    public string? RunningTargets { get; init; }
}
