// Copyright (c) ci-debug-mcp contributors
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
    private bool _authResolved;

    public string ProviderName => "ado";

    /// <param name="orgUrl">e.g. "https://dev.azure.com/myorg"</param>
    /// <param name="project">e.g. "myproject"</param>
    /// <param name="originalHost">Original hostname for GCM lookup (e.g. "myorg.visualstudio.com")</param>
    public AdoCiProvider(string orgUrl, string project, LogCache cache, string? originalHost = null)
    {
        _orgUrl = orgUrl.TrimEnd('/');
        _project = project;
        _cache = cache;
        _originalHost = originalHost;

        _http = new HttpClient { BaseAddress = new Uri($"{_orgUrl}/{_project}/_apis/") };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ci-debug-mcp", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

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

        return artifacts.Select(a => new CiArtifact
        {
            Id = a?["id"]?.GetValue<int>().ToString() ?? "0",
            Name = a?["name"]?.GetValue<string>() ?? "",
            SizeBytes = 0, // ADO doesn't include size in list response
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

            // Find the failed task within this job (needed at all detail levels for job name + type)
            var jobRecordId = rec?["id"]?.GetValue<string>();
            CiStepInfo? failedTask = null;
            string? failedLogId = null;

            // summary detail level: include minimal failure info (no log downloads)
            if (query.Detail == "summary")
            {
                foreach (var task in records)
                {
                    if (task?["type"]?.GetValue<string>() != "Task") continue;
                    if (task?["parentId"]?.GetValue<string>() != jobRecordId) continue;
                    if (task?["result"]?.GetValue<string>() == "failed")
                    {
                        var logRef = task["log"];
                        failedLogId = logRef?["id"]?.GetValue<int>().ToString();
                        failedTask = new CiStepInfo
                        {
                            Number = task["order"]?.GetValue<int>() ?? 0,
                            Name = task["name"]?.GetValue<string>() ?? "unknown",
                            Source = "timeline",
                        };
                        break;
                    }
                }
                failures.Add(new CiJobFailure
                {
                    Name = name,
                    JobId = failedLogId != null ? $"{buildId}:{failedLogId}" : buildId.ToString(),
                    BuildId = buildId.ToString(),
                    Conclusion = result,
                    FailedStep = failedTask,
                    FailureType = failedTask != null
                        ? LogParser.ClassifyStepType(failedTask.Name) is "unknown" ? null : LogParser.ClassifyStepType(failedTask.Name)
                        : null,
                });
                continue;
            }

            // Find the failed task within this job

            foreach (var task in records)
            {
                if (task?["type"]?.GetValue<string>() != "Task") continue;
                if (task?["parentId"]?.GetValue<string>() != jobRecordId) continue;
                if (task?["result"]?.GetValue<string>() == "failed")
                {
                    var logRef = task["log"];
                    failedLogId = logRef?["id"]?.GetValue<int>().ToString();
                    failedTask = new CiStepInfo
                    {
                        Number = task["order"]?.GetValue<int>() ?? 0,
                        Name = task["name"]?.GetValue<string>() ?? "unknown",
                        Source = "timeline",
                    };
                    break;
                }
            }

            var failure = new CiJobFailure
            {
                Name = name,
                JobId = failedLogId != null ? $"{buildId}:{failedLogId}" : buildId.ToString(),
                BuildId = buildId.ToString(),
                Conclusion = result,
                FailedStep = failedTask,
                // Classify from task name when available (works at all detail levels)
                FailureType = failedTask != null
                    ? LogParser.ClassifyStepType(failedTask.Name) is "unknown" ? null : LogParser.ClassifyStepType(failedTask.Name)
                    : null,
            };

            if (query.Detail == "errors" && failedLogId != null)
            {
                try
                {
                    var log = await GetJobLogAsync($"{buildId}:{failedLogId}");

                    // Use the universal error extraction pipeline
                    var allErrors = new List<CiExtractedError>();
                    foreach (var step in log.Steps.Length > 0 ? log.Steps : [new ParsedStep { Number = 1, Name = name, StartLine = 0, EndLine = log.Lines.Length - 1 }])
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
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ci-debug-mcp: ADO log retrieval failed for build {buildId}: {ex.Message}");
                }
            }

            // Detect child/triggered builds from the timeline
            var childBuildIds = DetectChildBuilds(records, jobRecordId);
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

    private void EnsureAuth()
    {
        if (_authResolved) return;
        _authResolved = true;

        var auth = ResolveAdoAuth();
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
        try
        {
            // On Windows, 'az' is az.cmd — must invoke via cmd.exe
            var isWindows = OperatingSystem.IsWindows();
            var fileName = isWindows ? "cmd.exe" : "az";
            var arguments = isWindows
                ? "/c az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv"
                : "account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv";
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var token = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(15_000)) return null;
            return proc.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ci-debug-mcp: az CLI token retrieval failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetTokenFromGcm(string host)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "credential fill")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            proc.StandardInput.Write($"protocol=https\nhost={host}\n\n");
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(10_000)) return null;
            if (proc.ExitCode != 0) return null;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                    return trimmed[9..];
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ci-debug-mcp: GCM credential lookup for {host} failed: {ex.Message}");
        }
        return null;
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
    private static void GuardHtmlResponse(string content)
    {
        if (content.Length > 0 && (content[0] == '<' ||
            content.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            content.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "ADO returned HTML instead of JSON — authentication failed. " +
                "Ensure Azure CLI is logged in (az login) or set AZURE_DEVOPS_PAT.");
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
