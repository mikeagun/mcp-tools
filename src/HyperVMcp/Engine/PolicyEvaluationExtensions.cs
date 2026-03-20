// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

namespace HyperVMcp.Engine;

/// <summary>
/// Extension methods on <see cref="McpSharp.Policy.PolicyEvaluation"/> for
/// type-safe access to HyperV-specific metadata stored in the Metadata dictionary.
/// </summary>
public static class PolicyEvaluationExtensions
{
    private const string TriggerKey = "trigger";
    private const string RiskTierKey = "risk_tier";
    private const string CategoryKey = "category";

    /// <summary>Gets the <see cref="ConfirmTrigger"/> from evaluation metadata.</summary>
    public static ConfirmTrigger? GetTrigger(this McpSharp.Policy.PolicyEvaluation eval) =>
        eval.Metadata?.TryGetValue(TriggerKey, out var v) == true && v is ConfirmTrigger t ? t : null;

    /// <summary>Gets the <see cref="RiskTier"/> from evaluation metadata.</summary>
    public static RiskTier? GetRiskTier(this McpSharp.Policy.PolicyEvaluation eval) =>
        eval.Metadata?.TryGetValue(RiskTierKey, out var v) == true && v is RiskTier r ? r : null;

    /// <summary>Gets the tool category string from evaluation metadata.</summary>
    public static string? GetCategory(this McpSharp.Policy.PolicyEvaluation eval) =>
        eval.Metadata?.TryGetValue(CategoryKey, out var v) == true ? v as string : null;

    /// <summary>
    /// Build a <see cref="McpSharp.Policy.PolicyEvaluation"/> populated with HyperV metadata.
    /// </summary>
    public static McpSharp.Policy.PolicyEvaluation BuildEvaluation(
        McpSharp.Policy.PolicyDecision decision,
        string reason,
        ConfirmTrigger? trigger = null,
        RiskTier? riskTier = null,
        string? category = null)
    {
        Dictionary<string, object>? metadata = null;
        if (trigger.HasValue || riskTier.HasValue || category != null)
        {
            metadata = new Dictionary<string, object>();
            if (trigger.HasValue) metadata[TriggerKey] = trigger.Value;
            if (riskTier.HasValue) metadata[RiskTierKey] = riskTier.Value;
            if (category != null) metadata[CategoryKey] = category;
        }

        return new McpSharp.Policy.PolicyEvaluation
        {
            Decision = decision,
            Reason = reason,
            Metadata = metadata,
        };
    }
}
