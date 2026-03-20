// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp.Policy;

/// <summary>
/// Dispatch-level policy interception. Evaluates tool calls against the policy
/// engine and uses MCP elicitation to prompt the user when confirmation is needed.
/// The agent never sees the approval flow -- tool calls either succeed or fail.
/// </summary>
public static class PolicyDispatch
{
    /// <summary>
    /// Policy-aware dispatch. Wraps the server's normal dispatch with policy checks.
    /// </summary>
    public static JsonNode? Dispatch(
        string method,
        JsonNode? parameters,
        McpServer server,
        PolicyEngine policy,
        IOptionGenerator optionGenerator,
        Func<string, JsonObject, JsonNode?>? preValidator = null,
        Action<string, JsonObject>? argsEnricher = null,
        int elicitationTimeoutSeconds = 0)
    {
        if (method != "tools/call")
            return server.Dispatch(method, parameters);

        var toolName = parameters?["name"]?.GetValue<string>();
        if (toolName == null)
            return server.Dispatch(method, parameters);

        var args = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        // Pre-validation (server-specific, optional).
        if (preValidator != null)
        {
            var preError = preValidator(toolName, args);
            if (preError != null)
                return preError;
        }

        // Args enrichment (inject context before evaluation, e.g. resolve build_id → sln_path).
        argsEnricher?.Invoke(toolName, args);

        var evaluation = policy.Evaluate(toolName, args);

        return evaluation.Decision switch
        {
            PolicyDecision.Allow => server.Dispatch(method, parameters),
            PolicyDecision.Deny => DeniedResponse(toolName, evaluation, args),
            PolicyDecision.Confirm => HandleElicitation(
                toolName, args, parameters!, evaluation, policy, server, optionGenerator,
                elicitationTimeoutSeconds),
            _ => server.Dispatch(method, parameters),
        };
    }

    // -- Elicitation flow ----------------------------------------------------

    private static JsonNode? HandleElicitation(
        string toolName,
        JsonObject args,
        JsonNode fullParameters,
        PolicyEvaluation evaluation,
        PolicyEngine policy,
        McpServer server,
        IOptionGenerator optionGenerator,
        int elicitationTimeoutSeconds = 0)
    {
        var options = optionGenerator.Generate(toolName, args, evaluation);
        var argsSummary = BuildArgsSummary(args);

        var schema = BuildElicitationSchema(toolName, argsSummary, evaluation, options, out var message);
        var result = server.Elicit(message, schema, elicitationTimeoutSeconds);

        if (result == null)
            return ElicitationDeniedResponse(toolName, evaluation, null);

        if (result.Action == ElicitationAction.Timeout)
            return ElicitationTimeoutResponse(toolName, evaluation, args);

        if (result.Action != ElicitationAction.Accept)
            return ElicitationDeniedResponse(toolName, evaluation, result.Action);

        var action = result.Content?["action"]?.GetValue<string>();
        if (action == null)
            return ElicitationDeniedResponse(toolName, evaluation, ElicitationAction.Decline);

        var matched = options.Find(o => o.Label == action);
        if (matched == null)
        {
            // User typed a freeform response (not one of the predefined options).
            // Use distinct status and forceful language so agents don't ignore it.
            Console.Error.WriteLine($"[policy] User feedback for {toolName}: {action}");
            return ErrorContent(new JsonObject
            {
                ["status"] = "user_feedback",
                ["IMPORTANT"] = "The user provided feedback via the approval prompt. STOP your current task and address their feedback before continuing.",
                ["user_feedback"] = action,
                ["tool"] = toolName,
                ["original_reason"] = evaluation.Reason,
            });
        }

        // Handle denials.
        if (matched.Polarity == ApprovalPolarity.Deny)
        {
            var denyPersistence = matched.Persistence ?? ApprovalPersistence.Once;
            if (matched.Rule != null)
            {
                if (denyPersistence == ApprovalPersistence.Permanent)
                {
                    policy.SaveDenyRuleToPolicy(matched.Rule,
                        $"User denied via elicitation: {toolName} ({argsSummary.ToJsonString()})");
                    policy.RegisterSessionDenial(matched.Rule);
                }
                else if (denyPersistence == ApprovalPersistence.Session)
                    policy.RegisterSessionDenial(matched.Rule);
            }
            return ElicitationDeniedResponse(toolName, evaluation, ElicitationAction.Decline);
        }

        // Determine persistence: either from the option directly (single-prompt)
        // or from a follow-up elicitation (two-prompt when Persistence is null).
        ApprovalPersistence persistence;
        if (matched.Persistence == null)
        {
            var p = PromptForPersistence(server, toolName, argsSummary, matched.Label);
            if (p == null)
                return ElicitationDeniedResponse(toolName, evaluation, ElicitationAction.Cancel);
            persistence = p.Value;
        }
        else
        {
            persistence = matched.Persistence.Value;
        }

        if (matched.Rule != null)
        {
            if (persistence == ApprovalPersistence.Permanent)
            {
                policy.RegisterSessionApproval(matched.Rule);
                policy.SaveRuleToPolicy(matched.Rule,
                    $"User approved via elicitation: {toolName} ({argsSummary.ToJsonString()})");
            }
            else if (persistence == ApprovalPersistence.Session)
                policy.RegisterSessionApproval(matched.Rule);
        }

        return server.Dispatch("tools/call", fullParameters);
    }

    // -- Persistence prompt (Prompt 2) ----------------------------------------

    private static ApprovalPersistence? PromptForPersistence(
        McpServer server, string toolName, JsonObject argsSummary, string scopeLabel)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "How long?",
                    ["enum"] = new JsonArray { "This session only", "Permanently" },
                },
            },
            ["required"] = new JsonArray { "action" },
        };

        // Single-line message with context (UI may truncate at newlines).
        var parts = new List<string>();
        foreach (var kv in argsSummary)
            parts.Add($"{FormatParamName(kv.Key)}={FormatParamValue(kv.Value)}");
        var context = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
        var message = $"Save \"{scopeLabel}\" as:{context}";

        var result = server.Elicit(message, schema);

        if (result == null || result.Action != ElicitationAction.Accept)
            return null;

        return result.Content?["action"]?.GetValue<string>() switch
        {
            "This session only" => ApprovalPersistence.Session,
            "Permanently" => ApprovalPersistence.Permanent,
            _ => null,
        };
    }

    // -- Schema builder ------------------------------------------------------

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["sln_path"] = "Solution",
        ["project_path"] = "Project",
        ["targets"] = "Targets",
        ["configuration"] = "Configuration",
        ["vm_name"] = "VM",
        ["session_id"] = "Session",
        ["command"] = "Command",
        ["source"] = "Source",
        ["destination"] = "Destination",
        ["path"] = "Path",
        ["name"] = "Name",
        ["action"] = "Action",
    };

    private static string FormatParamName(string key)
        => DisplayNames.TryGetValue(key, out var display) ? display : key;

    private static string FormatParamValue(JsonNode? value)
    {
        if (value is JsonArray arr)
            return $"[{string.Join(", ", arr.Select(n => n?.ToString()))}]";
        var str = value?.ToString() ?? "";
        // Strip surrounding quotes from JSON string values.
        if (str.Length >= 2 && str[0] == '"' && str[^1] == '"')
            str = str[1..^1];
        return str;
    }

    public static JsonObject BuildElicitationSchema(
        string toolName, JsonObject argsSummary, PolicyEvaluation evaluation,
        List<ElicitationOption> options, out string message)
    {
        var enumValues = new JsonArray();
        foreach (var opt in options)
            enumValues.Add(opt.Label);

        // Build a single-line message with key context (UI may truncate at newlines).
        var parts = new List<string> { $"Approve {toolName}:" };
        foreach (var kv in argsSummary)
            parts.Add($"{FormatParamName(kv.Key)}={FormatParamValue(kv.Value)}");
        message = string.Join(" ", parts);

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["title"] = "Choose an action",
                    ["enum"] = enumValues,
                },
            },
            ["required"] = new JsonArray { "action" },
        };
    }

    // -- Response builders ---------------------------------------------------

    private static JsonNode DeniedResponse(string toolName, PolicyEvaluation evaluation, JsonObject? args = null)
    {
        var json = new JsonObject
        {
            ["status"] = "denied",
            ["tool"] = toolName,
            ["reason"] = evaluation.Reason,
            ["hint"] = "This operation is blocked by server policy.",
        };
        // Include the denied command so the agent knows exactly what was blocked.
        var command = args?["command"]?.GetValue<string>();
        if (command != null)
            json["command"] = command;
        return ErrorContent(json);
    }

    private static JsonNode ElicitationDeniedResponse(
        string toolName, PolicyEvaluation evaluation, ElicitationAction? action)
    {
        var reason = action switch
        {
            ElicitationAction.Decline => "User declined the request.",
            ElicitationAction.Cancel => "User cancelled the request.",
            _ => "Approval was not granted.",
        };
        return ErrorContent(new JsonObject
        {
            ["status"] = "denied",
            ["tool"] = toolName,
            ["reason"] = reason,
            ["original_reason"] = evaluation.Reason,
            ["hint"] = "Try an alternative approach or ask the user for guidance.",
        });
    }

    private static JsonNode ElicitationTimeoutResponse(
        string toolName, PolicyEvaluation evaluation, JsonObject? args)
    {
        var json = new JsonObject
        {
            ["status"] = "approval_timeout",
            ["IMPORTANT"] = "The approval prompt timed out — the user has not responded yet. " +
                "STOP and ask the user if they want to approve this operation or take a different approach.",
            ["tool"] = toolName,
            ["original_reason"] = evaluation.Reason,
        };
        var command = args?["command"]?.GetValue<string>();
        if (command != null)
            json["command"] = command;
        return ErrorContent(json);
    }

    /// <summary>
    /// Build an isError tool response. Useful for pre-validation errors.
    /// </summary>
    public static JsonNode ErrorContent(JsonObject error)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = error.ToJsonString() }
            },
            ["isError"] = true,
        };
    }

    // -- Helpers --------------------------------------------------------------

    public static JsonObject BuildArgsSummary(JsonObject args,
        string[]? interestingParams = null)
    {
        interestingParams ??= ["sln_path", "project_path", "vm_name", "command",
            "session_id", "name", "action", "source", "destination", "path",
            "targets", "configuration"];
        var summary = new JsonObject();
        foreach (var param in interestingParams)
        {
            if (args.ContainsKey(param))
                summary[param] = JsonNode.Parse(args[param]!.ToJsonString());
        }
        return summary;
    }
}
