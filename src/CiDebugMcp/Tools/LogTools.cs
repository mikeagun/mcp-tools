using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CiDebugMcp.Engine;
using McpSharp;

namespace CiDebugMcp.Tools;

/// <summary>
/// CI log search and failure triage tools.
/// </summary>
public static partial class LogTools
{
    public static void Register(McpServer server, IGitHubApi github, CiProviderResolver? resolver = null)
    {
        RegisterGetCiFailures(server, github, resolver);
        RegisterSearchJobLogs(server, github, resolver);
        RegisterGetStepLogs(server, github, resolver);
    }

    private sealed class ErrorBudget(int max) { public int Remaining = max; }

    /// <summary>Parse a JSON node as long, handling both number and string types.</summary>
    private static long? ParseLongParam(JsonNode? node)
    {
        if (node == null) return null;
        if (long.TryParse(node.ToString(), out var result)) return result;
        return null;
    }

    /// <summary>Convert HTTP errors into structured JSON responses for agents.</summary>
    private static JsonObject HandleHttpError(HttpRequestException ex)
    {
        var statusCode = (int)(ex.StatusCode ?? 0);
        var result = new JsonObject
        {
            ["error"] = statusCode switch
            {
                403 => "Log access denied (HTTP 403)",
                404 => "Resource not found (HTTP 404)",
                _ => $"HTTP error {statusCode}: {ex.Message}",
            },
        };
        if (statusCode == 403)
            result["hint"] = "Your token may lack the 'actions:read' scope. " +
                "Try: download_artifact to get test results instead, or re-authenticate with broader permissions.";
        else if (statusCode == 404)
            result["hint"] = "The job or run may have been deleted, or the ID may be wrong.";
        return result;
    }

    /// <summary>Escape single quotes in step names for use in hint strings.</summary>
    private static string EscapeHintString(string s) => s.Replace("'", "\\'");

    /// <summary>Build a structured next_action for get_step_logs.</summary>
    private static JsonObject StepLogsAction(string owner, string repo, long jobId,
        string? stepName = null, int? stepNumber = null, string? pattern = null)
    {
        var p = new JsonObject
        {
            ["job_id"] = jobId,
            ["repo"] = $"{owner}/{repo}",
        };
        if (stepNumber.HasValue) p["step_number"] = stepNumber.Value;
        else if (stepName != null) p["step_name"] = stepName;
        if (pattern != null) p["pattern"] = pattern;
        return new JsonObject { ["tool"] = "get_step_logs", ["params"] = p };
    }

    /// <summary>Build a structured next_action for search_job_logs.</summary>
    private static JsonObject SearchLogsAction(string owner, string repo, long jobId,
        string pattern, bool includeSetup = false)
    {
        var p = new JsonObject
        {
            ["job_id"] = jobId,
            ["repo"] = $"{owner}/{repo}",
            ["pattern"] = pattern,
        };
        if (includeSetup) p["include_setup"] = true;
        return new JsonObject { ["tool"] = "search_job_logs", ["params"] = p };
    }

    // Legacy string hint helpers (kept for backward compat in some paths)
    private static string StepLogsHint(string owner, string repo, long jobId,
        string? stepName = null, int? stepNumber = null, string? pattern = null)
    {
        var parts = $"job_id={jobId}, repo='{owner}/{repo}'";
        if (stepNumber.HasValue)
            parts += $", step_number={stepNumber.Value}";
        else if (stepName != null)
            parts += $", step_name='{EscapeHintString(stepName)}'";
        if (pattern != null)
            parts += $", pattern='{pattern}'";
        return $"get_step_logs({parts})";
    }

    private static string SearchLogsHint(string owner, string repo, long jobId,
        string pattern, bool includeSetup = false)
    {
        var parts = $"job_id={jobId}, repo='{owner}/{repo}', pattern='{pattern}'";
        if (includeSetup)
            parts += ", include_setup=true";
        return $"search_job_logs({parts})";
    }

    [GeneratedRegex(@"github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?:pull/(?<pr>\d+)|actions/runs/(?<run>\d+)(?:/job/(?<job>\d+))?)")]
    private static partial Regex GitHubUrlRegex();

    /// <summary>
    /// Parse a CI URL into components. Returns provider hint ("github" or "ado")
    /// to guide dispatch. For ADO URLs, owner/repo are null — use CiProviderResolver instead.
    /// </summary>
    private static (string? provider, string? owner, string? repo, int? pr, long? runId, long? jobId) ParseUrl(string url)
    {
        // Try GitHub first
        var m = GitHubUrlRegex().Match(url);
        if (m.Success)
        {
            return (
                "github",
                m.Groups["owner"].Value,
                m.Groups["repo"].Value,
                m.Groups["pr"].Success ? int.Parse(m.Groups["pr"].Value) : null,
                m.Groups["run"].Success ? long.Parse(m.Groups["run"].Value) : null,
                m.Groups["job"].Success ? long.Parse(m.Groups["job"].Value) : null
            );
        }

        // Try ADO (dev.azure.com or *.visualstudio.com)
        if (url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var adoParsed = Engine.AdoCiProvider.ParseUrl(url);
            if (adoParsed != null)
            {
                // buildId maps to runId slot for downstream use
                long? buildId = adoParsed.Value.BuildId != null
                    ? long.Parse(adoParsed.Value.BuildId) : null;
                return ("ado", null, null, adoParsed.Value.PrNumber, buildId, null);
            }
        }

        return (null, null, null, null, null, null);
    }

    /// <summary>
    /// Resolve a job_id from a workflow run + optional job name substring.
    /// If job_name is null, returns the first failed job (or first job if none failed).
    /// </summary>
    private static async Task<long> ResolveJobIdAsync(
        IGitHubApi github, string owner, string repo, long runId, string? jobName)
    {
        var jobs = await github.GetJobs(owner, repo, runId);
        if (jobs.Count == 0)
            throw new ArgumentException($"No jobs found for run {runId}");

        JsonNode? match = null;
        if (jobName != null)
        {
            match = jobs.FirstOrDefault(j =>
                (j?["name"]?.GetValue<string>() ?? "")
                    .Contains(jobName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var available = string.Join(", ", jobs.Select(j => j?["name"]?.GetValue<string>() ?? ""));
                throw new ArgumentException(
                    $"No job matching '{jobName}' in run {runId}. Available: {available}");
            }
        }
        else
        {
            // Prefer first failed job, fall back to first job
            match = jobs.FirstOrDefault(j =>
                j?["conclusion"]?.GetValue<string>() == "failure") ?? jobs[0]!;
        }

        return match!["id"]!.GetValue<long>();
    }

    // ──────────────────────────────────────────────────────────────
    //  Tool 1: get_ci_failures
    // ──────────────────────────────────────────────────────────────

    private static void RegisterGetCiFailures(McpServer server, IGitHubApi github, CiProviderResolver? resolver)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_ci_failures",
            Description = "Unified CI failure entry point. Works with GitHub Actions and Azure DevOps. " +
                          "Identify what to check via a CI URL (preferred), PR number, " +
                          "run/build ID, commit SHA, branch, or workflow filter. " +
                          "For ADO: pass the git remote URL or build results URL as the url param. " +
                          "Returns failed checks with error lines extracted from logs. " +
                          "Resolution priority: run_id/buildId > sha > pr > branch > repo-wide failures. " +
                          "IMPORTANT: Failures may be pre-existing, not caused by the PR. " +
                          "To distinguish new regressions from pre-existing failures: " +
                          "1) Check if error files appear in changed_files (PR queries include this). " +
                          "2) Compare with base branch: get_ci_failures(branch='main') to see if same jobs fail there. " +
                          "3) For flaky tests: get_ci_failures(branch='main', count=5) and check if the same job intermittently fails. " +
                          "Response: { scope, provider, summary: {total, passed, failed, skipped, cancelled, pending}, " +
                          "failures: [{job, job_id, run_id, conclusion, failure_type? ('build'|'test'|'dependency'|'infra'|'unknown'), " +
                          "failed_step?: {name, source, number?}, " +
                          "failed_steps?: [{number, name, errors: [{raw, code?, message?, file?, source_line?} | string], failed_test_names?}], " +
                          "test_summary?: {framework, total, passed, failed, summary_line}, " +
                          "failed_test_names?: string[], " +
                          "diagnosis?, next_action?: {tool, params}, hint?, note?, " +
                          "available_steps?: [{number, name, lines, has_errors?, type?}]}], " +
                          "cancelled?: [{name, job_id}], pending?: [{name, status}], " +
                          "changed_files?: string[] (PR queries only), " +
                          "base_branch?: string (PR queries only), " +
                          "compare_hint?: string (how to check if failures are pre-existing) }. " +
                          "Each failure includes next_action: {tool, params} — pass params directly to the named tool for drill-down. " +
                          "For multi-run queries: { scope, summary: {failed_runs}, " +
                          "runs: [{run_id, workflow, branch, conclusion, date, failed_jobs?: [{job, failed_step?, first_error?}]}] }. " +
                          "ADO job_id format is 'buildId:logId'. For ADO step logs, use step_name (not step_number).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["owner"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Repository owner (auto-detected from url if provided)",
                    },
                    ["repo"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Repository name, or 'owner/repo' shorthand (auto-detected from url if provided)",
                    },
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "CI URL or git remote URL (preferred entry point). " +
                                          "Auto-detects GitHub or ADO, extracts all query parameters. " +
                                          "Examples: GitHub PR URL, ADO build URL, or git remote origin URL.",
                    },
                    ["pr"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Pull request number — checks on the PR's head commit",
                    },
                    ["run_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Specific workflow run ID",
                    },
                    ["sha"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Specific commit SHA — all check runs on that commit",
                    },
                    ["branch"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Branch name — latest run(s) on this branch",
                    },
                    ["workflow"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Substring filter on workflow/job name (case-insensitive). " +
                                          "Only jobs/runs whose name contains this string are included. " +
                                          "Example: 'CI/CD' to match 'CI/CD - Regular', 'onebranch' for OneBranch jobs.",
                    },
                    ["definition_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "ADO pipeline definition ID — filter builds to a specific pipeline. " +
                                          "Use with branch or count for targeted queries.",
                    },
                    ["count"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Number of recent builds/runs to return (default: 1). " +
                                          "Use count>1 with branch to detect flaky failures across runs. " +
                                          "Applies to branch and repo-wide queries.",
                        ["default"] = 1,
                    },
                    ["detail"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Verbosity: 'summary' = pass/fail/cancelled counts only; " +
                                          "'jobs' = failed jobs with step info + cancelled list; " +
                                          "'errors' = download logs and extract meaningful error lines.",
                        ["enum"] = new JsonArray("summary", "jobs", "errors"),
                        ["default"] = "errors",
                    },
                    ["max_errors"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Cap total error lines in response (default: 20). " +
                                          "Only applies when detail='errors'.",
                        ["default"] = 20,
                    },
                    ["max_test_names"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Cap failed test names per step (default: 20). " +
                                          "When exceeded, failed_test_names_total shows the full count.",
                        ["default"] = 20,
                    },
                    ["max_failures"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Cap failure entries returned (default: 10). " +
                                          "When exceeded, additional_failures shows the remaining count.",
                        ["default"] = 10,
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                string? owner = args["owner"]?.GetValue<string>();
                string? repo = args["repo"]?.GetValue<string>();
                int? pr = args["pr"]?.GetValue<int>();
                long? runId = args["run_id"]?.GetValue<long>();
                long? jobId = null;

                var url = args["url"]?.GetValue<string>();
                if (url != null)
                {
                    var parsed = ParseUrl(url);
                    owner ??= parsed.owner;
                    repo ??= parsed.repo;
                    pr ??= parsed.pr;
                    runId ??= parsed.runId;
                    jobId = parsed.jobId;

                    // ADO URL detected — dispatch through provider resolver
                    if (parsed.provider == "ado" && resolver != null)
                    {
                        var resolved = resolver.ResolveFromUrl(url);
                        if (resolved != null)
                        {
                            return HandleAdoFailuresAsync(resolved, args)
                                .GetAwaiter().GetResult();
                        }
                    }
                }

                // Support "owner/repo" shorthand in repo param
                if (owner == null && repo != null && repo.Contains('/'))
                {
                    var parts = repo.Split('/', 2);
                    owner = parts[0];
                    repo = parts[1];
                }

                if (owner == null || repo == null)
                    throw new ArgumentException("owner and repo are required (provide directly or via url)");

                var sha = args["sha"]?.GetValue<string>();
                var branch = args["branch"]?.GetValue<string>();
                var workflow = args["workflow"]?.GetValue<string>();
                var count = args["count"]?.GetValue<int>() ?? 1;
                var detail = args["detail"]?.GetValue<string>() ?? "errors";
                var maxErrors = args["max_errors"]?.GetValue<int>() ?? 20;
                var maxTestNames = args["max_test_names"]?.GetValue<int>() ?? 20;
                var maxFailures = args["max_failures"]?.GetValue<int>() ?? 10;

                return GetCiFailuresAsync(github, owner, repo, pr, runId, jobId,
                    sha, branch, workflow, count, detail, maxErrors, maxTestNames, maxFailures)
                    .GetAwaiter().GetResult();
            },
        });
    }

    /// <summary>
    /// Handle ADO CI failure queries through ICiProvider, returning the same JSON format.
    /// </summary>
    private static async Task<JsonNode> HandleAdoFailuresAsync(ResolvedProvider resolved, JsonObject args)
    {
        var provider = resolved.Provider;
        var query = new CiQuery
        {
            Url = resolved.ExtractedQuery?.Url,
            BuildId = resolved.ExtractedQuery?.BuildId ?? args["run_id"]?.GetValue<long>().ToString(),
            PrNumber = resolved.ExtractedQuery?.PrNumber ?? args["pr"]?.GetValue<int>(),
            CommitSha = args["sha"]?.GetValue<string>(),
            Branch = args["branch"]?.GetValue<string>(),
            PipelineFilter = args["workflow"]?.GetValue<string>(),
            DefinitionId = args["definition_id"]?.GetValue<int>(),
            Count = args["count"]?.GetValue<int>() ?? 1,
            Detail = args["detail"]?.GetValue<string>() ?? "errors",
            MaxErrors = args["max_errors"]?.GetValue<int>() ?? 20,
        };

        var report = await provider.GetFailuresAsync(query);
        var maxTestNames = args["max_test_names"]?.GetValue<int>() ?? 20;
        var maxFailures = args["max_failures"]?.GetValue<int>() ?? 10;
        return FailureReportFormatter.Format(report, provider.ProviderName, maxTestNames, maxFailures);
    }

    private static async Task<JsonNode> GetCiFailuresAsync(
        IGitHubApi github, string owner, string repo,
        int? pr, long? runId, long? jobId,
        string? sha, string? branch, string? workflow,
        int count, string detail, int maxErrors, int maxTestNames = 20, int maxFailures = 10)
    {
        bool includeJobs = detail != "summary";
        bool downloadLogs = detail == "errors";

        if (runId.HasValue)
        {
            var scope = $"run {runId.Value}";
            return await GetFailuresFromRunAsync(github, owner, repo, runId.Value,
                workflow, scope, includeJobs, downloadLogs, maxErrors, maxTestNames, maxFailures);
        }

        if (sha != null)
        {
            var scope = $"commit {sha[..Math.Min(8, sha.Length)]}";
            return await GetFailuresFromShaAsync(github, owner, repo, sha,
                workflow, scope, includeJobs, downloadLogs, maxErrors, maxTestNames, maxFailures);
        }

        if (pr.HasValue)
        {
            var prData = await github.GetPullRequest(owner, repo, pr.Value);
            var headSha = prData["head"]?["sha"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Could not get PR head SHA");
            var baseBranch = prData["base"]?["ref"]?.GetValue<string>() ?? "main";
            var scope = $"PR #{pr.Value} (head: {headSha[..8]})";
            var result = await GetFailuresFromShaAsync(github, owner, repo, headSha,
                workflow, scope, includeJobs, downloadLogs, maxErrors, maxTestNames, maxFailures);

            // Include changed files for PR-scoped queries
            var files = await github.GetPullRequestFiles(owner, repo, pr.Value);
            if (files.Length > 0)
            {
                var maxFiles = 30;
                result["changed_files"] = new JsonArray(
                    files.Take(maxFiles).Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
                if (files.Length > maxFiles)
                    result["changed_files_total"] = files.Length;
            }

            // Help agents distinguish new regressions from pre-existing failures
            result["base_branch"] = baseBranch;
            var failures = result["failures"]?.AsArray();
            if (failures != null && failures.Count > 0)
            {
                // Correlate: do error files appear in changed_files?
                var changedSet = files.Select(f => Path.GetFileName(f).ToLowerInvariant()).ToHashSet();
                foreach (var f in failures)
                {
                    var steps = f?["failed_steps"]?.AsArray();
                    if (steps == null) continue;
                    foreach (var step in steps)
                    {
                        var errors = step?["errors"]?.AsArray();
                        if (errors == null) continue;
                        foreach (var err in errors)
                        {
                            var fileField = err is JsonObject errObj ? errObj["file"]?.GetValue<string>() : null;
                            if (fileField != null && changedSet.Contains(Path.GetFileName(fileField).ToLowerInvariant()))
                            {
                                if (err is JsonObject eo)
                                    eo["in_changed_files"] = true;
                            }
                        }
                    }
                }

                result["compare_hint"] = $"To check if failures are pre-existing on {baseBranch}: " +
                    $"get_ci_failures(repo='{owner}/{repo}', branch='{baseBranch}'). " +
                    "Errors with in_changed_files=true are likely regressions from this PR.";
            }

            return result;
        }

        if (branch != null)
        {
            var scope = $"branch {branch}" + (workflow != null ? $", workflow '{workflow}'" : "");
            return await GetFailuresFromBranchAsync(github, owner, repo, branch,
                workflow, scope, includeJobs, downloadLogs, maxErrors, count, maxTestNames, maxFailures);
        }

        // Fix 4: repo-wide failure query — no branch filter, find failed runs
        {
            var scope = "repo-wide failures";
            return await GetFailuresRepoWideAsync(github, owner, repo,
                workflow, scope, includeJobs, downloadLogs, maxErrors, count, maxTestNames, maxFailures);
        }
    }

    private static async Task<JsonNode> GetFailuresFromShaAsync(
        IGitHubApi github, string owner, string repo, string sha,
        string? workflowFilter, string scope, bool includeJobs, bool downloadLogs, int maxErrors,
        int maxTestNames = 20, int maxFailures = 10)
    {
        var checkRuns = await github.GetCheckRuns(owner, repo, sha);

        int total = 0, passed = 0, failed = 0, pending = 0, skipped = 0, cancelled = 0;
        var failures = new JsonArray();
        var cancelledList = new JsonArray();
        var pendingList = new JsonArray();
        var budget = new ErrorBudget(maxErrors);

        foreach (var run in checkRuns)
        {
            var name = run?["name"]?.GetValue<string>() ?? "unknown";

            if (workflowFilter != null &&
                !name.Contains(workflowFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            total++;
            var status = run?["status"]?.GetValue<string>();
            var conclusion = run?["conclusion"]?.GetValue<string>();

            if (status != "completed")
            {
                pending++;
                pendingList.Add(new JsonObject { ["name"] = name, ["status"] = status });
                continue;
            }

            if (conclusion == "success")
            {
                passed++;
                continue;
            }

            if (conclusion == "skipped")
            {
                skipped++;
                continue;
            }

            if (conclusion == "cancelled")
            {
                cancelled++;
                var jobIdVal = run?["id"]?.GetValue<long>();
                if (includeJobs)
                    cancelledList.Add(new JsonObject { ["name"] = name, ["job_id"] = jobIdVal });
                continue;
            }

            // conclusion == "failure" (or other non-success)
            failed++;
            if (failures.Count < maxFailures)
            {
                var failure = await BuildFailureEntryAsync(
                    github, owner, repo, run, name, downloadLogs, budget, includeJobs, maxTestNames);
                failures.Add(failure);
            }
        }

        var result = BuildResult(scope, total, passed, failed, pending, skipped, cancelled,
            failures, cancelledList, pendingList);
        if (failed > maxFailures)
            result["additional_failures"] = failed - maxFailures;
        return result;
    }

    private static async Task<JsonNode> GetFailuresFromRunAsync(
        IGitHubApi github, string owner, string repo, long runId,
        string? workflowFilter, string scope, bool includeJobs, bool downloadLogs, int maxErrors,
        int maxTestNames = 20, int maxFailures = 10)
    {
        var jobs = await github.GetJobs(owner, repo, runId);

        int total = 0, passed = 0, failed = 0, pending = 0, skipped = 0, cancelled = 0;
        var failures = new JsonArray();
        var cancelledList = new JsonArray();
        var pendingList = new JsonArray();
        var budget = new ErrorBudget(maxErrors);

        foreach (var job in jobs)
        {
            var name = job?["name"]?.GetValue<string>() ?? "unknown";

            if (workflowFilter != null &&
                !name.Contains(workflowFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            total++;
            var status = job?["status"]?.GetValue<string>();
            var conclusion = job?["conclusion"]?.GetValue<string>();
            var jobIdVal = job?["id"]?.GetValue<long>() ?? 0;

            if (status != "completed")
            {
                pending++;
                pendingList.Add(new JsonObject { ["name"] = name, ["status"] = status });
                continue;
            }

            if (conclusion == "success")
            {
                passed++;
                continue;
            }

            if (conclusion == "skipped")
            {
                skipped++;
                continue;
            }

            if (conclusion == "cancelled")
            {
                cancelled++;
                if (includeJobs)
                    cancelledList.Add(new JsonObject { ["name"] = name, ["job_id"] = jobIdVal });
                continue;
            }

            // conclusion == "failure"
            failed++;

            if (failures.Count >= maxFailures)
                continue;

            var failure = new JsonObject
            {
                ["job"] = name,
                ["job_id"] = jobIdVal,
                ["run_id"] = runId,
                ["conclusion"] = conclusion,
            };

            // Classify from step name (available at all detail levels from Jobs API)
            ParsedStep? failedStep = null;
            var steps = job?["steps"]?.AsArray();
            if (steps != null)
            {
                var fs = steps.FirstOrDefault(s =>
                    s?["conclusion"]?.GetValue<string>() == "failure");
                if (fs != null)
                {
                    var stepNum = fs["number"]!.GetValue<int>();
                    var stepName = fs["name"]!.GetValue<string>();
                    failedStep = new ParsedStep { Number = stepNum, Name = stepName };
                    var stepType = LogParser.ClassifyStepType(stepName);
                    if (stepType != "unknown")
                        failure["failure_type"] = stepType;

                    // Jobs/errors level: include step metadata and hints
                    if (includeJobs)
                    {
                        failure["failed_step"] = new JsonObject
                        {
                            ["number"] = stepNum,
                            ["name"] = stepName,
                            ["source"] = "api",
                        };
                    }
                }
            }

            if (downloadLogs)
            {
                await EnrichWithLogErrorsAsync(github, owner, repo, jobIdVal, failure, budget, failedStep, maxTestNames);
            }
            else if (includeJobs)
            {
                failure["hint"] = StepLogsHint(owner, repo, jobIdVal, pattern: "error|FAIL");
                failure["next_action"] = SearchLogsAction(owner, repo, jobIdVal, "error|FAIL");
            }

            failures.Add(failure);
        }

        var result = BuildResult(scope, total, passed, failed, pending, skipped, cancelled,
            failures, cancelledList, pendingList);
        if (failed > maxFailures)
            result["additional_failures"] = failed - maxFailures;
        return result;
    }

    private static async Task<JsonNode> GetFailuresFromBranchAsync(
        IGitHubApi github, string owner, string repo, string? branch,
        string? workflowFilter, string scope, bool includeJobs, bool downloadLogs,
        int maxErrors, int count, int maxTestNames = 20, int maxFailures = 10)
    {
        if (branch == null)
        {
            var repoData = await github.GetJsonAsync($"/repos/{owner}/{repo}");
            branch = repoData?["default_branch"]?.GetValue<string>() ?? "main";
            scope = $"latest on {branch}";
        }

        var fetchCount = Math.Max(count * 3, 20);
        var path = $"/repos/{owner}/{repo}/actions/runs?branch={Uri.EscapeDataString(branch)}" +
                   $"&per_page={fetchCount}&status=completed";
        var runsData = await github.GetJsonAsync(path);
        var runs = runsData?["workflow_runs"]?.AsArray() ?? [];

        return await ProcessRunsList(github, owner, repo, runs, workflowFilter, scope,
            includeJobs, downloadLogs, maxErrors, count, maxTestNames, maxFailures);
    }

    /// <summary>
    /// Fix 4: Repo-wide failure query — no branch filter, find recent failed runs.
    /// </summary>
    private static async Task<JsonNode> GetFailuresRepoWideAsync(
        IGitHubApi github, string owner, string repo,
        string? workflowFilter, string scope, bool includeJobs, bool downloadLogs,
        int maxErrors, int count, int maxTestNames = 20, int maxFailures = 10)
    {
        // Use conclusion=failure filter — much more efficient than fetching all and filtering
        var fetchCount = Math.Max(count, 10);
        var path = $"/repos/{owner}/{repo}/actions/runs?status=failure&per_page={fetchCount}";
        var runsData = await github.GetJsonAsync(path);
        var runs = runsData?["workflow_runs"]?.AsArray() ?? [];

        // Apply workflow filter if specified
        var failedRuns = new JsonArray();
        foreach (var run in runs)
        {
            var workflowName = run?["name"]?.GetValue<string>() ?? "";
            if (workflowFilter != null &&
                !workflowName.Contains(workflowFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            failedRuns.Add(run!.DeepClone());
            if (failedRuns.Count >= count) break;
        }

        if (failedRuns.Count == 0)
        {
            return new JsonObject
            {
                ["scope"] = scope,
                ["summary"] = new JsonObject
                {
                    ["total"] = 0, ["passed"] = 0, ["failed"] = 0,
                    ["pending"] = 0, ["skipped"] = 0, ["cancelled"] = 0,
                },
                ["failures"] = new JsonArray(),
                ["note"] = "No failed runs found" +
                           (workflowFilter != null ? $" matching '{workflowFilter}'" : ""),
            };
        }

        // Single failed run — drill into it
        if (failedRuns.Count == 1)
        {
            var firstRunId = failedRuns[0]?["id"]?.GetValue<long>() ?? 0;
            scope += $" (run {firstRunId})";
            return await GetFailuresFromRunAsync(github, owner, repo, firstRunId,
                null, scope, includeJobs, downloadLogs, maxErrors, maxTestNames, maxFailures);
        }

        // Multiple failed runs
        var runsArr = new JsonArray();
        foreach (var run in failedRuns)
        {
            var mRunId = run?["id"]?.GetValue<long>() ?? 0;
            var mName = run?["name"]?.GetValue<string>() ?? "";
            var mDate = run?["created_at"]?.GetValue<string>() ?? "";
            var mBranch = run?["head_branch"]?.GetValue<string>() ?? "";

            var entry = new JsonObject
            {
                ["run_id"] = mRunId,
                ["workflow"] = mName,
                ["branch"] = mBranch,
                ["conclusion"] = "failure",
                ["date"] = mDate.Length >= 10 ? mDate[..10] : mDate,
            };

            var runResult = await GetFailuresFromRunAsync(github, owner, repo, mRunId,
                null, "", true, downloadLogs && failedRuns.Count <= 3, maxErrors, maxTestNames, maxFailures);
            var runFailures = runResult["failures"]?.AsArray();
            if (runFailures is { Count: > 0 })
            {
                var jobsArr = new JsonArray();
                foreach (var f in runFailures)
                {
                    var compact = new JsonObject { ["job"] = f?["job"]?.GetValue<string>() };
                    if (f?["failed_step"] is JsonObject step)
                        compact["failed_step"] = step["name"]?.GetValue<string>();
                    if (f?["errors"] is JsonArray errs && errs.Count > 0)
                        compact["first_error"] = errs[0]?.ToString();
                    jobsArr.Add(compact);
                }
                entry["failed_jobs"] = jobsArr;
            }

            runsArr.Add(entry);
        }

        return new JsonObject
        {
            ["scope"] = scope + $" (last {failedRuns.Count} failed runs)",
            ["summary"] = new JsonObject
            {
                ["failed_runs"] = failedRuns.Count,
            },
            ["runs"] = runsArr,
        };
    }

    private static async Task<JsonNode> ProcessRunsList(
        IGitHubApi github, string owner, string repo,
        JsonArray runs, string? workflowFilter, string scope,
        bool includeJobs, bool downloadLogs, int maxErrors, int count,
        int maxTestNames = 20, int maxFailures = 10)
    {
        var matchingRuns = new List<(long runId, string name, string conclusion, string date)>();
        foreach (var run in runs)
        {
            var workflowName = run?["name"]?.GetValue<string>() ?? "";
            if (workflowFilter != null &&
                !workflowName.Contains(workflowFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            matchingRuns.Add((
                run?["id"]?.GetValue<long>() ?? 0,
                workflowName,
                run?["conclusion"]?.GetValue<string>() ?? "",
                run?["created_at"]?.GetValue<string>() ?? ""));
            if (matchingRuns.Count >= count) break;
        }

        if (matchingRuns.Count == 0)
        {
            return new JsonObject
            {
                ["scope"] = scope,
                ["summary"] = new JsonObject
                {
                    ["total"] = 0, ["passed"] = 0, ["failed"] = 0,
                    ["pending"] = 0, ["skipped"] = 0, ["cancelled"] = 0,
                },
                ["runs"] = new JsonArray(),
                ["note"] = "No runs found" + (workflowFilter != null ? $" matching '{workflowFilter}'" : ""),
            };
        }

        // Single run — flat structure with failures array
        if (count == 1)
        {
            var (firstRunId, firstName, firstConclusion, _) = matchingRuns[0];
            if (firstConclusion != "failure")
            {
                return new JsonObject
                {
                    ["scope"] = scope + $" (run {firstRunId})",
                    ["summary"] = new JsonObject
                    {
                        ["total"] = 0, ["passed"] = 0, ["failed"] = 0,
                        ["pending"] = 0, ["skipped"] = 0, ["cancelled"] = 0,
                    },
                    ["failures"] = new JsonArray(),
                    ["note"] = $"Latest run passed ({firstName})",
                };
            }
            scope += $" (run {firstRunId})";
            return await GetFailuresFromRunAsync(github, owner, repo, firstRunId,
                null, scope, includeJobs, downloadLogs, maxErrors, maxTestNames, maxFailures);
        }

        // Multi-run — each run gets its own entry with flaky_score in summary
        var runsArr = new JsonArray();
        int totalFailed = 0, totalPassed = 0;
        foreach (var (mRunId, mName, mConclusion, mDate) in matchingRuns)
        {
            var entry = new JsonObject
            {
                ["run_id"] = mRunId,
                ["workflow"] = mName,
                ["conclusion"] = mConclusion,
                ["date"] = mDate.Length >= 10 ? mDate[..10] : mDate,
            };

            if (mConclusion == "failure")
            {
                totalFailed++;
                var runResult = await GetFailuresFromRunAsync(github, owner, repo, mRunId,
                    null, "", true, downloadLogs && count <= 3, maxErrors, maxTestNames, maxFailures);
                var runFailures = runResult["failures"]?.AsArray();
                if (runFailures is { Count: > 0 })
                {
                    var jobsArr = new JsonArray();
                    foreach (var f in runFailures)
                    {
                        var compact = new JsonObject { ["job"] = f?["job"]?.GetValue<string>() };
                        if (f?["failed_step"] is JsonObject step)
                            compact["failed_step"] = step["name"]?.GetValue<string>();
                        if (f?["errors"] is JsonArray errs && errs.Count > 0)
                            compact["first_error"] = errs[0]?.ToString();
                        jobsArr.Add(compact);
                    }
                    entry["failed_jobs"] = jobsArr;
                }
            }
            else
            {
                totalPassed++;
            }

            runsArr.Add(entry);
        }

        return new JsonObject
        {
            ["scope"] = scope + $" (last {matchingRuns.Count} runs)",
            ["summary"] = new JsonObject
            {
                ["runs_checked"] = matchingRuns.Count,
                ["failed"] = totalFailed,
                ["passed"] = totalPassed,
                ["flaky_score"] = matchingRuns.Count > 0
                    ? Math.Round((double)totalFailed / matchingRuns.Count, 2)
                    : 0,
            },
            ["runs"] = runsArr,
        };
    }

    private static async Task<JsonObject> BuildFailureEntryAsync(
        IGitHubApi github, string owner, string repo,
        JsonNode? checkRun, string name, bool downloadLogs, ErrorBudget budget,
        bool includeJobs = true, int maxTestNames = 20)
    {
        var failure = new JsonObject
        {
            ["job"] = name,
            ["conclusion"] = checkRun?["conclusion"]?.GetValue<string>(),
        };

        var jobIdVal = checkRun?["id"]?.GetValue<long>();
        if (jobIdVal.HasValue)
        {
            failure["job_id"] = jobIdVal.Value;

            // Extract run_id from html_url for cross-tool reference
            var htmlUrl = checkRun?["html_url"]?.GetValue<string>();
            if (htmlUrl != null)
            {
                var parsed = ParseUrl(htmlUrl);
                if (parsed.runId.HasValue)
                    failure["run_id"] = parsed.runId.Value;
            }

            // Summary level: minimal info, no API calls for step metadata
            if (!includeJobs && !downloadLogs)
                return failure;

            if (downloadLogs)
            {
                await EnrichWithLogErrorsAsync(github, owner, repo, jobIdVal.Value, failure, budget, null, maxTestNames);
            }
            else
            {
                // Jobs level: fetch API step metadata for the failed step
                var job = await github.GetJob(owner, repo, jobIdVal.Value);
                var apiSteps = job?["steps"]?.AsArray();
                var failedApiStep = apiSteps?.FirstOrDefault(s =>
                    s?["conclusion"]?.GetValue<string>() == "failure");
                if (failedApiStep != null)
                {
                    failure["failed_step"] = BuildStepInfoFromApi(failedApiStep);
                    var stepName = failedApiStep["name"]?.GetValue<string>() ?? "";
                    var stepType = LogParser.ClassifyStepType(stepName);
                    if (stepType != "unknown")
                        failure["failure_type"] = stepType;
                }
                failure["hint"] = StepLogsHint(owner, repo, jobIdVal.Value, pattern: "error|FAIL");
                failure["next_action"] = SearchLogsAction(owner, repo, jobIdVal.Value, "error|FAIL");
            }
        }

        return failure;
    }

    /// <summary>
    /// Fix 2+3+5: Use ExtractMeaningfulErrors instead of step.Errors, provide targeted hints.
    /// </summary>
    /// <summary>
    /// Classify a GitHub Actions step's phase from its name.
    /// </summary>
    private static string ClassifyStepPhase(string stepName)
    {
        if (stepName.StartsWith("Post ", StringComparison.OrdinalIgnoreCase))
            return "post";
        if (stepName.StartsWith("Pre ", StringComparison.OrdinalIgnoreCase))
            return "pre";
        if (stepName.Contains("checkout", StringComparison.OrdinalIgnoreCase) ||
            stepName.Contains("Set up job", StringComparison.OrdinalIgnoreCase) ||
            stepName.Contains("cache", StringComparison.OrdinalIgnoreCase) && !stepName.Contains("Run", StringComparison.OrdinalIgnoreCase))
            return "setup";
        return "main";
    }

    /// <summary>
    /// Build a diagnostic failed_step object from API step metadata.
    /// Used as fallback when log extraction yields nothing.
    /// </summary>
    private static JsonObject BuildStepInfoFromApi(JsonNode step)
    {
        var name = step["name"]?.GetValue<string>() ?? "unknown";
        var number = step["number"]?.GetValue<int>() ?? 0;
        var conclusion = step["conclusion"]?.GetValue<string>() ?? "unknown";
        var phase = ClassifyStepPhase(name);

        var info = new JsonObject
        {
            ["number"] = number,
            ["name"] = name,
            ["conclusion"] = conclusion,
            ["phase"] = phase,
        };

        // Provide diagnostic explanation for non-main phases
        if (phase == "post")
            info["diagnostic"] = "Post-job step failure — errors not in downloadable build logs. " +
                                 "Typically a cache upload, artifact publish, or cleanup issue.";
        else if (phase == "setup" || phase == "pre")
            info["diagnostic"] = "Setup/pre-job step failure — infrastructure issue, not a build/test error.";

        return info;
    }

    private static async Task EnrichWithLogErrorsAsync(
        IGitHubApi github, string owner, string repo, long jobId,
        JsonObject failure, ErrorBudget budget, ParsedStep? knownFailedStep,
        int maxTestNames = 20)
    {
        try
        {
            var log = await github.GetJobLog(owner, repo, jobId);

            // Build scan list: resolved step first, then all error-annotated steps,
            // then longest non-setup steps. This ensures we never miss errors just
            // because ResolveLogStep picked the wrong step.
            var stepsToScan = new List<ParsedStep>();
            var scannedNumbers = new HashSet<int>();

            // Priority 1: known failed step (cross-referenced from API)
            if (knownFailedStep != null)
            {
                var match = ResolveLogStep(knownFailedStep.Name, knownFailedStep.Number, log.Steps);
                if (match != null && scannedNumbers.Add(match.Number))
                    stepsToScan.Add(match);
            }

            // Priority 2: all steps with ##[error] annotations
            foreach (var s in log.Steps.Where(s => s.Errors.Count > 0))
            {
                if (scannedNumbers.Add(s.Number))
                    stepsToScan.Add(s);
            }

            // Priority 3: longest non-setup steps (likely build/test steps)
            // Only if we still have no scan targets
            if (stepsToScan.Count == 0)
            {
                var longSteps = log.Steps
                    .Where(s => (s.EndLine - s.StartLine) > 50)
                    .Where(s => !LogParser.IsSetupStep(s.Name))
                    .OrderByDescending(s => s.EndLine - s.StartLine)
                    .Take(3);
                foreach (var s in longSteps)
                {
                    if (scannedNumbers.Add(s.Number))
                        stepsToScan.Add(s);
                }
            }

            // Extract errors from log
            var stepsArr = new JsonArray();
            var allRawErrors = new List<string>();
            ParsedStep? primaryStep = null;

            foreach (var logStep in stepsToScan)
            {
                if (budget.Remaining <= 0) break;

                var meaningfulErrors = LogParser.ExtractMeaningfulErrors(log.Lines, logStep, budget.Remaining);
                if (meaningfulErrors.Count == 0) continue;

                primaryStep ??= logStep;

                var stepObj = new JsonObject
                {
                    ["number"] = logStep.Number,
                    ["name"] = logStep.Name,
                };

                var errorsArr = new JsonArray();
                foreach (var err in meaningfulErrors)
                {
                    if (budget.Remaining <= 0) break;

                    allRawErrors.Add(err);
                    var parsed = LogParser.TryParseError(err);
                    if (parsed != null)
                    {
                        errorsArr.Add(new JsonObject
                        {
                            ["raw"] = err,
                            ["code"] = parsed.Code,
                            ["message"] = parsed.Message,
                            ["file"] = parsed.File,
                            ["source_line"] = parsed.Line,
                        });
                    }
                    else
                    {
                        errorsArr.Add(err);
                    }
                    budget.Remaining--;
                }
                stepObj["errors"] = errorsArr;
                if (meaningfulErrors.Count > errorsArr.Count)
                    stepObj["total_errors"] = meaningfulErrors.Count;

                // Extract failed test names as structured data
                var testNames = LogParser.ExtractFailedTestNames(log.Lines, logStep);
                if (testNames.Count > 0)
                {
                    stepObj["failed_test_names"] = new JsonArray(
                        testNames.Take(maxTestNames).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                    if (testNames.Count > maxTestNames)
                        stepObj["failed_test_names_total"] = testNames.Count;
                }

                stepsArr.Add(stepObj);
            }

            if (stepsArr.Count > 0)
            {
                failure["failed_steps"] = stepsArr;

                // Classify failure type from extracted errors
                var failureType = ClassifyFailureType(allRawErrors, primaryStep);
                failure["failure_type"] = failureType;

                // Synthesize a one-line diagnosis from the highest-value error line
                if (allRawErrors.Count > 0)
                {
                    failure["diagnosis"] = SynthesizeDiagnosis(allRawErrors);
                }

                var hintStep = primaryStep ?? stepsToScan[0];
                failure["hint"] = StepLogsHint(owner, repo, jobId, stepNumber: hintStep.Number);
                var searchPattern = LogParser.SuggestSearchPattern(allRawErrors);
                failure["hint_search"] = SearchLogsHint(owner, repo, jobId, searchPattern);
                failure["next_action"] = StepLogsAction(owner, repo, jobId, stepNumber: hintStep.Number);
                return;
            }

            // Error extraction found nothing — try test summary extraction as last resort
            foreach (var s in log.Steps.Where(s =>
                LogParser.ClassifyStepType(s.Name) == "test" ||
                (s.EndLine - s.StartLine) > 100))
            {
                var summary = LogParser.ExtractTestSummary(log.Lines, s);
                if (summary != null && summary.Failed > 0)
                {
                    failure["failure_type"] = "test";
                    var summaryObj = new JsonObject
                    {
                        ["framework"] = summary.Framework,
                        ["total"] = summary.Total,
                        ["passed"] = summary.Passed,
                        ["failed"] = summary.Failed,
                        ["summary_line"] = summary.SummaryLine,
                    };

                    // Also extract test names for the summary
                    var fallbackTestNames = LogParser.ExtractFailedTestNames(log.Lines, s);
                    if (fallbackTestNames.Count > 0)
                    {
                        summaryObj["failed_test_names"] = new JsonArray(
                            fallbackTestNames.Take(maxTestNames).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                        if (fallbackTestNames.Count > maxTestNames)
                            summaryObj["failed_test_names_total"] = fallbackTestNames.Count;
                    }

                    failure["test_summary"] = summaryObj;
                    failure["diagnosis"] = $"Test failure: {summary.Failed} test(s) failed" +
                        (summary.Total > 0 ? $" of {summary.Total}" : "") +
                        $" ({summary.Framework})";
                    var pattern = LogParser.SuggestPatternForStepType("test");
                    failure["hint"] = StepLogsHint(owner, repo, jobId,
                        stepNumber: s.Number, pattern: pattern);
                    failure["hint_search"] = SearchLogsHint(owner, repo, jobId, pattern);
                    failure["next_action"] = StepLogsAction(owner, repo, jobId,
                        stepNumber: s.Number, pattern: pattern);
                    return;
                }
            }

            // Still nothing — include available_steps so agent can pick the right one
            var stepsWithInfo = BuildAvailableSteps(log.Steps);
            failure["available_steps"] = stepsWithInfo;

            // Fall back to API step metadata
            await FallbackToApiStepInfo(github, owner, repo, jobId, failure, knownFailedStep, log.Steps);
        }
        catch (Exception ex)
        {
            // Log download failed entirely — fall back to API step metadata
            failure["errors_note"] = $"Log download failed: {ex.Message}";
            await FallbackToApiStepInfo(github, owner, repo, jobId, failure, knownFailedStep, null);
        }
    }

    /// <summary>
    /// When log-based extraction fails, fetch job step metadata from API
    /// to identify which step failed. Cross-references API step names
    /// (YAML name: field) against log step names (run: command text) to
    /// produce hints that work with get_step_logs.
    /// </summary>
    private static async Task FallbackToApiStepInfo(
        IGitHubApi github, string owner, string repo, long jobId,
        JsonObject failure, ParsedStep? knownFailedStep, ParsedStep[]? logSteps)
    {
        try
        {
            var job = await github.GetJob(owner, repo, jobId);
            var steps = job?["steps"]?.AsArray();
            if (steps != null)
            {
                var failedApiStep = steps.FirstOrDefault(s =>
                    s?["conclusion"]?.GetValue<string>() == "failure");
                if (failedApiStep != null)
                {
                    var stepInfo = BuildStepInfoFromApi(failedApiStep);
                    stepInfo["source"] = "api";
                    failure["failed_step_info"] = stepInfo;

                    var phase = stepInfo["phase"]?.GetValue<string>();
                    var apiStepName = stepInfo["name"]?.GetValue<string>() ?? "";
                    var apiStepNum = stepInfo["number"]?.GetValue<int>() ?? 0;

                    if (phase == "post")
                    {
                        failure["failure_type"] = "infra";
                        failure["diagnosis"] = $"Post-job step '{apiStepName}' failed (infrastructure, not code)";
                        failure["diagnostic_path"] = "Failure is in a post-job step — not in downloadable build logs. " +
                            "This is typically a cache, artifact, or cleanup issue, not a code problem.";
                        failure["hint"] = SearchLogsHint(owner, repo, jobId,
                            "error|FAIL", includeSetup: true);
                        failure["next_action"] = SearchLogsAction(owner, repo, jobId,
                            "error|FAIL", includeSetup: true);
                    }
                    else
                    {
                        // Use log step name if we can find a match, otherwise let auto-detect work
                        var logStep = ResolveLogStep(apiStepName, apiStepNum, logSteps);
                        if (logStep != null)
                        {
                            var stepType = LogParser.ClassifyStepType(logStep.Name);
                            var searchPat = LogParser.SuggestPatternForStepType(stepType);
                            failure["hint"] = StepLogsHint(owner, repo, jobId,
                                stepNumber: logStep.Number, pattern: searchPat);
                            failure["next_action"] = StepLogsAction(owner, repo, jobId,
                                stepNumber: logStep.Number, pattern: searchPat);
                        }
                        else
                        {
                            failure["hint"] = StepLogsHint(owner, repo, jobId, pattern: "error|FAIL");
                            failure["next_action"] = SearchLogsAction(owner, repo, jobId, "error|FAIL");
                        }
                        failure["hint_search"] = SearchLogsHint(owner, repo, jobId,
                            "error|FAIL", includeSetup: true);
                    }
                    return;
                }
            }
        }
        catch
        {
            // API fallback also failed — give best possible hint
        }

        // Last resort
        failure["hint"] = StepLogsHint(owner, repo, jobId, pattern: "error|FAIL");
        failure["next_action"] = SearchLogsAction(owner, repo, jobId, "error|FAIL");
        failure["diagnostic_path"] = "Could not determine failure cause from logs or API. " +
            $"Try: {SearchLogsHint(owner, repo, jobId, "error|FAIL", includeSetup: true)}";
    }

    /// <summary>
    /// Match an API step (YAML name + number) to a parsed log step.
    /// Strategies: exact name match, substring overlap, positional proximity.
    /// </summary>
    internal static ParsedStep? ResolveLogStep(string apiName, int apiNumber, ParsedStep[]? logSteps)
    {
        if (logSteps == null || logSteps.Length == 0) return null;

        // 1. Exact name match (unlikely but fast)
        var exact = logSteps.FirstOrDefault(s =>
            s.Name.Equals(apiName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // 2. Substring match: API name contains log step name or vice versa
        var substring = logSteps.FirstOrDefault(s =>
            apiName.Contains(s.Name, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains(apiName, StringComparison.OrdinalIgnoreCase));
        if (substring != null) return substring;

        // 3. Word overlap: find log step with most words in common with API name
        var apiWords = apiName.Split([' ', '-', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2) // skip short words like "in", "to", "a"
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        if (apiWords.Count > 0)
        {
            var bestMatch = logSteps
                .Select(s => {
                    var stepWords = s.Name.Split([' ', '-', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.ToLowerInvariant())
                        .ToHashSet();
                    return (step: s, overlap: apiWords.Intersect(stepWords).Count());
                })
                .Where(x => x.overlap >= 2) // require at least 2 words in common
                .OrderByDescending(x => x.overlap)
                .FirstOrDefault();

            if (bestMatch.step != null) return bestMatch.step;
        }

        // 4. Positional: API step N is often log step N-1 or nearby (setup steps shift numbering).
        // Try steps around the API number, preferring ones with errors.
        var nearby = logSteps
            .Where(s => Math.Abs(s.Number - apiNumber) <= 3)
            .OrderBy(s => s.Errors.Count > 0 ? 0 : 1) // prefer steps with errors
            .ThenBy(s => Math.Abs(s.Number - apiNumber))
            .FirstOrDefault();
        if (nearby != null) return nearby;

        // 5. Last resort: last log step with errors
        return logSteps.LastOrDefault(s => s.Errors.Count > 0);
    }

    /// <summary>
    /// Classify the failure type from the primary step name and extracted error lines.
    /// Uses step type as primary signal, error patterns as secondary.
    /// </summary>
    private static string ClassifyFailureType(List<string> errors, ParsedStep? primaryStep = null)
    {
        // Primary signal: step name classification
        if (primaryStep != null)
        {
            var stepType = LogParser.ClassifyStepType(primaryStep.Name);
            if (stepType == "build") return "build";
            if (stepType == "test") return "test";
        }

        if (errors.Count == 0) return "unknown";

        // Secondary signal: error content patterns
        bool hasCompiler = errors.Any(e => LogParser.TryParseError(e) != null);
        bool hasDeps = errors.Any(e => e.Contains("Mismatch", StringComparison.OrdinalIgnoreCase) ||
                                       e.Contains("Dependencies", StringComparison.OrdinalIgnoreCase));
        bool hasTest = errors.Any(e => e.Contains("FAILED:", StringComparison.Ordinal) ||
                                       e.Contains("CRASHED", StringComparison.OrdinalIgnoreCase) ||
                                       e.Contains("REQUIRE(", StringComparison.Ordinal) ||
                                       e.Contains("CHECK(", StringComparison.Ordinal) ||
                                       e.Contains("test cases:", StringComparison.OrdinalIgnoreCase));
        bool hasInfra = errors.Any(e => e.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                                        e.Contains("harden-runner", StringComparison.OrdinalIgnoreCase) ||
                                        e.Contains("artifact", StringComparison.OrdinalIgnoreCase));

        if (hasCompiler) return "build";
        if (hasDeps) return "dependency";
        if (hasTest) return "test";
        if (hasInfra) return "infra";
        return "unknown";
    }

    /// <summary>
    /// Score a line's diagnostic value. Higher = more actionable for a fix agent.
    /// </summary>
    private static int DiagnosticScore(string line)
    {
        if (LogParser.TryParseError(line) != null) return 10; // MSVC/GCC structured error
        if (Regex.IsMatch(line, @"FAILED:\s*", RegexOptions.IgnoreCase)) return 9;
        if (Regex.IsMatch(line, @"REQUIRE\s*\(|CHECK\s*\(", RegexOptions.IgnoreCase)) return 9;
        if (line.Contains("CRASHED", StringComparison.OrdinalIgnoreCase)) return 9;
        if (Regex.IsMatch(line, @"_load failed|_program_load|error -\d+", RegexOptions.IgnoreCase)) return 8;
        if (line.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)) return 7;
        if (Regex.IsMatch(line, @"test cases:.*failed", RegexOptions.IgnoreCase)) return 7;
        if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return 5;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1;
    }

    /// <summary>
    /// Synthesize a one-line diagnosis from extracted errors, picking the highest-value line.
    /// </summary>
    private static string SynthesizeDiagnosis(List<string> errors)
    {
        // Find the highest-ranked error line
        var best = errors.OrderByDescending(DiagnosticScore).First();
        var score = DiagnosticScore(best);

        // Format based on type
        var parsed = LogParser.TryParseError(best);
        if (parsed != null)
            return $"Build error: {parsed.Code} {parsed.Message} at {Path.GetFileName(parsed.File ?? "")}:{parsed.Line}";

        if (errors.Any(e => e.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)))
            return $"DLL dependency mismatch ({errors.Count(e => e.Contains("Mismatch", StringComparison.OrdinalIgnoreCase))} binaries)";

        if (best.Contains("FAILED:", StringComparison.Ordinal))
            return $"Test failure: {Truncate(best, 120)}";

        if (best.Contains("CRASHED", StringComparison.OrdinalIgnoreCase))
            return "Test crashed during execution";

        return Truncate(best, 120);
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;

    /// <summary>
    /// Build a compact available_steps list for empty-error responses.
    /// Includes step type classification and error indicators.
    /// </summary>
    private static JsonArray BuildAvailableSteps(ParsedStep[] logSteps)
    {
        var arr = new JsonArray();
        foreach (var s in logSteps)
        {
            if (LogParser.IsSetupStep(s.Name)) continue;
            var lineCount = s.EndLine - s.StartLine + 1;
            if (lineCount < 5) continue; // skip trivial steps

            var entry = new JsonObject
            {
                ["number"] = s.Number,
                ["name"] = s.Name,
                ["lines"] = lineCount,
            };
            if (s.Errors.Count > 0)
                entry["has_errors"] = true;
            var stepType = LogParser.ClassifyStepType(s.Name);
            if (stepType != "unknown")
                entry["type"] = stepType;
            arr.Add(entry);
        }
        return arr;
    }

    /// <summary>
    /// Fix 1+6: No passing list. Separate skipped/cancelled counts. Cancelled array at jobs/errors detail.
    /// </summary>
    private static JsonObject BuildResult(string scope, int total, int passed, int failed,
        int pending, int skipped, int cancelled,
        JsonArray failures, JsonArray cancelledList, JsonArray pendingList)
    {
        var result = new JsonObject
        {
            ["scope"] = scope,
            ["provider"] = "github",
            ["summary"] = new JsonObject
            {
                ["total"] = total,
                ["passed"] = passed,
                ["failed"] = failed,
                ["skipped"] = skipped,
                ["cancelled"] = cancelled,
                ["pending"] = pending,
            },
            ["failures"] = failures,
        };

        if (cancelledList.Count > 0) result["cancelled"] = cancelledList;
        if (pendingList.Count > 0) result["pending"] = pendingList;

        return result;
    }

    // ──────────────────────────────────────────────────────────────
    //  Tool 2: search_job_logs
    // ──────────────────────────────────────────────────────────────

    private static void RegisterSearchJobLogs(McpServer server, IGitHubApi github, CiProviderResolver? resolver)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "search_job_logs",
            Description = "Regex search in a CI job's full log (GitHub Actions / Azure Pipelines). " +
                          "Returns matches with context lines and parsed MSVC/GCC error info. " +
                          "Setup/checkout boilerplate is filtered by default. " +
                          "Identify job by job_id, or by run_id + job_name to auto-resolve. " +
                          "For ADO: use url param or job_id as 'buildId:logId' string. " +
                          "Response: { total_matches, showing, matches: [{line, text, context[], " +
                          "parsed?: {type, code, message, file, source_line}}], " +
                          "log_lines, filtered_setup_matches?, step?: {number, name} }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["owner"] = new JsonObject { ["type"] = "string", ["description"] = "Repository owner (optional if repo is 'owner/repo' or url provided)" },
                    ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "Repository name, or 'owner/repo' shorthand" },
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "CI URL — auto-detects GitHub or ADO. For ADO, owner/repo are not needed.",
                    },
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = new JsonArray("number", "string"),
                        ["description"] = "Job ID — numeric for GitHub, 'buildId:logId' string for ADO",
                    },
                    ["run_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Workflow/build run ID — used with job_name to resolve job_id automatically",
                    },
                    ["job_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Job name substring — used with run_id to find the job (e.g. 'msbuild', 'ARM64')",
                    },
                    ["pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex pattern (e.g. 'error C\\\\d+', 'FAIL', 'Mismatch')",
                    },
                    ["step_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Restrict search to a specific step by name (substring match)",
                    },
                    ["step_number"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Restrict search to a specific step by number",
                    },
                    ["context_lines"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Lines of context around each match (default: 3)",
                        ["default"] = 3,
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Maximum matches to return (default: 20)",
                        ["default"] = 20,
                    },
                    ["skip"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Skip first N matches (default: 0)",
                        ["default"] = 0,
                    },
                    ["include_setup"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include matches in setup/checkout steps (default: false)",
                        ["default"] = false,
                    },
                },
                ["required"] = new JsonArray("pattern"),
            },
            Handler = args =>
            {
                var owner = args["owner"]?.GetValue<string>();
                var repo = args["repo"]?.GetValue<string>();
                var pattern = args["pattern"]!.GetValue<string>();
                var contextLines = args["context_lines"]?.GetValue<int>() ?? 3;
                var maxResults = args["max_results"]?.GetValue<int>() ?? 20;
                var skip = args["skip"]?.GetValue<int>() ?? 0;
                var includeSetup = args["include_setup"]?.GetValue<bool>() ?? false;
                var stepNameFilter = args["step_name"]?.GetValue<string>();
                var stepNumberFilter = args["step_number"]?.GetValue<int>();

                // Check for ADO job_id (string with colon) or ADO URL
                var jobIdRaw = args["job_id"]?.ToString();
                if (jobIdRaw != null && jobIdRaw.Contains(':') && resolver != null)
                {
                    try
                    {
                        return SearchAdoJobLogsAsync(resolver, jobIdRaw, pattern,
                            contextLines, maxResults, skip, includeSetup, stepNameFilter, stepNumberFilter)
                            .GetAwaiter().GetResult();
                    }
                    catch (HttpRequestException ex)
                    {
                        return HandleHttpError(ex);
                    }
                }

                var url = args["url"]?.GetValue<string>();
                if (url != null && resolver != null)
                {
                    var resolved = resolver.ResolveFromUrl(url);
                    if (resolved?.Provider.ProviderName == "ado")
                    {
                        throw new ArgumentException("For ADO log search, provide job_id as 'buildId:logId' " +
                            "(found in get_ci_failures results)");
                    }
                    // GitHub URL — extract owner/repo
                    var parsed = ParseUrl(url);
                    owner ??= parsed.owner;
                    repo ??= parsed.repo;
                }

                var jobId = ParseLongParam(args["job_id"]);
                var runId = ParseLongParam(args["run_id"]);
                var jobName = args["job_name"]?.GetValue<string>();

                // Support "owner/repo" shorthand in repo param
                if (repo != null && repo.Contains('/'))
                {
                    var parts = repo.Split('/', 2);
                    owner ??= parts[0];
                    repo = parts[1];
                }

                if (owner == null || repo == null)
                    throw new ArgumentException("owner and repo are required (provide repo as 'owner/repo', url, or set both)");

                // Resolve job_id from run_id + job_name if needed
                if (!jobId.HasValue && runId.HasValue)
                {
                    jobId = ResolveJobIdAsync(github, owner, repo, runId.Value, jobName)
                        .GetAwaiter().GetResult();
                }

                if (!jobId.HasValue)
                    throw new ArgumentException("job_id is required (or provide run_id + job_name to resolve it)");

                try
                {
                    return SearchJobLogsAsync(github, owner, repo, jobId.Value, pattern,
                        contextLines, maxResults, skip, includeSetup, stepNameFilter, stepNumberFilter)
                        .GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex)
                {
                    return HandleHttpError(ex);
                }
            },
        });
    }

    /// <summary>
    /// Fix 7: Filter setup block matches unless include_setup is true.
    /// </summary>
    private static async Task<JsonNode> SearchJobLogsAsync(
        IGitHubApi github, string owner, string repo, long jobId,
        string pattern, int contextLines, int maxResults, int skip, bool includeSetup,
        string? stepNameFilter = null, int? stepNumberFilter = null)
    {
        var log = await github.GetJobLog(owner, repo, jobId);
        return SearchInParsedLog(
            new ParsedLog { Lines = log.Lines, Steps = log.Steps },
            pattern, contextLines, maxResults, skip, includeSetup, stepNameFilter, stepNumberFilter);
    }

    /// <summary>
    /// Provider-agnostic log search. Operates on ParsedLog — shared by GitHub and ADO paths.
    /// </summary>
    private static JsonNode SearchInParsedLog(
        ParsedLog log, string pattern, int contextLines, int maxResults, int skip, bool includeSetup,
        string? stepNameFilter = null, int? stepNumberFilter = null)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Determine scan range based on step filtering
        int scanStart = 0;
        int scanEnd = log.Lines.Length - 1;
        ParsedStep? scopedStep = null;

        if (stepNumberFilter.HasValue)
        {
            scopedStep = log.Steps.FirstOrDefault(s => s.Number == stepNumberFilter.Value);
        }
        else if (stepNameFilter != null)
        {
            scopedStep = log.Steps.FirstOrDefault(s =>
                s.Name.Contains(stepNameFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (scopedStep != null)
        {
            scanStart = scopedStep.StartLine;
            scanEnd = scopedStep.EndLine;
        }

        var allMatches = new List<(int lineNum, string text)>();
        int filteredSetup = 0;

        for (int i = scanStart; i <= scanEnd && i < log.Lines.Length; i++)
        {
            var stripped = LogParser.StripTimestamp(log.Lines[i]);
            if (!regex.IsMatch(stripped)) continue;

            if (!includeSetup && LogParser.IsInSetupBlock(log.Lines, i))
            {
                filteredSetup++;
                continue;
            }

            allMatches.Add((i, stripped));
        }

        var matches = new JsonArray();
        foreach (var (lineNum, text) in allMatches.Skip(skip).Take(maxResults))
        {
            var context = new JsonArray();
            int start = Math.Max(0, lineNum - contextLines);
            int end = Math.Min(log.Lines.Length - 1, lineNum + contextLines);
            for (int j = start; j <= end; j++)
            {
                var prefix = j == lineNum ? ">>> " : "    ";
                context.Add(prefix + LogParser.StripTimestamp(log.Lines[j]));
            }

            var match = new JsonObject
            {
                ["line"] = lineNum + 1,
                ["text"] = text,
                ["context"] = context,
            };

            var parsed = LogParser.TryParseError(text);
            if (parsed != null)
            {
                match["parsed"] = new JsonObject
                {
                    ["type"] = parsed.Type,
                    ["code"] = parsed.Code,
                    ["message"] = parsed.Message,
                    ["file"] = parsed.File,
                    ["source_line"] = parsed.Line,
                };
            }

            matches.Add(match);
        }

        var result = new JsonObject
        {
            ["total_matches"] = allMatches.Count,
            ["showing"] = allMatches.Count == 0
                ? "0 of 0"
                : $"{skip + 1}-{Math.Min(skip + maxResults, allMatches.Count)} of {allMatches.Count}",
            ["matches"] = matches,
            ["log_lines"] = log.Lines.Length,
        };

        if (filteredSetup > 0)
            result["filtered_setup_matches"] = filteredSetup;

        if (scopedStep != null)
            result["step"] = new JsonObject { ["number"] = scopedStep.Number, ["name"] = scopedStep.Name };

        return result;
    }

    /// <summary>
    /// Search ADO job logs — gets ParsedLog from provider, then delegates to shared search.
    /// </summary>
    private static async Task<JsonNode> SearchAdoJobLogsAsync(
        CiProviderResolver resolver, string adoJobId, string pattern,
        int contextLines, int maxResults, int skip, bool includeSetup,
        string? stepNameFilter, int? stepNumberFilter)
    {
        var parts = adoJobId.Split(':');
        if (parts.Length != 2)
            throw new ArgumentException($"ADO job_id must be 'buildId:logId', got '{adoJobId}'");

        var provider = resolver.GetCachedAdoProvider()
            ?? throw new ArgumentException("No ADO provider available. Call get_ci_failures with an ADO URL first.");

        var log = await provider.GetJobLogAsync(adoJobId);
        return SearchInParsedLog(log, pattern, contextLines, maxResults, skip, includeSetup,
            stepNameFilter, stepNumberFilter);
    }

    // ──────────────────────────────────────────────────────────────
    //  Tool 3: get_step_logs
    // ──────────────────────────────────────────────────────────────

    private static void RegisterGetStepLogs(McpServer server, IGitHubApi github, CiProviderResolver? resolver)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_step_logs",
            Description = "Get one step's log lines from a CI job (GitHub Actions / Azure Pipelines). " +
                          "Identify step by name (substring match) or number. " +
                          "Defaults to the tail of the last failed step. " +
                          "Use pattern param to search within the step — returns max_lines centered " +
                          "around the first match (±max_lines/2, default ±25 lines). " +
                          "Identify job by job_id, or by run_id + job_name to auto-resolve. " +
                          "Response: { step: {number, name, errors_count}, lines[], total_step_lines, " +
                          "from_line, to_line, truncated, match_line? } " +
                          "or { ambiguous: true, matches: [{number, name, has_errors}], hint } " +
                          "or { error, available_steps: [{number, name, has_errors}] }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["owner"] = new JsonObject { ["type"] = "string", ["description"] = "Repository owner (optional if repo is 'owner/repo' or url provided)" },
                    ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "Repository name, or 'owner/repo' shorthand" },
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "CI URL — auto-detects GitHub or ADO. For ADO, owner/repo are not needed.",
                    },
                    ["job_id"] = new JsonObject
                    {
                        ["type"] = new JsonArray("number", "string"),
                        ["description"] = "Job ID — numeric for GitHub, 'buildId:logId' string for ADO",
                    },
                    ["run_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Workflow run ID — used with job_name to resolve job_id automatically",
                    },
                    ["job_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Job name substring — used with run_id to find the job (e.g. 'msbuild', 'ARM64')",
                    },
                    ["step_name"] = new JsonObject { ["type"] = "string", ["description"] = "Step name (substring match)" },
                    ["step_number"] = new JsonObject { ["type"] = "number", ["description"] = "Step number (alternative to step_name)" },
                    ["pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex pattern — if provided, return lines centered around matches within the step instead of the tail",
                    },
                    ["from_line"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Start from this line within the step (omit for tail)",
                    },
                    ["max_lines"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Max lines to return (default: 50)",
                        ["default"] = 50,
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                var owner = args["owner"]?.GetValue<string>();
                var repo = args["repo"]?.GetValue<string>();
                var stepName = args["step_name"]?.GetValue<string>();
                var stepNumber = args["step_number"]?.GetValue<int>();
                var pattern = args["pattern"]?.GetValue<string>();
                var fromLine = args["from_line"]?.GetValue<int>();
                var maxLines = args["max_lines"]?.GetValue<int>() ?? 50;

                // Check for ADO job_id (string with colon)
                var jobIdRaw = args["job_id"]?.ToString();
                if (jobIdRaw != null && jobIdRaw.Contains(':') && resolver != null)
                {
                    try
                    {
                        return GetAdoStepLogsAsync(resolver, jobIdRaw, stepName, stepNumber, pattern, fromLine, maxLines)
                            .GetAwaiter().GetResult();
                    }
                    catch (HttpRequestException ex)
                    {
                        return HandleHttpError(ex);
                    }
                }

                var url = args["url"]?.GetValue<string>();
                if (url != null && resolver != null)
                {
                    var resolved = resolver.ResolveFromUrl(url);
                    if (resolved?.Provider.ProviderName == "ado")
                    {
                        throw new ArgumentException("For ADO step logs, provide job_id as 'buildId:logId' " +
                            "(found in get_ci_failures results)");
                    }
                    var parsed = ParseUrl(url);
                    owner ??= parsed.owner;
                    repo ??= parsed.repo;
                }

                var jobId = ParseLongParam(args["job_id"]);
                var runId = ParseLongParam(args["run_id"]);
                var jobName = args["job_name"]?.GetValue<string>();

                // Support "owner/repo" shorthand in repo param
                if (repo != null && repo.Contains('/'))
                {
                    var parts = repo.Split('/', 2);
                    owner ??= parts[0];
                    repo = parts[1];
                }

                if (owner == null || repo == null)
                    throw new ArgumentException("owner and repo are required (provide repo as 'owner/repo', url, or set both)");

                // Resolve job_id from run_id + job_name if needed
                if (!jobId.HasValue && runId.HasValue)
                {
                    jobId = ResolveJobIdAsync(github, owner, repo, runId.Value, jobName)
                        .GetAwaiter().GetResult();
                }

                if (!jobId.HasValue)
                    throw new ArgumentException("job_id is required (or provide run_id + job_name to resolve it)");

                try
                {
                    return GetStepLogsAsync(github, owner, repo, jobId.Value, stepName, stepNumber, pattern, fromLine, maxLines)
                        .GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex)
                {
                    return HandleHttpError(ex);
                }
            },
        });
    }

    /// <summary>
    /// Get step logs from ADO via ICiProvider.
    /// </summary>
    private static async Task<JsonNode> GetAdoStepLogsAsync(
        CiProviderResolver resolver, string adoJobId,
        string? stepName, int? stepNumber, string? pattern, int? fromLine, int maxLines)
    {
        var provider = resolver.GetCachedAdoProvider()
            ?? throw new ArgumentException("No ADO provider available. Call get_ci_failures with an ADO URL first.");

        var log = await provider.GetJobLogAsync(adoJobId);

        // Reuse the same step-finding and line-extraction logic
        return FormatStepLogs(log, stepName, stepNumber, pattern, fromLine, maxLines);
    }

    private static async Task<JsonNode> GetStepLogsAsync(
        IGitHubApi github, string owner, string repo, long jobId,
        string? stepName, int? stepNumber, string? pattern, int? fromLine, int maxLines)
    {
        var log = await github.GetJobLog(owner, repo, jobId);
        return FormatStepLogs(new ParsedLog { Lines = log.Lines, Steps = log.Steps },
            stepName, stepNumber, pattern, fromLine, maxLines);
    }

    private static JsonNode FormatStepLogs(ParsedLog log,
        string? stepName, int? stepNumber, string? pattern, int? fromLine, int maxLines)
    {
        ParsedStep[] matchingSteps;
        if (stepNumber.HasValue)
        {
            matchingSteps = log.Steps.Where(s => s.Number == stepNumber.Value).ToArray();
        }
        else if (stepName != null)
        {
            matchingSteps = log.Steps
                .Where(s => s.Name.Contains(stepName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            // Default: find step with meaningful errors (not just "Process completed with exit code 1")
            var failed = log.Steps.LastOrDefault(s =>
                s.Errors.Any(e => !e.StartsWith("Process completed with exit code")) ||
                LogParser.ExtractMeaningfulErrors(log.Lines, s, 1).Count > 0);
            // Fallback: any step with ##[error], then last step
            failed ??= log.Steps.LastOrDefault(s => s.Errors.Count > 0);
            matchingSteps = failed != null ? [failed] : log.Steps.Length > 0 ? [log.Steps[^1]] : [];
        }

        if (matchingSteps.Length == 0)
        {
            return new JsonObject
            {
                ["error"] = "No matching step found",
                ["available_steps"] = new JsonArray(
                    log.Steps.Select(s => (JsonNode)new JsonObject
                    {
                        ["number"] = s.Number,
                        ["name"] = s.Name,
                        ["has_errors"] = s.Errors.Count > 0,
                    }).ToArray()),
            };
        }

        if (matchingSteps.Length > 1)
        {
            return new JsonObject
            {
                ["ambiguous"] = true,
                ["matches"] = new JsonArray(
                    matchingSteps.Select(s => (JsonNode)new JsonObject
                    {
                        ["number"] = s.Number,
                        ["name"] = s.Name,
                        ["has_errors"] = s.Errors.Count > 0,
                    }).ToArray()),
                ["hint"] = "Specify step_number to disambiguate",
            };
        }

        var step = matchingSteps[0];
        int totalStepLines = step.EndLine - step.StartLine + 1;

        int start, end;
        int? matchLine = null;
        if (fromLine.HasValue)
        {
            start = step.StartLine + fromLine.Value;
            end = Math.Min(start + maxLines - 1, step.EndLine);
        }
        else if (pattern != null)
        {
            // Search within step lines for the pattern and center the window
            var patternRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            int? firstMatchIdx = null;
            for (int i = step.StartLine; i <= step.EndLine && i < log.Lines.Length; i++)
            {
                if (patternRegex.IsMatch(LogParser.StripTimestamp(log.Lines[i])))
                {
                    firstMatchIdx = i;
                    break;
                }
            }

            if (firstMatchIdx.HasValue)
            {
                matchLine = firstMatchIdx.Value - step.StartLine;
                int half = maxLines / 2;
                start = Math.Max(step.StartLine, firstMatchIdx.Value - half);
                end = Math.Min(step.EndLine, start + maxLines - 1);
                start = Math.Max(step.StartLine, end - maxLines + 1);
            }
            else
            {
                // Pattern not found — fall back to tail
                end = step.EndLine;
                start = Math.Max(step.StartLine, end - maxLines + 1);
            }
        }
        else
        {
            end = step.EndLine;
            start = Math.Max(step.StartLine, end - maxLines + 1);
        }

        var lines = new JsonArray();
        for (int i = start; i <= end && i < log.Lines.Length; i++)
        {
            lines.Add(LogParser.StripTimestamp(log.Lines[i]));
        }

        var result = new JsonObject
        {
            ["step"] = new JsonObject
            {
                ["number"] = step.Number,
                ["name"] = step.Name,
                ["errors_count"] = step.Errors.Count,
            },
            ["lines"] = lines,
            ["total_step_lines"] = totalStepLines,
            ["from_line"] = start - step.StartLine,
            ["to_line"] = end - step.StartLine,
            ["truncated"] = (end - start + 1) < totalStepLines,
        };

        if (matchLine.HasValue)
            result["match_line"] = matchLine.Value;

        return result;
    }
}
