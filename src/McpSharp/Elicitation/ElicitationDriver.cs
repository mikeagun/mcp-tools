// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp.Elicitation;

/// <summary>Outcome status of a driven elicitation.</summary>
public enum DriverStatus
{
    /// <summary>User accepted and content passed validation.</summary>
    Accepted,
    /// <summary>User explicitly declined.</summary>
    Declined,
    /// <summary>User dismissed without choosing.</summary>
    Cancelled,
    /// <summary>Server-side timeout.</summary>
    TimedOut,
    /// <summary>No prompt was possible (client lacks form support / no transport).</summary>
    NoPrompt,
    /// <summary>Accepted but content failed validation after the retry budget.</summary>
    Invalid,
}

/// <summary>The structured result of a driven elicitation.</summary>
public sealed class DriverOutcome
{
    public required DriverStatus Status { get; init; }

    /// <summary>Logical (remapped, recombined) values, present only when accepted.</summary>
    public JsonObject? Values { get; init; }

    /// <summary>Fallback rungs that fired, e.g. ["F1","F2"].</summary>
    public IReadOnlyList<string> FallbacksUsed { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the user actively made a decision — i.e. a decision field was returned
    /// with a value. False whenever a decision field was omitted (an omitted decision
    /// is never defaulted to a value). True when no decision field is present
    /// and the form was accepted.
    ///
    /// NOTE: this reflects only that a choice was <em>made</em>, not <em>which</em>
    /// choice. A negative option (e.g. "Deny") still sets this true — callers MUST read
    /// the decision value to know what was chosen.
    /// </summary>
    public bool DecisionMade { get; init; }

    /// <summary>Human-readable detail (validation error, no-prompt reason, etc.).</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Orchestrates a full form-mode elicitation: plans each field via the pure
/// <see cref="ElicitationPlanner"/>, assembles one wire schema, drives the
/// round-trip through <see cref="McpServer.Elicit"/>, and applies the
/// multi-round-trip degradations (F2 multi-select→booleans, F3 re-validate-and-retry)
/// plus server-side validation. Enforces the decision-field deny-safety invariant:
/// a fallback or default NEVER resolves a decision field to an allow.
/// </summary>
public sealed class ElicitationDriver
{
    private readonly McpServer _server;
    private readonly ElicitationPlanner _planner = new();

    public ElicitationDriver(McpServer server) => _server = server;

    /// <summary>
    /// Run a form elicitation for the given fields. <paramref name="maxRetries"/>
    /// is the number of additional re-prompts on validation failure (F3).
    /// </summary>
    public DriverOutcome Run(
        string message,
        IReadOnlyList<DesiredField> fields,
        ClientFormSupport support,
        int timeoutSeconds = 0,
        int maxRetries = 1)
    {
        // F8: client did not advertise form mode — no prompt is possible.
        if (!support.Modes.Supports(ElicitationMode.Form))
            return new DriverOutcome
            {
                Status = DriverStatus.NoPrompt,
                Message = "Client does not support form-mode elicitation.",
            };

        var plans = BuildPlans(fields, support);
        var fallbacks = plans.SelectMany(p => p.Fallbacks()).Where(f => f != "none").Distinct().ToList();

        string currentMessage = message;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var schema = BuildSchema(plans);
            var result = _server.Elicit(currentMessage, schema, timeoutSeconds);

            if (result == null)
                return new DriverOutcome { Status = DriverStatus.NoPrompt, FallbacksUsed = fallbacks };

            switch (result.Action)
            {
                case ElicitationAction.Decline:
                    return Terminal(DriverStatus.Declined, fallbacks);
                case ElicitationAction.Cancel:
                    return Terminal(DriverStatus.Cancelled, fallbacks);
                case ElicitationAction.Timeout:
                    return Terminal(DriverStatus.TimedOut, fallbacks);
            }

            // Accept: remap + recombine into logical values.
            var content = result.Content ?? new JsonObject();
            var values = new JsonObject();
            foreach (var plan in plans)
                values[plan.Field.Name] = plan.Extract(content);

            // Re-validate each field's logical value against the requested constraints.
            string? validationError = null;
            foreach (var plan in plans)
            {
                if (!FormValidator.Validate(plan.Field, values[plan.Field.Name], out var err))
                {
                    validationError = err;
                    break;
                }
            }

            // Decision-field deny-safety: an omitted decision is never recorded.
            bool decisionMade = ComputeDecisionMade(plans, values);

            if (validationError != null)
            {
                if (attempt < maxRetries)
                {
                    currentMessage = $"{message}\n(Previous response was invalid: {validationError})";
                    continue;
                }
                return new DriverOutcome
                {
                    Status = DriverStatus.Invalid,
                    FallbacksUsed = fallbacks,
                    DecisionMade = false, // invalid input is never a recorded decision
                    Message = validationError,
                };
            }

            return new DriverOutcome
            {
                Status = DriverStatus.Accepted,
                Values = values,
                FallbacksUsed = fallbacks,
                DecisionMade = decisionMade,
            };
        }

        // Unreachable in practice; safety net.
        return new DriverOutcome { Status = DriverStatus.Invalid, FallbacksUsed = fallbacks };
    }

    private static DriverOutcome Terminal(DriverStatus status, IReadOnlyList<string> fallbacks)
        => new() { Status = status, FallbacksUsed = fallbacks, DecisionMade = false };

    private static bool ComputeDecisionMade(List<FieldPlan> plans, JsonObject values)
    {
        var decisionFields = plans.Where(p => p.Field.IsDecision).ToList();
        if (decisionFields.Count == 0)
            return true; // no decision field — acceptance is the outcome
        // Every decision field must be present (non-null). An omitted decision is not made.
        return decisionFields.All(p => values[p.Field.Name] is not null);
    }

    private List<FieldPlan> BuildPlans(IReadOnlyList<DesiredField> fields, ClientFormSupport support)
    {
        var plans = new List<FieldPlan>();
        foreach (var field in fields)
        {
            if (field.Kind == FieldKind.EnumMulti && !support.Arrays)
                plans.Add(FieldPlan.MultiAsBooleans(field));   // F2
            else
                plans.Add(FieldPlan.Single(field, _planner.Rewrite(field, support)));
        }
        return plans;
    }

    private static JsonObject BuildSchema(List<FieldPlan> plans)
    {
        var builder = new FormSchemaBuilder();
        foreach (var plan in plans)
            plan.Contribute(builder);
        return builder.Build();
    }

    // -- Per-field plan -------------------------------------------------------

    private sealed class FieldPlan
    {
        public required DesiredField Field { get; init; }
        private PlannedField? _single;
        private List<(string Wire, string Value)>? _boolParts; // F2 expansion

        public static FieldPlan Single(DesiredField field, PlannedField planned)
            => new() { Field = field, _single = planned };

        public static FieldPlan MultiAsBooleans(DesiredField field)
        {
            var parts = new List<(string, string)>();
            var choices = field.Choices ?? Array.Empty<EnumChoice>();
            for (int i = 0; i < choices.Count; i++)
                parts.Add(($"{field.Name}__{i}", choices[i].Value));
            return new FieldPlan { Field = field, _boolParts = parts };
        }

        public IEnumerable<string> Fallbacks()
        {
            if (_boolParts != null) yield return "F2";
            else if (_single != null) yield return _single.FallbackUsed;
        }

        public void Contribute(FormSchemaBuilder builder)
        {
            if (_boolParts != null)
            {
                var choices = Field.Choices!;
                for (int i = 0; i < _boolParts.Count; i++)
                {
                    builder.AddField(_boolParts[i].Wire, new JsonObject
                    {
                        ["type"] = "boolean",
                        ["title"] = choices[i].Title ?? choices[i].Value,
                    });
                }
            }
            else
            {
                builder.AddField(Field.Name, Clone(_single!.Schema), Field.Required);
            }
        }

        public JsonNode? Extract(JsonObject content)
        {
            if (_boolParts != null)
            {
                // F2 recombine: collect selected values into an array.
                var selected = new JsonArray();
                foreach (var (wire, value) in _boolParts)
                {
                    if (content.TryGetPropertyValue(wire, out var n) &&
                        n is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                    {
                        selected.Add(value);
                    }
                }
                return selected;
            }

            content.TryGetPropertyValue(Field.Name, out var raw);
            return _single!.Remap(raw);
        }

        private static JsonObject Clone(JsonObject o) => JsonNode.Parse(o.ToJsonString())!.AsObject();
    }
}
