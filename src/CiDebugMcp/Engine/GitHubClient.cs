using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CiDebugMcp.Engine;

/// <summary>
/// Minimal GitHub API client for CI/CD operations.
/// Resolves authentication from multiple sources: GITHUB_TOKEN env, GH_TOKEN env,
/// Git Credential Manager (supports MSFT SSO), or GitHub CLI.
/// </summary>
public sealed class GitHubClient : IGitHubApi
{
    private readonly HttpClient _http;
    private readonly LogCache _cache;
    private bool _authResolved;

    public GitHubClient(LogCache cache)
    {
        _cache = cache;

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            BaseAddress = new Uri("https://api.github.com"),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ci-debug-mcp", "0.1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    }

    /// <summary>
    /// Create a new HttpClient with the same auth for use by download jobs.
    /// Each download needs its own client (separate connection, no base address for redirect URLs).
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        EnsureAuth();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromHours(2), // Downloads can take a long time for large artifacts
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ci-debug-mcp", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (_http.DefaultRequestHeaders.Authorization != null)
        {
            client.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
        }
        return client;
    }

    /// <summary>
    /// Resolve auth lazily on first API call (avoids GCM popup at server startup).
    /// </summary>
    private void EnsureAuth()
    {
        if (_authResolved) return;
        _authResolved = true;

        var (token, source) = ResolveToken();
        if (token != null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
            Console.Error.WriteLine($"ci-debug-mcp: authenticated via {source}");
        }
        else
        {
            Console.Error.WriteLine("ci-debug-mcp: no authentication found — log downloads will fail (403). " +
                                    "Set GITHUB_TOKEN, or ensure Git Credential Manager or gh CLI is configured.");
        }
    }

    /// <summary>
    /// Try multiple credential sources in priority order.
    /// </summary>
    private static (string? token, string source) ResolveToken()
    {
        // 1. Explicit env vars (highest priority — user/CI override)
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            return (token, "GITHUB_TOKEN env var");

        token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrEmpty(token))
            return (token, "GH_TOKEN env var");

        // 2. Git Credential Manager (works with MSFT SSO, AAD, OAuth)
        token = GetTokenFromGitCredentialManager();
        if (!string.IsNullOrEmpty(token))
            return (token, "Git Credential Manager");

        // 3. GitHub CLI (if installed)
        token = GetTokenFromGhCli();
        if (!string.IsNullOrEmpty(token))
            return (token, "GitHub CLI (gh auth)");

        return (null, "none");
    }

    /// <summary>
    /// Get token from Git Credential Manager by piping a credential query to `git credential fill`.
    /// This works with MSFT SSO (gho_ OAuth tokens), PATs, and any other GCM-stored credential.
    /// Uses GCM_INTERACTIVE=never to avoid UI prompts, with username disambiguation fallback.
    /// </summary>
    private static string? GetTokenFromGitCredentialManager()
    {
        // Try without username first (works when only one credential is stored)
        var token = TryGcmFill(null);
        if (token != null) return token;

        // GCM may have failed due to multiple accounts — try with username from:
        // 1. credential.username git config
        // 2. GitHub username parsed from origin remote URL
        var username = GetGitConfigValue("credential.username");
        if (username != null)
        {
            token = TryGcmFill(username);
            if (token != null) return token;
        }

        // Parse GitHub username from remote URL (e.g., github.com/mikeagun/repo)
        var remoteUrl = GetGitConfigValue("remote.origin.url");
        if (remoteUrl != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(remoteUrl,
                @"github\.com[/:]([^/]+)/");
            if (m.Success)
            {
                var remoteUser = m.Groups[1].Value;
                token = TryGcmFill(remoteUser);
                if (token != null) return token;
            }
        }

        return null;
    }

    private static string? TryGcmFill(string? username)
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
            // Prevent UI prompts — fail fast if GCM can't resolve non-interactively
            psi.Environment["GCM_INTERACTIVE"] = "never";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // Send credential query — include username if provided to disambiguate
            var query = "protocol=https\nhost=github.com\n";
            if (username != null)
                query += $"username={username}\n";
            query += "\n";
            proc.StandardInput.Write(query);
            proc.StandardInput.Close();

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5_000))
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            if (proc.ExitCode != 0) return null;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                    return trimmed[9..];
            }
        }
        catch
        {
            // git not on PATH, or GCM not configured
        }

        return null;
    }

    private static string? GetGitConfigValue(string key)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"config --get {key}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var value = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(3_000)) return null;
            return proc.ExitCode == 0 && value.Length > 0 ? value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Get token from GitHub CLI if installed.
    /// </summary>
    private static string? GetTokenFromGhCli()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var token = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(5_000)) return null;

            return proc.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch
        {
            // gh not installed — that's fine
            return null;
        }
    }

    /// <summary>
    /// Get PR metadata (head SHA, base ref).
    /// </summary>
    public async Task<JsonObject> GetPullRequest(string owner, string repo, int prNumber)
    {
        var json = await GetJsonAsync($"/repos/{owner}/{repo}/pulls/{prNumber}");
        return json?.AsObject() ?? throw new InvalidOperationException("Failed to get PR");
    }

    /// <summary>
    /// Get changed files in a PR (filenames only, max 100).
    /// </summary>
    public async Task<string[]> GetPullRequestFiles(string owner, string repo, int prNumber)
    {
        try
        {
            var json = await GetJsonAsync($"/repos/{owner}/{repo}/pulls/{prNumber}/files?per_page=100");
            return json?.AsArray()
                ?.Select(f => f?["filename"]?.GetValue<string>() ?? "")
                .Where(f => f.Length > 0)
                .ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Get a single job's details including steps array.
    /// </summary>
    public async Task<JsonObject?> GetJob(string owner, string repo, long jobId)
    {
        try
        {
            var json = await GetJsonAsync($"/repos/{owner}/{repo}/actions/jobs/{jobId}");
            return json?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all check runs for a commit SHA.
    /// </summary>
    public async Task<JsonArray> GetCheckRuns(string owner, string repo, string sha)
    {
        var all = new JsonArray();
        int page = 1;
        while (true)
        {
            var json = await GetJsonAsync($"/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100&page={page}");
            var runs = json?["check_runs"]?.AsArray();
            if (runs == null || runs.Count == 0) break;
            foreach (var run in runs)
            {
                all.Add(JsonNode.Parse(run!.ToJsonString())!);
            }
            if (runs.Count < 100) break;
            page++;
        }
        return all;
    }

    /// <summary>
    /// Get jobs for a workflow run.
    /// </summary>
    public async Task<JsonArray> GetJobs(string owner, string repo, long runId)
    {
        var json = await GetJsonAsync($"/repos/{owner}/{repo}/actions/runs/{runId}/jobs?per_page=100&filter=latest");
        return json?["jobs"]?.AsArray() ?? [];
    }

    /// <summary>
    /// Get workflow runs for a specific workflow on a branch.
    /// </summary>
    public async Task<JsonArray> GetWorkflowRuns(string owner, string repo, string workflow, string branch, int count = 10)
    {
        var json = await GetJsonAsync(
            $"/repos/{owner}/{repo}/actions/workflows/{workflow}/runs?branch={Uri.EscapeDataString(branch)}&per_page={count}&status=completed");
        return json?["workflow_runs"]?.AsArray() ?? [];
    }

    /// <summary>
    /// Download full job log text. Returns cached version if available.
    /// </summary>
    public async Task<LogCache.CachedLog> GetJobLog(string owner, string repo, long jobId)
    {
        var cached = _cache.Get(jobId);
        if (cached != null) return cached;

        EnsureAuth();

        // GitHub redirects to a download URL
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/repos/{owner}/{repo}/actions/jobs/{jobId}/logs");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        string text;
        if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
            response.StatusCode == System.Net.HttpStatusCode.Found)
        {
            var redirectUrl = response.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("Log redirect without Location header");
            text = await _http.GetStringAsync(redirectUrl);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync();
        }

        var lines = text.Split('\n');
        var steps = LogParser.ParseSteps(lines);

        var log = new LogCache.CachedLog
        {
            RawText = text,
            Lines = lines,
            Steps = steps,
        };

        _cache.Set(jobId, log);
        return log;
    }

    public async Task<JsonNode?> GetJsonAsync(string path)
    {
        EnsureAuth();
        var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(content);
    }
}
