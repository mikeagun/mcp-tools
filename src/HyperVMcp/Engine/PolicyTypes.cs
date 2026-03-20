// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace HyperVMcp.Engine;

// ── HyperV-specific enums ──────────────────────────────────────────────

/// <summary>
/// Overall policy mode controlling default strictness.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PolicyMode>))]
public enum PolicyMode
{
    /// <summary>All tools allowed, no checks.</summary>
    Unrestricted,

    /// <summary>Read-only/session always allowed; destructive requires confirmation; blocked patterns denied.</summary>
    Standard,

    /// <summary>Only read-only tools allowed; everything else requires confirmation.</summary>
    Restricted,

    /// <summary>Only read-only + explicitly-allowed tools work; everything else denied.</summary>
    Lockdown,
}

/// <summary>
/// Risk tier for a tool, determining its default behavior under each policy mode.
/// </summary>
public enum RiskTier
{
    /// <summary>Purely observational — cannot modify state.</summary>
    ReadOnly,

    /// <summary>Session management — creates connections but doesn't modify VM state.</summary>
    Session,

    /// <summary>Can modify VM state. Most day-to-day usage.</summary>
    Moderate,

    /// <summary>Can lose in-flight work, discard state. Recoverable but disruptive.</summary>
    Destructive,
}

/// <summary>
/// What triggered a confirmation requirement. Drives suggested_approvals generation.
/// </summary>
public enum ConfirmTrigger
{
    /// <summary>Tool's risk tier required confirmation.</summary>
    ToolRiskTier,

    /// <summary>Tool was explicitly set to "confirm" in policy overrides.</summary>
    ToolOverride,

    /// <summary>Command text matched a warn pattern.</summary>
    CommandPattern,

    /// <summary>Host path was outside allowed patterns.</summary>
    HostPath,

    /// <summary>Bulk VM operation exceeded max_bulk_vms.</summary>
    BulkVmCount,

    /// <summary>VM name matched the blocklist (should be Deny, not Confirm).</summary>
    VmNameFilter,
}

// ── HyperV-specific context ────────────────────────────────────────────

/// <summary>
/// Contextual information about a tool call, extracted by the policy evaluator
/// and used for approval rule matching and suggestion generation.
/// </summary>
public sealed class ToolCallContext
{
    /// <summary>VM names involved in this call (from vm_name parameter).</summary>
    public IReadOnlyList<string>? VmNames { get; init; }

    /// <summary>Command text (from invoke_command/run_script command parameter).</summary>
    public string? Command { get; init; }

    /// <summary>Host-side file path (from copy_to_vm source, copy_from_vm dest, save_to, etc.).</summary>
    public string? HostPath { get; init; }

    /// <summary>Number of VMs in a bulk operation.</summary>
    public int? BulkVmCount { get; init; }

    /// <summary>Session ID for the call.</summary>
    public string? SessionId { get; init; }

    /// <summary>Service action for manage_service ("start", "stop", "restart").</summary>
    public string? ServiceAction { get; init; }
}

/// <summary>
/// Host path restriction configuration.
/// Each field is a list of glob patterns for allowed paths.
/// </summary>
public sealed class HostPathRestrictions
{
    [JsonPropertyName("copy_to_vm_source_allow")]
    public List<string>? CopyToVmSourceAllow { get; set; }

    [JsonPropertyName("copy_from_vm_dest_allow")]
    public List<string>? CopyFromVmDestAllow { get; set; }

    [JsonPropertyName("save_to_allow")]
    public List<string>? SaveToAllow { get; set; }
}
