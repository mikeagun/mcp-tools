// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using CiDebugMcp.Tools;
using Xunit;

namespace CiDebugMcp.Tests;

public class AdoCiProviderTests
{
    // ── URL Parsing ─────────────────────────────────────────────

    [Fact]
    public void FromUrl_DevAzureCom_ParsesOrgAndProject()
    {
        var cache = new LogCache();
        var provider = AdoCiProvider.FromUrl(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=123", cache);

        Assert.NotNull(provider);
        Assert.Equal("ado", provider!.ProviderName);
    }

    [Fact]
    public void FromUrl_VisualStudioCom_ParsesLegacy()
    {
        var cache = new LogCache();
        var provider = AdoCiProvider.FromUrl(
            "https://myorg.visualstudio.com/myproject/_build/results?buildId=456", cache);

        Assert.NotNull(provider);
    }

    [Fact]
    public void FromUrl_GitHubUrl_ReturnsNull()
    {
        var cache = new LogCache();
        var provider = AdoCiProvider.FromUrl(
            "https://github.com/microsoft/ebpf-for-windows/pull/5078", cache);

        Assert.Null(provider);
    }

    [Fact]
    public void FromUrl_InvalidUrl_ReturnsNull()
    {
        var cache = new LogCache();
        Assert.Null(AdoCiProvider.FromUrl("not a url", cache));
    }

    // ── ParseUrl (structured extraction) ────────────────────────

    [Fact]
    public void ParseUrl_DevAzureCom_ExtractsOrgProjectBuildId()
    {
        var parsed = AdoCiProvider.ParseUrl(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=123");

        Assert.NotNull(parsed);
        Assert.Equal("https://dev.azure.com/myorg", parsed!.Value.OrgUrl);
        Assert.Equal("myproject", parsed.Value.Project);
        Assert.Equal("123", parsed.Value.BuildId);
        Assert.Null(parsed.Value.PrNumber);
    }

    [Fact]
    public void ParseUrl_VisualStudioCom_GitRemoteUrl()
    {
        // Exact URL format from ADO repo: git remote get-url origin
        var parsed = AdoCiProvider.ParseUrl(
            "https://myorg.visualstudio.com/MyProject/_git/my-repo");

        Assert.NotNull(parsed);
        Assert.Equal("https://dev.azure.com/myorg", parsed!.Value.OrgUrl);
        Assert.Equal("MyProject", parsed.Value.Project);
        Assert.Null(parsed.Value.BuildId);
        Assert.Null(parsed.Value.PrNumber);
    }

    [Fact]
    public void ParseUrl_WithPullRequest()
    {
        var parsed = AdoCiProvider.ParseUrl(
            "https://dev.azure.com/myorg/myproject/_git/myrepo/pullrequest/42");

        Assert.NotNull(parsed);
        Assert.Equal(42, parsed!.Value.PrNumber);
        Assert.Null(parsed.Value.BuildId);
    }

    [Fact]
    public void ParseUrl_NoBuildIdOrPr()
    {
        var parsed = AdoCiProvider.ParseUrl(
            "https://dev.azure.com/myorg/myproject/_build");

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Value.BuildId);
        Assert.Null(parsed.Value.PrNumber);
    }

    [Fact]
    public void ParseUrl_GithubUrl_ReturnsNull()
    {
        Assert.Null(AdoCiProvider.ParseUrl("https://github.com/microsoft/ebpf-for-windows"));
    }

    // ── CiProviderResolver ──────────────────────────────────────

    [Fact]
    public void Resolver_FromUrl_GitRemoteUrl_CreatesAdoProvider()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        var resolved = resolver.ResolveFromUrl(
            "https://myorg.visualstudio.com/MyProject/_git/my-repo");

        Assert.NotNull(resolved);
        Assert.Equal("ado", resolved!.Provider.ProviderName);
        // No buildId in git remote URL
        Assert.Null(resolved.ExtractedQuery?.BuildId);
    }

    [Fact]
    public void Resolver_FromUrl_AdoBuildUrl_ExtractsBuildId()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        var resolved = resolver.ResolveFromUrl(
            "https://dev.azure.com/myorg/MyProject/_build/results?buildId=456");

        Assert.NotNull(resolved);
        Assert.Equal("ado", resolved!.Provider.ProviderName);
        Assert.Equal("456", resolved.ExtractedQuery?.BuildId);
    }

    [Fact]
    public void Resolver_FromUrl_GithubUrl_CreatesGithubProvider()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        var resolved = resolver.ResolveFromUrl(
            "https://github.com/microsoft/ebpf-for-windows/pull/5078");

        Assert.NotNull(resolved);
        Assert.Equal("github", resolved!.Provider.ProviderName);
    }

    [Fact]
    public void Resolver_FromUrl_InvalidUrl_ReturnsNull()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        Assert.Null(resolver.ResolveFromUrl("not a url"));
    }

    [Fact]
    public void Resolver_CachesProviderByOrg()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        var r1 = resolver.ResolveFromUrl(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=1");
        var r2 = resolver.ResolveFromUrl(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=2");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        // Same provider instance (cached by org/project)
        Assert.Same(r1!.Provider, r2!.Provider);
    }

    [Fact]
    public void Resolver_DifferentOrgs_DifferentProviders()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        var r1 = resolver.ResolveFromUrl("https://dev.azure.com/org1/proj1/_build");
        var r2 = resolver.ResolveFromUrl("https://dev.azure.com/org2/proj2/_build");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotSame(r1!.Provider, r2!.Provider);
    }

    [Fact]
    public void Resolver_GetCachedAdoProvider_ReturnsAfterResolve()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var resolver = new CiProviderResolver(github, cache);

        // Before any resolve — no cached provider
        Assert.Null(resolver.GetCachedAdoProvider());

        // After resolving an ADO URL — cached provider available
        resolver.ResolveFromUrl("https://dev.azure.com/myorg/myproject/_build");
        Assert.NotNull(resolver.GetCachedAdoProvider());
    }

    // ── FailureReportFormatter ──────────────────────────────────

    [Fact]
    public void FormatFailureReport_ProducesSameSchema()
    {
        var report = new CiFailureReport
        {
            Scope = "build 123",
            Summary = new CiSummary { Total = 4, Passed = 2, Failed = 1, Cancelled = 1 },
            Failures =
            [
                new CiJobFailure
                {
                    Name = "Build x64",
                    JobId = "123:5",
                    BuildId = "123",
                    Conclusion = "failed",
                    FailureType = "build",
                    Diagnosis = "Build error: C2084 at file.cpp:42",
                    Errors =
                    [
                        new CiExtractedError
                        {
                            Raw = "file.cpp(42): error C2084: body",
                            Code = "C2084",
                            Message = "body",
                            File = "file.cpp",
                            SourceLine = 42,
                        },
                    ],
                },
            ],
            Cancelled = [new CiJobInfo { Name = "Deploy", JobId = "456" }],
        };

        var json = FailureReportFormatter.Format(report);

        Assert.Equal("build 123", json["scope"]!.GetValue<string>());
        Assert.Equal(4, json["summary"]!["total"]!.GetValue<int>());
        Assert.Equal(1, json["summary"]!["failed"]!.GetValue<int>());

        var failures = json["failures"]!.AsArray();
        Assert.Single(failures);
        Assert.Equal("Build x64", failures[0]!["job"]!.GetValue<string>());
        Assert.Equal("build", failures[0]!["failure_type"]!.GetValue<string>());

        var steps = failures[0]!["failed_steps"]!.AsArray();
        Assert.Single(steps);
        var errors = steps[0]!["errors"]!.AsArray();
        Assert.Single(errors);
        Assert.Equal("C2084", errors[0]!["code"]!.GetValue<string>());

        var cancelled = json["cancelled"]!.AsArray();
        Assert.Single(cancelled);
        Assert.Equal("Deploy", cancelled[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void FormatFailureReport_EmptyReport()
    {
        var report = new CiFailureReport
        {
            Scope = "repo-wide",
            Summary = new CiSummary(),
        };

        var json = FailureReportFormatter.Format(report);

        Assert.Equal("repo-wide", json["scope"]!.GetValue<string>());
        Assert.Equal(0, json["summary"]!["total"]!.GetValue<int>());
        Assert.Empty(json["failures"]!.AsArray());
        Assert.Null(json["cancelled"]);
    }

    // ── ADO Log Parsing ─────────────────────────────────────────

    [Fact]
    public void ParseAdoSteps_SectionMarkers()
    {
        // ADO uses ##[section]Starting: and ##[section]Finishing: markers
        var lines = new[]
        {
            "2026-01-01T00:00:00.0Z ##[section]Starting: Build solution",
            "2026-01-01T00:00:01.0Z Building...",
            "2026-01-01T00:00:02.0Z ##[error]file.cpp(10): error C1234: msg",
            "2026-01-01T00:00:03.0Z ##[section]Finishing: Build solution",
            "2026-01-01T00:00:04.0Z ##[section]Starting: Run tests",
            "2026-01-01T00:00:05.0Z All tests passed",
            "2026-01-01T00:00:06.0Z ##[section]Finishing: Run tests",
        };

        // Use reflection to test the private ParseAdoSteps method
        var method = typeof(AdoCiProvider).GetMethod("ParseAdoSteps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var steps = (ParsedStep[])method!.Invoke(null, [lines])!;

        Assert.Equal(2, steps.Length);
        Assert.Equal("Build solution", steps[0].Name);
        Assert.Equal(1, steps[0].Number);
        Assert.Single(steps[0].Errors);
        Assert.Contains("error C1234", steps[0].Errors[0]);
        Assert.Equal("Run tests", steps[1].Name);
        Assert.Empty(steps[1].Errors);
    }

    [Fact]
    public void ParseAdoSteps_NoMarkers_ReturnsEmpty()
    {
        var lines = new[] { "just plain output", "no markers here" };
        var method = typeof(AdoCiProvider).GetMethod("ParseAdoSteps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var steps = (ParsedStep[])method!.Invoke(null, [lines])!;

        Assert.Empty(steps);
    }

    // ── GitHubCiProvider URL Parsing ────────────────────────────

    [Fact]
    public void GitHubFromUrl_ParsesOwnerRepo()
    {
        var cache = new LogCache();
        var client = new GitHubClient(cache);
        var provider = GitHubCiProvider.FromUrl(client,
            "https://github.com/microsoft/ebpf-for-windows/pull/5078");

        Assert.NotNull(provider);
        Assert.Equal("github", provider!.ProviderName);
    }

    [Fact]
    public void GitHubFromUrl_AdoUrl_ReturnsNull()
    {
        var cache = new LogCache();
        var client = new GitHubClient(cache);
        var provider = GitHubCiProvider.FromUrl(client,
            "https://dev.azure.com/myorg/myproject/_build");

        Assert.Null(provider);
    }

    // ── ICiProvider Types ───────────────────────────────────────

    [Fact]
    public void CiFailureReport_WithExpression_Works()
    {
        var report = new CiFailureReport
        {
            Scope = "original",
            Summary = new CiSummary { Total = 5, Failed = 1 },
        };

        var modified = report with { Scope = "modified", BaseBranch = "main" };

        Assert.Equal("modified", modified.Scope);
        Assert.Equal("main", modified.BaseBranch);
        Assert.Equal(5, modified.Summary.Total); // inherited
    }

    [Fact]
    public void CiJobFailure_WithExpression_Works()
    {
        var failure = new CiJobFailure
        {
            Name = "Build",
            JobId = "123",
        };

        var enriched = failure with
        {
            FailureType = "build",
            Diagnosis = "error C2084",
            Errors = [new CiExtractedError { Raw = "file.cpp(1): error C2084: msg" }],
        };

        Assert.Equal("build", enriched.FailureType);
        Assert.Single(enriched.Errors);
        Assert.Equal("Build", enriched.Name); // inherited
    }

    [Fact]
    public void CiQuery_Defaults()
    {
        var query = new CiQuery();
        Assert.Equal(1, query.Count);
        Assert.Equal("errors", query.Detail);
        Assert.Equal(20, query.MaxErrors);
        Assert.Null(query.PrNumber);
        Assert.Null(query.BuildId);
    }
}
