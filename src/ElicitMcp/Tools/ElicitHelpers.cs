// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp;
using McpSharp.Elicitation;

namespace ElicitMcp.Tools;

/// <summary>
/// Shared helpers for the elicit-mcp tools: simulate_caps parsing, running a
/// desired-field elicitation through the driver, and producing the machine-checkable
/// conformance record.
/// </summary>
internal static class ElicitHelpers
{
    internal const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Resolve client form support. When the caller passes <c>simulate_caps</c>, the
    /// requested feature flags are honored verbatim so a single real client can
    /// exercise every fallback rung deterministically. Otherwise the
    /// real advertised modes are used with full feature support assumed.
    /// </summary>
    internal static ClientFormSupport ResolveSupport(McpServer server, JsonObject args)
    {
        if (args["simulate_caps"] is not JsonObject sim)
            return ProductionSupport(server);

        bool Flag(string key, bool dflt) =>
            sim.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
            v.TryGetValue<bool>(out var b) ? b : dflt;

        bool form = Flag("form", true);
        bool url = Flag("url", false);
        return new ClientFormSupport
        {
            Modes = new ElicitationCapabilities { Supported = form || url, Form = form, Url = url },
            Titles = Flag("titles", true),
            Arrays = Flag("arrays", true),
            Numbers = Flag("numbers", true),
            Booleans = Flag("booleans", true),
        };
    }

    /// <summary>
    /// Form support for production (agent-facing) tools: the client's real advertised
    /// modes with full feature support assumed. simulate_caps is never honored here —
    /// it is a conformance-only concern.
    /// </summary>
    internal static ClientFormSupport ProductionSupport(McpServer server)
        => new() { Modes = server.ElicitationCaps };

    internal static JsonObject RecordFromOutcome(string feature, DriverOutcome o)
    {
        var record = new JsonObject
        {
            ["feature"] = feature,
            ["status"] = o.Status.ToString().ToLowerInvariant(),
            ["fallback_used"] = ToArray(o.FallbacksUsed),
            ["decision_made"] = o.DecisionMade,
            ["verdict"] = Verdict(o),
        };
        if (o.Values != null)
            record["values"] = JsonNode.Parse(o.Values.ToJsonString());
        if (o.Message != null)
            record["driver_message"] = o.Message;
        return record;
    }

    private static string Verdict(DriverOutcome o) => o.Status switch
    {
        DriverStatus.Accepted => "pass",
        DriverStatus.Invalid => "fail",
        DriverStatus.NoPrompt => "no-prompt",
        DriverStatus.Declined => "declined",
        DriverStatus.Cancelled => "cancelled",
        DriverStatus.TimedOut => "timeout",
        _ => "needs-human",
    };

    internal static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    // -- Argument parsing -----------------------------------------------------

    internal static string MessageArg(JsonObject args, string fallback)
        => args["message"]?.GetValue<string>() ?? fallback;

    /// <summary>
    /// Parse a list of enum choices from a JSON array. Each item may be a bare
    /// string (value only) or an object <c>{ value, title? }</c>.
    /// </summary>
    internal static List<EnumChoice> ParseChoices(JsonNode? node)
    {
        var choices = new List<EnumChoice>();
        if (node is not JsonArray arr)
            return choices;
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
                choices.Add(new EnumChoice(s));
            else if (item is JsonObject o && o["value"]?.GetValue<string>() is { } value)
                choices.Add(new EnumChoice(value, o["title"]?.GetValue<string>()));
        }
        return choices;
    }

    /// <summary>
    /// Parse a list of field specs (for request_input) into desired fields. Each spec
    /// is an object: { name, type, required?, title?, description?, min?, max?,
    /// minLength?, maxLength?, pattern?, format?, choices?, default? }. type ∈
    /// string|number|integer|boolean|enum|multi_enum.
    /// </summary>
    internal static List<DesiredField> ParseDesiredFields(JsonNode? node)
    {
        var fields = new List<DesiredField>();
        if (node is not JsonArray arr)
            return fields;

        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var name = o["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;

            var typeStr = o["type"]?.GetValue<string>() ?? "string";
            var kind = typeStr switch
            {
                "number" => FieldKind.Number,
                "integer" => FieldKind.Integer,
                "boolean" => FieldKind.Boolean,
                "enum" => FieldKind.EnumSingle,
                "multi_enum" => FieldKind.EnumMulti,
                _ => FieldKind.String,
            };

            StringFormat? format = o["format"]?.GetValue<string>() switch
            {
                "email" => StringFormat.Email,
                "uri" => StringFormat.Uri,
                "date" => StringFormat.Date,
                "date-time" => StringFormat.DateTime,
                _ => null,
            };

            fields.Add(new DesiredField
            {
                Name = name,
                Kind = kind,
                Required = o["required"]?.GetValue<bool>() ?? false,
                Title = o["title"]?.GetValue<string>(),
                Description = o["description"]?.GetValue<string>(),
                MinLength = o["minLength"]?.GetValue<int>(),
                MaxLength = o["maxLength"]?.GetValue<int>(),
                Pattern = o["pattern"]?.GetValue<string>(),
                Format = format,
                Minimum = Dbl(o["min"] ?? o["minimum"]),
                Maximum = Dbl(o["max"] ?? o["maximum"]),
                MinItems = o["minItems"]?.GetValue<int>(),
                MaxItems = o["maxItems"]?.GetValue<int>(),
                Choices = o["choices"] != null ? ParseChoices(o["choices"]) : null,
                Default = o["default"] is { } d ? JsonNode.Parse(d.ToJsonString()) : null,
            });
        }
        return fields;
    }

    /// <summary>The note-only input property for production (agent-facing) tools.</summary>
    internal static void AddNoteProp(JsonObject properties)
    {
        properties["note"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Optional note echoed back in the result.",
        };
    }

    /// <summary>The shared input-schema fragment exposing note + simulate_caps to agents.</summary>
    internal static void AddCommonProps(JsonObject properties)
    {
        properties["note"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Optional note echoed back in the result.",
        };
        properties["simulate_caps"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Optionally simulate reduced client capabilities to exercise fallbacks: { form, url, titles, arrays, numbers, booleans } (booleans).",
            ["properties"] = new JsonObject
            {
                ["form"] = Bool(), ["url"] = Bool(), ["titles"] = Bool(),
                ["arrays"] = Bool(), ["numbers"] = Bool(), ["booleans"] = Bool(),
            },
        };
    }

    private static JsonObject Bool() => new() { ["type"] = "boolean" };

    /// <summary>Read a JSON number argument as a double, tolerant of int/long/decimal backing.</summary>
    internal static double? Dbl(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }
}
