// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MsBuildMcp.Engine;

/// <summary>
/// A Visual Studio (or Build Tools) installation reported by vswhere.
/// </summary>
public sealed record VsInstance
{
    public required string InstallationPath { get; init; }
    public required string InstallationVersion { get; init; }
    public required Version Version { get; init; }
    public string? DisplayName { get; init; }
    public string? ProductId { get; init; }
    public int? ProductYear { get; init; }
    public bool IsPrerelease { get; init; }

    /// <summary>VS MSBuild.exe (amd64 preferred). Null if not found on disk.</summary>
    public string? MsBuildPath { get; init; }

    /// <summary>Highest complete <c>MSBuild\Microsoft\VC\vNNN\</c> directory (trailing slash). Null if no C++ targets are installed.</summary>
    public string? VcTargetsPath { get; init; }

    public string Describe() =>
        $"{DisplayName ?? "Visual Studio"} {InstallationVersion}{(IsPrerelease ? " (prerelease)" : "")} at {InstallationPath}";

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["display_name"] = DisplayName,
            ["version"] = InstallationVersion,
            ["path"] = InstallationPath,
        };
        if (ProductYear.HasValue) obj["year"] = ProductYear.Value;
        if (IsPrerelease) obj["prerelease"] = true;
        return obj;
    }
}

/// <summary>
/// Parsed Visual Studio selector. Accepted forms:
/// <list type="bullet">
/// <item><c>latest</c> (or empty) — newest installation</item>
/// <item>product year, e.g. <c>2022</c>, <c>2026</c></item>
/// <item>version prefix, e.g. <c>17</c>, <c>17.14</c></item>
/// <item>version range, e.g. <c>[17,18)</c>, <c>[18.0,)</c></item>
/// <item>absolute installation path (must be an installation reported by vswhere)</item>
/// </list>
/// </summary>
public sealed class VsSelector
{
    public const string SyntaxHelp =
        "'latest', a product year (e.g. '2022', '2026'), a version prefix (e.g. '17', '17.14'), " +
        "a version range (e.g. '[17,18)'), or an installation path";

    private enum Kind { Latest, Year, VersionPrefix, Range, Path }

    private static readonly Regex YearRegex = new(@"^20\d\d$", RegexOptions.Compiled);
    private static readonly Regex PrefixRegex = new(@"^\d{1,3}(\.\d+){0,3}$", RegexOptions.Compiled);
    private static readonly Regex RangeRegex = new(
        @"^(?<lo>[\[\(])\s*(?<min>[\d.]*)\s*,\s*(?<max>[\d.]*)\s*(?<hi>[\]\)])$", RegexOptions.Compiled);

    private Kind _kind;
    private int _year;
    private int[] _prefix = [];
    private Version? _min, _max;
    private bool _minInclusive, _maxInclusive;
    private string _path = "";

    public string Spec { get; private init; } = "latest";

    private VsSelector() { }

    /// <summary>Parse a selector. Null/empty/"latest" select the newest installation.</summary>
    /// <exception cref="ArgumentException">The selector is not in a recognized form.</exception>
    public static VsSelector Parse(string? spec)
    {
        var s = spec?.Trim() ?? "";
        if (s.Length == 0 || s.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return new VsSelector { _kind = Kind.Latest };

        if (YearRegex.IsMatch(s))
            return new VsSelector { Spec = s, _kind = Kind.Year, _year = int.Parse(s) };

        if (PrefixRegex.IsMatch(s))
            return new VsSelector { Spec = s, _kind = Kind.VersionPrefix, _prefix = s.Split('.').Select(int.Parse).ToArray() };

        var m = RangeRegex.Match(s);
        if (m.Success)
        {
            var min = ParseBound(m.Groups["min"].Value, s);
            var max = ParseBound(m.Groups["max"].Value, s);
            if (min == null && max == null)
                throw Invalid(s);
            return new VsSelector
            {
                Spec = s,
                _kind = Kind.Range,
                _min = min,
                _max = max,
                _minInclusive = m.Groups["lo"].Value == "[",
                _maxInclusive = m.Groups["hi"].Value == "]",
            };
        }

        if (System.IO.Path.IsPathFullyQualified(s))
            return new VsSelector { Spec = s, _kind = Kind.Path, _path = NormalizePath(s) };

        throw Invalid(s);
    }

    public bool Matches(VsInstance instance)
    {
        switch (_kind)
        {
            case Kind.Latest:
                return true;
            case Kind.Year:
                return instance.ProductYear == _year;
            case Kind.VersionPrefix:
                var parts = new[] { instance.Version.Major, instance.Version.Minor, instance.Version.Build, instance.Version.Revision };
                for (var i = 0; i < _prefix.Length; i++)
                    if (parts[i] != _prefix[i]) return false;
                return true;
            case Kind.Range:
                if (_min != null)
                {
                    var c = instance.Version.CompareTo(_min);
                    if (c < 0 || (c == 0 && !_minInclusive)) return false;
                }
                if (_max != null)
                {
                    var c = instance.Version.CompareTo(_max);
                    if (c > 0 || (c == 0 && !_maxInclusive)) return false;
                }
                return true;
            case Kind.Path:
                return string.Equals(NormalizePath(instance.InstallationPath), _path, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    internal static string NormalizePath(string path)
    {
        try { path = System.IO.Path.GetFullPath(path); } catch { /* keep as-is */ }
        return path.TrimEnd('\\', '/');
    }

    private static Version? ParseBound(string text, string spec)
    {
        if (text.Length == 0) return null;
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? v : throw Invalid(spec);
    }

    private static ArgumentException Invalid(string spec) =>
        new($"Invalid Visual Studio selector '{spec}'. Use {SyntaxHelp}.");
}

/// <summary>
/// Discovers Visual Studio installations via vswhere and resolves selectors to an instance.
/// The installation list is discovered once and cached for the process lifetime.
/// </summary>
public sealed class VsLocator
{
    // Year ↔ major version for products whose catalog productLineVersion is not the year
    // (VS 2026 reports productLineVersion "18").
    private static readonly Dictionary<int, int> YearByMajor = new()
    {
        [15] = 2017, [16] = 2019, [17] = 2022, [18] = 2026,
    };

    private static readonly Regex YearInNameRegex = new(@"\b(20\d\d)\b", RegexOptions.Compiled);

    private readonly Func<string?> _runVsWhere;
    private readonly bool _probeFileSystem;
    private readonly Lazy<IReadOnlyList<VsInstance>> _instances;

    public static VsLocator Default { get; } = new(RunVsWhere);

    /// <param name="runVsWhere">Returns vswhere JSON output, or null when vswhere is unavailable.</param>
    /// <param name="probeFileSystem">When false, MSBuild/VCTargets paths are not resolved (test seam).</param>
    public VsLocator(Func<string?> runVsWhere, bool probeFileSystem = true)
    {
        _runVsWhere = runVsWhere;
        _probeFileSystem = probeFileSystem;
        // PublicationOnly: don't cache a transient vswhere failure (exception) forever.
        _instances = new(Discover, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>Installations ordered by preference: stable before prerelease, then newest first.</summary>
    public IReadOnlyList<VsInstance> Instances => _instances.Value;

    /// <summary>
    /// Resolve a selector to the preferred matching installation.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid selector syntax.</exception>
    /// <exception cref="InvalidOperationException">No installation matches.</exception>
    public VsInstance Resolve(string? spec)
    {
        var selector = VsSelector.Parse(spec);
        var instances = Instances;
        if (instances.Count == 0)
            throw new InvalidOperationException(
                "No Visual Studio installation with MSBuild was found via vswhere. " +
                "Install Visual Studio or Build Tools with the MSBuild component.");

        var match = instances.FirstOrDefault(selector.Matches);
        if (match == null)
            throw new InvalidOperationException(
                $"No Visual Studio installation matches '{selector.Spec}'. Available: " +
                string.Join("; ", instances.Select(i => i.Describe())));
        return match;
    }

    private IReadOnlyList<VsInstance> Discover()
    {
        var json = _runVsWhere();
        if (string.IsNullOrWhiteSpace(json)) return [];
        var instances = ParseVsWhereJson(json);
        if (_probeFileSystem)
            instances = instances.Select(i => i with
            {
                MsBuildPath = FindMsBuildExe(i.InstallationPath),
                VcTargetsPath = FindVcTargetsPath(i.InstallationPath),
            }).ToList();
        return Order(instances);
    }

    internal static IReadOnlyList<VsInstance> Order(IEnumerable<VsInstance> instances) =>
        instances.OrderBy(i => i.IsPrerelease).ThenByDescending(i => i.Version).ToList();

    /// <summary>Parse <c>vswhere -format json</c> output. Entries without a path or parsable version are skipped.</summary>
    internal static List<VsInstance> ParseVsWhereJson(string json)
    {
        var result = new List<VsInstance>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var path = GetString(e, "installationPath");
            var versionText = GetString(e, "installationVersion");
            if (string.IsNullOrEmpty(path) || versionText == null || !Version.TryParse(versionText, out var version))
                continue;

            var displayName = GetString(e, "displayName");
            string? productLine = null;
            if (e.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Object)
                productLine = GetString(catalog, "productLineVersion");

            result.Add(new VsInstance
            {
                InstallationPath = path,
                InstallationVersion = versionText,
                Version = version,
                DisplayName = displayName,
                ProductId = GetString(e, "productId"),
                ProductYear = InferYear(productLine, version.Major, displayName),
                IsPrerelease = e.TryGetProperty("isPrerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            });
        }
        return result;
    }

    internal static int? InferYear(string? productLineVersion, int major, string? displayName)
    {
        if (int.TryParse(productLineVersion, out var line) && line >= 2000)
            return line;
        if (YearByMajor.TryGetValue(major, out var year))
            return year;
        var m = displayName != null ? YearInNameRegex.Match(displayName) : Match.Empty;
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    internal static string? FindMsBuildExe(string installPath)
    {
        foreach (var rel in new[] { @"MSBuild\Current\Bin\amd64\MSBuild.exe", @"MSBuild\Current\Bin\MSBuild.exe" })
        {
            var p = Path.Combine(installPath, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Find the highest <c>MSBuild\Microsoft\VC\vNNN</c> directory that contains a complete
    /// C++ targets set (<c>Microsoft.Cpp.Default.props</c>). Newer VS versions also ship stub
    /// directories for older toolsets (e.g. VS 2026 has v150–v170 stubs alongside v180).
    /// </summary>
    internal static string? FindVcTargetsPath(string installPath)
    {
        var vcRoot = Path.Combine(installPath, @"MSBuild\Microsoft\VC");
        if (!Directory.Exists(vcRoot)) return null;

        var best = Directory.GetDirectories(vcRoot, "v*")
            .Select(d => (Dir: d, Num: int.TryParse(Path.GetFileName(d)[1..], out var n) ? n : -1))
            .Where(x => x.Num >= 0 && File.Exists(Path.Combine(x.Dir, "Microsoft.Cpp.Default.props")))
            .OrderByDescending(x => x.Num)
            .Select(x => x.Dir)
            .FirstOrDefault();
        return best == null ? null : best.TrimEnd('\\') + "\\";
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? RunVsWhere()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
        {
            Console.Error.WriteLine($"msbuild-mcp: vswhere.exe not found at {vswhere}");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-prerelease -products * -requires Microsoft.Component.MSBuild -format json -utf8",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"msbuild-mcp: vswhere failed: {ex.Message}");
            return null;
        }
    }
}
