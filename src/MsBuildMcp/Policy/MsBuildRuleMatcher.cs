// Copyright (c) MsBuildMcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using McpSharp.Policy;

namespace MsBuildMcp.Policy;

/// <summary>
/// Rule matcher for msbuild-mcp. Matches the "sln_path" constraint
/// against the tool call's sln_path argument.
/// </summary>
public sealed class MsBuildRuleMatcher : IRuleMatcher
{
    public bool Matches(ApprovalRule rule, string toolName, JsonObject args)
    {
        if (rule.Constraints == null || rule.Constraints.Count == 0)
            return true;

        // sln_path constraint.
        if (rule.Constraints.TryGetValue("sln_path", out var slnElement))
        {
            var ruleSln = slnElement.GetString();
            var argSln = args["sln_path"]?.GetValue<string>();
            if (ruleSln == null || argSln == null)
                return false;
            if (!string.Equals(NormalizePath(ruleSln), NormalizePath(argSln),
                StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // targets constraint (subset matching).
        if (rule.Constraints.TryGetValue("targets", out var targetsElement))
        {
            var ruleTargets = ParseTargetsElement(targetsElement);
            var argTargetsStr = args["targets"]?.GetValue<string>();
            // No targets in call = full solution build; rule with targets doesn't match.
            if (string.IsNullOrEmpty(argTargetsStr))
                return false;
            var argTargets = ParseTargetsString(argTargetsStr);
            // Every requested target must be in the rule's target set.
            if (!argTargets.All(t => ruleTargets.Contains(t)))
                return false;
        }

        // configuration constraint (case-insensitive exact match).
        if (rule.Constraints.TryGetValue("configuration", out var configElement))
        {
            var ruleConfig = configElement.GetString();
            var argConfig = args["configuration"]?.GetValue<string>() ?? "Debug";
            if (ruleConfig == null ||
                !string.Equals(ruleConfig, argConfig, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Create an ApprovalRule for a solution path with optional target and configuration constraints.
    /// </summary>
    public static ApprovalRule BuildRule(string normalizedSlnPath,
        List<string>? targets = null, string? configuration = null)
    {
        var constraints = new Dictionary<string, JsonElement>
        {
            ["sln_path"] = JsonSerializer.SerializeToElement(normalizedSlnPath),
        };

        if (targets is { Count: > 0 })
            constraints["targets"] = JsonSerializer.SerializeToElement(targets);

        if (configuration != null)
            constraints["configuration"] = JsonSerializer.SerializeToElement(configuration);

        return new ApprovalRule
        {
            Tools = ["build"],
            Constraints = constraints,
        };
    }

    /// <summary>Parse comma-separated targets string into normalized set.</summary>
    public static HashSet<string> ParseTargetsString(string targets)
    {
        return targets
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
    }

    /// <summary>Parse targets JSON array element into normalized set.</summary>
    public static HashSet<string> ParseTargetsElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray()
                .Select(e => e.GetString()?.ToLowerInvariant() ?? "")
                .Where(s => s.Length > 0)
                .ToHashSet();
        return [];
    }

    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }
}
