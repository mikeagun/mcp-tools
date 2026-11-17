// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using MsBuildMcp.Tools;

namespace MsBuildMcp.Tests;

/// <summary>
/// Per-call Visual Studio selection (<c>vs</c> argument) for evaluation and builds.
/// Uses fake vswhere output and temp "installations" — no Visual Studio required.
/// </summary>
public class VsPerCallSelectionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vs-percall-{Guid.NewGuid():N}");
    private readonly string _vs2022;
    private readonly string _vs2026;

    public VsPerCallSelectionTests()
    {
        ProjectEngine.EnsureMsBuildRegistered();

        _vs2022 = CreateFakeInstall("2022", "v170");
        _vs2026 = CreateFakeInstall("18", "v180");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateFakeInstall(string name, string vcDir)
    {
        var root = Path.Combine(_tempDir, name);
        var vc = Path.Combine(root, @"MSBuild\Microsoft\VC", vcDir);
        Directory.CreateDirectory(vc);
        File.WriteAllText(Path.Combine(vc, "Microsoft.Cpp.Default.props"), "<Project />");
        return root;
    }

    private VsLocator Locator() => new(() => new JsonArray(
        new JsonObject
        {
            ["installationPath"] = _vs2026,
            ["installationVersion"] = "18.12.1.0",
            ["displayName"] = "Visual Studio Enterprise 2026",
            ["catalog"] = new JsonObject { ["productLineVersion"] = "18" },
        },
        new JsonObject
        {
            ["installationPath"] = _vs2022,
            ["installationVersion"] = "17.14.1.0",
            ["displayName"] = "Visual Studio Enterprise 2022",
            ["catalog"] = new JsonObject { ["productLineVersion"] = "2022" },
        }).ToJsonString());

    private static string VcDir(string install, string vcDir) =>
        Path.Combine(install, @"MSBuild\Microsoft\VC", vcDir) + "\\";

    [Fact]
    public void Evaluate_VsSelector_SetsOverridableVcTargetsPath()
    {
        var proj = Path.Combine(_tempDir, "probe.proj");
        File.WriteAllText(proj, """
            <Project>
              <PropertyGroup>
                <Seen>$(VCTargetsPath)</Seen>
                <VCTargetsPath>redirected</VCTargetsPath>
                <After>$(VCTargetsPath)</After>
              </PropertyGroup>
            </Project>
            """);
        var engine = new ProjectEngine(new VsToolchainProvider(Locator(), null));

        var defaultSnap = engine.Evaluate(proj);
        var snap2022 = engine.Evaluate(proj, vs: "2022");
        var snap2026 = engine.Evaluate(proj, vs: "2026");

        Assert.NotEqual(VcDir(_vs2022, "v170"), defaultSnap.AllProperties["Seen"]);
        Assert.Equal(VcDir(_vs2022, "v170"), snap2022.AllProperties["Seen"]);
        Assert.Equal(VcDir(_vs2026, "v180"), snap2026.AllProperties["Seen"]);
        // Toolset property, not a global property: project files (e.g. the VC toolset-location
        // props) can still reassign it.
        Assert.Equal("redirected", snap2022.AllProperties["After"]);
    }

    [Fact]
    public void Evaluate_UnknownVs_Throws()
    {
        var proj = Path.Combine(_tempDir, "probe.proj");
        File.WriteAllText(proj, "<Project />");
        var engine = new ProjectEngine(new VsToolchainProvider(Locator(), null));

        Assert.Throws<InvalidOperationException>(() => engine.Evaluate(proj, vs: "2019"));
    }

    [Fact]
    public void DependencyGraph_InvalidVs_IsNotSwallowed()
    {
        var engine = new ProjectEngine(new VsToolchainProvider(Locator(), null));
        var solution = new SolutionInfo
        {
            Path = Path.Combine(_tempDir, "x.sln"), Directory = _tempDir, Projects = [], Configurations = [],
        };

        Assert.Throws<ArgumentException>(() => DependencyGraph.Build(solution, engine, vs: "bogus"));
    }

    [Fact]
    public void ListVsInstallations_MarksDefault()
    {
        var result = VsTools.ListInstallations(new VsToolchainProvider(Locator(), "2022"));

        Assert.Equal("2022", result["default_selector"]!.GetValue<string>());
        var installations = result["installations"]!.AsArray();
        Assert.Equal(2, installations.Count);
        Assert.False(installations[0]!["is_default"]!.GetValue<bool>());
        Assert.True(installations[1]!["is_default"]!.GetValue<bool>());
        Assert.True(installations[1]!["has_cpp_targets"]!.GetValue<bool>());
    }

    [Fact]
    public void ListVsInstallations_BadDefault_ReportsError()
    {
        var result = VsTools.ListInstallations(new VsToolchainProvider(Locator(), "2019"));

        Assert.Contains("'2019'", result["default_error"]!.GetValue<string>());
        Assert.All(result["installations"]!.AsArray(), i => Assert.False(i!["is_default"]!.GetValue<bool>()));
    }

    /// <summary>
    /// A request for a different VS installation than the running build is reported as a
    /// collision; a same-installation request just polls. The toolchain for the colliding
    /// installation is not captured (vcvarsall is slow) because no build is started.
    /// </summary>
    [Fact]
    public void StartOrPoll_DifferentVs_ReportsCollision()
    {
        var fakeMsBuild = Path.Combine(_tempDir, "fake-msbuild.bat");
        File.WriteAllText(fakeMsBuild, "@ping -n 30 127.0.0.1 > nul\r\n");
        var captured = new List<string>();
        var provider = new VsToolchainProvider(Locator(), null, i =>
        {
            captured.Add(i.InstallationPath);
            return new VsToolchain(i, fakeMsBuild, new());
        });
        using var manager = new BuildManager(provider);
        var sln = Path.Combine(_tempDir, "x.sln");

        try
        {
            var first = manager.StartOrPoll(sln, "A", "Debug", "x64", false, null, 0, vs: "2022");
            var other = manager.StartOrPoll(sln, "A", "Debug", "x64", false, null, 0, vs: "2026");
            var same = manager.StartOrPoll(sln, "A", "Debug", "x64", false, null, 0, vs: "2022");

            Assert.Equal("running", first.Status);
            Assert.Equal(_vs2022, first.VisualStudio!.InstallationPath);
            Assert.NotNull(other.Collision);
            Assert.Equal(_vs2022, other.Collision!.RunningVisualStudio!.InstallationPath);
            Assert.Equal(_vs2026, other.Collision.RequestedVisualStudio!.InstallationPath);
            Assert.Null(same.Collision);
            Assert.Equal(1, manager.BuildCounter);
            Assert.Equal([_vs2022], captured);

            var json = BuildTools.StatusToJson(other)["collision"]!;
            Assert.Contains("different Visual Studio installation", json["message"]!.GetValue<string>());
            Assert.Equal("18.12.1.0", json["requested_visual_studio"]!["version"]!.GetValue<string>());
        }
        finally
        {
            manager.Cancel(null);
        }
    }
}
