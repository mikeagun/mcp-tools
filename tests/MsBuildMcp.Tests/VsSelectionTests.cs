// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using MsBuildMcp.Engine;
using MsBuildMcp.Tools;

namespace MsBuildMcp.Tests;

public class VsSelectionTests : IDisposable
{
    // Mirrors real vswhere output: VS 2026 reports productLineVersion "18", not the year.
    private const string VsWhereJson = """
        [
          {
            "installationPath": "C:\\VS\\18\\Enterprise",
            "installationVersion": "18.12.12405.243",
            "displayName": "Visual Studio Enterprise 2026",
            "productId": "Microsoft.VisualStudio.Product.Enterprise",
            "isPrerelease": false,
            "catalog": { "productLineVersion": "18" }
          },
          {
            "installationPath": "C:\\VS\\18\\Preview",
            "installationVersion": "18.13.1.0",
            "displayName": "Visual Studio Enterprise 2026 Insiders",
            "isPrerelease": true,
            "catalog": { "productLineVersion": "18" }
          },
          {
            "installationPath": "C:\\VS\\2022\\Enterprise",
            "installationVersion": "17.14.37906.3",
            "displayName": "Visual Studio Enterprise 2022",
            "isPrerelease": false,
            "catalog": { "productLineVersion": "2022" }
          },
          {
            "installationPath": "C:\\VS\\2022\\BuildTools",
            "installationVersion": "17.8.5.0",
            "displayName": "Visual Studio Build Tools 2022",
            "catalog": { "productLineVersion": "2022" }
          },
          { "installationPath": "C:\\VS\\broken", "installationVersion": "not-a-version" }
        ]
        """;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vs-sel-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static VsLocator Locator(string? json = VsWhereJson) => new(() => json, probeFileSystem: false);

    private static VsToolchain FakeToolchain(VsInstance i) => new(i, @"C:\fake\MSBuild.exe", new());

    [Fact]
    public void Parse_InfersYearsAndSkipsMalformedEntries()
    {
        var instances = VsLocator.ParseVsWhereJson(VsWhereJson);

        Assert.Equal(4, instances.Count);
        Assert.Equal(2026, instances[0].ProductYear);
        Assert.True(instances[1].IsPrerelease);
        Assert.Equal(2022, instances[2].ProductYear);
        Assert.False(instances[3].IsPrerelease);
    }

    [Theory]
    [InlineData("2022", null, 2022)]
    [InlineData("18", null, 2026)]
    [InlineData(null, "Visual Studio 2028", 2028)]
    public void InferYear_UsesProductLineThenMajorThenDisplayName(string? productLine, string? displayName, int expected)
    {
        var major = productLine == "18" ? 18 : 19;
        Assert.Equal(expected, VsLocator.InferYear(productLine, major, displayName));
    }

    [Theory]
    [InlineData(null, @"C:\VS\18\Enterprise")]
    [InlineData("latest", @"C:\VS\18\Enterprise")]
    [InlineData("2026", @"C:\VS\18\Enterprise")]
    [InlineData("2022", @"C:\VS\2022\Enterprise")]
    [InlineData("17", @"C:\VS\2022\Enterprise")]
    [InlineData("17.8", @"C:\VS\2022\BuildTools")]
    [InlineData("[17,18)", @"C:\VS\2022\Enterprise")]
    [InlineData("[18.0,)", @"C:\VS\18\Enterprise")]
    [InlineData("(,17.10]", @"C:\VS\2022\BuildTools")]
    [InlineData("18.13", @"C:\VS\18\Preview")] // prerelease chosen only when it's the sole match
    [InlineData(@"c:\vs\2022\buildtools\", @"C:\VS\2022\BuildTools")]
    public void Resolve_PrefersNewestStableMatch(string? spec, string expectedPath)
    {
        Assert.Equal(expectedPath, Locator().Resolve(spec).InstallationPath);
    }

    [Theory]
    [InlineData("vs2022")]
    [InlineData("[,)")]
    [InlineData("[17,x)")]
    [InlineData(@"relative\path")]
    public void Resolve_InvalidSelector_Throws(string spec)
    {
        var ex = Assert.Throws<ArgumentException>(() => Locator().Resolve(spec));
        Assert.Contains("Invalid Visual Studio selector", ex.Message);
    }

    [Theory]
    [InlineData("2019")]
    [InlineData(@"C:\NotInstalled\VS")]
    public void Resolve_NoMatch_ListsAvailableInstallations(string spec)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Locator().Resolve(spec));
        Assert.Contains($"'{spec}'", ex.Message);
        Assert.Contains(@"C:\VS\2022\Enterprise", ex.Message);
        Assert.Contains(@"C:\VS\18\Enterprise", ex.Message);
    }

    [Fact]
    public void Resolve_NoVsWhere_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Locator(json: null).Resolve(null));
        Assert.Contains("No Visual Studio installation", ex.Message);
    }

    [Fact]
    public void FindVcTargetsPath_PicksHighestCompleteDirectory()
    {
        var vc = Path.Combine(_tempDir, @"MSBuild\Microsoft\VC");
        foreach (var (dir, complete) in new[] { ("v160", true), ("v170", false), ("v180", true), ("v190", false) })
        {
            Directory.CreateDirectory(Path.Combine(vc, dir));
            if (complete)
                File.WriteAllText(Path.Combine(vc, dir, "Microsoft.Cpp.Default.props"), "<Project />");
        }

        Assert.Equal(Path.Combine(vc, "v180") + "\\", VsLocator.FindVcTargetsPath(_tempDir));
    }

    [Fact]
    public void FindVcTargetsPath_NoCppTargets_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, @"MSBuild\Microsoft\VC\v170"));
        Assert.Null(VsLocator.FindVcTargetsPath(_tempDir));
        Assert.Null(VsLocator.FindVcTargetsPath(Path.Combine(_tempDir, "missing")));
    }

    [Theory]
    [InlineData("2022", "2026", "2022")]      // --vs wins over MSBUILD_MCP_VS
    [InlineData(null, "2026", "2026")]        // env var used when --vs absent
    [InlineData("  ", "2026", "2026")]        // blank --vs doesn't mask the env var
    [InlineData(" 17.14 ", null, "17.14")]    // trimmed
    [InlineData(null, "", null)]              // neither set → latest
    [InlineData(null, null, null)]
    public void SelectDefaultSpec_PrefersArgThenEnvironment(string? arg, string? env, string? expected)
    {
        Assert.Equal(expected, VsToolchainProvider.SelectDefaultSpec(arg, env));
    }

    [Theory]
    [InlineData("2022", "2026", @"C:\VS\2022\Enterprise")]
    [InlineData(null, "2022", @"C:\VS\2022\Enterprise")]
    [InlineData(null, null, @"C:\VS\18\Enterprise")]
    public void Create_DefaultSelectionFollowsPrecedence(string? arg, string? env, string expectedPath)
    {
        var provider = VsToolchainProvider.Create(Locator(), arg,
            name => name == VsToolchainProvider.EnvVar ? env : null, FakeToolchain);

        Assert.Equal(expectedPath, provider.ResolveInstance().InstallationPath);
    }

    [Theory]
    [InlineData(new[] { "--vs", "2022" }, "2022")]
    [InlineData(new[] { "--policy", "p.json", "--vs", "[17,18)" }, "[17,18)")]
    [InlineData(new[] { "--policy", "p.json" }, null)]
    [InlineData(new[] { "--vs" }, null)]   // missing value
    public void GetArg_ReadsVsArgument(string[] args, string? expected)
    {
        Assert.Equal(expected, MsBuildMcp.Program.GetArg(args, "--vs"));
    }

    [Fact]
    public void Provider_UsesDefaultSpec_AndExplicitSpecOverrides()
    {
        var provider = new VsToolchainProvider(Locator(), "2022", FakeToolchain);

        Assert.Equal(@"C:\VS\2022\Enterprise", provider.ResolveInstance().InstallationPath);
        Assert.Equal(@"C:\VS\2022\Enterprise", provider.ResolveInstance("").InstallationPath);
        Assert.Equal(@"C:\VS\18\Enterprise", provider.ResolveInstance("2026").InstallationPath);
    }

    [Fact]
    public void Provider_CachesToolchainPerInstallation_ButRetriesFailures()
    {
        var calls = 0;
        var fail = true;
        var provider = new VsToolchainProvider(Locator(), null, i =>
        {
            calls++;
            if (fail) throw new InvalidOperationException("vcvarsall failed");
            return FakeToolchain(i);
        });

        Assert.Throws<InvalidOperationException>(() => provider.GetToolchain());
        fail = false;
        var first = provider.GetToolchain();
        var second = provider.GetToolchain("2026");   // same install as default (latest)
        provider.GetToolchain("2022");

        Assert.Same(first, second);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void StartOrPoll_SelectionFailure_DoesNotStartBuild()
    {
        using var manager = new BuildManager(new VsToolchainProvider(Locator(), "2019", FakeToolchain));

        var ex = Assert.Throws<InvalidOperationException>(() => manager.StartOrPoll(
            @"C:\x.sln", null, "Debug", "x64", restore: false, additionalArgs: null, timeoutSeconds: 0));

        Assert.Contains("'2019'", ex.Message);
        Assert.Equal(0, manager.BuildCounter);
    }

    [Theory]
    [InlineData(@"C:\injected\v180\", @"C:\injected\v180\", false)]
    [InlineData(@"C:\user\v170\", @"C:\injected\v180\", true)]
    [InlineData(@"C:\user\v170\", null, true)]
    public void CreateProcessStartInfo_StripsOnlyInjectedVcTargetsPath(string envValue, string? injected, bool kept)
    {
        var toolchain = new VsToolchain(Locator().Resolve(null), @"C:\fake\MSBuild.exe",
            new Dictionary<string, string> { ["VCTargetsPath"] = envValue });

        var psi = BuildManager.CreateProcessStartInfo(toolchain, @"C:\x.sln", ["x"], injected);

        Assert.Equal(kept, psi.Environment.ContainsKey("VCTargetsPath"));
    }

    [Fact]
    public void StatusToJson_IncludesVisualStudio()
    {
        var status = new BuildStatus
        {
            BuildId = "b-1", Status = "succeeded", ElapsedMs = 1, ErrorCount = 0, WarningCount = 0,
            Errors = [], Warnings = [], Command = "msbuild", IsCompleted = true,
            VisualStudio = Locator().Resolve("2022"),
        };

        var vs = BuildTools.StatusToJson(status)["visual_studio"]!;

        Assert.Equal("17.14.37906.3", vs["version"]!.GetValue<string>());
        Assert.Equal(2022, vs["year"]!.GetValue<int>());
        Assert.Equal(@"C:\VS\2022\Enterprise", vs["path"]!.GetValue<string>());
    }
}
