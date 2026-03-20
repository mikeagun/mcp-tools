using System.Diagnostics;
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

    private static string? FindDumpbin(string? vsToolsPath)
    {
        if (vsToolsPath != null)
        {
            var path = Path.Combine(vsToolsPath, "bin", "Hostx64", "x64", "dumpbin.exe");
            if (File.Exists(path)) return path;
        }

        // Try VS_TOOLS_PATH env
        var envPath = Environment.GetEnvironmentVariable("VS_TOOLS_PATH");
        if (envPath != null)
        {
            var path = Path.Combine(envPath, "bin", "Hostx64", "x64", "dumpbin.exe");
            if (File.Exists(path)) return path;
        }

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
                    if (latest != null)
                    {
                        var path = Path.Combine(latest, "bin", "Hostx64", "x64", "dumpbin.exe");
                        if (File.Exists(path)) return path;
                    }
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"^[\w\-]+\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex DllPattern();
}
