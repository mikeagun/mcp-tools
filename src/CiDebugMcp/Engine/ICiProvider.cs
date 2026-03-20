// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace CiDebugMcp.Engine;

/// <summary>
/// Provider-agnostic CI/CD interface. Implemented by GitHubCiProvider and AdoCiProvider.
/// </summary>
public interface ICiProvider
{
    /// <summary>The provider name: "github" or "ado".</summary>
    string ProviderName { get; }

    /// <summary>Find failures matching the query. This is the main entry point.</summary>
    Task<CiFailureReport> GetFailuresAsync(CiQuery query);

    /// <summary>Get parsed log for a job/task.</summary>
    Task<ParsedLog> GetJobLogAsync(string jobId);

    /// <summary>Get PR metadata (head SHA, base branch).</summary>
    Task<PrInfo?> GetPullRequestAsync(string prIdentifier);

    /// <summary>Get files changed in a PR.</summary>
    Task<string[]> GetPullRequestFilesAsync(string prIdentifier);

    /// <summary>List artifacts for a build/run.</summary>
    Task<CiArtifact[]> ListArtifactsAsync(string buildId);

    /// <summary>Create an authenticated HTTP client for artifact downloads.</summary>
    HttpClient CreateDownloadClient();
}

/// <summary>
/// How to find CI failures — the union of all supported scopes.
/// </summary>
public sealed class CiQuery
{
    public string? Url { get; init; }
    public int? PrNumber { get; init; }
    public string? BuildId { get; init; }
    public string? CommitSha { get; init; }
    public string? Branch { get; init; }
    public string? PipelineFilter { get; init; }
    public int? DefinitionId { get; init; }
    public int Count { get; init; } = 1;
    public string Detail { get; init; } = "errors";
    public int MaxErrors { get; init; } = 20;
}

/// <summary>
/// Provider-agnostic CI failure report. Returned by ICiProvider.GetFailuresAsync.
/// </summary>
public sealed record CiFailureReport
{
    public required string Scope { get; init; }
    public required CiSummary Summary { get; init; }
    public CiJobFailure[] Failures { get; init; } = [];
    public CiJobInfo[] Cancelled { get; init; } = [];
    public CiJobInfo[] Pending { get; init; } = [];
    public string[]? ChangedFiles { get; init; }
    public string? BaseBranch { get; init; }
}

public sealed class CiSummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Cancelled { get; init; }
    public int Pending { get; init; }
}

public sealed record CiJobFailure
{
    public required string Name { get; init; }
    public required string JobId { get; init; }
    public string? BuildId { get; init; }
    public string? Conclusion { get; init; }
    public string? FailureType { get; init; }
    public string? Diagnosis { get; init; }
    public CiStepInfo? FailedStep { get; init; }
    public CiExtractedError[] Errors { get; init; } = [];
    public string[]? FailedTestNames { get; init; }
    public CiTestSummary? TestSummary { get; init; }
    public string? Hint { get; init; }
    public string? HintSearch { get; init; }
    public string? DiagnosticPath { get; init; }
    public CiStepInfo[]? AvailableSteps { get; init; }
}

public sealed class CiJobInfo
{
    public required string Name { get; init; }
    public string? JobId { get; init; }
    public string? Status { get; init; }
}

public sealed class CiStepInfo
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? Source { get; init; }
    public string? Phase { get; init; }
    public string? Diagnostic { get; init; }
    public int? Lines { get; init; }
    public bool HasErrors { get; init; }
    public string? Type { get; init; }
}

public sealed class CiExtractedError
{
    public required string Raw { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? File { get; init; }
    public int? SourceLine { get; init; }
    public bool InChangedFiles { get; init; }
}

public sealed class CiTestSummary
{
    public required string Framework { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public string? SummaryLine { get; init; }
}

public sealed class CiArtifact
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long SizeBytes { get; init; }
    public bool Expired { get; init; }
}

/// <summary>
/// Parsed log — provider-agnostic container.
/// </summary>
public sealed class ParsedLog
{
    public required string[] Lines { get; init; }
    public required ParsedStep[] Steps { get; init; }
}

/// <summary>
/// PR metadata — provider-agnostic.
/// </summary>
public sealed class PrInfo
{
    public required string HeadSha { get; init; }
    public required string BaseBranch { get; init; }
    public int Number { get; init; }
}

/// <summary>
/// Authentication result with scheme (Basic vs Bearer).
/// </summary>
public sealed record AuthResult(string Scheme, string Token);
