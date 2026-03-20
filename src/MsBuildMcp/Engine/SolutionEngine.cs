using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MsBuildMcp.Engine;

/// <summary>
/// Parses .sln files to enumerate projects, solution folders, and configurations.
/// Caches results keyed by (path, file-mtime).
/// </summary>
public sealed class SolutionEngine
{
    // Project("{GUID}") = "Name", "Path", "{GUID}"
    private static readonly Regex ProjectLineRegex = new(
        @"^Project\(""(?<TypeGuid>\{[^}]+\})""\)\s*=\s*""(?<Name>[^""]+)""\s*,\s*""(?<Path>[^""]+)""\s*,\s*""(?<Guid>\{[^}]+\})""",
        RegexOptions.Compiled);

    // GlobalSection(SolutionConfigurationPlatforms)
    private static readonly Regex ConfigRegex = new(
        @"^\s*(?<Config>[^|]+)\|(?<Platform>[^=]+)\s*=",
        RegexOptions.Compiled);

    // GlobalSection(NestedProjects) entries: {child} = {parent}
    private static readonly Regex NestedRegex = new(
        @"^\s*(?<Child>\{[^}]+\})\s*=\s*(?<Parent>\{[^}]+\})",
        RegexOptions.Compiled);

    public static readonly string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

    private readonly ConcurrentDictionary<string, CachedSolution> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SolutionInfo Parse(string slnPath)
    {
        slnPath = Path.GetFullPath(slnPath);
        if (!File.Exists(slnPath))
            throw new FileNotFoundException($"Solution file not found: {slnPath}");

        var mtime = File.GetLastWriteTimeUtc(slnPath);
        if (_cache.TryGetValue(slnPath, out var cached) && cached.Mtime == mtime)
            return cached.Info;

        var lines = File.ReadAllLines(slnPath);
        var projects = new List<SolutionProject>();
        var configs = new List<SolutionConfig>();
        var nesting = new Dictionary<string, string>(); // childGuid -> parentGuid
        // ProjectDependencies: projectGuid -> list of dependency GUIDs
        var dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var inConfigSection = false;
        var inNestedSection = false;
        var inDepsSection = false;
        string? currentProjectGuid = null;

        foreach (var line in lines)
        {
            var match = ProjectLineRegex.Match(line);
            if (match.Success)
            {
                currentProjectGuid = match.Groups["Guid"].Value;
                projects.Add(new SolutionProject
                {
                    TypeGuid = match.Groups["TypeGuid"].Value,
                    Name = match.Groups["Name"].Value,
                    RelativePath = match.Groups["Path"].Value.Replace('\\', Path.DirectorySeparatorChar),
                    Guid = currentProjectGuid,
                    IsSolutionFolder = match.Groups["TypeGuid"].Value
                        .Equals(SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase),
                });
                continue;
            }

            if (line.Contains("ProjectSection(ProjectDependencies)"))
            {
                inDepsSection = true;
                if (currentProjectGuid != null && !dependencies.ContainsKey(currentProjectGuid))
                    dependencies[currentProjectGuid] = [];
                continue;
            }
            if (line.Contains("GlobalSection(SolutionConfigurationPlatforms)"))
            {
                inConfigSection = true;
                continue;
            }
            if (line.Contains("GlobalSection(NestedProjects)"))
            {
                inNestedSection = true;
                continue;
            }
            if (line.TrimStart().StartsWith("EndProjectSection"))
            {
                inDepsSection = false;
                continue;
            }
            if (line.TrimStart().StartsWith("EndGlobalSection"))
            {
                inConfigSection = false;
                inNestedSection = false;
                continue;
            }
            if (line.TrimStart().StartsWith("EndProject"))
            {
                currentProjectGuid = null;
                continue;
            }

            if (inDepsSection && currentProjectGuid != null)
            {
                // {GUID} = {GUID} — dependency declaration
                var dm = NestedRegex.Match(line); // Same GUID=GUID pattern
                if (dm.Success)
                    dependencies[currentProjectGuid].Add(dm.Groups["Child"].Value);
            }

            if (inConfigSection)
            {
                var cm = ConfigRegex.Match(line);
                if (cm.Success)
                {
                    var cfg = new SolutionConfig
                    {
                        Configuration = cm.Groups["Config"].Value.Trim(),
                        Platform = cm.Groups["Platform"].Value.Trim(),
                    };
                    if (!configs.Any(c => c.Configuration == cfg.Configuration && c.Platform == cfg.Platform))
                        configs.Add(cfg);
                }
            }

            if (inNestedSection)
            {
                var nm = NestedRegex.Match(line);
                if (nm.Success)
                    nesting[nm.Groups["Child"].Value] = nm.Groups["Parent"].Value;
            }
        }

        // Resolve solution folder paths
        var guidToProject = projects.ToDictionary(p => p.Guid, StringComparer.OrdinalIgnoreCase);
        foreach (var proj in projects)
        {
            proj.SolutionFolder = ResolveFolderPath(proj.Guid, nesting, guidToProject);

            // Resolve dependency GUIDs to project names
            if (dependencies.TryGetValue(proj.Guid, out var depGuids))
            {
                foreach (var depGuid in depGuids)
                {
                    if (guidToProject.TryGetValue(depGuid, out var depProj))
                        proj.SolutionDependencies.Add(depProj.Name);
                }
            }
        }

        var info = new SolutionInfo
        {
            Path = slnPath,
            Directory = Path.GetDirectoryName(slnPath)!,
            Projects = projects,
            Configurations = configs,
        };

        _cache[slnPath] = new CachedSolution { Mtime = mtime, Info = info };
        return info;
    }

    private static string ResolveFolderPath(
        string guid,
        Dictionary<string, string> nesting,
        Dictionary<string, SolutionProject> guidToProject)
    {
        var parts = new List<string>();
        var current = guid;
        while (nesting.TryGetValue(current, out var parentGuid))
        {
            if (guidToProject.TryGetValue(parentGuid, out var parent))
                parts.Insert(0, parent.Name);
            current = parentGuid;
        }
        return string.Join("\\", parts);
    }

    private sealed class CachedSolution
    {
        public DateTime Mtime { get; init; }
        public required SolutionInfo Info { get; init; }
    }
}


