// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace CiDebugMcp.Engine;

/// <summary>
/// Abstraction over GitHub API for testability.
/// </summary>
public interface IGitHubApi
{
    Task<JsonObject> GetPullRequest(string owner, string repo, int prNumber);
    Task<string[]> GetPullRequestFiles(string owner, string repo, int prNumber);
    Task<JsonObject?> GetJob(string owner, string repo, long jobId);
    Task<JsonArray> GetCheckRuns(string owner, string repo, string sha);
    Task<JsonArray> GetJobs(string owner, string repo, long runId);
    Task<LogCache.CachedLog> GetJobLog(string owner, string repo, long jobId);
    Task<JsonNode?> GetJsonAsync(string path);
    HttpClient CreateAuthenticatedClient();
}
