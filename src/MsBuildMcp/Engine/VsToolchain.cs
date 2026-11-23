// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MsBuildMcp.Engine;

/// <summary>
/// A resolved VS build toolchain: MSBuild.exe plus the vcvarsall environment.
/// </summary>
public sealed record VsToolchain(VsInstance Instance, string MsBuildPath, Dictionary<string, string> Environment);

/// <summary>
/// Resolves Visual Studio selectors to toolchains. Holds the server-wide default selector
/// (<c>--vs</c> / <c>MSBUILD_MCP_VS</c>) and caches one captured toolchain per installation.
/// </summary>
public sealed class VsToolchainProvider
{
    public const string EnvVar = "MSBUILD_MCP_VS";

    private readonly Func<VsInstance, VsToolchain> _capture;
    private readonly ConcurrentDictionary<string, Lazy<VsToolchain>> _toolchains =
        new(StringComparer.OrdinalIgnoreCase);

    public VsLocator Locator { get; }

    /// <summary>Server-wide default selector; null means "latest".</summary>
    public string? DefaultSpec { get; }

    public VsToolchainProvider(VsLocator locator, string? defaultSpec,
        Func<VsInstance, VsToolchain>? capture = null)
    {
        Locator = locator;
        DefaultSpec = string.IsNullOrWhiteSpace(defaultSpec) ? null : defaultSpec.Trim();
        _capture = capture ?? CaptureToolchain;
    }

    /// <summary>
    /// Provider using the shared vswhere locator. The default selector is chosen by
    /// <see cref="SelectDefaultSpec"/>: <paramref name="explicitSpec"/> (<c>--vs</c>) &gt;
    /// <c>MSBUILD_MCP_VS</c> &gt; latest.
    /// </summary>
    /// <remarks>Without an explicit selector this returns a process-wide instance so captured toolchains are shared.</remarks>
    public static VsToolchainProvider FromEnvironment(string? explicitSpec = null) =>
        string.IsNullOrWhiteSpace(explicitSpec)
            ? _shared.Value
            : Create(VsLocator.Default, explicitSpec, Environment.GetEnvironmentVariable);

    private static readonly Lazy<VsToolchainProvider> _shared =
        new(() => Create(VsLocator.Default, null, Environment.GetEnvironmentVariable));

    /// <summary>Create a provider whose default selector follows <see cref="SelectDefaultSpec"/>.</summary>
    internal static VsToolchainProvider Create(VsLocator locator, string? explicitSpec,
        Func<string, string?> getEnvironmentVariable, Func<VsInstance, VsToolchain>? capture = null) =>
        new(locator, SelectDefaultSpec(explicitSpec, getEnvironmentVariable(EnvVar)), capture);

    /// <summary>
    /// Default selector precedence: explicit (<c>--vs</c>) &gt; <c>MSBUILD_MCP_VS</c> &gt; null (latest).
    /// Blank values are treated as unset.
    /// </summary>
    internal static string? SelectDefaultSpec(string? explicitSpec, string? environmentSpec) =>
        !string.IsNullOrWhiteSpace(explicitSpec) ? explicitSpec.Trim()
        : !string.IsNullOrWhiteSpace(environmentSpec) ? environmentSpec.Trim()
        : null;

    /// <summary>Resolve a selector (or the server default when null/empty) to an installation.</summary>
    public VsInstance ResolveInstance(string? spec = null) =>
        Locator.Resolve(string.IsNullOrWhiteSpace(spec) ? DefaultSpec : spec);

    /// <summary>
    /// Get the build toolchain for a selector (or the server default). The vcvarsall environment is
    /// captured once per installation; a failed capture is not cached so it can be retried.
    /// </summary>
    public VsToolchain GetToolchain(string? spec = null) => GetToolchain(ResolveInstance(spec));

    /// <summary>Get (capturing on first use) the build toolchain for a resolved installation.</summary>
    public VsToolchain GetToolchain(VsInstance instance)
    {
        var key = VsSelector.NormalizePath(instance.InstallationPath);
        var lazy = _toolchains.GetOrAdd(key, _ => new Lazy<VsToolchain>(() => _capture(instance)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _toolchains.TryRemove(new KeyValuePair<string, Lazy<VsToolchain>>(key, lazy));
            throw;
        }
    }

    internal static VsToolchain CaptureToolchain(VsInstance instance) =>
        CaptureToolchain(instance, VsLocator.HostArchitecture, RunVcvarsall);

    /// <summary>
    /// vcvarsall.bat architecture arguments to try, in order. ARM64 hosts prefer the native
    /// <c>arm64</c> toolset and fall back to <c>amd64</c> (emulated x64 tools) when it isn't installed.
    /// </summary>
    internal static string[] VcvarsArchitectures(Architecture hostArch) =>
        hostArch == Architecture.Arm64 ? ["arm64", "amd64"] : ["amd64"];

    internal static VsToolchain CaptureToolchain(VsInstance instance, Architecture hostArch,
        Func<string, string, (int ExitCode, Dictionary<string, string> Environment)> runVcvarsall)
    {
        Console.Error.WriteLine($"msbuild-mcp: capturing build toolchain for {instance.Describe()}");

        var msbuildPath = instance.MsBuildPath
            ?? throw new FileNotFoundException($"MSBuild.exe not found under {instance.InstallationPath}");

        var vcvarsall = Path.Combine(instance.InstallationPath, @"VC\Auxiliary\Build\vcvarsall.bat");
        if (!File.Exists(vcvarsall))
            throw new FileNotFoundException(
                "vcvarsall.bat not found — VC++ build tools may not be installed", vcvarsall);

        var archs = VcvarsArchitectures(hostArch);
        for (var i = 0; ; i++)
        {
            var arch = archs[i];
            var isLast = i == archs.Length - 1;
            var (exitCode, env) = runVcvarsall(vcvarsall, arch);
            if (exitCode != 0)
            {
                if (isLast)
                    throw new InvalidOperationException(
                        $"vcvarsall.bat {arch} failed (exit code {exitCode}) for {instance.Describe()}");
                Console.Error.WriteLine(
                    $"msbuild-mcp: vcvarsall.bat {arch} failed (exit code {exitCode}); falling back to {archs[i + 1]}");
                continue;
            }

            // vcvarsall succeeds even when the requested host toolset isn't installed (its bin
            // directory is added to PATH only if present), so check for the compiler explicitly.
            if (!isLast && !HasCompiler(env, arch))
            {
                Console.Error.WriteLine(
                    $"msbuild-mcp: {arch} C++ build tools not installed; falling back to {archs[i + 1]}");
                continue;
            }

            Console.Error.WriteLine($"msbuild-mcp: MSBuild: {msbuildPath}");
            Console.Error.WriteLine($"msbuild-mcp: captured {env.Count} environment variables from vcvarsall {arch}");
            return new VsToolchain(instance, msbuildPath, env);
        }
    }

    /// <summary>Whether the vcvarsall environment's toolset has the host-native <c>cl.exe</c> for <paramref name="arch"/>.</summary>
    internal static bool HasCompiler(IReadOnlyDictionary<string, string> env, string arch)
    {
        if (!env.TryGetValue("VCToolsInstallDir", out var toolsDir) || string.IsNullOrEmpty(toolsDir))
            return false;
        var (host, target) = arch == "arm64" ? ("HostARM64", "arm64") : ("Hostx64", "x64");
        return File.Exists(Path.Combine(toolsDir, "bin", host, target, "cl.exe"));
    }

    private static (int ExitCode, Dictionary<string, string> Environment) RunVcvarsall(string vcvarsall, string arch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{vcvarsall}\" {arch} && set\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var envProc = Process.Start(psi)!;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (envProc.StandardOutput.ReadLine() is { } line)
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                env[line[..eq]] = line[(eq + 1)..];
        }
        envProc.WaitForExit();
        return (envProc.ExitCode, env);
    }
}
