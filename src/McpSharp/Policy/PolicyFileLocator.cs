// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

namespace McpSharp.Policy;

/// <summary>
/// Resolves the policy file a server reads its rules from and saves approved
/// rules to. Lookup order:
/// <list type="number">
///   <item>An explicit path (e.g. a <c>--policy</c> argument).</item>
///   <item>A path from an environment variable.</item>
///   <item>An existing file next to the executable (kept for existing installs).</item>
///   <item>A per-user file under <c>%LOCALAPPDATA%\mcp-tools\</c>.</item>
/// </list>
/// The resolved path is used for both reading and saving, so rules are only
/// written next to the executable when a policy file already exists there.
/// This keeps rules from being lost when the install directory is replaced
/// on upgrade or is not writable.
/// </summary>
public sealed class PolicyFileLocator
{
    /// <summary>Name of the per-user directory that holds policy files.</summary>
    public const string UserDirectoryName = "mcp-tools";

    /// <summary>
    /// Locator using the executable's directory and the per-user
    /// <c>%LOCALAPPDATA%\mcp-tools\</c> directory. The per-user path is resolved
    /// even if the folder doesn't exist yet; only when it can't be resolved at
    /// all does the locator fall back to the executable's directory.
    /// </summary>
    public static PolicyFileLocator Default { get; } =
        new(AppContext.BaseDirectory, GetDefaultUserDirectory());

    public PolicyFileLocator(string exeDirectory, string userDirectory)
    {
        ExeDirectory = exeDirectory;
        UserDirectory = userDirectory;
    }

    /// <summary>Directory checked for an existing policy file next to the executable.</summary>
    public string ExeDirectory { get; }

    /// <summary>Per-user directory where new policy files are created.</summary>
    public string UserDirectory { get; }

    /// <summary>
    /// Returns the policy file path to read from and save to. Empty explicit
    /// paths and empty environment variables are treated as unset.
    /// </summary>
    public string Resolve(string? explicitPath, string? envVarName, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        if (envVarName != null)
        {
            var fromEnv = Environment.GetEnvironmentVariable(envVarName);
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv;
        }

        var nextToExe = Path.Combine(ExeDirectory, fileName);
        if (File.Exists(nextToExe))
            return nextToExe;

        return UserFilePath(fileName);
    }

    /// <summary>Path of <paramref name="fileName"/> in the per-user directory.</summary>
    public string UserFilePath(string fileName) => Path.Combine(UserDirectory, fileName);

    private static string GetDefaultUserDirectory()
    {
        // DoNotVerify: the default option returns "" when %LOCALAPPDATA% doesn't
        // exist yet, which would silently fall back to the executable directory.
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, UserDirectoryName);
    }
}
