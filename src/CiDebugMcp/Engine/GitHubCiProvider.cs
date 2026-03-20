// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Tools;

namespace CiDebugMcp.Engine;

/// <summary>
/// GitHub Actions implementation of ICiProvider.
/// Wraps GitHubClient and provides failure discovery, log access, PR context, and artifacts.
/// </summary>
public sealed class GitHubCiProvider : ICiProvider
{
    private readonly GitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;

    public string ProviderName => "github";

    public GitHubCiProvider(GitHubClient client, string owner, string repo)
    {
        _client = client;
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// Create a provider from owner/repo, or parse from a GitHub URL.
    /// </summary>
    public static GitHubCiProvider? FromUrl(GitHubClient client, string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url,
            @"github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)");
        if (!match.Success) return null;
        return new GitHubCiProvider(client, match.Groups["owner"].Value, match.Groups["repo"].Value);
    }

    public async Task<CiFailureReport> GetFailuresAsync(CiQuery query)
    {
        // Resolution priority: BuildId (run_id) > CommitSha > PrNumber > Branch > repo-wide
        if (query.BuildId != null && long.TryParse(query.BuildId, out var runId))
        {
            return await GetFailuresFromRunAsync(runId, query);
        }

        if (query.CommitSha != null)
        {
            return await GetFailuresFromShaAsync(query.CommitSha, query);
        }

        if (query.PrNumber.HasValue)
        {
            var pr = await GetPullRequestAsync(query.PrNumber.Value.ToString());
            if (pr == null) throw new InvalidOperationException($"PR #{query.PrNumber} not found");

            var report = await GetFailuresFromShaAsync(pr.HeadSha, query);
            var files = await GetPullRequestFilesAsync(query.PrNumber.Value.ToString());

            return report with
            {
                Scope = $"PR #{query.PrNumber} (head: {pr.HeadSha[..Math.Min(8, pr.HeadSha.Length)]})",
                ChangedFiles = files.Length > 0 ? files : null,
                BaseBranch = pr.BaseBranch,
            };
        }

        if (query.Branch != null)
        {
            return await GetFailuresFromBranchAsync(query.Branch, query);
        }

        // Repo-wide
        return await GetFailuresRepoWideAsync(query);
    }

    public async Task<ParsedLog> GetJobLogAsync(string jobId)
    {
        var log = await _client.GetJobLog(_owner, _repo, long.Parse(jobId));
        return new ParsedLog { Lines = log.Lines, Steps = log.Steps };
    }

    public async Task<PrInfo?> GetPullRequestAsync(string prIdentifier)
    {
        if (!int.TryParse(prIdentifier, out var prNumber)) return null;
        try
        {
            var pr = await _client.GetPullRequest(_owner, _repo, prNumber);
            return new PrInfo
            {
                HeadSha = pr["head"]?["sha"]?.GetValue<string>() ?? "",
                BaseBranch = pr["base"]?["ref"]?.GetValue<string>() ?? "main",
                Number = prNumber,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ci-debug-mcp: GitHub PR #{prNumber} fetch failed: {ex.Message}");
            return null;
        }
    }

    public async Task<string[]> GetPullRequestFilesAsync(string prIdentifier)
    {
        if (!int.TryParse(prIdentifier, out var prNumber)) return [];
        return await _client.GetPullRequestFiles(_owner, _repo, prNumber);
    }

    public async Task<CiArtifact[]> ListArtifactsAsync(string buildId)
    {
        var json = await _client.GetJsonAsync(
            $"/repos/{_owner}/{_repo}/actions/runs/{buildId}/artifacts?per_page=50");
        var artifacts = json?["artifacts"]?.AsArray() ?? [];

        return artifacts.Select(a => new CiArtifact
        {
            Id = (a?["id"]?.GetValue<long>() ?? 0).ToString(),
            Name = a?["name"]?.GetValue<string>() ?? "",
            SizeBytes = a?["size_in_bytes"]?.GetValue<long>() ?? 0,
            Expired = a?["expired"]?.GetValue<bool>() ?? false,
        }).ToArray();
    }

    public HttpClient CreateDownloadClient() => _client.CreateAuthenticatedClient();

    // ── Private: failure discovery ──────────────────────────────

    private async Task<CiFailureReport> GetFailuresFromRunAsync(long runId, CiQuery query)
    {
        var jobs = await _client.GetJobs(_owner, _repo, runId);
        var summary = new CountAccumulator();
        var failures = new List<CiJobFailure>();
        var cancelled = new List<CiJobInfo>();
        var pending = new List<CiJobInfo>();

        foreach (var job in jobs)
        {
            var name = job?["name"]?.GetValue<string>() ?? "unknown";
            if (query.PipelineFilter != null &&
                !name.Contains(query.PipelineFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            summary.Total++;
            var status = job?["status"]?.GetValue<string>();
            var conclusion = job?["conclusion"]?.GetValue<string>();
            var jobIdVal = job?["id"]?.GetValue<long>() ?? 0;

            if (status != "completed") { summary.Pending++; pending.Add(new CiJobInfo { Name = name, Status = status }); continue; }
            if (conclusion == "success") { summary.Passed++; continue; }
            if (conclusion == "skipped") { summary.Skipped++; continue; }
            if (conclusion == "cancelled") { summary.Cancelled++; cancelled.Add(new CiJobInfo { Name = name, JobId = jobIdVal.ToString() }); continue; }

            summary.Failed++;

            // Find failed step from API
            CiStepInfo? failedStep = null;
            var steps = job?["steps"]?.AsArray();
            if (steps != null)
            {
                var fs = steps.FirstOrDefault(s => s?["conclusion"]?.GetValue<string>() == "failure");
                if (fs != null)
                {
                    failedStep = new CiStepInfo
                    {
                        Number = fs["number"]!.GetValue<int>(),
                        Name = fs["name"]!.GetValue<string>(),
                        Source = "api",
                    };
                }
            }

            var failure = new CiJobFailure
            {
                Name = name,
                JobId = jobIdVal.ToString(),
                BuildId = runId.ToString(),
                Conclusion = conclusion,
                FailedStep = failedStep,
            };

            // Enrich with log errors if detail=errors
            if (query.Detail == "errors")
            {
                failure = await EnrichWithLogErrors(failure, query.MaxErrors);
            }

            failures.Add(failure);
        }

        return new CiFailureReport
        {
            Scope = $"run {runId}",
            Summary = summary.ToSummary(),
            Failures = failures.ToArray(),
            Cancelled = cancelled.ToArray(),
            Pending = pending.ToArray(),
        };
    }

    private async Task<CiFailureReport> GetFailuresFromShaAsync(string sha, CiQuery query)
    {
        var checkRuns = await _client.GetCheckRuns(_owner, _repo, sha);
        var summary = new CountAccumulator();
        var failures = new List<CiJobFailure>();
        var cancelled = new List<CiJobInfo>();
        var pending = new List<CiJobInfo>();

        foreach (var run in checkRuns)
        {
            var name = run?["name"]?.GetValue<string>() ?? "unknown";
            if (query.PipelineFilter != null &&
                !name.Contains(query.PipelineFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            summary.Total++;
            var status = run?["status"]?.GetValue<string>();
            var conclusion = run?["conclusion"]?.GetValue<string>();
            var jobId = run?["id"]?.GetValue<long>() ?? 0;

            if (status != "completed") { summary.Pending++; pending.Add(new CiJobInfo { Name = name, Status = status }); continue; }
            if (conclusion == "success") { summary.Passed++; continue; }
            if (conclusion == "skipped") { summary.Skipped++; continue; }
            if (conclusion == "cancelled") { summary.Cancelled++; cancelled.Add(new CiJobInfo { Name = name, JobId = jobId.ToString() }); continue; }

            summary.Failed++;

            // Extract run_id from html_url
            string? buildId = null;
            var htmlUrl = run?["html_url"]?.GetValue<string>();
            if (htmlUrl != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(htmlUrl, @"actions/runs/(\d+)");
                if (m.Success) buildId = m.Groups[1].Value;
            }

            var failure = new CiJobFailure
            {
                Name = name,
                JobId = jobId.ToString(),
                BuildId = buildId,
                Conclusion = conclusion,
            };

            if (query.Detail == "errors")
            {
                failure = await EnrichWithLogErrors(failure, query.MaxErrors);
            }

            failures.Add(failure);
        }

        return new CiFailureReport
        {
            Scope = $"commit {sha[..Math.Min(8, sha.Length)]}",
            Summary = summary.ToSummary(),
            Failures = failures.ToArray(),
            Cancelled = cancelled.ToArray(),
            Pending = pending.ToArray(),
        };
    }

    private async Task<CiFailureReport> GetFailuresFromBranchAsync(string branch, CiQuery query)
    {
        var path = $"/repos/{_owner}/{_repo}/actions/runs?branch={Uri.EscapeDataString(branch)}" +
                   $"&per_page={Math.Max(query.Count * 3, 20)}&status=completed";
        var runsData = await _client.GetJsonAsync(path);
        var runs = runsData?["workflow_runs"]?.AsArray() ?? [];

        if (runs.Count == 0)
            return EmptyReport($"branch {branch}");

        // Single run — drill into it
        var firstRun = runs[0]!;
        var firstRunId = firstRun["id"]!.GetValue<long>();
        var report = await GetFailuresFromRunAsync(firstRunId, query);
        return report with { Scope = $"branch {branch} (run {firstRunId})" };
    }

    private async Task<CiFailureReport> GetFailuresRepoWideAsync(CiQuery query)
    {
        var path = $"/repos/{_owner}/{_repo}/actions/runs?status=failure&per_page={Math.Max(query.Count, 10)}";
        var runsData = await _client.GetJsonAsync(path);
        var runs = runsData?["workflow_runs"]?.AsArray() ?? [];

        if (runs.Count == 0)
            return EmptyReport("repo-wide failures");

        var firstRunId = runs[0]!["id"]!.GetValue<long>();
        var report = await GetFailuresFromRunAsync(firstRunId, query);
        return report with { Scope = "repo-wide failures" };
    }

    // ── Private: error enrichment ───────────────────────────────

    private async Task<CiJobFailure> EnrichWithLogErrors(CiJobFailure failure, int maxErrors)
    {
        try
        {
            var log = await GetJobLogAsync(failure.JobId);

            // Build scan list: inclusive scanning
            var stepsToScan = new List<ParsedStep>();
            var scanned = new HashSet<int>();

            // Priority 1: known failed step (cross-reference API → log)
            if (failure.FailedStep != null)
            {
                var match = LogTools.ResolveLogStep(
                    failure.FailedStep.Name, failure.FailedStep.Number, log.Steps);
                if (match != null && scanned.Add(match.Number))
                    stepsToScan.Add(match);
            }

            // Priority 2: all steps with ##[error]
            foreach (var s in log.Steps.Where(s => s.Errors.Count > 0))
                if (scanned.Add(s.Number)) stepsToScan.Add(s);

            // Priority 3: longest non-setup steps
            if (stepsToScan.Count == 0)
            {
                foreach (var s in log.Steps
                    .Where(s => (s.EndLine - s.StartLine) > 50 && !LogParser.IsSetupStep(s.Name))
                    .OrderByDescending(s => s.EndLine - s.StartLine)
                    .Take(3))
                    if (scanned.Add(s.Number)) stepsToScan.Add(s);
            }

            // Extract errors
            var allErrors = new List<CiExtractedError>();
            ParsedStep? primaryStep = null;
            string[]? testNames = null;

            foreach (var step in stepsToScan)
            {
                if (allErrors.Count >= maxErrors) break;

                var meaningful = LogParser.ExtractMeaningfulErrors(log.Lines, step, maxErrors);
                if (meaningful.Count == 0) continue;

                primaryStep ??= step;

                foreach (var err in meaningful)
                {
                    if (allErrors.Count >= maxErrors) break;
                    var parsed = LogParser.TryParseError(err);
                    allErrors.Add(new CiExtractedError
                    {
                        Raw = err,
                        Code = parsed?.Code,
                        Message = parsed?.Message,
                        File = parsed?.File,
                        SourceLine = parsed?.Line,
                    });
                }

                // Extract test names
                var names = LogParser.ExtractFailedTestNames(log.Lines, step);
                if (names.Count > 0)
                    testNames = names.ToArray();
            }

            if (allErrors.Count > 0)
            {
                var failureType = ClassifyFailureType(allErrors, primaryStep);
                return failure with
                {
                    Errors = allErrors.ToArray(),
                    FailureType = failureType,
                    Diagnosis = SynthesizeDiagnosis(allErrors),
                    FailedTestNames = testNames,
                };
            }

            // Try test summary extraction
            foreach (var s in log.Steps.Where(s =>
                LogParser.ClassifyStepType(s.Name) == "test" || (s.EndLine - s.StartLine) > 100))
            {
                var summary = LogParser.ExtractTestSummary(log.Lines, s);
                if (summary != null && summary.Failed > 0)
                {
                    var names2 = LogParser.ExtractFailedTestNames(log.Lines, s);
                    return failure with
                    {
                        FailureType = "test",
                        TestSummary = new CiTestSummary
                        {
                            Framework = summary.Framework,
                            Total = summary.Total,
                            Passed = summary.Passed,
                            Failed = summary.Failed,
                            SummaryLine = summary.SummaryLine,
                        },
                        FailedTestNames = names2.Count > 0 ? names2.ToArray() : null,
                        Diagnosis = $"Test failure: {summary.Failed} test(s) failed" +
                            (summary.Total > 0 ? $" of {summary.Total}" : "") +
                            $" ({summary.Framework})",
                    };
                }
            }

            // Nothing found — include available steps
            var availSteps = log.Steps
                .Where(s => !LogParser.IsSetupStep(s.Name) && (s.EndLine - s.StartLine) > 5)
                .Select(s => new CiStepInfo
                {
                    Number = s.Number,
                    Name = s.Name,
                    Lines = s.EndLine - s.StartLine + 1,
                    HasErrors = s.Errors.Count > 0,
                    Type = LogParser.ClassifyStepType(s.Name) is "unknown" ? null : LogParser.ClassifyStepType(s.Name),
                }).ToArray();

            return failure with { AvailableSteps = availSteps };
        }
        catch (Exception ex)
        {
            return failure with { DiagnosticPath = $"Log download failed: {ex.Message}" };
        }
    }

    private static string ClassifyFailureType(List<CiExtractedError> errors, ParsedStep? step)
    {
        if (step != null)
        {
            var stepType = LogParser.ClassifyStepType(step.Name);
            if (stepType is "build" or "test") return stepType;
        }

        if (errors.Any(e => e.Code != null)) return "build";
        if (errors.Any(e => e.Raw.Contains("Mismatch", StringComparison.OrdinalIgnoreCase))) return "dependency";
        if (errors.Any(e => e.Raw.Contains("FAILED:", StringComparison.Ordinal) ||
                            e.Raw.Contains("CRASHED", StringComparison.OrdinalIgnoreCase))) return "test";
        return "unknown";
    }

    private static string SynthesizeDiagnosis(List<CiExtractedError> errors)
    {
        var best = errors.OrderByDescending(e => DiagnosticScore(e.Raw)).First();
        if (best.Code != null && best.File != null)
            return $"Build error: {best.Code} {best.Message} at {Path.GetFileName(best.File)}:{best.SourceLine}";
        if (best.Raw.Contains("FAILED:", StringComparison.Ordinal))
            return $"Test failure: {Truncate(best.Raw, 120)}";
        if (best.Raw.Contains("CRASHED", StringComparison.OrdinalIgnoreCase))
            return "Test crashed during execution";
        if (best.Raw.Contains("Mismatch", StringComparison.OrdinalIgnoreCase))
            return $"DLL dependency mismatch";
        return Truncate(best.Raw, 120);
    }

    private static int DiagnosticScore(string line)
    {
        if (LogParser.TryParseError(line) != null) return 10;
        if (line.Contains("FAILED:", StringComparison.Ordinal)) return 9;
        if (line.Contains("CRASHED", StringComparison.OrdinalIgnoreCase)) return 9;
        if (line.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)) return 7;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;

    private static CiFailureReport EmptyReport(string scope) => new()
    {
        Scope = scope,
        Summary = new CiSummary(),
    };

    private sealed class CountAccumulator
    {
        public int Total, Passed, Failed, Skipped, Cancelled, Pending;
        public CiSummary ToSummary() => new()
        {
            Total = Total, Passed = Passed, Failed = Failed,
            Skipped = Skipped, Cancelled = Cancelled, Pending = Pending,
        };
    }
}
