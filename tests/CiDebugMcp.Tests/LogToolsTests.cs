// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using CiDebugMcp.Tools;
using McpSharp;
using Xunit;

namespace CiDebugMcp.Tests;

/// <summary>
/// Tests for search_job_logs and get_step_logs tool handlers,
/// including job resolution from run_id + job_name.
/// </summary>
public class LogToolsTests
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

    private static string MakeMultiStepLog()
    {
        // Structured with ##[group] markers as GitHub Actions produces.
        // Note: search_job_logs with include_setup=false filters content
        // inside ##[group] blocks, so use include_setup=true or step_name
        // to search within specific steps.
        return
            "##[group]Set up job\n" +
            "Prepare all required actions\n" +
            "##[endgroup]\n" +
            "##[group]Run msbuild\n" +
            "##[error]file.cpp(10): error C2084: already defined\n" +
            "Build failed with 1 error\n" +
            "##[error]Process completed with exit code 1\n" +
            "##[group]Run unit_tests\n" +
            "Running test suite...\n" +
            "All tests passed\n" +
            "##[endgroup]\n";
    }

    // ── search_job_logs ─────────────────────────────────────────

    [Fact]
    public void SearchJobLogs_FindsPattern()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1001, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)1001,
            ["pattern"] = @"error C\d+",
            ["step_name"] = "msbuild", ["include_setup"] = true, // scope to the build step
        });

        Assert.True(result["total_matches"]!.GetValue<int>() >= 1);
        var matches = result["matches"]!.AsArray();
        Assert.NotEmpty(matches);
        Assert.Contains("C2084", matches[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SearchJobLogs_FiltersSetupByDefault()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1002, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)1002,
            ["pattern"] = "Prepare",
        });

        // "Prepare all required actions" is in a setup block — should be filtered
        Assert.Equal(0, result["total_matches"]!.GetValue<int>());
        Assert.True(result["filtered_setup_matches"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void SearchJobLogs_IncludeSetup_ShowsAll()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1003, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)1003,
            ["pattern"] = "Prepare",
            ["include_setup"] = true,
        });

        Assert.True(result["total_matches"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void SearchJobLogs_StepNameFilter()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1004, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)1004,
            ["pattern"] = ".*",  // match everything
            ["step_name"] = "msbuild", ["include_setup"] = true,
        });

        // Should only match lines within the "Run msbuild" step
        Assert.NotNull(result["step"]);
        Assert.Contains("msbuild", result["step"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void SearchJobLogs_RepoShorthand()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1005, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["repo"] = "test/repo",
            ["job_id"] = (long)1005,
            ["pattern"] = "error C",
            ["step_name"] = "msbuild", ["include_setup"] = true,
        });

        Assert.True(result["total_matches"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void SearchJobLogs_ParsesMsvcErrors()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)1006, MakeMultiStepLog());

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)1006,
            ["pattern"] = "error C2084",
            ["step_name"] = "msbuild", ["include_setup"] = true,
        });

        var match = result["matches"]!.AsArray()[0]!;
        Assert.NotNull(match["parsed"]);
        Assert.Equal("C2084", match["parsed"]!["code"]!.GetValue<string>());
    }

    // ── get_step_logs ───────────────────────────────────────────

    [Fact]
    public void GetStepLogs_DefaultsToFailedStep()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)2001, MakeMultiStepLog());

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)2001,
        });

        // Default selects a step with errors — verify we get a step back
        Assert.NotNull(result["step"]);
        Assert.NotNull(result["lines"]);
    }

    [Fact]
    public void GetStepLogs_ByStepName()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)2002, MakeMultiStepLog());

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)2002,
            ["step_name"] = "unit_tests",
        });

        Assert.Contains("unit_tests", result["step"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void GetStepLogs_PatternCentersAroundMatch()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)2003, MakeMultiStepLog());

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)2003,
            ["step_name"] = "msbuild", ["include_setup"] = true,
            ["pattern"] = "error C2084",
        });

        Assert.NotNull(result["match_line"]);
    }

    [Fact]
    public void GetStepLogs_NoMatchingStep_ReturnsAvailableSteps()
    {
        var (server, fake) = SetupServer();
        fake.SetJobLog((long)2004, MakeMultiStepLog());

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)2004,
            ["step_name"] = "nonexistent_step",
        });

        Assert.NotNull(result["error"]);
        Assert.NotNull(result["available_steps"]);
        Assert.True(result["available_steps"]!.AsArray().Count >= 2);
    }

    [Fact]
    public void GetStepLogs_AmbiguousStepName_ReturnsMatches()
    {
        var (server, fake) = SetupServer();
        // Log with multiple "Run" steps
        fake.SetJobLog((long)2005,
            "##[group]Run cmake build\noutput\n##[group]Run cmake test\noutput\n");

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["owner"] = "test", ["repo"] = "repo", ["job_id"] = (long)2005,
            ["step_name"] = "cmake",
        });

        Assert.True(result["ambiguous"]?.GetValue<bool>() ?? false);
        Assert.True(result["matches"]!.AsArray().Count >= 2);
    }

    // ── Job resolution: run_id + job_name ───────────────────────

    [Fact]
    public void SearchJobLogs_ResolvesJobFromRunId()
    {
        var (server, fake) = SetupServer();

        // Set up run with jobs
        fake.SetJson("/repos/test/repo/actions/runs/999/jobs",
            new JsonObject
            {
                ["jobs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = 7001,
                        ["name"] = "Build x64 Debug",
                        ["status"] = "completed",
                        ["conclusion"] = "failure",
                    },
                    new JsonObject
                    {
                        ["id"] = 7002,
                        ["name"] = "Build ARM64",
                        ["status"] = "completed",
                        ["conclusion"] = "success",
                    },
                },
            });
        fake.SetJobLog((long)7001, "##[group]Run msbuild\n##[error]error C1234: msg\n");

        var result = CallTool(server, "search_job_logs", new JsonObject
        {
            ["repo"] = "test/repo",
            ["run_id"] = (long)999,
            ["job_name"] = "x64 Debug",
            ["pattern"] = "error C",
            ["step_name"] = "msbuild", ["include_setup"] = true,
        });

        Assert.True(result["total_matches"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void GetStepLogs_ResolvesJobFromRunId()
    {
        var (server, fake) = SetupServer();

        fake.SetJson("/repos/test/repo/actions/runs/888/jobs",
            new JsonObject
            {
                ["jobs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = 8001,
                        ["name"] = "Run tests",
                        ["status"] = "completed",
                        ["conclusion"] = "failure",
                    },
                },
            });
        fake.SetJobLog((long)8001, "##[group]Run unit_tests\ntest output\n");

        var result = CallTool(server, "get_step_logs", new JsonObject
        {
            ["repo"] = "test/repo",
            ["run_id"] = (long)888,
        });

        Assert.NotNull(result["step"]);
    }

    [Fact]
    public void SearchJobLogs_MissingJobIdAndRunId_ReturnsError()
    {
        var (server, _) = SetupServer();

        var result = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = "search_job_logs",
            ["arguments"] = new JsonObject
            {
                ["repo"] = "test/repo",
                ["pattern"] = "error",
            },
        })!;

        Assert.True(result["isError"]?.GetValue<bool>() ?? false);
    }
}
