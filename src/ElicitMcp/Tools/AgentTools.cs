// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp;
using McpSharp.Elicitation;

namespace ElicitMcp.Tools;

/// <summary>
/// The production purpose of elicit-mcp: agent-initiated elicitation tools. The
/// agent invokes them to obtain a user decision or structured input (free-text
/// feedback is just an optional string field of request_input); the tool prompts
/// the user via MCP elicitation and returns the result. Sensitive data is refused
/// in form mode. These tools always use the client's real advertised
/// capabilities — capability simulation is a conformance-only concern.
/// </summary>
internal static class AgentTools
{
    internal static void Register(McpServer server)
    {
        RegisterDecision(server);
        RegisterInput(server);
    }

    // -- request_decision -----------------------------------------------------

    private static void RegisterDecision(McpServer server)
    {
        var props = new JsonObject
        {
            ["message"] = StringProp("Prompt shown to the user."),
            ["options"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Choices: bare strings, or { value, title } objects for display titles.",
            },
            ["multi"] = new JsonObject { ["type"] = "boolean", ["description"] = "Allow multiple selections." },
            ["min"] = new JsonObject { ["type"] = "integer", ["description"] = "Minimum selections (multi)." },
            ["max"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum selections (multi)." },
        };
        ElicitHelpers.AddNoteProp(props);

        server.RegisterTool(new ToolInfo
        {
            Name = "request_decision",
            Description = "Ask the user to choose among options (single or multi-select, with or without display titles). "
                + "Returns: { status, decision_made, decision|selections, fallback_used }. decision_made is true only "
                + "when the user actively selected a value (an omitted/declined/cancelled decision is never recorded). "
                + "You MUST read 'decision'/'selections' to know WHICH option was chosen — a negative option (e.g. 'Deny') "
                + "still returns decision_made=true with decision='Deny'.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray("message", "options"),
            },
            Handler = args =>
            {
                var choices = ElicitHelpers.ParseChoices(args["options"]);
                if (choices.Count == 0)
                    throw new ArgumentException("'options' must contain at least one choice.");

                bool multi = args["multi"]?.GetValue<bool>() ?? false;
                var field = new DesiredField
                {
                    Name = "decision",
                    Kind = multi ? FieldKind.EnumMulti : FieldKind.EnumSingle,
                    IsDecision = true,
                    Required = true,
                    Title = "Decision",
                    Choices = choices,
                    MinItems = multi ? args["min"]?.GetValue<int>() : null,
                    MaxItems = multi ? args["max"]?.GetValue<int>() : null,
                };

                var support = ElicitHelpers.ProductionSupport(server);
                var outcome = new ElicitationDriver(server).Run(
                    ElicitHelpers.MessageArg(args, "Please choose:"),
                    new[] { field }, support, ElicitHelpers.DefaultTimeoutSeconds, maxRetries: 0);

                var result = new JsonObject
                {
                    ["status"] = outcome.Status.ToString().ToLowerInvariant(),
                    ["decision_made"] = outcome.DecisionMade,
                    ["fallback_used"] = ElicitHelpers.ToArray(outcome.FallbacksUsed),
                };
                if (outcome.DecisionMade && outcome.Values?["decision"] is { } dec)
                    result[multi ? "selections" : "decision"] = JsonNode.Parse(dec.ToJsonString());
                if (args["note"]?.GetValue<string>() is { } note) result["note"] = note;
                return result;
            },
        });
    }

    // -- request_input (also covers free-text feedback) -----------------------

    private static void RegisterInput(McpServer server)
    {
        var props = new JsonObject
        {
            ["message"] = StringProp("Prompt shown to the user."),
            ["fields"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Field specs: { name, type (string|number|integer|boolean|enum|multi_enum), "
                    + "required?, title?, description?, min?, max?, minLength?, maxLength?, pattern?, "
                    + "format? (email|uri|date|date-time), choices?, default? }. "
                    + "For free-text feedback, use a single optional string field.",
            },
        };
        ElicitHelpers.AddNoteProp(props);

        server.RegisterTool(new ToolInfo
        {
            Name = "request_input",
            Description = "Collect typed structured input from the user (string/number/integer/boolean/enum with "
                + "constraints, formats, and defaults). Use a single optional string field to ask for free-text "
                + "feedback. Returns: { status, values, fallback_used, driver_message? }. Sensitive field names are "
                + "refused (use a secure out-of-band flow instead).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray("message", "fields"),
            },
            Handler = args =>
            {
                var fields = ElicitHelpers.ParseDesiredFields(args["fields"]);
                if (fields.Count == 0)
                    throw new ArgumentException("'fields' must contain at least one field.");

                var support = ElicitHelpers.ProductionSupport(server);
                DriverOutcome outcome;
                try
                {
                    outcome = new ElicitationDriver(server).Run(
                        ElicitHelpers.MessageArg(args, "Please provide input:"),
                        fields, support, ElicitHelpers.DefaultTimeoutSeconds, maxRetries: 1);
                }
                catch (InvalidOperationException ex)
                {
                    // A requested field name was sensitive — refuse in form mode.
                    return new JsonObject { ["status"] = "refused", ["reason"] = ex.Message };
                }

                var result = new JsonObject
                {
                    ["status"] = outcome.Status.ToString().ToLowerInvariant(),
                    ["fallback_used"] = ElicitHelpers.ToArray(outcome.FallbacksUsed),
                };
                if (outcome.Values != null)
                    result["values"] = JsonNode.Parse(outcome.Values.ToJsonString());
                if (outcome.Message != null)
                    result["driver_message"] = outcome.Message;
                if (args["note"]?.GetValue<string>() is { } note) result["note"] = note;
                return result;
            },
        });
    }

    private static JsonObject StringProp(string description)
        => new() { ["type"] = "string", ["description"] = description };
}
