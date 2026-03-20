// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Engine;

namespace CiDebugMcp.Tests;

/// <summary>
/// In-memory fake for IGitHubApi. Returns canned responses keyed by API path.
/// </summary>
public sealed class FakeGitHubApi : IGitHubApi
{
    private readonly Dictionary<string, JsonNode?> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, LogCache.CachedLog> _logs = new();

    /// <summary>Register a canned JSON response for a given API path.</summary>
    public void SetJson(string pathPrefix, JsonNode? response) =>
        _responses[pathPrefix] = response;

    /// <summary>Register a canned job log.</summary>
    public void SetJobLog(long jobId, string logText)
    {
        var lines = logText.Split('\n');
        var steps = LogParser.ParseSteps(lines);
        _logs[jobId] = new LogCache.CachedLog { RawText = logText, Lines = lines, Steps = steps };
    }

    public Task<JsonNode?> GetJsonAsync(string path)
    {
        // Match the most specific key first (longest match)
        var match = _responses.Keys
            .Where(key => path.Contains(key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(key => key.Length)
            .FirstOrDefault();

        if (match != null)
            return Task.FromResult(_responses[match] is not null
                ? JsonNode.Parse(_responses[match]!.ToJsonString())
                : null);

        throw new HttpRequestException($"FakeGitHubApi: no canned response for {path}");
    }

    public async Task<JsonObject> GetPullRequest(string owner, string repo, int prNumber)
    {
        var json = await GetJsonAsync($"/repos/{owner}/{repo}/pulls/{prNumber}");
        return json?.AsObject() ?? throw new InvalidOperationException("No PR data");
    }

    public async Task<string[]> GetPullRequestFiles(string owner, string repo, int prNumber)
    {
        try
        {
            var json = await GetJsonAsync($"/repos/{owner}/{repo}/pulls/{prNumber}/files");
            return json?.AsArray()
                ?.Select(f => f?["filename"]?.GetValue<string>() ?? "")
                .Where(f => f.Length > 0)
                .ToArray() ?? [];
        }
        catch { return []; }
    }

    public async Task<JsonObject?> GetJob(string owner, string repo, long jobId)
    {
        try
        {
            var json = await GetJsonAsync($"/repos/{owner}/{repo}/actions/jobs/{jobId}");
            return json?.AsObject();
        }
        catch { return null; }
    }

    public async Task<JsonArray> GetCheckRuns(string owner, string repo, string sha)
    {
        var json = await GetJsonAsync($"/repos/{owner}/{repo}/commits/{sha}/check-runs");
        return json?["check_runs"]?.AsArray() ?? [];
    }

    public Task<JsonArray> GetJobs(string owner, string repo, long runId)
    {
        // Route to canned response keyed by the runs/{runId}/jobs path
        var result = GetJsonAsync($"/repos/{owner}/{repo}/actions/runs/{runId}/jobs")
            .GetAwaiter().GetResult();
        return Task.FromResult(result?["jobs"]?.AsArray() ?? []);
    }

    public Task<LogCache.CachedLog> GetJobLog(string owner, string repo, long jobId)
    {
        if (_logs.TryGetValue(jobId, out var log))
            return Task.FromResult(log);
        throw new InvalidOperationException($"FakeGitHubApi: no log for job {jobId}");
    }

    public HttpClient CreateAuthenticatedClient() => new();
}
