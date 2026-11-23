// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class BinaryAnalyzerTests : IDisposable
{
    private readonly string _toolsDir = Path.Combine(Path.GetTempPath(), $"msvc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_toolsDir))
            Directory.Delete(_toolsDir, recursive: true);
    }

    private void CreateDumpbin(string hostDir, string targetDir)
    {
        var dir = Path.Combine(_toolsDir, "bin", hostDir, targetDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dumpbin.exe"), "");
    }

    [Theory]
    [InlineData(Architecture.X64, true, true, @"Hostx64\x64")]
    [InlineData(Architecture.X64, true, false, null)]
    [InlineData(Architecture.Arm64, true, true, @"HostARM64\arm64")]
    [InlineData(Architecture.Arm64, false, true, @"Hostx64\x64")]
    [InlineData(Architecture.Arm64, false, false, null)]
    public void FindDumpbinInToolsDir_PrefersHostNativeTools(
        Architecture hostArch, bool hasArm64, bool hasX64, string? expectedDir)
    {
        if (hasArm64) CreateDumpbin("HostARM64", "arm64");
        if (hasX64) CreateDumpbin("Hostx64", "x64");

        var expected = expectedDir == null ? null : Path.Combine(_toolsDir, "bin", expectedDir, "dumpbin.exe");
        Assert.Equal(expected, BinaryAnalyzer.FindDumpbinInToolsDir(_toolsDir, hostArch));
    }
}
