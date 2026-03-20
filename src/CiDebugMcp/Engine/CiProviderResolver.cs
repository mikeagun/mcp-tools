// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace CiDebugMcp.Engine;

/// <summary>
/// Resolves CI provider from URL or params. Creates/caches AdoCiProvider instances on demand.
/// GitHub requests use the existing IGitHubApi path; ADO requests go through ICiProvider.
/// </summary>
public sealed class CiProviderResolver
{
    private readonly GitHubClient _github;
    private readonly LogCache _cache;
    private readonly Dictionary<string, AdoCiProvider> _adoProviders = new(StringComparer.OrdinalIgnoreCase);

    public CiProviderResolver(GitHubClient github, LogCache cache)
    {
        _github = github;
        _cache = cache;
    }

    /// <summary>Existing GitHub API for backward-compat code paths.</summary>
    public IGitHubApi GitHubApi => _github;

    /// <summary>
    /// Try to resolve a provider from a URL (git remote URL, CI build URL, or PR URL).
    /// Returns the provider and any query info extracted from the URL (e.g., buildId, PR number).
    /// Returns null if the URL doesn't match any known CI provider.
    /// </summary>
    public ResolvedProvider? ResolveFromUrl(string url)
    {
        // Try ADO first (dev.azure.com and *.visualstudio.com)
        var adoParsed = AdoCiProvider.ParseUrl(url);
        if (adoParsed != null)
        {
            var provider = GetOrCreateAdoProvider(
                adoParsed.Value.OrgUrl, adoParsed.Value.Project, adoParsed.Value.OriginalHost);
            var query = new CiQuery
            {
                Url = url,
                BuildId = adoParsed.Value.BuildId,
                PrNumber = adoParsed.Value.PrNumber,
            };
            return new ResolvedProvider(provider, query);
        }

        // Try GitHub
        var ghProvider = GitHubCiProvider.FromUrl(_github, url);
        if (ghProvider != null)
        {
            return new ResolvedProvider(ghProvider, null);
        }

        return null;
    }

    /// <summary>
    /// Get or create an ADO provider for the given org, reusing cached instances.
    /// </summary>
    public AdoCiProvider GetOrCreateAdoProvider(string orgUrl, string project, string? originalHost = null)
    {
        var key = $"{orgUrl.TrimEnd('/')}/{project}";
        if (!_adoProviders.TryGetValue(key, out var provider))
        {
            provider = new AdoCiProvider(orgUrl, project, _cache, originalHost);
            _adoProviders[key] = provider;
        }
        return provider;
    }

    /// <summary>
    /// Get any cached ADO provider (for follow-up calls like search_job_logs
    /// that don't have enough context to create a new provider).
    /// </summary>
    public AdoCiProvider? GetCachedAdoProvider()
    {
        return _adoProviders.Values.FirstOrDefault();
    }
}

/// <summary>
/// Result of URL-based provider resolution.
/// </summary>
public sealed record ResolvedProvider(ICiProvider Provider, CiQuery? ExtractedQuery);
