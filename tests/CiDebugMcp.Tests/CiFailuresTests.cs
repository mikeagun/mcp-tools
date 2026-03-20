// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using CiDebugMcp.Tools;
using McpSharp;
using Xunit;

namespace CiDebugMcp.Tests;

/// <summary>
/// Tests for get_ci_failures orchestration via the registered MCP tool handler.
/// Uses FakeGitHubApi to return canned API responses.
/// </summary>
public class CiFailuresTests
{
    private static (McpServer server, FakeGitHubApi fake) SetupServer()
    {
        var fake = new FakeGitHubApi();
        var server = new McpServer("test");
        LogTools.Register(server, fake);
        return (server, fake);
    }

    private static JsonNode CallTool(McpServer server, string toolName, JsonObject args)
    {
        var result = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = args,
        })!;

        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.False(isError, $"Tool {toolName} returned error: {text}");
        return JsonNode.Parse(text)!;
    }

    // ── Helpers to build canned GitHub API responses ─────────────

    private static JsonObject MakeCheckRunsResponse(params (string name, string conclusion, long id)[] runs)
    {
        var arr = new JsonArray();
        foreach (var (name, conclusion, id) in runs)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["status"] = "completed",
                ["conclusion"] = conclusion,
                ["html_url"] = $"https://github.com/test/repo/actions/runs/100/job/{id}",
            });
        }
        return new JsonObject { ["check_runs"] = arr, ["total_count"] = arr.Count };
    }

    private static JsonObject MakeJobsResponse(params (string name, string conclusion, long id)[] jobs)
    {
        var arr = new JsonArray();
        foreach (var (name, conclusion, id) in jobs)
        {
            var job = new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["status"] = "completed",
                ["conclusion"] = conclusion,
            };
            if (conclusion == "failure")
            {
                job["steps"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["number"] = 1,
                        ["name"] = "Run build",
                        ["conclusion"] = "failure",
                    }
                };
            }
            arr.Add(job);
        }
        return new JsonObject { ["jobs"] = arr };
    }

    private static string MakeBuildLog(string errorLine)
    {
        return $"##[group]Run msbuild\nBuilding project...\n{errorLine}\n##[error]Process completed with exit code 1\n";
    }

    // ── By PR ───────────────────────────────────────────────────

    [Fact]
    public void GetCiFailures_ByPr_ReturnsFailures()
    {
        var (server, fake) = SetupServer();

        // PR endpoint
        fake.SetJson("/repos/test/repo/pulls/42", new JsonObject
        {
            ["head"] = new JsonObject { ["sha"] = "abc12345def67890" },
            ["base"] = new JsonObject { ["ref"] = "main" },
        });

        // PR files
        fake.SetJson("/repos/test/repo/pulls/42/files", new JsonArray
        {
            new JsonObject { ["filename"] = "src/main.cpp" },
        });

        // Check runs for the head SHA
        fake.SetJson("/repos/test/repo/commits/abc12345def67890/check-runs",
            MakeCheckRunsResponse(
                ("Build x64", "failure", 1001),
                ("Build ARM", "success", 1002)));

        // Job log for the failed check run
        fake.SetJobLog(1001, MakeBuildLog("src/main.cpp(42): error C2084: function has a body"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test",
            ["repo"] = "repo",
            ["pr"] = 42,
        });

        // Summary
        Assert.Equal(2, result["summary"]!["total"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["passed"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["failed"]!.GetValue<int>());

        // Failures
        var failures = result["failures"]!.AsArray();
        Assert.Single(failures);
        Assert.Contains("Build x64", failures[0]!["job"]!.GetValue<string>());

        // Changed files present
        Assert.NotNull(result["changed_files"]);
        Assert.Contains("src/main.cpp", result["changed_files"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Base branch present
        Assert.Equal("main", result["base_branch"]!.GetValue<string>());
    }

    [Fact]
    public void GetCiFailures_ByPr_InChangedFilesCorrelation()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/pulls/10", new JsonObject
        {
            ["head"] = new JsonObject { ["sha"] = "sha1234567890" },
            ["base"] = new JsonObject { ["ref"] = "main" },
        });
        fake.SetJson("/repos/test/repo/pulls/10/files", new JsonArray
        {
            new JsonObject { ["filename"] = "libs/api/Verifier.cpp" },
        });
        fake.SetJson("/repos/test/repo/commits/sha1234567890/check-runs",
            MakeCheckRunsResponse(("CI Build", "failure", 2001)));
        fake.SetJobLog(2001, MakeBuildLog("Verifier.cpp(100): error C9999: bad code"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["pr"] = 10,
        });

        // The error's file should have in_changed_files=true since Verifier.cpp is in changed_files
        var failures = result["failures"]!.AsArray();
        var steps = failures[0]?["failed_steps"]?.AsArray();
        Assert.NotNull(steps);

        // Find structured error
        var errors = steps![0]!["errors"]!.AsArray();
        var structuredErr = errors.FirstOrDefault(e => e is JsonObject eo && eo.ContainsKey("file"));
        Assert.NotNull(structuredErr);
        Assert.True(structuredErr!["in_changed_files"]?.GetValue<bool>() ?? false);
    }

    // ── By run_id ───────────────────────────────────────────────

    [Fact]
    public void GetCiFailures_ByRunId_ReturnsFailedJobs()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/500/jobs",
            MakeJobsResponse(
                ("x64 Debug", "failure", 3001),
                ("x64 Release", "success", 3002),
                ("ARM64", "cancelled", 3003)));

        fake.SetJobLog(3001, MakeBuildLog("error LNK2019: unresolved external symbol"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)500,
        });

        Assert.Equal(3, result["summary"]!["total"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["failed"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["cancelled"]!.GetValue<int>());

        var failures = result["failures"]!.AsArray();
        Assert.Single(failures);
        Assert.Equal(500L, failures[0]!["run_id"]!.GetValue<long>());
    }

    // ── Detail levels ───────────────────────────────────────────

    [Fact]
    public void GetCiFailures_DetailSummary_NoErrors()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/600/jobs",
            MakeJobsResponse(("Build", "failure", 4001)));
        // No job log needed — detail=summary doesn't download logs

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)600,
            ["detail"] = "summary",
        });

        Assert.Equal(1, result["summary"]!["failed"]!.GetValue<int>());
        // No failures array detail at summary level (still has the array but no error extraction)
        var failures = result["failures"]!.AsArray();
        Assert.Single(failures);
        // Should NOT have failed_steps (no log download at summary level)
        Assert.Null(failures[0]!["failed_steps"]);
    }

    // ── Failure classification ──────────────────────────────────

    [Fact]
    public void GetCiFailures_ClassifiesBuildFailure()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/700/jobs",
            MakeJobsResponse(("msbuild Build", "failure", 5001)));
        fake.SetJobLog(5001, MakeBuildLog("file.cpp(1): error C2084: already defined"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)700,
        });

        var failure = result["failures"]!.AsArray()[0]!;
        Assert.Equal("build", failure["failure_type"]!.GetValue<string>());
    }

    [Fact]
    public void GetCiFailures_ClassifiesTestFailure()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/701/jobs",
            MakeJobsResponse(("Run unit_tests", "failure", 5002)));
        fake.SetJobLog(5002,
            "##[group]Run unit_tests\n" +
            "Running tests...\n" +
            "FAILED:\n" +
            "  REQUIRE( x == 5 )\n" +
            "##[error]Process completed with exit code 1\n");

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)701,
        });

        var failure = result["failures"]!.AsArray()[0]!;
        Assert.Equal("test", failure["failure_type"]!.GetValue<string>());
    }

    // ── Hint quality ────────────────────────────────────────────

    [Fact]
    public void GetCiFailures_HintsIncludeRepo()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/myorg/myrepo/actions/runs/800/jobs",
            MakeJobsResponse(("Build", "failure", 6001)));
        fake.SetJobLog(6001, MakeBuildLog("file.cpp(1): error C1234: msg"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "myorg", ["repo"] = "myrepo", ["run_id"] = (long)800,
        });

        var hint = result["failures"]!.AsArray()[0]!["hint"]!.GetValue<string>();
        Assert.Contains("myorg/myrepo", hint);
    }

    [Fact]
    public void GetCiFailures_DiagnosisPicksHighestValue()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/900/jobs",
            MakeJobsResponse(("Build", "failure", 7001)));
        // Log with low-value line before high-value MSVC error
        fake.SetJobLog(7001,
            "##[group]Run msbuild\n" +
            "##[error]Dump output folder: c:/dumps/x64/Release\n" +
            "file.cpp(42): error C2084: function has a body" + "\n" +
            "##[error]Process completed with exit code 1\n");

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)900,
        });

        var diagnosis = result["failures"]!.AsArray()[0]!["diagnosis"]!.GetValue<string>();
        // Should pick the MSVC error, not "Dump output folder"
        Assert.Contains("C2084", diagnosis);
    }

    // ── Notifications/skipped ───────────────────────────────────

    [Fact]
    public void GetCiFailures_SeparatesCancelledAndSkipped()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/1000/jobs",
            MakeJobsResponse(
                ("Build", "success", 8001),
                ("Test", "failure", 8002),
                ("Deploy", "cancelled", 8003),
                ("Lint", "skipped", 8004)));
        fake.SetJobLog(8002, MakeBuildLog("FAILED: some test"));

        var result = CallTool(server, "get_ci_failures", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["run_id"] = (long)1000,
        });

        Assert.Equal(1, result["summary"]!["passed"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["failed"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["cancelled"]!.GetValue<int>());
        Assert.Equal(1, result["summary"]!["skipped"]!.GetValue<int>());

        // Cancelled should be in separate array
        var cancelled = result["cancelled"]?.AsArray();
        Assert.NotNull(cancelled);
        Assert.Single(cancelled!);
    }
}
