// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Management.Automation;
using System.Management.Automation.Runspaces;

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
        // Use a fresh runspace to call Get-StoredCredential.
        using var ps = PowerShell.Create();

        // First try the CredentialManager module.
        ps.AddScript($@"
            $ErrorActionPreference = 'Stop'
            try {{
                Import-Module CredentialManager -ErrorAction Stop
                $c = Get-StoredCredential -Target '{PsUtils.PsEscape(target)}' -ErrorAction Stop
                if ($null -eq $c) {{ throw 'Credential not found' }}
                $c
            }} catch {{
                # Fall back to cmdkey-based resolution.
                throw ""Could not resolve credential for target '{PsUtils.PsEscape(target)}'. Ensure 'Install-Module CredentialManager' has been run and 'New-StoredCredential -Target {PsUtils.PsEscape(target)}' has been set. Error: $_""
            }}
        ");

        var results = ps.Invoke();

        if (ps.HadErrors || results.Count == 0)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
            throw new InvalidOperationException(
                $"Failed to resolve credential for target '{target}': {errors}");
        }

        var credObj = results[0].BaseObject;
        if (credObj is PSCredential psCred)
            return psCred;

        throw new InvalidOperationException(
            $"Credential Manager returned unexpected type '{credObj?.GetType().Name}' for target '{target}'.");
    }
}
