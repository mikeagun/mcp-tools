// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CiDebugMcp.Engine;

/// <summary>
/// Azure DevOps implementation of ICiProvider.
/// Supports PAT, SYSTEM_ACCESSTOKEN, Azure CLI, and Git Credential Manager auth.
/// </summary>
public sealed class AdoCiProvider : ICiProvider
{
    private readonly HttpClient _http;
    private readonly LogCache _cache;
    private readonly string _orgUrl;
    private readonly string _project;
    private readonly string? _originalHost;
    private readonly object _authLock = new();
    private volatile bool _authResolved;
    private readonly Func<AuthResult?> _authResolver;
    private readonly Func<int, string, Task<ParsedLog?>> _logFetcher;

    /// <summary>
    /// Default per-request HTTP timeout for the ADO REST API client.
    /// Matches GitHubClient's posture — short enough to surface a managed
    /// timeout before the MCP client gives up, long enough for large
    /// timeline/log responses.
    /// </summary>
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(50);

    /// <summary>
    /// Maximum number of ADO log downloads to run concurrently when assembling
    /// a failure report with detail=errors. Bounded to avoid hammering the
    /// ADO API but high enough to remove the sequential-download wall-clock.
    /// </summary>
    private const int LogFetchConcurrency = 4;

    public string ProviderName => "ado";

    /// <param name="orgUrl">e.g. "https://dev.azure.com/myorg"</param>
    /// <param name="project">e.g. "myproject"</param>
    /// <param name="originalHost">Original hostname for GCM lookup (e.g. "myorg.visualstudio.com")</param>
    public AdoCiProvider(string orgUrl, string project, LogCache cache, string? originalHost = null)
        : this(orgUrl, project, cache, originalHost, authResolver: null, logFetcher: null)
    {
    }

    /// <summary>
    /// Test seam: lets tests inject deterministic <paramref name="authResolver"/>
    /// and <paramref name="logFetcher"/> delegates instead of routing through
    /// the real credential-resolution chain (env vars, Azure CLI subprocess,
    /// Git Credential Manager subprocess) or the real per-log-id HTTP fetch
    /// via <see cref="GetJobLogAsync"/>. Production callers use the public
    /// constructor, which defaults to <see cref="ResolveAdoAuth"/> for auth
    /// and to <see cref="GetJobLogAsync"/> with the standard
    /// <c>"{buildId}:{logId}"</c> jobId encoding for log fetches.
    /// </summary>
    internal AdoCiProvider(string orgUrl, string project, LogCache cache, string? originalHost,
        Func<AuthResult?>? authResolver = null,
        Func<int, string, Task<ParsedLog?>>? logFetcher = null)
    {
        _orgUrl = orgUrl.TrimEnd('/');
        _project = project;
        _cache = cache;
        _originalHost = originalHost;
        _authResolver = authResolver ?? ResolveAdoAuth;
        _logFetcher = logFetcher ?? (async (buildId, logId) =>
            await GetJobLogAsync($"{buildId}:{logId}").ConfigureAwait(false));

        _http = new HttpClient
        {
            BaseAddress = new Uri($"{_orgUrl}/{_project}/_apis/"),
            Timeout = HttpRequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ci-debug-mcp", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Test seam: read-only view of the current Authorization header so tests
    /// can observe what <see cref="EnsureAuth"/> set without depending on
    /// reflection or making the underlying <see cref="HttpClient"/> public.
    /// </summary>
    internal AuthenticationHeaderValue? AuthorizationHeader
        => _http.DefaultRequestHeaders.Authorization;

    /// <summary>
    /// Start auth resolution in the background so the first API call doesn't pay
    /// the cost of az CLI / GCM subprocess invocation. Non-blocking — returns
    /// immediately, auth resolves on a thread pool thread. Safe to call multiple
    /// times; subsequent calls are no-ops once auth has been resolved.
    /// </summary>
    public void WarmAuth() => Task.Run(EnsureAuth);

    /// <summary>
    /// Parse an ADO URL extracting org, project, and optional buildId/PR number.
    /// Supports: dev.azure.com/{org}/{project}/..., {org}.visualstudio.com/{project}/...,
    /// and git remote URLs like {org}.visualstudio.com/{project}/_git/{repo}.
    /// </summary>
    public static AdoParsedUrl? ParseUrl(string url)
    {
        string? org = null, project = null;
        string? originalHost = null;

        // dev.azure.com/{org}/{project}/...
        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)");
        if (m.Success)
        {
            org = m.Groups["org"].Value;
            project = m.Groups["project"].Value;
            // dev.azure.com URLs don't need host remapping for GCM
        }
        else
        {
            // {org}.visualstudio.com/{project}/... (legacy)
            m = System.Text.RegularExpressions.Regex.Match(url,
                @"(?<org>[^/.]+)\.visualstudio\.com/(?<project>[^/]+)");
            if (m.Success)
            {
                org = m.Groups["org"].Value;
                project = m.Groups["project"].Value;
                // Preserve original host for GCM credential lookup
                originalHost = $"{org}.visualstudio.com";
            }
        }

        if (org == null || project == null) return null;

        var orgUrl = $"https://dev.azure.com/{org}";

        // Extract buildId from query string (?buildId=123)
        string? buildId = null;
        var buildMatch = System.Text.RegularExpressions.Regex.Match(url, @"[?&]buildId=(\d+)");
        if (buildMatch.Success)
            buildId = buildMatch.Groups[1].Value;

        // Extract PR number from _git/.../pullrequest/123
        int? prNumber = null;
        var prMatch = System.Text.RegularExpressions.Regex.Match(url, @"pullrequest/(\d+)");
        if (prMatch.Success)
            prNumber = int.Parse(prMatch.Groups[1].Value);

        return new AdoParsedUrl(orgUrl, project, buildId, prNumber, originalHost);
    }

    /// <summary>
    /// Try to create a provider from an ADO URL.
    /// Supports: https://dev.azure.com/{org}/{project}/_build/results?buildId=123
    /// </summary>
    public static AdoCiProvider? FromUrl(string url, LogCache cache)
    {
        var parsed = ParseUrl(url);
        if (parsed == null) return null;
        return new AdoCiProvider(parsed.Value.OrgUrl, parsed.Value.Project, cache, parsed.Value.OriginalHost);
    }

    // ── ICiProvider ─────────────────────────────────────────────

    public async Task<CiFailureReport> GetFailuresAsync(CiQuery query)
    {
        if (query.BuildId != null)
        {
            return await GetFailuresFromBuildAsync(int.Parse(query.BuildId), query);
        }

        if (query.PrNumber.HasValue)
        {
            // Find builds for this PR
            var builds = await GetJsonAsync(
                $"build/builds?reasonFilter=pullRequest&$top=1" +
                $"&repositoryType=TfsGit&queryOrder=finishTimeDescending" +
                $"&api-version=7.1");
            // Filter by PR number from source branch
            var allBuilds = builds?["value"]?.AsArray() ?? [];
            JsonNode? targetBuild = null;
            foreach (var b in allBuilds)
            {
                var sourceBranch = b?["sourceBranch"]?.GetValue<string>() ?? "";
                if (sourceBranch.Contains($"pull/{query.PrNumber}"))
                {
                    targetBuild = b;
                    break;
                }
            }

            if (targetBuild != null)
            {
                var buildId = targetBuild["id"]!.GetValue<int>();
                var report = await GetFailuresFromBuildAsync(buildId, query);

                var pr = await GetPullRequestAsync(query.PrNumber.Value.ToString());
                var files = await GetPullRequestFilesAsync(query.PrNumber.Value.ToString());

                return report with
                {
                    Scope = $"PR #{query.PrNumber} (build {buildId})",
                    ChangedFiles = files.Length > 0 ? files : null,
                    BaseBranch = pr?.BaseBranch,
                };
            }

            return new CiFailureReport
            {
                Scope = $"PR #{query.PrNumber}",
                Summary = new CiSummary(),
            };
        }

        if (query.Branch != null)
        {
            var path = $"build/builds?branchName=refs/heads/{Uri.EscapeDataString(query.Branch)}" +
                $"&$top={Math.Max(query.Count, 1)}&queryOrder=finishTimeDescending&api-version=7.1";
            if (query.DefinitionId.HasValue)
                path += $"&definitions={query.DefinitionId.Value}";
            var builds = await GetJsonAsync(path);
            var buildList = builds?["value"]?.AsArray() ?? [];

            if (query.Count > 1 && buildList.Count > 1)
            {
                return await BuildMultiRunReport(buildList, query, $"branch {query.Branch}");
            }

            var first = buildList.FirstOrDefault();
            if (first != null)
            {
                var buildId = first["id"]!.GetValue<int>();
                var report = await GetFailuresFromBuildAsync(buildId, query);
                return report with { Scope = $"branch {query.Branch} (build {buildId})" };
            }
            return new CiFailureReport { Scope = $"branch {query.Branch}", Summary = new CiSummary() };
        }

        // Repo-wide: find recent failed builds
        {
            var path = $"build/builds?resultFilter=failed&$top={Math.Max(query.Count, 5)}" +
                $"&queryOrder=finishTimeDescending&api-version=7.1";
            if (query.DefinitionId.HasValue)
                path += $"&definitions={query.DefinitionId.Value}";
            var failedBuilds = await GetJsonAsync(path);
            var failedList = failedBuilds?["value"]?.AsArray() ?? [];

            if (query.Count > 1 && failedList.Count > 1)
            {
                return await BuildMultiRunReport(failedList, query, "repo-wide failures");
            }

            if (failedList.Count > 0)
            {
                var buildId = failedList[0]!["id"]!.GetValue<int>();
                return await GetFailuresFromBuildAsync(buildId, query);
            }
        }

        return new CiFailureReport { Scope = "repo-wide", Summary = new CiSummary() };
    }

    public async Task<ParsedLog> GetJobLogAsync(string jobId)
    {
        // jobId format: "buildId:logId"
        var parts = jobId.Split(':');
        if (parts.Length != 2)
            throw new ArgumentException("ADO job ID should be 'buildId:logId'");

        var buildId = parts[0];
        var logId = parts[1];
        var cacheKey = long.Parse(buildId) * 10000 + long.Parse(logId);

        var cached = _cache.Get(cacheKey);
        if (cached != null)
            return new ParsedLog { Lines = cached.Lines, Steps = cached.Steps };

        EnsureAuth();
        var text = await _http.GetStringAsync($"build/builds/{buildId}/logs/{logId}?api-version=7.1");
        GuardHtmlResponse(text);

        // ADO log API returns JSON: {"count":N,"value":["line1","line2",...]}
        // Deserialize the value array to get individual lines.
        string[] lines;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(text);
                var valueArr = json?["value"]?.AsArray();
                if (valueArr != null)
                {
                    lines = valueArr.Select(v => v?.GetValue<string>() ?? "").ToArray();
                }
                else
                {
                    lines = text.Split('\n');
                }
            }
            catch
            {
                // Not valid JSON — treat as raw text
                lines = text.Split('\n');
            }
        }
        else
        {
            lines = text.Split('\n');
        }

        var steps = ParseAdoSteps(lines);

        _cache.Set(cacheKey, new LogCache.CachedLog
        {
            RawText = text,
            Lines = lines,
            Steps = steps,
        });

        return new ParsedLog { Lines = lines, Steps = steps };
    }

    public async Task<PrInfo?> GetPullRequestAsync(string prIdentifier)
    {
        if (!int.TryParse(prIdentifier, out var prId)) return null;
        try
        {
            // Use Git PR API
            var json = await GetJsonAsync(
                $"git/repositories/{_project}/pullrequests/{prId}?api-version=7.1");
            return new PrInfo
            {
                HeadSha = json?["lastMergeSourceCommit"]?["commitId"]?.GetValue<string>() ?? "",
                BaseBranch = (json?["targetRefName"]?.GetValue<string>() ?? "refs/heads/main")
                    .Replace("refs/heads/", ""),
                Number = prId,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ci-debug-mcp: ADO PR #{prId} fetch failed: {ex.Message}");
            return null;
        }
    }

    public async Task<string[]> GetPullRequestFilesAsync(string prIdentifier)
    {
        if (!int.TryParse(prIdentifier, out var prId)) return [];
        try
        {
            // Get iterations, then changes from last iteration
            var iterations = await GetJsonAsync(
                $"git/repositories/{_project}/pullrequests/{prId}/iterations?api-version=7.1");
            var iterArr = iterations?["value"]?.AsArray();
            if (iterArr == null || iterArr.Count == 0) return [];

            var lastIterId = iterArr[^1]!["id"]!.GetValue<int>();
            var changes = await GetJsonAsync(
                $"git/repositories/{_project}/pullrequests/{prId}/iterations/{lastIterId}/changes?api-version=7.1");
            var changeEntries = changes?["changeEntries"]?.AsArray() ?? [];

            return changeEntries
                .Select(c => c?["item"]?["path"]?.GetValue<string>()?.TrimStart('/') ?? "")
                .Where(p => p.Length > 0)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ci-debug-mcp: ADO PR files fetch failed: {ex.Message}");
            return [];
        }
    }

    public async Task<CiArtifact[]> ListArtifactsAsync(string buildId)
    {
        var json = await GetJsonAsync($"build/builds/{buildId}/artifacts?api-version=7.1");
        var artifacts = json?["value"]?.AsArray() ?? [];

        return artifacts.Select(a =>
        {
            long size = 0;
            var sizeStr = a?["resource"]?["properties"]?["artifactsize"]?.GetValue<string>();
            if (sizeStr != null)
                long.TryParse(sizeStr, out size);

            return new CiArtifact
            {
                Id = a?["id"]?.GetValue<int>().ToString() ?? "0",
                Name = a?["name"]?.GetValue<string>() ?? "",
                SizeBytes = size,
                DownloadUrl = a?["resource"]?["downloadUrl"]?.GetValue<string>(),
            };
        }).ToArray();
    }

    public HttpClient CreateDownloadClient()
    {
        EnsureAuth();
        var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ci-debug-mcp", "0.1"));
        if (_http.DefaultRequestHeaders.Authorization != null)
            client.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
        return client;
    }

    // ── Private: build failure discovery ────────────────────────

    private async Task<CiFailureReport> GetFailuresFromBuildAsync(int buildId, CiQuery query)
    {
        // Get timeline — contains all stages, jobs, and tasks
        var timeline = await GetJsonAsync($"build/builds/{buildId}/timeline?api-version=7.1");
        var records = timeline?["records"]?.AsArray() ?? [];

        var summary = new CountAccumulator();
        var failures = new List<CiJobFailure>();
        var cancelled = new List<CiJobInfo>();

        // ── Pass 1 ─── classify all jobs, collect failed-job work items in
        // timeline order. No I/O yet — this is pure JSON inspection.
        var failedJobs = new List<FailedJobItem>();
        foreach (var rec in records)
        {
            var type = rec?["type"]?.GetValue<string>();
            if (type != "Job") continue; // Only look at job-level records

            var name = rec?["name"]?.GetValue<string>() ?? "unknown";
            if (query.PipelineFilter != null &&
                !name.Contains(query.PipelineFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            summary.Total++;
            var result = rec?["result"]?.GetValue<string>();
            var state = rec?["state"]?.GetValue<string>();

            if (state != "completed") { summary.Pending++; continue; }
            if (result == "succeeded") { summary.Passed++; continue; }
            if (result == "skipped") { summary.Skipped++; continue; }
            if (result == "canceled") { summary.Cancelled++; cancelled.Add(new CiJobInfo { Name = name }); continue; }

            summary.Failed++;

            var jobRecordId = rec?["id"]?.GetValue<string>();
            var (failedTask, failedLogId) = FindFailedTask(records, jobRecordId);

            failedJobs.Add(new FailedJobItem(
                Name: name,
                JobRecordId: jobRecordId,
                Result: result,
                FailedTask: failedTask,
                FailedLogId: failedLogId));
        }

        // ── Pass 2 ─── parallel-fetch logs for detail=errors so per-job log
        // downloads run concurrently (bounded) instead of sequentially. This
        // is the main lever for "large first request" timeouts on builds with
        // multiple failed jobs.
        Dictionary<string, ParsedLog>? logsByLogId = null;
        if (query.Detail == "errors")
        {
            logsByLogId = await FetchLogsInParallel(buildId, failedJobs);
        }

        // ── Pass 3 ─── assemble CiJobFailure records in timeline order using
        // the prefetched logs (if any) and detect child/triggered builds.
        foreach (var item in failedJobs)
        {
            var failure = new CiJobFailure
            {
                Name = item.Name,
                JobId = item.FailedLogId != null ? $"{buildId}:{item.FailedLogId}" : buildId.ToString(),
                BuildId = buildId.ToString(),
                Conclusion = item.Result,
                FailedStep = item.FailedTask,
                FailureType = item.FailedTask != null
                    ? LogParser.ClassifyStepType(item.FailedTask.Name) is "unknown" ? null : LogParser.ClassifyStepType(item.FailedTask.Name)
                    : null,
            };

            if (query.Detail == "summary")
            {
                failures.Add(failure);
                continue;
            }

            if (query.Detail == "errors"
                && item.FailedLogId != null
                && logsByLogId != null
                && logsByLogId.TryGetValue(item.FailedLogId, out var log))
            {
                var allErrors = new List<CiExtractedError>();
                foreach (var step in log.Steps.Length > 0 ? log.Steps : [new ParsedStep { Number = 1, Name = item.Name, StartLine = 0, EndLine = log.Lines.Length - 1 }])
                {
                    var meaningful = LogParser.ExtractMeaningfulErrors(log.Lines, step, query.MaxErrors);
                    foreach (var err in meaningful)
                    {
                        if (allErrors.Count >= query.MaxErrors) break;
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
                }

                if (allErrors.Count > 0)
                {
                    failure = failure with
                    {
                        Errors = allErrors.ToArray(),
                        FailureType = ClassifyAdoFailureType(allErrors),
                        Diagnosis = SynthesizeAdoDiagnosis(allErrors),
                    };
                }
            }

            // Detect child/triggered builds from the timeline
            var childBuildIds = DetectChildBuilds(records, item.JobRecordId);
            if (childBuildIds.Count > 0)
            {
                failure = failure with
                {
                    Hint = $"Trigger build — child build(s) failed: {string.Join(", ", childBuildIds)}",
                    HintSearch = $"get_ci_failures(url='{_orgUrl}/{_project}/_build/results?buildId={childBuildIds[0]}')",
                };
            }

            failures.Add(failure);
        }

        return new CiFailureReport
        {
            Scope = $"build {buildId}",
            Summary = summary.ToSummary(),
            Failures = failures.ToArray(),
            Cancelled = cancelled.ToArray(),
        };
    }

    /// <summary>
    /// Per-failed-job descriptor produced by the first pass of
    /// <see cref="GetFailuresFromBuildAsync"/>. Carries the timeline-derived
    /// information needed to assemble a <see cref="CiJobFailure"/> after
    /// per-job log downloads complete.
    /// </summary>
    internal sealed record FailedJobItem(
        string Name,
        string? JobRecordId,
        string? Result,
        CiStepInfo? FailedTask,
        string? FailedLogId);

    /// <summary>
    /// Walk the timeline records once to find the first failed Task under the
    /// given job and return both the task descriptor and its log id. No I/O.
    /// </summary>
    private static (CiStepInfo? Task, string? LogId) FindFailedTask(
        System.Text.Json.Nodes.JsonArray records, string? jobRecordId)
    {
        foreach (var task in records)
        {
            if (task?["type"]?.GetValue<string>() != "Task") continue;
            if (task?["parentId"]?.GetValue<string>() != jobRecordId) continue;
            if (task?["result"]?.GetValue<string>() != "failed") continue;

            var logRef = task["log"];
            var failedLogId = logRef?["id"]?.GetValue<int>().ToString();
            var failedTask = new CiStepInfo
            {
                Number = task["order"]?.GetValue<int>() ?? 0,
                Name = task["name"]?.GetValue<string>() ?? "unknown",
                Source = "timeline",
            };
            return (failedTask, failedLogId);
        }
        return (null, null);
    }

    /// <summary>
    /// Fetch the per-job logs for every failed job in parallel with bounded
    /// concurrency (<see cref="LogFetchConcurrency"/>). Each log id appears
    /// at most once in the result regardless of how many jobs reference it.
    /// Individual fetch failures are logged and skipped — the surrounding
    /// assembly step then treats the missing log as "no errors extracted",
    /// preserving the prior single-job-fails-don't-fail-the-report semantics.
    /// </summary>
    internal async Task<Dictionary<string, ParsedLog>> FetchLogsInParallel(
        int buildId, IReadOnlyList<FailedJobItem> failedJobs)
    {
        // De-duplicate by log id so we don't fetch the same log twice.
        var logIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in failedJobs)
        {
            if (item.FailedLogId != null) logIds.Add(item.FailedLogId);
        }
        if (logIds.Count == 0) return [];

        using var sem = new SemaphoreSlim(LogFetchConcurrency);
        var fetchTasks = logIds.Select(async logId =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    var log = await _logFetcher(buildId, logId).ConfigureAwait(false);
                    return (logId, log);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"ci-debug-mcp: ADO log retrieval failed for build {buildId} log {logId}: {ex.Message}");
                    return (logId, (ParsedLog?)null);
                }
            }
            finally
            {
                sem.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        var dict = new Dictionary<string, ParsedLog>(StringComparer.Ordinal);
        foreach (var (logId, log) in results)
        {
            if (log != null) dict[logId] = log;
        }
        return dict;
    }

    /// <summary>
    /// Detect child/triggered build IDs from timeline task records.
    /// Scans issue messages and result text for buildId references.
    /// </summary>
    private static List<string> DetectChildBuilds(
        System.Text.Json.Nodes.JsonArray records, string? parentJobId)
    {
        var childIds = new List<string>();
        var buildIdRegex = new System.Text.RegularExpressions.Regex(@"buildId[=:](\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var rec in records)
        {
            if (rec?["type"]?.GetValue<string>() != "Task") continue;
            if (parentJobId != null && rec?["parentId"]?.GetValue<string>() != parentJobId) continue;

            // Check issues array for build references
            var issues = rec?["issues"]?.AsArray();
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    var msg = issue?["message"]?.GetValue<string>() ?? "";
                    var m = buildIdRegex.Match(msg);
                    if (m.Success && !childIds.Contains(m.Groups[1].Value))
                        childIds.Add(m.Groups[1].Value);
                }
            }

            // Check result text (often contains "Build NNNN was not successful")
            var resultMsg = rec?["resultCode"]?.GetValue<string>() ?? "";
            var m2 = System.Text.RegularExpressions.Regex.Match(resultMsg, @"Build\s+(\d+)");
            if (m2.Success && !childIds.Contains(m2.Groups[1].Value))
                childIds.Add(m2.Groups[1].Value);
        }
        return childIds;
    }

    /// <summary>
    /// Build a multi-run flaky detection report from a list of ADO builds.
    /// </summary>
    private async Task<CiFailureReport> BuildMultiRunReport(
        System.Text.Json.Nodes.JsonArray builds, CiQuery query, string scope)
    {
        int totalFailed = 0, totalPassed = 0;
        var allFailures = new List<CiJobFailure>();

        foreach (var build in builds.Take(query.Count))
        {
            var bId = build?["id"]?.GetValue<int>() ?? 0;
            var bResult = build?["result"]?.GetValue<string>() ?? "";
            var bName = build?["definition"]?["name"]?.GetValue<string>() ?? "";
            var bDate = build?["finishTime"]?.GetValue<string>() ?? "";

            if (bResult == "succeeded") { totalPassed++; continue; }
            if (bResult is "failed" or "partiallySucceeded")
            {
                totalFailed++;
                var summaryQuery = new CiQuery
                {
                    Detail = "summary",
                    Count = 1,
                    PipelineFilter = query.PipelineFilter,
                    MaxErrors = query.MaxErrors,
                };
                var report = await GetFailuresFromBuildAsync(bId, summaryQuery);
                foreach (var f in report.Failures)
                {
                    allFailures.Add(f with
                    {
                        Hint = $"Build {bId} ({(bDate.Length >= 10 ? bDate[..10] : bDate)})",
                    });
                }
            }
        }

        var total = totalFailed + totalPassed;
        return new CiFailureReport
        {
            Scope = $"{scope} (last {total} builds, {totalFailed} failed)",
            Summary = new CiSummary
            {
                Total = total,
                Passed = totalPassed,
                Failed = totalFailed,
            },
            Failures = allFailures.ToArray(),
        };
    }

    private static string ClassifyAdoFailureType(List<CiExtractedError> errors)
    {
        if (errors.Any(e => e.Code != null)) return "build";
        if (errors.Any(e => e.Raw.Contains("FAILED:", StringComparison.Ordinal) ||
                            e.Raw.Contains("REQUIRE(", StringComparison.Ordinal) ||
                            e.Raw.Contains("REQUIRE_", StringComparison.Ordinal) ||
                            e.Raw.Contains("CHECK(", StringComparison.Ordinal) ||
                            e.Raw.Contains("CRASHED", StringComparison.OrdinalIgnoreCase) ||
                            e.Raw.Contains("test cases:", StringComparison.OrdinalIgnoreCase) ||
                            e.Raw.Contains("assertions:", StringComparison.OrdinalIgnoreCase) ||
                            e.Raw.Contains("[ FAILED ]", StringComparison.Ordinal)))
            return "test";
        if (errors.Any(e => e.Raw.Contains("Mismatch", StringComparison.OrdinalIgnoreCase) ||
                            e.Raw.Contains("Dependencies", StringComparison.OrdinalIgnoreCase)))
            return "dependency";
        return "unknown";
    }

    /// <summary>
    /// Synthesize a one-line diagnosis from ADO errors, picking the highest-value line.
    /// Uses the same ranking strategy as the GitHub path.
    /// </summary>
    private static string SynthesizeAdoDiagnosis(List<CiExtractedError> errors)
    {
        var best = errors.OrderByDescending(e => AdoDiagnosticScore(e.Raw)).First();

        var parsed = LogParser.TryParseError(best.Raw);
        if (parsed != null)
            return $"Build error: {parsed.Code} {parsed.Message} at {Path.GetFileName(parsed.File ?? "")}:{parsed.Line}";

        if (best.Raw.Contains("FAILED:", StringComparison.Ordinal) ||
            best.Raw.Contains("REQUIRE(", StringComparison.Ordinal))
            return $"Test failure: {Truncate(best.Raw, 120)}";

        if (best.Raw.Contains("CRASHED", StringComparison.OrdinalIgnoreCase))
            return "Test crashed during execution";

        if (best.Raw.Contains("Mismatch", StringComparison.OrdinalIgnoreCase))
            return "DLL dependency mismatch";

        return Truncate(best.Raw, 120);
    }

    private static int AdoDiagnosticScore(string line)
    {
        if (LogParser.TryParseError(line) != null) return 10;
        if (line.Contains("FAILED:", StringComparison.Ordinal)) return 9;
        if (line.Contains("REQUIRE(", StringComparison.Ordinal)) return 9;
        if (line.Contains("CRASHED", StringComparison.OrdinalIgnoreCase)) return 9;
        if (line.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)) return 7;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return 3;
        if (line.Contains("fail", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;

    // ── Private: ADO log parsing ────────────────────────────────

    /// <summary>
    /// Parse ADO task log into steps. ADO uses "##[section]Starting: TaskName" markers.
    /// Falls back to treating the entire log as a single step if no markers found.
    /// </summary>
    private static ParsedStep[] ParseAdoSteps(string[] lines)
    {
        var steps = new List<ParsedStep>();
        ParsedStep? current = null;
        int stepNumber = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var stripped = LogParser.StripTimestamp(lines[i]);

            // ADO section markers: "##[section]Starting: TaskName"
            if (stripped.StartsWith("##[section]Starting:"))
            {
                if (current != null) { current.EndLine = i - 1; steps.Add(current); }
                stepNumber++;
                var name = stripped["##[section]Starting:".Length..].Trim();
                current = new ParsedStep { Number = stepNumber, Name = name, StartLine = i };
            }
            else if (stripped.StartsWith("##[section]Finishing:"))
            {
                // Close current step
            }
            else if (current != null)
            {
                if (stripped.StartsWith("##[error]"))
                    current.Errors.Add(stripped["##[error]".Length..]);
                else if (stripped.StartsWith("##[warning]"))
                    current.Warnings.Add(stripped["##[warning]".Length..]);
            }
        }

        if (current != null) { current.EndLine = lines.Length - 1; steps.Add(current); }

        return steps.ToArray();
    }

    // ── Private: auth ───────────────────────────────────────────

    internal void EnsureAuth()
    {
        if (_authResolved) return;

        // Lock so concurrent callers (e.g. WarmAuth's background task and the
        // first real API call) cannot both proceed past the gate and race on
        // setting _authResolved before DefaultRequestHeaders.Authorization is
        // populated. Double-check inside the lock for the common fast path.
        lock (_authLock)
        {
            if (_authResolved) return;

            var auth = _authResolver();
            if (auth != null)
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(auth.Scheme, auth.Token);
                Console.Error.WriteLine($"ci-debug-mcp: ADO authenticated via {auth.Scheme}");
            }
            else
            {
                Console.Error.WriteLine("ci-debug-mcp: no ADO authentication found. " +
                    "Set AZURE_DEVOPS_PAT or SYSTEM_ACCESSTOKEN.");
            }

            // Only mark resolved *after* the header is in place so other
            // callers observing _authResolved == true see a fully-configured
            // HttpClient.
            _authResolved = true;
        }
    }

    /// <summary>
    /// Reset cached auth state so the next API call re-resolves credentials.
    /// Used after the user re-authenticates via elicitation prompt.
    /// </summary>
    internal void ResetAuth()
    {
        lock (_authLock)
        {
            _authResolved = false;
            _http.DefaultRequestHeaders.Authorization = null;
        }
        Console.Error.WriteLine("ci-debug-mcp: ADO auth reset — will re-resolve on next call");
    }

    private AuthResult? ResolveAdoAuth()
    {
        // 1. Explicit PAT
        var pat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT");
        if (!string.IsNullOrEmpty(pat))
            return new AuthResult("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pat}")));

        // 2. Pipeline system token
        var sysToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        if (!string.IsNullOrEmpty(sysToken))
            return new AuthResult("Bearer", sysToken);

        // 3. Azure CLI
        var azToken = GetTokenFromAzCli();
        if (azToken != null)
            return new AuthResult("Bearer", azToken);

        // 4. Git Credential Manager — try original host first (e.g. myorg.visualstudio.com),
        // then fall back to dev.azure.com
        string?[] gcmHosts = _originalHost != null
            ? [_originalHost, "dev.azure.com"]
            : ["dev.azure.com"];

        foreach (var host in gcmHosts)
        {
            if (host == null) continue;
            var gcmToken = GetTokenFromGcm(host);
            if (gcmToken != null)
            {
                Console.Error.WriteLine($"ci-debug-mcp: GCM credential found for {host}");
                var scheme = gcmToken.StartsWith("eyJ") ? "Bearer" : "Basic";
                var value = scheme == "Basic"
                    ? Convert.ToBase64String(Encoding.UTF8.GetBytes($":{gcmToken}"))
                    : gcmToken;
                return new AuthResult(scheme, value);
            }
        }

        return null;
    }

    private static string? GetTokenFromAzCli()
    {
        // On Windows, 'az' is az.cmd — must invoke via cmd.exe
        var isWindows = OperatingSystem.IsWindows();
        var fileName = isWindows ? "cmd.exe" : "az";
        var arguments = isWindows
            ? "/c az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv"
            : "account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv";

        var result = RunCredentialSubprocess(
            fileName, arguments,
            timeoutMs: 10_000,
            timeoutLogContext: "az CLI token retrieval",
            errorLogContext: "az CLI token retrieval");
        if (result == null)
        {
            // Either timeout (already logged inside helper) or Process.Start failure / exception.
            return null;
        }

        var token = result.Stdout.Trim();
        return result.ExitCode == 0 && token.Length > 0 ? token : null;
    }

    private static string? GetTokenFromGcm(string host)
    {
        var result = RunCredentialSubprocess(
            fileName: "git",
            arguments: "credential fill",
            timeoutMs: 5_000,
            writeInput: stdin => stdin.Write($"protocol=https\nhost={host}\n\n"),
            environment: new Dictionary<string, string>
            {
                // Prevent UI prompts and terminal prompts — fail fast if GCM
                // can't resolve non-interactively. Without these, GCM can
                // pop a browser window or block on a tty read on first use.
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_TERMINAL_PROMPT"] = "0",
            },
            timeoutLogContext: $"GCM credential lookup for {host}",
            errorLogContext: $"GCM credential lookup for {host}");

        if (result == null || result.ExitCode != 0) return null;

        foreach (var line in result.Stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                return trimmed[9..];
        }
        return null;
    }

    /// <summary>
    /// Result of a credential-resolution subprocess invocation. <c>null</c>
    /// return from <see cref="RunCredentialSubprocess"/> means the process
    /// either timed out (and was killed) or failed to start / threw.
    /// </summary>
    internal sealed record CredentialSubprocessResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Run a short-lived credential-resolution subprocess (az CLI, git
    /// credential, etc.) with a hard wall-clock timeout, draining stdout
    /// and stderr concurrently to avoid pipe-buffer-fill deadlocks. On
    /// timeout the entire process tree is killed and <c>null</c> is
    /// returned; the timeout is logged with the supplied
    /// <paramref name="timeoutLogContext"/> (defaults to a generic message
    /// when not supplied).
    ///
    /// Returns <c>null</c> on:
    ///   - <see cref="Process.Start(ProcessStartInfo)"/> returning null
    ///     (process couldn't be launched, e.g. file-not-found),
    ///   - timeout exceeded (process killed; stderr/stdout content is lost
    ///     and not surfaced — the caller can only know it timed out),
    ///   - any exception thrown by the subprocess machinery (logged with
    ///     the supplied <paramref name="errorLogContext"/>).
    ///
    /// Otherwise returns a populated result regardless of exit code.
    ///
    /// Exposed as <c>internal static</c> so tests can drive the
    /// timeout-and-kill path with controlled commands (e.g. a long-running
    /// <c>ping</c> against a short timeout) without depending on whether
    /// <c>az</c> / <c>git</c> are installed in the test environment.
    /// </summary>
    internal static CredentialSubprocessResult? RunCredentialSubprocess(
        string fileName, string arguments, int timeoutMs,
        Action<StreamWriter>? writeInput = null,
        IDictionary<string, string>? environment = null,
        string? timeoutLogContext = null,
        string? errorLogContext = null)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                // RedirectStandardInput prevents the child from inheriting the
                // MCP server's stdin pipe. Without this, cmd.exe / az.cmd /
                // conhost.exe can block reading from the inherited stdin — the
                // same hang documented in GitHubClient.GetGitConfigValue.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (environment != null)
            {
                foreach (var (k, v) in environment) psi.Environment[k] = v;
            }

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // Once Process.Start succeeds, we own the child process. Any
            // exception in the post-start logic must kill the process tree
            // before propagating so we don't orphan a running subprocess.
            try
            {
                // Write stdin (if requested) then close so any read attempt sees EOF.
                if (writeInput != null) writeInput(proc.StandardInput);
                proc.StandardInput.Close();

                // Drain stderr concurrently with stdout. Without this, a chatty
                // subprocess can fill the stderr pipe buffer (~4 KB on Windows)
                // and block on write, while we block reading stdout.
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Console.Error.WriteLine(
                        $"ci-debug-mcp: {timeoutLogContext ?? "credential subprocess"} timed out");
                    return null;
                }

                // After WaitForExit, stdout/stderr have been closed so the drain
                // tasks complete promptly.
                var stdout = stdoutTask.GetAwaiter().GetResult();
                var stderr = stderrTask.GetAwaiter().GetResult();

                return new CredentialSubprocessResult(proc.ExitCode, stdout, stderr);
            }
            catch
            {
                // Failure after Process.Start succeeded — kill the process
                // tree before letting the outer try/catch log + return null,
                // so we don't leak a running subprocess.
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ci-debug-mcp: {errorLogContext ?? "credential subprocess"} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string path)
    {
        EnsureAuth();
        var response = await _http.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode == 404)
                throw new HttpRequestException(
                    $"ADO API returned 404 for {path}. Verify the build/resource exists and you have access.");
            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                throw new McpSharp.AuthenticationException(
                    "ADO",
                    $"ADO API returned {(int)response.StatusCode} — credentials are invalid or expired.",
                    "1. Run: az login\n" +
                    "2. Or set the AZURE_DEVOPS_PAT environment variable with a valid Personal Access Token\n" +
                    "3. Then restart the CLI session")
                { ResetAuth = ResetAuth };
            GuardHtmlResponse(body);
            response.EnsureSuccessStatusCode();
        }
        var content = await response.Content.ReadAsStringAsync();
        GuardHtmlResponse(content);
        return JsonNode.Parse(content);
    }

    /// <summary>
    /// Detect HTML login pages returned by ADO when auth fails (200 status but HTML body).
    /// </summary>
    private void GuardHtmlResponse(string content)
    {
        if (content.Length > 0 && (content[0] == '<' ||
            content.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpSharp.AuthenticationException(
                "ADO",
                "ADO returned HTML instead of JSON — authentication failed.",
                "1. Run: az login\n" +
                "2. Or set the AZURE_DEVOPS_PAT environment variable with a valid Personal Access Token\n" +
                "3. Then restart the CLI session")
            { ResetAuth = ResetAuth };
        }
    }

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

/// <summary>
/// Parsed components from an ADO URL.
/// </summary>
public readonly record struct AdoParsedUrl(string OrgUrl, string Project, string? BuildId, int? PrNumber, string? OriginalHost);
