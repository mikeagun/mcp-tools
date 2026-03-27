// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpSharp.Policy;
using McpDecision = McpSharp.Policy.PolicyDecision;
using McpEvaluation = McpSharp.Policy.PolicyEvaluation;

namespace HyperVMcp.Engine;

/// <summary>
/// HyperV-specific tool classifier implementing the shared McpSharp.Policy.IToolClassifier.
/// Wraps the static ToolClassifier and contains the full evaluation pipeline
/// (excluding deny/session/user rule checks which are handled by PolicyEngine).
/// </summary>
public sealed class HyperVToolClassifier : IToolClassifier
{
    private readonly HyperVPolicyConfig _config;

    // Compiled regex patterns (lazily built from config).
    private List<Regex>? _blockedPatterns;
    private List<Regex>? _warnPatterns;
    private List<Regex>? _allowedPatterns;

    public HyperVPolicyConfig Config => _config;

    /// <summary>
    /// Optional resolver that maps session IDs to VM names.
    /// Set during server startup to enable VM-scoped policy for session-based tools.
    /// </summary>
    public Func<string, string?>? SessionVmResolver { get; set; }

    /// <summary>
    /// Optional resolver that maps command IDs to session IDs.
    /// Set during server startup to enable VM-scoped policy for command-based tools
    /// like cancel_command and free_command_output.
    /// </summary>
    public Func<string, string?>? CommandSessionResolver { get; set; }

    public HyperVToolClassifier(HyperVPolicyConfig config)
    {
        _config = config;
        CompilePatterns();
    }

    /// <summary>
    /// Default policy decision for this tool call based on risk tier and policy mode.
    /// </summary>
    public McpSharp.Policy.PolicyDecision Classify(string toolName, JsonObject args)
    {
        var tier = ToolClassifier.GetRiskTier(toolName);
        return ToolClassifier.DefaultDecision(tier, _config.Mode);
    }

    /// <summary>
    /// Full evaluation pipeline with HyperV-specific checks.
    /// Does NOT check deny/session/user rules — those are handled by PolicyEngine.
    /// </summary>
    public McpEvaluation Evaluate(string toolName, JsonObject args)
    {
        // 1. Unrestricted mode: allow everything.
        if (_config.Mode == PolicyMode.Unrestricted)
            return PolicyEvaluationExtensions.BuildEvaluation(
                McpDecision.Allow, "Unrestricted mode");

        // 2. Extract context.
        var context = ExtractContext(toolName, args);
        var tier = ToolClassifier.GetRiskTier(toolName);
        var category = ToolClassifier.GetCategory(toolName);

        // 3. (SKIPPED) Deny/session/user rule checks are handled by PolicyEngine.

        // 4. Tool overrides from config.
        if (_config.ToolOverrides != null &&
            _config.ToolOverrides.TryGetValue(toolName, out var overrideStr))
        {
            var decision = overrideStr.ToLowerInvariant() switch
            {
                "allow" => McpDecision.Allow,
                "deny" => McpDecision.Deny,
                "confirm" => McpDecision.Confirm,
                _ => McpDecision.Confirm,
            };

            return PolicyEvaluationExtensions.BuildEvaluation(
                decision,
                $"Tool override: {toolName} = {overrideStr}",
                trigger: decision == McpDecision.Allow ? null : ConfirmTrigger.ToolOverride,
                riskTier: tier,
                category: category);
        }

        // 5-6. VM name filter (blocklist = deny, allowlist = allow).
        if (context.VmNames != null && context.VmNames.Count > 0)
        {
            // 5. VM blocklist → Deny.
            if (_config.VmBlocklist != null)
            {
                foreach (var vm in context.VmNames)
                {
                    if (_config.VmBlocklist.Any(pattern => GlobMatcher.Match(pattern, vm)))
                        return PolicyEvaluationExtensions.BuildEvaluation(
                            McpDecision.Deny,
                            $"VM '{vm}' matches blocklist pattern",
                            trigger: ConfirmTrigger.VmNameFilter,
                            riskTier: tier,
                            category: category);
                }
            }

            // 6. VM allowlist → Allow.
            if (_config.VmAllowlist != null)
            {
                var allAllowed = context.VmNames.All(vm =>
                    _config.VmAllowlist.Any(pattern => GlobMatcher.Match(pattern, vm)));
                if (allAllowed)
                    return PolicyEvaluationExtensions.BuildEvaluation(
                        McpDecision.Allow, "VM in allowlist");
            }
        }

        // 7. Bulk VM count → Confirm.
        if (context.BulkVmCount.HasValue && context.BulkVmCount.Value > _config.MaxBulkVms)
        {
            return PolicyEvaluationExtensions.BuildEvaluation(
                McpDecision.Confirm,
                $"Bulk operation on {context.BulkVmCount.Value} VMs exceeds limit of {_config.MaxBulkVms}",
                trigger: ConfirmTrigger.BulkVmCount,
                riskTier: tier,
                category: category);
        }

        // 8. Command pattern analysis (invoke_command, run_script).
        if (context.Command != null)
        {
            // Blocked patterns → Deny.
            if (_blockedPatterns != null)
            {
                foreach (var pattern in _blockedPatterns)
                {
                    if (pattern.IsMatch(context.Command))
                        return PolicyEvaluationExtensions.BuildEvaluation(
                            McpDecision.Deny,
                            $"Command matches blocked pattern: '{pattern}'",
                            trigger: ConfirmTrigger.CommandPattern,
                            riskTier: tier,
                            category: category);
                }
            }

            // Allowed patterns → Allow.
            if (_allowedPatterns != null)
            {
                foreach (var pattern in _allowedPatterns)
                {
                    if (pattern.IsMatch(context.Command))
                        return PolicyEvaluationExtensions.BuildEvaluation(
                            McpDecision.Allow,
                            $"Command matches allowed pattern: '{pattern}'");
                }
            }

            // Warn patterns → Confirm.
            if (_warnPatterns != null)
            {
                foreach (var pattern in _warnPatterns)
                {
                    if (pattern.IsMatch(context.Command))
                        return PolicyEvaluationExtensions.BuildEvaluation(
                            McpDecision.Confirm,
                            $"Command matches warning pattern: '{pattern}'",
                            trigger: ConfirmTrigger.CommandPattern,
                            riskTier: tier,
                            category: category);
                }
            }

            // No pattern matched — in Standard/Restricted mode, require confirmation.
            if (_config.Mode is PolicyMode.Standard or PolicyMode.Restricted)
            {
                return PolicyEvaluationExtensions.BuildEvaluation(
                    McpDecision.Confirm,
                    "Command not in allowed list",
                    trigger: ConfirmTrigger.CommandPattern,
                    riskTier: tier,
                    category: category);
            }
        }

        // 9. Host path restrictions.
        if (context.HostPath != null)
        {
            var pathResult = CheckHostPath(toolName, context.HostPath);
            if (pathResult != null)
                return PolicyEvaluationExtensions.BuildEvaluation(
                    pathResult.Decision,
                    pathResult.Reason,
                    trigger: pathResult.Metadata?.TryGetValue("trigger", out var t) == true && t is ConfirmTrigger ct ? ct : null,
                    riskTier: tier,
                    category: category);
        }

        // 10. manage_service stop/restart → Confirm.
        if (toolName == "manage_service" && context.ServiceAction is "stop" or "restart"
            && _config.Mode is PolicyMode.Standard or PolicyMode.Restricted)
        {
            return PolicyEvaluationExtensions.BuildEvaluation(
                McpDecision.Confirm,
                $"Service {context.ServiceAction} requires confirmation",
                trigger: ConfirmTrigger.ToolOverride,
                riskTier: tier,
                category: category);
        }

        // 11. Default decision from ToolClassifier.DefaultDecision.
        var defaultDecision = ToolClassifier.DefaultDecision(tier, _config.Mode);
        return PolicyEvaluationExtensions.BuildEvaluation(
            defaultDecision,
            defaultDecision == McpDecision.Allow
                ? $"Tool '{toolName}' ({tier}) allowed by {_config.Mode} mode"
                : $"Tool '{toolName}' ({tier}) requires confirmation in {_config.Mode} mode",
            trigger: defaultDecision == McpDecision.Allow ? null : ConfirmTrigger.ToolRiskTier,
            riskTier: tier,
            category: category);
    }

    // ── Context extraction ─────────────────────────────────────────────

    /// <summary>
    /// Extract contextual information from tool call arguments for policy evaluation.
    /// </summary>
    internal ToolCallContext ExtractContext(string toolName, JsonObject args)
    {
        IReadOnlyList<string>? vmNames = null;
        string? command = null;
        string? hostPath = null;
        int? bulkVmCount = null;
        string? sessionId = args["session_id"]?.GetValue<string>();

        // VM names (from vm_name parameter — string or array).
        var vmNameNode = args["vm_name"];
        if (vmNameNode is JsonArray vmArr)
        {
            vmNames = vmArr.Select(n => n!.GetValue<string>()).ToList();
            bulkVmCount = vmNames.Count;
        }
        else if (vmNameNode != null)
        {
            vmNames = [vmNameNode.GetValue<string>()];
        }

        // connect_vm can also use computer_name (WinRM) — treat as a VM name for filtering.
        if (vmNames == null && toolName == "connect_vm")
        {
            var computerName = args["computer_name"]?.GetValue<string>();
            if (computerName != null)
                vmNames = [computerName];
        }

        // Resolve command_id to session_id for command-based tools.
        if (sessionId == null && CommandSessionResolver != null)
        {
            var commandId = args["command_id"]?.GetValue<string>();
            if (commandId != null)
                sessionId = CommandSessionResolver(commandId);
        }

        // Resolve session_id to VM name if no vm_name was provided directly.
        if (vmNames == null && sessionId != null && SessionVmResolver != null)
        {
            var resolved = SessionVmResolver(sessionId);
            if (resolved != null)
                vmNames = [resolved];
        }

        // Command text.
        if (toolName == "invoke_command")
            command = args["command"]?.GetValue<string>();
        else if (toolName == "run_script")
        {
            var scriptPath = args["script_path"]?.GetValue<string>();
            if (scriptPath != null)
            {
                command = $"& '{PsUtils.PsEscape(scriptPath)}'";
                var scriptArgs = args["args"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(scriptArgs))
                    command += $" {scriptArgs}";
            }
        }

        // Service action.
        string? serviceAction = null;
        if (toolName == "manage_service")
            serviceAction = args["action"]?.GetValue<string>();

        // Host path.
        hostPath = toolName switch
        {
            "copy_to_vm" => GetFirstStringOrArray(args["source"]),
            "copy_from_vm" => args["destination"]?.GetValue<string>(),
            "save_command_output" => args["path"]?.GetValue<string>(),
            "invoke_command" => null,
            _ => null,
        };

        return new ToolCallContext
        {
            VmNames = vmNames,
            Command = command,
            HostPath = hostPath,
            BulkVmCount = bulkVmCount,
            SessionId = sessionId,
            ServiceAction = serviceAction,
        };
    }

    private static string? GetFirstStringOrArray(JsonNode? node)
    {
        if (node is JsonArray arr && arr.Count > 0)
            return arr[0]!.GetValue<string>();
        return node?.GetValue<string>();
    }

    // ── Host path checking ─────────────────────────────────────────────

    private McpEvaluation? CheckHostPath(string toolName, string path)
    {
        var restrictions = _config.HostPathRestrictions;

        List<string>? allowPatterns = restrictions == null ? null : toolName switch
        {
            "copy_to_vm" => restrictions.CopyToVmSourceAllow,
            "copy_from_vm" => restrictions.CopyFromVmDestAllow,
            "save_command_output" or "invoke_command" => restrictions.SaveToAllow,
            _ => null,
        };

        // If path matches an allow pattern, explicitly allow.
        if (allowPatterns != null && allowPatterns.Count > 0
            && allowPatterns.Any(p => GlobMatcher.Match(p, path)))
            return PolicyEvaluationExtensions.BuildEvaluation(
                McpDecision.Allow,
                $"Host path '{path}' matches allowed pattern for {toolName}");

        // No allow patterns configured, or path doesn't match — require confirmation.
        return PolicyEvaluationExtensions.BuildEvaluation(
            McpDecision.Confirm,
            $"Host path '{path}' not in allowed list for {toolName}",
            trigger: ConfirmTrigger.HostPath);
    }

    // ── Pattern compilation ────────────────────────────────────────────

    private void CompilePatterns()
    {
        _blockedPatterns = CompilePatternList(_config.EffectiveBlockedPatterns);
        _warnPatterns = CompilePatternList(_config.EffectiveWarnPatterns);
        _allowedPatterns = CompilePatternList(_config.EffectiveAllowedPatterns);
    }

    private static List<Regex>? CompilePatternList(List<string>? patterns)
    {
        if (patterns == null || patterns.Count == 0) return null;
        return patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToList();
    }
}

/// <summary>
/// Simple glob matching: supports * (any chars within segment) and ** (any path).
/// Case-insensitive. Extracted for shared use across classifier and rule matcher.
/// </summary>
public static class GlobMatcher
{
    public static bool Match(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }
}
