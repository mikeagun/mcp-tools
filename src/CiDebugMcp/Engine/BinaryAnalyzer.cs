// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CiDebugMcp.Engine;

/// <summary>
/// Analyzes PE binary DLL dependencies using dumpbin.exe.
/// </summary>
public sealed partial class BinaryAnalyzer
{
    private readonly string? _dumpbinPath;

    public BinaryAnalyzer(string? vsToolsPath = null)
    {
        _dumpbinPath = FindDumpbin(vsToolsPath);
    }

    public bool IsAvailable => _dumpbinPath != null;

    /// <summary>
    /// Get sorted DLL dependencies for a binary.
    /// </summary>
    public string[] GetDependencies(string binaryPath, bool isExe)
    {
        if (_dumpbinPath == null)
            throw new InvalidOperationException("dumpbin.exe not found. Set VS_TOOLS_PATH or install Visual Studio.");

        var psi = new ProcessStartInfo(_dumpbinPath, $"/dependents \"{binaryPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dumpbin");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);

        var deps = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => DllPattern().IsMatch(l))
            .ToList();

        // For DLLs, first match is the DLL itself
        if (!isExe && deps.Count > 0)
        {
            deps = deps.Skip(1).ToList();
        }

        deps.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. deps];
    }

    /// <summary>
    /// Compare actual dependencies against a baseline file.
    /// </summary>
    public (bool matches, string[] missing, string[] extra) CompareBaseline(
        string binaryPath, string baselinePath, bool isExe)
    {
        var actual = GetDependencies(binaryPath, isExe);
        var expected = File.ReadAllLines(baselinePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();

        return (missing.Length == 0 && extra.Length == 0, missing, extra);
    }

    /// <summary>
    /// dumpbin.exe locations relative to an MSVC tools directory (<c>VC\Tools\MSVC\&lt;version&gt;</c>), in
    /// preference order: the host-native tools on ARM64 hosts, then the x64 tools.
    /// </summary>
    internal static string[] DumpbinRelativePaths(Architecture hostArch) =>
        hostArch == Architecture.Arm64
            ? [Path.Combine("bin", "HostARM64", "arm64", "dumpbin.exe"), Path.Combine("bin", "Hostx64", "x64", "dumpbin.exe")]
            : [Path.Combine("bin", "Hostx64", "x64", "dumpbin.exe")];

    /// <summary>First existing dumpbin.exe under an MSVC tools directory for the host architecture, or null.</summary>
    internal static string? FindDumpbinInToolsDir(string toolsDir, Architecture hostArch) =>
        DumpbinRelativePaths(hostArch)
            .Select(rel => Path.Combine(toolsDir, rel))
            .FirstOrDefault(File.Exists);

    private static string? FindDumpbin(string? vsToolsPath)
    {
        // OS (not process) architecture: an x64 server emulated on an ARM64 host still prefers native tools.
        var hostArch = RuntimeInformation.OSArchitecture;

        if (vsToolsPath != null && FindDumpbinInToolsDir(vsToolsPath, hostArch) is { } explicitPath)
            return explicitPath;

        // Try VS_TOOLS_PATH env
        var envPath = Environment.GetEnvironmentVariable("VS_TOOLS_PATH");
        if (envPath != null && FindDumpbinInToolsDir(envPath, hostArch) is { } envDumpbin)
            return envDumpbin;

        // Try vswhere
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (File.Exists(vswhere))
        {
            var psi = new ProcessStartInfo(vswhere, "-latest -property installationPath")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var vsPath = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(10000);

                // Find latest MSVC tools version
                var toolsDir = Path.Combine(vsPath, "VC", "Tools", "MSVC");
                if (Directory.Exists(toolsDir))
                {
                    var latest = Directory.GetDirectories(toolsDir)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();
                    if (latest != null && FindDumpbinInToolsDir(latest, hostArch) is { } vsDumpbin)
                        return vsDumpbin;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[\w\-]+\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex DllPattern();
}
