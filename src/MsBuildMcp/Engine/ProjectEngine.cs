// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

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
    private readonly VsToolchainProvider _vs;
    private static bool _locatorRegistered;
    private static readonly object _locatorLock = new();

    /// <summary>Uses the shared vswhere locator with <c>MSBUILD_MCP_VS</c> as the default selector.</summary>
    public ProjectEngine() : this(VsToolchainProvider.FromEnvironment()) { }

    /// <param name="vs">Resolves per-call <c>vs</c> selectors passed to <see cref="Evaluate"/>.</param>
    public ProjectEngine(VsToolchainProvider vs)
    {
        _vs = vs;
    }

    /// <summary>
    /// The VCTargetsPath this process set in its own environment for evaluation, or null if
    /// none was injected (e.g. the user already set VCTargetsPath, or no VS C++ targets were found).
    /// </summary>
    internal static string? InjectedVcTargetsPath { get; private set; }

    /// <param name="vs">VS selection to use for C++ evaluation; defaults to <c>MSBUILD_MCP_VS</c> / latest.</param>
    public static void EnsureMsBuildRegistered(VsToolchainProvider? vs = null)
    {
        lock (_locatorLock)
        {
            if (!_locatorRegistered)
            {
                // Strategy: use dotnet SDK MSBuild (has working .NET SDK resolvers for
                // <Sdk Name="..."/> imports) + VS VCTargetsPath (for C++ project evaluation).
                //
                // VS MSBuild's SDK resolvers target .NET Framework and can't load in our .NET
                // host process. But the dotnet SDK MSBuild evaluates C++ projects fine when
                // VCTargetsPath is set as an environment variable.

                // Step 1: Resolve the selected VS install for VCTargetsPath (needed for C++ evaluation)
                ApplyVcTargetsPath(vs ?? VsToolchainProvider.FromEnvironment());

                // Step 2: Register dotnet SDK MSBuild (has working NuGet/DotNet SDK resolvers)
                var instance = MSBuildLocator.RegisterDefaults();
                Console.Error.WriteLine($"msbuild-mcp: MSBuild registered: {instance.Name} at {instance.MSBuildPath}");

                _locatorRegistered = true;
            }
        }
    }

    private static void ApplyVcTargetsPath(VsToolchainProvider vs)
    {
        VsInstance selected;
        try
        {
            selected = vs.ResolveInstance();
        }
        catch (Exception ex)
        {
            // Non-fatal: .NET projects still evaluate; builds report the same error.
            Console.Error.WriteLine($"msbuild-mcp: Visual Studio selection failed: {ex.Message}");
            return;
        }

        Console.Error.WriteLine($"msbuild-mcp: Visual Studio: {selected.Describe()}" +
            (vs.DefaultSpec != null ? $" (selected by '{vs.DefaultSpec}')" : ""));

        if (selected.VcTargetsPath == null)
        {
            Console.Error.WriteLine("msbuild-mcp: no C++ targets found in selected Visual Studio — C++ evaluation unavailable");
            return;
        }

        var existing = Environment.GetEnvironmentVariable("VCTargetsPath");
        if (!string.IsNullOrEmpty(existing))
        {
            if (!string.Equals(existing.TrimEnd('\\'), selected.VcTargetsPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(
                    $"msbuild-mcp: warning: keeping user-set VCTargetsPath={existing} " +
                    $"(selected Visual Studio uses {selected.VcTargetsPath})");
            return;
        }

        Environment.SetEnvironmentVariable("VCTargetsPath", selected.VcTargetsPath);
        InjectedVcTargetsPath = selected.VcTargetsPath;
        Console.Error.WriteLine($"msbuild-mcp: VCTargetsPath={selected.VcTargetsPath}");
    }

    /// <summary>
    /// Evaluate a project file with the given global properties.
    /// Returns a snapshot of the evaluated state.
    /// </summary>
    /// <param name="vs">
    /// Optional Visual Studio selector (see <see cref="VsSelector"/>). When it resolves to an
    /// installation other than the one whose C++ targets this process uses by default, that
    /// installation's VCTargetsPath is applied for this evaluation only.
    /// </param>
    public ProjectSnapshot Evaluate(string projectPath, string configuration = "Debug",
        string platform = "x64", string? solutionDir = null,
        Dictionary<string, string>? additionalProperties = null, string? vs = null)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
            throw new FileNotFoundException($"Project file not found: {projectPath}");

        // Auto-infer SolutionDir if not provided
        solutionDir ??= InferSolutionDir(projectPath);

        var vcTargetsPath = ResolveVcTargetsOverride(vs);

        var mtime = File.GetLastWriteTimeUtc(projectPath);
        var propsKey = BuildPropsKey(additionalProperties);
        var key = $"{projectPath}|{configuration}|{platform}|{solutionDir}|{propsKey}";
        if (vcTargetsPath != null)
            key += $"|vc={Uri.EscapeDataString(vcTargetsPath)}";

        if (_cache.TryGetValue(key, out var cached) && cached.Mtime == mtime)
            return cached.Snapshot;

        var snapshot = EvaluateCore(projectPath, configuration, platform, solutionDir, additionalProperties, vcTargetsPath);
        _cache[key] = new CachedProject { Mtime = mtime, Snapshot = snapshot };
        return snapshot;
    }

    /// <summary>
    /// Throw if a <c>vs</c> selector is invalid or matches no installation. Call before evaluation
    /// loops that swallow per-project errors, so a bad selector is reported once.
    /// </summary>
    public void ValidateVs(string? vs)
    {
        if (!string.IsNullOrWhiteSpace(vs))
            _vs.ResolveInstance(vs);
    }

    /// <summary>
    /// VCTargetsPath to apply for an explicit <paramref name="vs"/> selector, or null to use the
    /// process default (no selector, no C++ targets in the selected install, or it is already
    /// the VCTargetsPath this process injected).
    /// </summary>
    private string? ResolveVcTargetsOverride(string? vs)
    {
        if (string.IsNullOrWhiteSpace(vs)) return null;
        var vcTargets = _vs.ResolveInstance(vs).VcTargetsPath;
        if (vcTargets == null) return null;
        return string.Equals(vcTargets, InjectedVcTargetsPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : vcTargets;
    }

    /// <summary>
    /// Build a collision-free cache-key fragment from an <c>additionalProperties</c>
    /// dictionary. Each key and value is URL-encoded with <see cref="Uri.EscapeDataString"/>
    /// before being joined with <c>=</c>, and the resulting pairs are joined with <c>,</c>.
    /// Encoding both halves is required because property values can contain the
    /// separator characters <c>,</c> and <c>=</c> — without escaping, distinct
    /// inputs such as <c>{A=B, C=D}</c> and <c>{A="B,C=D"}</c> collide on the
    /// naive <c>k=v,k=v</c> serialization.
    /// </summary>
    internal static string BuildPropsKey(Dictionary<string, string>? additionalProperties)
    {
        if (additionalProperties is not { Count: > 0 }) return "";
        return string.Join(",", additionalProperties
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static ProjectSnapshot EvaluateCore(string projectPath, string configuration, string platform,
        string? solutionDir, Dictionary<string, string>? additionalProperties, string? vcTargetsPath = null)
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
        if (vcTargetsPath != null)
            SetToolsetProperty(collection, "VCTargetsPath", vcTargetsPath);
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

    /// <summary>
    /// Override a property on the collection's default toolset. Toolset properties take precedence
    /// over environment variables but — unlike global properties — can still be reassigned by
    /// project files. The VC targets rely on that: Microsoft.Cpp.ToolsetLocation.props temporarily
    /// redirects VCTargetsPath to locate platform toolsets from other VS versions. This mirrors how
    /// VS's MSBuild.exe.config defines VCTargetsPath.
    /// </summary>
    private static void SetToolsetProperty(ProjectCollection collection, string name, string value)
    {
        var toolsVersion = collection.DefaultToolsVersion;
        var toolset = collection.GetToolset(toolsVersion);
        if (toolset == null) return;

        var props = toolset.Properties.ToDictionary(
            kv => kv.Key, kv => kv.Value.EvaluatedValue, StringComparer.OrdinalIgnoreCase);
        props[name] = value;
        collection.RemoveToolset(toolsVersion);
        collection.AddToolset(new Toolset(toolsVersion, toolset.ToolsPath, props, collection, null));
    }

    private sealed class CachedProject
    {
        public DateTime Mtime { get; init; }
        public required ProjectSnapshot Snapshot { get; init; }
    }
}


