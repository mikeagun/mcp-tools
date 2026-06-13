// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpSharp.Policy;

// -- Enums -------------------------------------------------------------------

/// <summary>The outcome of a policy evaluation for a tool call.</summary>
public enum PolicyDecision { Allow, Deny, Confirm }

/// <summary>How long an approval or denial persists.</summary>
public enum ApprovalPersistence { Once, Session, Permanent }

/// <summary>Whether the user is allowing or denying the operation.</summary>
public enum ApprovalPolarity { Allow, Deny }

// -- Elicitation option ------------------------------------------------------

/// <summary>
/// A single elicitation option presented to the user.
/// </summary>
public sealed class ElicitationOption
{
    public required string Label { get; init; }
    public required ApprovalPolarity Polarity { get; init; }

    /// <summary>
    /// How long the approval/denial persists. When null, the dispatch layer issues a
    /// follow-up elicitation prompt asking the user to choose "This session only" or
    /// "Permanently" — separating scope decisions (what) from persistence decisions
    /// (how long). Set explicitly for terminal options (Allow once, Deny) and for
    /// single-prompt servers that encode persistence in the option label.
    /// </summary>
    public ApprovalPersistence? Persistence { get; init; }

    /// <summary>Rule to register when chosen. Null for "once" operations.</summary>
    public ApprovalRule? Rule { get; init; }
}

// -- Approval rule -----------------------------------------------------------

/// <summary>
/// A rule describing what operations to auto-approve or auto-deny.
/// Tools is the shared constraint; Constraints carries server-specific
/// matching data (e.g., vm_names, sln_path, command_prefixes).
/// Matching is delegated to the server's IRuleMatcher.
/// </summary>
public sealed class ApprovalRule
{
    /// <summary>Tool names to match, or ["*"] for all tools.</summary>
    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    /// <summary>
    /// Server-specific constraint data for matching. The server's IRuleMatcher
    /// interprets these fields. Serialized as-is to the policy file.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Constraints { get; set; }
}

// -- Policy evaluation -------------------------------------------------------

/// <summary>Result of evaluating a single tool call against the policy.</summary>
public sealed class PolicyEvaluation
{
    public required PolicyDecision Decision { get; init; }
    public required string Reason { get; init; }

    /// <summary>
    /// Server-specific metadata for option generation (e.g., risk tier, trigger type).
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

// -- Persisted rule ----------------------------------------------------------

/// <summary>A rule persisted in the policy file with provenance metadata.</summary>
public sealed class UserRule
{
    [JsonPropertyName("added")]
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("rule")]
    public ApprovalRule? Rule { get; set; }
}

// -- Interfaces --------------------------------------------------------------

/// <summary>
/// Server-specific tool classification. Determines the default policy decision
/// and extracts scope keys for display and matching.
/// </summary>
public interface IToolClassifier
{
    /// <summary>Default policy decision for this tool call.</summary>
    PolicyDecision Classify(string toolName, JsonObject args);

    /// <summary>
    /// Build a policy evaluation with server-specific metadata.
    /// Default implementation returns a basic evaluation from Classify().
    /// Servers can override to add Trigger, RiskTier, etc. to Metadata.
    /// </summary>
    PolicyEvaluation Evaluate(string toolName, JsonObject args)
    {
        var decision = Classify(toolName, args);
        return new PolicyEvaluation
        {
            Decision = decision,
            Reason = decision == PolicyDecision.Allow
                ? $"Tool '{toolName}' is allowed by default"
                : $"Tool '{toolName}' requires confirmation",
        };
    }
}

/// <summary>Server-specific elicitation option generator.</summary>
public interface IOptionGenerator
{
    List<ElicitationOption> Generate(
        string toolName, JsonObject args, PolicyEvaluation evaluation);
}

/// <summary>
/// Server-specific rule matching. Determines whether an ApprovalRule's
/// constraints match a given tool call's arguments.
/// </summary>
public interface IRuleMatcher
{
    /// <summary>
    /// Test whether a rule matches a tool call. The framework already checks
    /// the Tools field; this method checks server-specific constraints.
    /// Return true if all constraints match (or if there are no constraints).
    /// </summary>
    bool Matches(ApprovalRule rule, string toolName, JsonObject args);
}

// -- Policy configuration ----------------------------------------------------

/// <summary>
/// Base policy configuration. Servers extend with additional fields.
///
/// Thread-safety: <see cref="UserRules"/> and <see cref="DenyRules"/> are
/// the only fields a live <c>PolicyEngine</c> mutates after construction
/// (via <c>SaveRuleToPolicy</c> / <c>SaveDenyRuleToPolicy</c>, which build
/// a fresh <see cref="List{T}"/> off the existing one and atomically swap
/// the reference). Reads from <c>Evaluate</c> can race with that swap on
/// any architecture with a weak memory model (e.g. ARM64), so the
/// publication path uses <see cref="Volatile.Write{T}(ref T, T)"/> on the
/// writer side and <see cref="Volatile.Read{T}(ref T)"/> on the reader
/// side. Mutating an in-place list returned from these properties would
/// break this invariant — always replace the entire list.
/// </summary>
public class PolicyConfig
{
    private List<UserRule>? _userRules;
    private List<UserRule>? _denyRules;

    [JsonPropertyName("user_rules")]
    public List<UserRule>? UserRules
    {
        get => Volatile.Read(ref _userRules);
        set => Volatile.Write(ref _userRules, value);
    }

    [JsonPropertyName("deny_rules")]
    public List<UserRule>? DenyRules
    {
        get => Volatile.Read(ref _denyRules);
        set => Volatile.Write(ref _denyRules, value);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
