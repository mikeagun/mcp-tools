// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Management.Automation;

namespace HyperVMcp.Engine;

/// <summary>
/// Resolves credentials from Windows Credential Manager or explicit values.
/// Caches resolved credentials in-memory for the process lifetime.
/// </summary>
public sealed class CredentialStore
{
    private readonly Dictionary<string, PSCredential> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Resolve a PSCredential from the Credential Manager target, or from explicit user/pass.
    /// </summary>
    public PSCredential GetCredential(string? target = null, string? username = null, string? password = null)
    {
        // Explicit credentials take priority.
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var secPass = new System.Security.SecureString();
            foreach (var c in password) secPass.AppendChar(c);
            secPass.MakeReadOnly();
            return new PSCredential(username, secPass);
        }

        target ??= "TEST_VM";

        lock (_lock)
        {
            if (_cache.TryGetValue(target, out var cached))
                return cached;
        }

        var cred = ResolveFromCredentialManager(target);

        lock (_lock)
        {
            _cache[target] = cred;
        }

        return cred;
    }

    /// <summary>
    /// Clear cached credentials (e.g., after credential rotation).
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    private static PSCredential ResolveFromCredentialManager(string target)
    {
        // Read a Generic credential directly from the Windows Credential Manager
        // via the native CredRead API (no PowerShell module dependency).
        var stored = Win32CredentialManager.Read(target);

        if (stored is null || string.IsNullOrEmpty(stored.UserName))
        {
            throw new InvalidOperationException(
                $"Credential target '{target}' not found in Windows Credential Manager. " +
                $"Store it as a Generic credential, e.g.: cmdkey /generic:<target> /user:<username> /pass:<password>");
        }

        var secPass = new System.Security.SecureString();
        foreach (var c in stored.Password ?? string.Empty) secPass.AppendChar(c);
        secPass.MakeReadOnly();
        return new PSCredential(stored.UserName, secPass);
    }
}
