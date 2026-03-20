using System.Collections.Concurrent;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;

namespace MsBuildMcp.Engine;

/// <summary>
/// Evaluates .vcxproj/.csproj files using Microsoft.Build APIs.
/// Caches results keyed by (path, config, platform, file-mtime).
/// </summary>
public sealed class ProjectEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedProject> _cache = new();
    private static bool _locatorRegistered;
    private static readonly object _locatorLock = new();

    public static void EnsureMsBuildRegistered()
    {
        lock (_locatorLock)
        {
            if (!_locatorRegistered)
            {
                // Strategy: use dotnet SDK MSBuild (has working .NET 9 SDK resolvers for
                // <Sdk Name="..."/> imports) + VS VCTargetsPath (for C++ project evaluation).
                //
                // VS MSBuild's SDK resolvers target .NET Framework and can't load in our .NET 9
                // host process. But the dotnet SDK MSBuild evaluates C++ projects fine when
                // VCTargetsPath is set as an environment variable.

                // Step 1: Discover VS install for VCTargetsPath (needed for C++ evaluation)
                var vsPath = DiscoverVsInstallPath();
                if (vsPath != null)
                {
                    var vcTargets = Path.Combine(vsPath, @"MSBuild\Microsoft\VC\v170\");
                    if (Directory.Exists(vcTargets) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VCTargetsPath")))
                    {
                        Environment.SetEnvironmentVariable("VCTargetsPath", vcTargets);
                        Console.Error.WriteLine($"msbuild-mcp: VCTargetsPath={vcTargets}");
                    }
                }

                // Step 2: Register dotnet SDK MSBuild (has working NuGet/DotNet SDK resolvers)
                var instance = MSBuildLocator.RegisterDefaults();
                Console.Error.WriteLine($"msbuild-mcp: MSBuild registered: {instance.Name} at {instance.MSBuildPath}");

                _locatorRegistered = true;
            }
        }
    }

    private static string? DiscoverVsInstallPath()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere)) return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = vswhere,
                Arguments = "-latest -requires Microsoft.Component.MSBuild -property installationPath",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var path = proc.StandardOutput.ReadLine()?.Trim();
            proc.WaitForExit();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluate a project file with the given global properties.
    /// Returns a snapshot of the evaluated state.
    /// </summary>
    public ProjectSnapshot Evaluate(string projectPath, string configuration = "Debug",
        string platform = "x64", string? solutionDir = null,
        Dictionary<string, string>? additionalProperties = null)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project file not found: {projectPath}");

        // Auto-infer SolutionDir if not provided
        solutionDir ??= InferSolutionDir(projectPath);

        var mtime = File.GetLastWriteTimeUtc(projectPath);
        var key = $"{projectPath}|{configuration}|{platform}|{solutionDir}";

        if (_cache.TryGetValue(key, out var cached) && cached.Mtime == mtime)
            return cached.Snapshot;

        var snapshot = EvaluateCore(projectPath, configuration, platform, solutionDir, additionalProperties);
        _cache[key] = new CachedProject { Mtime = mtime, Snapshot = snapshot };
        return snapshot;
    }

    private static ProjectSnapshot EvaluateCore(string projectPath, string configuration, string platform,
        string? solutionDir, Dictionary<string, string>? additionalProperties)
    {
        var globalProps = new Dictionary<string, string>
        {
            ["Configuration"] = configuration,
            ["Platform"] = platform,
        };

        // Set SolutionDir and related properties (MSBuild normally sets these from the .sln)
        if (solutionDir != null)
        {
            // MSBuild requires trailing backslash on SolutionDir
            if (!solutionDir.EndsWith('\\') && !solutionDir.EndsWith('/'))
                solutionDir += Path.DirectorySeparatorChar;
            globalProps["SolutionDir"] = solutionDir;

            // Find the .sln file for SolutionPath/Name/FileName
            var slnFiles = Directory.GetFiles(solutionDir.TrimEnd('\\', '/'), "*.sln");
            if (slnFiles.Length > 0)
            {
                var slnFile = slnFiles[0];
                globalProps["SolutionPath"] = slnFile;
                globalProps["SolutionName"] = Path.GetFileNameWithoutExtension(slnFile);
                globalProps["SolutionFileName"] = Path.GetFileName(slnFile);
                globalProps["SolutionExt"] = Path.GetExtension(slnFile);
            }
        }

        // Merge additional properties (highest priority — overrides everything)
        if (additionalProperties != null)
        {
            foreach (var (key, value) in additionalProperties)
                globalProps[key] = value;
        }

        using var collection = new ProjectCollection(globalProps);
        var project = collection.LoadProject(projectPath);

        var snapshot = new ProjectSnapshot
        {
            FullPath = projectPath,
            Configuration = configuration,
            Platform = platform,
            Properties = ExtractProperties(project),
            AllProperties = ExtractAllProperties(project),
            Items = ExtractItems(project),
            ItemDefinitions = ExtractItemDefinitions(project),
            ProjectReferences = ExtractProjectReferences(project, projectPath),
            PackageReferences = ExtractPackageReferences(project),
            Imports = ExtractImports(project),
        };

        return snapshot;
    }

    private static Dictionary<string, string> ExtractProperties(Project project)
    {
        var interestingProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OutputType", "TargetName", "TargetExt", "OutDir", "IntDir",
            "ConfigurationType", "PlatformToolset", "WindowsTargetPlatformVersion",
            "RootNamespace", "ProjectGuid", "TargetPath",
            "ClCompile_PreprocessorDefinitions", "NMakePreprocessorDefinitions",
            "LanguageStandard", "AdditionalIncludeDirectories",
        };

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in project.AllEvaluatedProperties)
        {
            if (interestingProps.Contains(prop.Name))
                result[prop.Name] = prop.EvaluatedValue;
        }

        // Always include Configuration and Platform
        result["Configuration"] = project.GetPropertyValue("Configuration");
        result["Platform"] = project.GetPropertyValue("Platform");
        result["OutputType"] = project.GetPropertyValue("ConfigurationType");
        if (string.IsNullOrEmpty(result["OutputType"]))
            result["OutputType"] = project.GetPropertyValue("OutputType");
        result["OutDir"] = project.GetPropertyValue("OutDir");
        result["TargetName"] = project.GetPropertyValue("TargetName");
        result["TargetExt"] = project.GetPropertyValue("TargetExt");

        return result;
    }

    /// <summary>
    /// Extract ALL evaluated properties (for find_property queries and properties filter).
    /// Filters out environment-derived properties and MSBuild internals.
    /// </summary>
    private static Dictionary<string, string> ExtractAllProperties(Project project)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in project.AllEvaluatedProperties)
        {
            // Skip environment-derived properties (PATH, APPDATA, etc.) — they have no XML source
            // and aren't global properties we set explicitly. This cuts ~107K of noise.
            if (prop.Xml == null && !prop.IsGlobalProperty)
                continue;
            if (string.IsNullOrEmpty(prop.EvaluatedValue))
                continue;
            result[prop.Name] = prop.EvaluatedValue;
        }
        return result;
    }

    private static Dictionary<string, List<string>> ExtractItems(Project project)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // All common source/content item types across C++, C#, WiX
        // Note: ProjectReference is handled separately by ExtractProjectReferences
        var itemTypes = new[] {
            "ClCompile", "ClInclude", "None", "Midl", "ResourceCompile", "CustomBuild",
            "Compile", "Content", "EmbeddedResource", "Resource",
        };

        foreach (var itemType in itemTypes)
        {
            var items = project.GetItems(itemType).Select(i => i.EvaluatedInclude).ToList();
            if (items.Count > 0)
                result[itemType] = items;
        }

        return result;
    }

    private static List<ProjectReference> ExtractProjectReferences(Project project, string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        return project.GetItems("ProjectReference")
            .Select(i =>
            {
                var refPath = Path.GetFullPath(Path.Combine(projectDir, i.EvaluatedInclude));
                return new ProjectReference
                {
                    Include = i.EvaluatedInclude,
                    FullPath = refPath,
                    Name = Path.GetFileNameWithoutExtension(refPath),
                };
            })
            .ToList();
    }

    private static List<PackageReference> ExtractPackageReferences(Project project)
    {
        // Build a lookup from PackageVersion items (Central Package Management)
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pv in project.GetItems("PackageVersion"))
        {
            var ver = pv.GetMetadataValue("Version");
            if (!string.IsNullOrEmpty(ver))
                centralVersions[pv.EvaluatedInclude] = ver;
        }

        return project.GetItems("PackageReference")
            .Select(i =>
            {
                var version = i.GetMetadataValue("Version");
                // Fall back to central PackageVersion for CPM repos
                if (string.IsNullOrEmpty(version))
                    centralVersions.TryGetValue(i.EvaluatedInclude, out version);
                return new PackageReference
                {
                    Name = i.EvaluatedInclude,
                    Version = version ?? "",
                };
            })
            .ToList();
    }

    private static Dictionary<string, Dictionary<string, string>> ExtractItemDefinitions(Project project)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var itemTypes = new[] { "ClCompile", "Link", "Lib", "Midl" };
        var interestingMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PreprocessorDefinitions", "AdditionalIncludeDirectories", "RuntimeLibrary",
            "Optimization", "DebugInformationFormat", "WarningLevel", "TreatWarningAsError",
            "AdditionalOptions", "LanguageStandard", "ConformanceMode", "SDLCheck",
            "AdditionalDependencies", "SubSystem", "GenerateDebugInformation",
            "EnableCOMDATFolding", "OptimizeReferences", "OutputFile",
            "LinkTimeCodeGeneration",
        };

        // If the project compiles C++ source files, it's a C++ project — include all definition types.
        // If not (WiX, C#, cmake utility), skip all — inherited defs don't apply.
        bool isCppProject = project.GetItems("ClCompile").Count > 0;
        if (!isCppProject)
            return result;

        foreach (var itemType in itemTypes)
        {
            if (!project.ItemDefinitions.TryGetValue(itemType, out var definition))
                continue;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in definition.Metadata)
            {
                if (interestingMetadata.Contains(m.Name))
                    metadata[m.Name] = m.EvaluatedValue;
            }
            if (metadata.Count > 0)
                result[itemType] = metadata;
        }

        return result;
    }

    private static List<ProjectImport> ExtractImports(Project project)
    {
        var result = new List<ProjectImport>();
        foreach (var import in project.Imports)
        {
            result.Add(new ProjectImport
            {
                ImportedProject = import.ImportedProject.FullPath,
                ImportingElement = import.ImportingElement?.ContainingProject?.FullPath ?? "(root)",
                Condition = import.ImportingElement?.Condition ?? "",
                IsImported = import.IsImported,
            });
        }
        return result;
    }

    /// <summary>
    /// Walk parent directories to find a .sln file for SolutionDir inference.
    /// </summary>
    private static string? InferSolutionDir(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        while (dir != null)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
                return dir + Path.DirectorySeparatorChar;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public void InvalidateCache() => _cache.Clear();

    public void Dispose() => _cache.Clear();

    private sealed class CachedProject
    {
        public DateTime Mtime { get; init; }
        public required ProjectSnapshot Snapshot { get; init; }
    }
}


