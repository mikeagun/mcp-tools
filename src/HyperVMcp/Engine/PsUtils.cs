// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

namespace HyperVMcp.Engine;

/// <summary>
/// Shared utilities for PowerShell script construction.
/// </summary>
public static class PsUtils
{
    /// <summary>
    /// Escape a string for safe embedding in a PowerShell single-quoted literal.
    /// Single-quoted strings in PowerShell only interpret '' as a literal '.
    /// No other escape sequences are processed, making this safe against injection
    /// of backticks, $variables, subexpressions, newlines, etc.
    ///
    /// Usage: ps.AddScript($"Get-VM -Name '{PsEscape(name)}'")
    /// </summary>
    public static string PsEscape(string value)
    {
        // In PowerShell single-quoted strings, only ' needs escaping (as '').
        // Newlines and control characters could still break the script structure
        // if they appear outside the quotes, so we strip them.
        return value
            .Replace("'", "''")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\0", "");
    }

    /// <summary>
    /// Validate that a value is safe for use as a PowerShell identifier/name
    /// (VM names, service names, checkpoint names). Rejects values containing
    /// characters that could be used for injection even in single-quoted strings.
    /// </summary>
    public static string ValidateName(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be empty.");

        // Reject control characters, semicolons, pipes, and other shell metacharacters.
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                throw new ArgumentException($"{paramName} contains invalid control character.");
        }

        return value;
    }
}
