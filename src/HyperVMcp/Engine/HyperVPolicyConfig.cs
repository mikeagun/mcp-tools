// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperVMcp.Engine;

/// <summary>
/// HyperV-specific policy configuration extending the shared base class.
/// Deserialized from hyperv-mcp-policy.json.
/// </summary>
public sealed class HyperVPolicyConfig : McpSharp.Policy.PolicyConfig
{
    [JsonPropertyName("mode")]
    public PolicyMode Mode { get; set; } = PolicyMode.Standard;

    [JsonPropertyName("vm_allowlist")]
    public List<string>? VmAllowlist { get; set; }

    [JsonPropertyName("vm_blocklist")]
    public List<string>? VmBlocklist { get; set; }

    [JsonPropertyName("max_bulk_vms")]
    public int MaxBulkVms { get; set; } = 3;

    [JsonPropertyName("blocked_command_patterns")]
    public List<string>? BlockedCommandPatterns { get; set; }

    [JsonPropertyName("warn_command_patterns")]
    public List<string>? WarnCommandPatterns { get; set; }

    [JsonPropertyName("allowed_command_patterns")]
    public List<string>? AllowedCommandPatterns { get; set; }

    [JsonPropertyName("host_path_restrictions")]
    public HostPathRestrictions? HostPathRestrictions { get; set; }

    [JsonPropertyName("tool_overrides")]
    public Dictionary<string, string>? ToolOverrides { get; set; }

    [JsonPropertyName("approve_flag_mode")]
    public bool ApproveFlagMode { get; set; }

    [JsonPropertyName("confirmation_timeout_seconds")]
    public int ConfirmationTimeoutSeconds { get; set; } = 175;

    /// <summary>
    /// Maximum time (in seconds) for a sync backend operation to complete before
    /// the server returns a timeout error to the agent. Must be less than the MCP
    /// protocol timeout (typically 60s) to ensure the response arrives in time.
    /// </summary>
    [JsonPropertyName("backend_timeout_seconds")]
    public int BackendTimeoutSeconds { get; set; } = 50;

    // ── Safe defaults ──────────────────────────────────────────────────

    /// <summary>
    /// Default allowed command patterns — read-only and diagnostic operations
    /// that are safe to execute without confirmation. Applied when AllowedCommandPatterns is null.
    /// Evaluated after blocked patterns but before warn patterns.
    /// Set to empty list [] in policy file to require confirmation for everything.
    /// </summary>
    public static readonly List<string> DefaultAllowedPatterns =
    [
        // PowerShell read-only verbs
        @"(?i)^Get-\w",
        @"(?i)^Test-\w",
        @"(?i)^Measure-\w",
        @"(?i)^Select-\w",
        @"(?i)^Where-\w",
        @"(?i)^Sort-\w",
        @"(?i)^Group-\w",
        @"(?i)^Format-(?!Volume)\w",  // Format-Table/List/Wide, NOT Format-Volume (blocked)
        @"(?i)^ConvertTo-\w",
        @"(?i)^ConvertFrom-\w",
        @"(?i)^Compare-\w",
        @"(?i)^Find-\w",
        @"(?i)^Resolve-\w",
        @"(?i)^Split-\w",
        @"(?i)^Join-\w",
        @"(?i)^Write-\w",            // Write-Host, Write-Output, Write-Verbose, etc.
        // Variable access
        @"(?i)^\$",                   // $env:PATH, $PSVersionTable, etc.
        // Safe .NET type accelerators (read-only / computational methods)
        // Dangerous types like [System.IO.File], [System.Diagnostics.Process] are NOT included.
        @"(?i)^\[(math|string|int|long|double|float|decimal|datetime|datetimeoffset|timespan|guid|uri|version|regex|convert|enum|char|array|bool|byte|xml|ipaddress)\]::",
        // Safe native utilities (read-only / diagnostic)
        @"(?i)^hostname$",
        @"(?i)^whoami$",
        @"(?i)^echo\b",
        @"(?i)^type\b",
        @"(?i)^cat\b",
        @"(?i)^dir\b",
        @"(?i)^ls\b",
        @"(?i)^pwd$",
        @"(?i)^ipconfig\b",
        @"(?i)^systeminfo$",
        @"(?i)^ver$",
        @"(?i)^netstat\b",
        @"(?i)^ping\b",
        @"(?i)^nslookup\b",
        @"(?i)^tracert\b",
        @"(?i)^route\s+print\b",
        @"(?i)^sc\s+query\b",
        @"(?i)^wmic\b.*\bget\b",
        @"(?i)^reg\s+query\b",       // Registry read (not reg delete/add)
        // Git read-only subcommands
        @"(?i)^git\s+(status|log|diff|show|branch|tag|remote|rev-parse|ls-files|ls-tree|cat-file|describe|shortlog|blame|stash\s+list)\b",
    ];

    /// <summary>
    /// Default blocked command patterns — operations that have dedicated MCP tools
    /// and should not be run via invoke_command (would break session state).
    /// Applied when BlockedCommandPatterns is null (not explicitly configured).
    /// Set to empty list [] in policy file to disable.
    /// </summary>
    public static readonly List<string> DefaultBlockedPatterns =
    [
        // VM shutdown/restart from inside the VM — use stop_vm/restart_vm MCP tools instead.
        // Running these via invoke_command kills the PSSession without proper cleanup.
        @"(?i)\bStop-Computer\b",
        @"(?i)\bRestart-Computer\b",
    ];

    /// <summary>
    /// Default warn command patterns — state-modifying operations that deserve
    /// confirmation before execution. Applied when WarnCommandPatterns is null.
    /// Set to empty list [] in policy file to disable.
    /// </summary>
    public static readonly List<string> DefaultWarnPatterns =
    [
        // Disk destruction — irreversible
        @"(?i)\bFormat-Volume\b",
        @"(?i)\bClear-Disk\b",
        @"(?i)\bRemove-Partition\b",
        @"(?i)\bInitialize-Disk\b",
        // VM destruction
        @"(?i)\bRemove-VM\b",
        @"(?i)\bRemove-VMSnapshot\b",
        @"(?i)\bRemove-VMSwitch\b",
        // File/registry deletion
        @"(?i)\bRemove-Item\b",
        @"(?i)\breg\s+delete\b",
        // Service disruption
        @"(?i)\bStop-Service\b",
        // Security policy changes
        @"(?i)\bSet-ExecutionPolicy\b",
        // Network/exfiltration
        @"(?i)\bInvoke-WebRequest\b",
        @"(?i)\bInvoke-RestMethod\b",
        @"(?i)\bcurl\b",
        @"(?i)\bwget\b",
        // Arbitrary code execution
        @"(?i)\bInvoke-Expression\b",
        @"(?i)\biex\b",
        @"(?i)\bStart-Process\b",
        // Firewall changes
        @"(?i)\bNew-NetFirewallRule\b",
        @"(?i)\bRemove-NetFirewallRule\b",
        @"(?i)\bDisable-NetFirewallRule\b",
        // Credential manipulation
        @"(?i)\bnet\s+user\b",
        @"(?i)\bAdd-LocalGroupMember\b",
    ];

    // ── Computed effective patterns ────────────────────────────────────

    /// <summary>
    /// Get the effective allowed patterns: explicit config if set, otherwise safe defaults.
    /// An empty list in the config requires confirmation for all commands.
    /// </summary>
    [JsonIgnore]
    public List<string> EffectiveAllowedPatterns =>
        AllowedCommandPatterns ?? DefaultAllowedPatterns;

    /// <summary>
    /// Get the effective blocked patterns: explicit config if set, otherwise safe defaults.
    /// An empty list in the config explicitly disables all blocked patterns.
    /// </summary>
    [JsonIgnore]
    public List<string> EffectiveBlockedPatterns =>
        BlockedCommandPatterns ?? DefaultBlockedPatterns;

    /// <summary>
    /// Get the effective warn patterns: explicit config if set, otherwise safe defaults.
    /// An empty list in the config explicitly disables all warn patterns.
    /// </summary>
    [JsonIgnore]
    public List<string> EffectiveWarnPatterns =>
        WarnCommandPatterns ?? DefaultWarnPatterns;

    // ── Serialization ──────────────────────────────────────────────────

    /// <summary>
    /// JSON serializer options for HyperV policy files.
    /// Extends the base class options with the PolicyMode enum converter.
    /// </summary>
    public static readonly JsonSerializerOptions HyperVJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<PolicyMode>() },
    };
}
