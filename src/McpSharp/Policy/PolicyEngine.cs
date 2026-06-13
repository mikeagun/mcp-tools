// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpSharp.Policy;

/// <summary>
/// Core policy engine: evaluates tool calls, manages session approvals/denials,
/// and persists rules to a policy file. Delegates server-specific behavior to
/// IToolClassifier, IRuleMatcher, and IOptionGenerator.
/// </summary>
public sealed class PolicyEngine
{
    private readonly IToolClassifier _classifier;
    private readonly IRuleMatcher _matcher;
    private readonly string? _policyFilePath;
    private readonly string _defaultFileName;
    private readonly object _fileLock = new();

    // Plain reference field (no `volatile`). The volatile semantics that
    // matter for `Evaluate` ↔ `SaveRuleInternal` concurrency live on the
    // PolicyConfig.UserRules/DenyRules properties, which use
    // Volatile.Read/Write on their backing fields. The reference held in
    // `_config` itself is only ever assigned in the ctor; making it
    // `volatile` here misled readers into thinking the publication
    // invariant lived at this level when it actually lives one level
    // deeper.
    private PolicyConfig _config;
    private readonly List<ApprovalRule> _sessionApprovals = [];
    private readonly List<ApprovalRule> _sessionDenials = [];
    private readonly object _sessionLock = new();

    public PolicyConfig Config => _config;
    public string? PolicyFilePath => _policyFilePath;

    public PolicyEngine(PolicyConfig config, IToolClassifier classifier,
        IRuleMatcher matcher, string? policyFilePath = null,
        string? defaultFileName = null)
    {
        _config = config;
        _classifier = classifier;
        _matcher = matcher;
        _policyFilePath = policyFilePath;
        _defaultFileName = defaultFileName ?? "policy.json";
    }

    // -- Loading -------------------------------------------------------------

    public static PolicyEngine Load<TConfig>(
        IToolClassifier classifier,
        IRuleMatcher matcher,
        string? explicitPath = null,
        string? envVarName = null,
        string? defaultFileName = null)
        where TConfig : PolicyConfig, new()
    {
        var path = explicitPath
            ?? (envVarName != null ? Environment.GetEnvironmentVariable(envVarName) : null)
            ?? FindPolicyFileNextToExe(defaultFileName ?? "policy.json");

        PolicyConfig config;
        if (path != null && File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<TConfig>(json, PolicyConfig.JsonOptions)
                    ?? new TConfig();
                Console.Error.WriteLine($"policy: loaded from {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"policy: failed to load from {path}: {ex.Message}");
                config = new TConfig();
            }
        }
        else
        {
            Console.Error.WriteLine("policy: no policy file found, using defaults");
            config = new TConfig();
        }

        return new PolicyEngine(config, classifier, matcher, path,
            defaultFileName: defaultFileName ?? "policy.json");
    }

    public static string DefaultPolicyFilePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, fileName);

    private static string? FindPolicyFileNextToExe(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    // -- Evaluation ----------------------------------------------------------

    public PolicyEvaluation Evaluate(string toolName, JsonObject args)
    {
        // Deny rules first (absolute precedence).
        if (MatchesAny(_config.DenyRules, toolName, args))
            return new PolicyEvaluation { Decision = PolicyDecision.Deny, Reason = "Matched deny rule" };

        if (MatchesSessionList(_sessionDenials, toolName, args))
            return new PolicyEvaluation { Decision = PolicyDecision.Deny, Reason = "Session denial" };

        // Allow rules.
        if (MatchesAny(_config.UserRules, toolName, args))
            return new PolicyEvaluation { Decision = PolicyDecision.Allow, Reason = "Matched user rule" };

        if (MatchesSessionList(_sessionApprovals, toolName, args))
            return new PolicyEvaluation { Decision = PolicyDecision.Allow, Reason = "Session approval" };

        // Server-specific classification (may add metadata for option generation).
        return _classifier.Evaluate(toolName, args);
    }

    // -- Rule matching -------------------------------------------------------

    private bool RuleMatches(ApprovalRule rule, string toolName, JsonObject args)
    {
        // Tools check (shared).
        if (rule.Tools != null && rule.Tools.Count > 0)
        {
            if (!rule.Tools.Contains("*") && !rule.Tools.Contains(toolName))
                return false;
        }

        // Delegate constraint matching to server.
        return _matcher.Matches(rule, toolName, args);
    }

    private bool MatchesAny(List<UserRule>? rules, string toolName, JsonObject args)
    {
        return rules?.Any(ur => ur.Rule != null && RuleMatches(ur.Rule, toolName, args)) ?? false;
    }

    private bool MatchesSessionList(List<ApprovalRule> rules, string toolName, JsonObject args)
    {
        lock (_sessionLock)
            return rules.Any(r => RuleMatches(r, toolName, args));
    }

    // -- Session management --------------------------------------------------

    public void RegisterSessionApproval(ApprovalRule rule)
    {
        lock (_sessionLock)
            _sessionApprovals.Add(rule);
        Console.Error.WriteLine($"policy: registered session approval (tools={FormatList(rule.Tools)})");
    }

    public void RegisterSessionDenial(ApprovalRule rule)
    {
        lock (_sessionLock)
            _sessionDenials.Add(rule);
        Console.Error.WriteLine($"policy: registered session denial (tools={FormatList(rule.Tools)})");
    }

    // -- Persistence ---------------------------------------------------------

    public void SaveRuleToPolicy(ApprovalRule rule, string? reason)
        => SaveRuleInternal(rule, reason, isDeny: false);

    public void SaveDenyRuleToPolicy(ApprovalRule rule, string? reason)
        => SaveRuleInternal(rule, reason, isDeny: true);

    private void SaveRuleInternal(ApprovalRule rule, string? reason, bool isDeny)
    {
        var filePath = _policyFilePath
            ?? DefaultPolicyFilePath(_defaultFileName);

        lock (_fileLock)
        {
            // Read existing file as JSON tree to preserve all fields (including
            // server-specific config like build_constraints, vm_allowlist, etc.).
            JsonObject root;
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var entry = new UserRule
            {
                Added = DateTimeOffset.UtcNow,
                Reason = reason,
                Rule = rule,
            };

            // Add rule to JSON tree.
            var key = isDeny ? "deny_rules" : "user_rules";
            if (root[key] is not JsonArray rulesArray)
            {
                rulesArray = new JsonArray();
                root[key] = rulesArray;
            }
            rulesArray.Add(JsonSerializer.SerializeToNode(entry, PolicyConfig.JsonOptions));

            // Write atomically via temp file + rename.
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(PolicyConfig.JsonOptions));
            File.Move(tempPath, filePath, overwrite: true);

            // Update in-memory config: create new list instances so concurrent
            // readers (Evaluate → MatchesAny) see either the old or the new
            // list, never a half-modified one. The PolicyConfig.UserRules /
            // DenyRules setters publish via Volatile.Write so readers using
            // the matching Volatile.Read getter observe the swap with proper
            // acquire/release semantics even on weak-memory architectures
            // (e.g. ARM64).
            if (isDeny)
            {
                var updated = _config.DenyRules != null
                    ? new List<UserRule>(_config.DenyRules) : [];
                updated.Add(entry);
                _config.DenyRules = updated;
            }
            else
            {
                var updated = _config.UserRules != null
                    ? new List<UserRule>(_config.UserRules) : [];
                updated.Add(entry);
                _config.UserRules = updated;
            }

            Console.Error.WriteLine(
                $"policy: saved {(isDeny ? "deny" : "allow")} rule to {filePath}");
        }
    }

    private static string FormatList(List<string>? list)
        => list == null ? "null" : $"[{string.Join(", ", list)}]";
}
