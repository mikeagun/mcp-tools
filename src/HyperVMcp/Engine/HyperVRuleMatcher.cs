// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using McpSharp.Policy;

namespace HyperVMcp.Engine;

/// <summary>
/// HyperV-specific rule matcher implementing McpSharp.Policy.IRuleMatcher.
/// Interprets HyperV constraint fields stored in ApprovalRule.Constraints
/// (vm_names, vm_pattern, command_prefixes, host_paths, max_bulk_vms).
/// </summary>
public sealed class HyperVRuleMatcher : IRuleMatcher
{
    private readonly HyperVToolClassifier _classifier;

    public HyperVRuleMatcher(HyperVToolClassifier classifier)
    {
        _classifier = classifier;
    }

    /// <summary>
    /// Test whether a rule's HyperV-specific constraints match a tool call.
    /// The framework already checks the Tools field; this method checks
    /// vm_names, vm_pattern, command_prefixes, host_paths, and max_bulk_vms.
    /// </summary>
    public bool Matches(McpSharp.Policy.ApprovalRule rule, string toolName, JsonObject args)
    {
        var constraints = rule.Constraints;
        var context = _classifier.ExtractContext(toolName, args);

        // Track whether any constraint was actually tested.
        // A rule must positively match at least one constraint to approve a call.
        var anyConstraintTested = false;

        // Tool name check — VM-scoped rules must specify tools.
        if (rule.Tools == null || rule.Tools.Count == 0)
        {
            var hasVmConstraint = constraints != null &&
                (constraints.ContainsKey("vm_names") || constraints.ContainsKey("vm_pattern"));
            if (hasVmConstraint)
                return false;
        }

        // VM name check (exact).
        if (constraints != null && constraints.TryGetValue("vm_names", out var vmNamesElem))
        {
            var vmNames = ReadStringList(vmNamesElem);
            if (vmNames != null && vmNames.Count > 0)
            {
                if (context.VmNames == null) return false;
                anyConstraintTested = true;
                if (!context.VmNames.All(vm => vmNames.Contains(vm)))
                    return false;
            }
        }

        // VM pattern check (glob).
        if (constraints != null && constraints.TryGetValue("vm_pattern", out var vmPatternElem))
        {
            if (vmPatternElem.ValueKind == JsonValueKind.String)
            {
                var vmPattern = vmPatternElem.GetString();
                if (vmPattern != null)
                {
                    if (context.VmNames == null) return false;
                    anyConstraintTested = true;
                    if (!context.VmNames.All(vm => GlobMatcher.Match(vmPattern, vm)))
                        return false;
                }
            }
        }

        // Command prefix check.
        if (constraints != null && constraints.TryGetValue("command_prefixes", out var cmdPrefixElem))
        {
            var commandPrefixes = ReadStringList(cmdPrefixElem);
            if (commandPrefixes != null && commandPrefixes.Count > 0)
            {
                if (context.Command == null) return false;
                anyConstraintTested = true;
                if (!CommandAnalyzer.AllCommandsApproved(context.Command, commandPrefixes))
                    return false;
            }
        }

        // Host path check (glob).
        if (constraints != null && constraints.TryGetValue("host_paths", out var hostPathsElem))
        {
            var hostPaths = ReadStringList(hostPathsElem);
            if (hostPaths != null && hostPaths.Count > 0)
            {
                if (context.HostPath == null) return false;
                anyConstraintTested = true;
                if (!hostPaths.Any(pattern => GlobMatcher.Match(pattern, context.HostPath)))
                    return false;
            }
        }

        // Bulk VM count check.
        if (constraints != null && constraints.TryGetValue("max_bulk_vms", out var maxBulkElem))
        {
            if (maxBulkElem.ValueKind == JsonValueKind.Number)
            {
                var maxBulkVms = maxBulkElem.GetInt32();
                if (!context.BulkVmCount.HasValue) return false;
                anyConstraintTested = true;
                if (context.BulkVmCount.Value > maxBulkVms)
                    return false;
            }
        }

        // A rule with no testable constraints matches nothing.
        return anyConstraintTested;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static List<string>? ReadStringList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return null;
        return element.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    // ── BuildRule helpers ──────────────────────────────────────────────

    /// <summary>Build a rule scoped to a specific VM name.</summary>
    public static McpSharp.Policy.ApprovalRule BuildVmRule(List<string> tools, string vmName)
    {
        return new McpSharp.Policy.ApprovalRule
        {
            Tools = tools,
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_names"] = ToJsonElement(new[] { vmName }),
            },
        };
    }

    /// <summary>Build a rule with command prefix constraints, optionally scoped to a VM.</summary>
    public static McpSharp.Policy.ApprovalRule BuildCommandRule(List<string>? tools, string? vmName, List<string> prefixes)
    {
        var constraints = new Dictionary<string, JsonElement>
        {
            ["command_prefixes"] = ToJsonElement(prefixes),
        };
        if (vmName != null)
            constraints["vm_names"] = ToJsonElement(new[] { vmName });

        return new McpSharp.Policy.ApprovalRule
        {
            Tools = tools,
            Constraints = constraints,
        };
    }

    /// <summary>Build a rule with host path constraints, optionally scoped to a VM.</summary>
    public static McpSharp.Policy.ApprovalRule BuildHostPathRule(List<string> tools, string? vmName, List<string> paths)
    {
        var constraints = new Dictionary<string, JsonElement>
        {
            ["host_paths"] = ToJsonElement(paths),
        };
        if (vmName != null)
            constraints["vm_names"] = ToJsonElement(new[] { vmName });

        return new McpSharp.Policy.ApprovalRule
        {
            Tools = tools,
            Constraints = constraints,
        };
    }

    /// <summary>Build a rule with tool constraints only (no VM constraint).</summary>
    public static McpSharp.Policy.ApprovalRule BuildVmWildcardRule(List<string> tools)
    {
        // Tools-only rule — needs at least one non-tool constraint to be testable.
        // Use a wildcard VM pattern to match any VM.
        return new McpSharp.Policy.ApprovalRule
        {
            Tools = tools,
        };
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}
