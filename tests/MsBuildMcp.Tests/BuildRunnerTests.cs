// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

public class BuildRunnerTests
{
    [Theory]
    [InlineData("Debug", "x64")]
    [InlineData("Release", "Any CPU")]
    [InlineData("Debug", "Mixed Platforms")]
    public void BuildArgs_QuotesPlatformAndConfiguration(string configuration, string platform)
    {
        var args = BuildManager.BuildArgs(
            @"C:\my solution\test.sln", targets: null,
            configuration, platform, restore: false, additionalArgs: null);

        var joined = string.Join(" ", args);

        // Solution path must be quoted
        Assert.Contains("\"C:\\my solution\\test.sln\"", joined);
        // Configuration and Platform values must be quoted so spaces don't split args
        Assert.Contains($"/p:Configuration=\"{configuration}\"", joined);
        Assert.Contains($"/p:Platform=\"{platform}\"", joined);
        // Verify no arg was split — each /p: should be a single element in the list
        Assert.DoesNotContain(args, a => a.StartsWith("/p:") && a.Contains(' ') && !a.Contains('"'));
    }

    [Fact]
    public void BuildArgs_IncludesTargetsAndRestore()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: "Build", "Debug", "x64",
            restore: true, additionalArgs: "/p:Analysis=True");

        Assert.Contains("/t:Build", args);
        Assert.Contains("-Restore", args);
        Assert.Contains("/p:Analysis=True", args);
        Assert.Contains("/v:minimal", args);
    }

    [Fact]
    public void BuildArgs_NullTargets_NoTargetArg()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: null);

        Assert.DoesNotContain(args, a => a.StartsWith("/t:"));
    }

    [Fact]
    public void BuildArgs_RestoreFalse_NoRestoreFlag()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: null);

        Assert.DoesNotContain("-Restore", args);
    }

    [Fact]
    public void BuildArgs_EmptyAdditionalArgs_IgnoresEmpty()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: "");

        Assert.DoesNotContain("", args);
    }

    // --- PublishArgs tests ---

    [Fact]
    public void PublishArgs_MinimalArgs()
    {
        var args = BuildManager.PublishArgs(
            @"C:\my project\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: null, additionalArgs: null);

        var joined = string.Join(" ", args);
        Assert.StartsWith("publish", joined);
        Assert.Contains("\"C:\\my project\\MyApp.csproj\"", joined);
        Assert.Contains("-c", args);
        Assert.Contains("Release", args);
        Assert.Contains("--nologo", args);
        Assert.Contains("-v", args);
        Assert.Contains("minimal", args);
        Assert.DoesNotContain(args, a => a == "-r");
        Assert.DoesNotContain(args, a => a == "-f");
        Assert.DoesNotContain(args, a => a == "-o");
        Assert.DoesNotContain(args, a => a.StartsWith("--self-contained"));
    }

    [Fact]
    public void PublishArgs_AllOptions()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Debug",
            runtime: "win-x64", framework: "net9.0",
            output: @"C:\out", selfContained: true,
            additionalArgs: "/p:PublishTrimmed=true");

        var joined = string.Join(" ", args);
        Assert.Contains("-r", args);
        Assert.Contains("win-x64", args);
        Assert.Contains("-f", args);
        Assert.Contains("net9.0", args);
        Assert.Contains("-o", args);
        Assert.Contains("\"C:\\out\"", joined);
        Assert.Contains("--self-contained=true", joined);
        Assert.Contains("/p:PublishTrimmed=true", joined);
    }

    [Fact]
    public void PublishArgs_SelfContainedFalse()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: false, additionalArgs: null);

        Assert.Contains("--self-contained=false", args);
    }

    [Fact]
    public void PublishArgs_EmptyAdditionalArgs_IgnoresEmpty()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: null, additionalArgs: "");

        Assert.DoesNotContain("", args);
    }
}
