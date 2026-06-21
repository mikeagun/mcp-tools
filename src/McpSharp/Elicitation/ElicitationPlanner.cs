// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;

namespace McpSharp.Elicitation;

/// <summary>
/// Pure, single-shot schema rewriter. Maps one <see cref="DesiredField"/>
/// plus client support into the richest wire construct the client supports, with a
/// value-remap to translate the response back. It has NO I/O. Multi-round-trip
/// degradations (F2 sequential, F3 re-validate-and-retry, F7 url→instruction) are
/// orchestrated by <see cref="ElicitationDriver"/>, not here.
///
/// Single-shot rungs handled here: F1 (titles→labels), F4 (default pre-population,
/// never on a decision field), F5 (bool→Yes/No), F6 (number→string).
/// </summary>
public sealed class ElicitationPlanner
{
    /// <summary>Rewrite one desired field into a wire property schema + value-remap.</summary>
    public PlannedField Rewrite(DesiredField field, ClientFormSupport support)
    {
        return field.Kind switch
        {
            FieldKind.String => PlanString(field),
            FieldKind.Number or FieldKind.Integer => PlanNumber(field, support),
            FieldKind.Boolean => PlanBoolean(field, support),
            FieldKind.EnumSingle => PlanEnumSingle(field, support),
            FieldKind.EnumMulti => PlanEnumMulti(field, support),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
    }

    // -- String ---------------------------------------------------------------

    private static PlannedField PlanString(DesiredField f)
    {
        var schema = new JsonObject { ["type"] = "string" };
        ApplyCommon(schema, f);
        if (f.MinLength != null) schema["minLength"] = f.MinLength;
        if (f.MaxLength != null) schema["maxLength"] = f.MaxLength;
        if (f.Pattern != null) schema["pattern"] = f.Pattern;
        if (f.Format != null)
        {
            schema["format"] = FormSchemaBuilder.FormatToString(f.Format.Value);
            // Surface a format hint for text-only clients when the field gave none.
            if (f.Description == null && FormSchemaBuilder.DefaultFormatHint(f.Format.Value) is { } hint)
                schema["description"] = hint;
        }
        ApplyDefault(schema, f, f.Default);
        return new PlannedField(schema, DefaultingRemap(f, Identity), "none");
    }

    // -- Number / integer (F6 when numeric input unsupported) -----------------

    private static PlannedField PlanNumber(DesiredField f, ClientFormSupport support)
    {
        bool integer = f.Kind == FieldKind.Integer;

        if (support.Numbers)
        {
            var schema = new JsonObject { ["type"] = integer ? "integer" : "number" };
            ApplyCommon(schema, f);
            if (f.Minimum != null) schema["minimum"] = f.Minimum;
            if (f.Maximum != null) schema["maximum"] = f.Maximum;
            ApplyDefault(schema, f, f.Default);
            return new PlannedField(schema, DefaultingRemap(f, CoerceNumber(integer)), "none");
        }

        // F6: render as a constrained string and parse back.
        var s = new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = integer ? @"^-?\d+$" : @"^-?\d+(\.\d+)?$",
        };
        ApplyCommon(s, f);
        if (!f.IsDecision && f.Default != null)
            s["default"] = f.Default.ToJsonString().Trim('"');
        return new PlannedField(s, DefaultingRemap(f, ParseNumber(integer)), "F6");
    }

    // -- Boolean (F5 when native boolean unsupported) -------------------------

    private static PlannedField PlanBoolean(DesiredField f, ClientFormSupport support)
    {
        if (support.Booleans)
        {
            var schema = new JsonObject { ["type"] = "boolean" };
            ApplyCommon(schema, f);
            ApplyDefault(schema, f, f.Default);
            return new PlannedField(schema, DefaultingRemap(f, CoerceBool), "none");
        }

        // F5: Yes/No single-select mapped to true/false.
        var s = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("Yes", "No"),
        };
        ApplyCommon(s, f);
        if (!f.IsDecision && f.Default is JsonValue dv && dv.TryGetValue<bool>(out var b))
            s["default"] = b ? "Yes" : "No";

        Func<JsonNode?, JsonNode?> map = raw =>
        {
            var str = AsString(raw);
            if (str == "Yes") return JsonValue.Create(true);
            if (str == "No") return JsonValue.Create(false);
            return raw is null ? null : Clone(raw);
        };
        return new PlannedField(s, DefaultingRemap(f, map), "F5");
    }

    // -- Single-select enum (F1 when titles unsupported) ----------------------

    private static PlannedField PlanEnumSingle(DesiredField f, ClientFormSupport support)
    {
        var choices = RequireChoices(f);
        bool hasTitles = choices.Any(c => c.Title != null);

        if (hasTitles && support.Titles)
        {
            var schema = new JsonObject { ["type"] = "string", ["oneOf"] = OneOf(choices) };
            ApplyCommon(schema, f);
            ApplyDefault(schema, f, f.Default);
            return new PlannedField(schema, DefaultingRemap(f, Identity), "none");
        }

        if (hasTitles && !support.Titles)
        {
            // F1: present the titles as a plain enum, map the chosen title back to value.
            var labels = choices.Select(c => c.Title ?? c.Value).ToList();
            var schema = new JsonObject { ["type"] = "string", ["enum"] = ToArray(labels) };
            ApplyCommon(schema, f);
            if (!f.IsDecision && f.Default != null)
            {
                var defVal = f.Default.ToJsonString().Trim('"');
                var defLabel = choices.FirstOrDefault(c => c.Value == defVal);
                schema["default"] = defLabel.Value != null ? (defLabel.Title ?? defLabel.Value) : defVal;
            }
            var byTitle = choices.ToDictionary(c => c.Title ?? c.Value, c => c.Value);
            Func<JsonNode?, JsonNode?> map = raw =>
            {
                var s = AsString(raw);
                return s != null && byTitle.TryGetValue(s, out var v) ? JsonValue.Create(v) : (raw is null ? null : Clone(raw));
            };
            return new PlannedField(schema, DefaultingRemap(f, map), "F1");
        }

        // No titles: plain enum of values.
        var plain = new JsonObject { ["type"] = "string", ["enum"] = ToArray(choices.Select(c => c.Value)) };
        ApplyCommon(plain, f);
        ApplyDefault(plain, f, f.Default);
        return new PlannedField(plain, DefaultingRemap(f, Identity), "none");
    }

    // -- Multi-select array (F1 on items; F2 is the driver's job) -------------

    private static PlannedField PlanEnumMulti(DesiredField f, ClientFormSupport support)
    {
        if (!support.Arrays)
            throw new InvalidOperationException(
                $"Field '{f.Name}': multi-select requires the driver-orchestrated F2 fallback when arrays are unsupported.");

        var choices = RequireChoices(f);
        bool hasTitles = choices.Any(c => c.Title != null);

        JsonObject items;
        string fallback;
        Func<JsonNode?, JsonNode?> map;

        if (hasTitles && support.Titles)
        {
            items = new JsonObject { ["anyOf"] = OneOf(choices) };
            fallback = "none";
            map = CloneArray;
        }
        else if (hasTitles && !support.Titles)
        {
            // F1 on the item set: enum of titles, remap each title back to value.
            items = new JsonObject { ["type"] = "string", ["enum"] = ToArray(choices.Select(c => c.Title ?? c.Value)) };
            fallback = "F1";
            var byTitle = choices.ToDictionary(c => c.Title ?? c.Value, c => c.Value);
            map = raw => MapArray(raw, s => byTitle.TryGetValue(s, out var v) ? v : s);
        }
        else
        {
            items = new JsonObject { ["type"] = "string", ["enum"] = ToArray(choices.Select(c => c.Value)) };
            fallback = "none";
            map = CloneArray;
        }

        var schema = new JsonObject { ["type"] = "array", ["items"] = items };
        ApplyCommon(schema, f);
        if (f.MinItems != null) schema["minItems"] = f.MinItems;
        if (f.MaxItems != null) schema["maxItems"] = f.MaxItems;
        if (!f.IsDecision && f.Default is JsonArray da)
            schema["default"] = Clone(da);

        return new PlannedField(schema, DefaultingRemap(f, map), fallback);
    }

    // -- Remap helpers --------------------------------------------------------

    private static readonly Func<JsonNode?, JsonNode?> Identity = raw => raw is null ? null : Clone(raw);

    /// <summary>
    /// Wrap a value-map so an omitted (null) value applies the field's default
    /// for non-decision fields only (F4). A decision field is NEVER defaulted —
    /// an omitted decision stays omitted (deny-safe).
    /// </summary>
    private static Func<JsonNode?, JsonNode?> DefaultingRemap(DesiredField f, Func<JsonNode?, JsonNode?> inner)
    {
        return raw =>
        {
            if (raw is null)
                return f.IsDecision || f.Default is null ? null : Clone(f.Default);
            return inner(raw);
        };
    }

    private static Func<JsonNode?, JsonNode?> CoerceNumber(bool integer) => raw =>
    {
        if (raw is JsonValue v)
        {
            if (integer && v.TryGetValue<long>(out var l)) return JsonValue.Create(l);
            if (v.TryGetValue<double>(out var d))
            {
                if (!integer) return JsonValue.Create(d);
                // Only coerce a whole value to an integer; leave a fractional value
                // intact so the validator's integer/range checks reject it instead of
                // silently truncating (e.g. 5.7 must not pass as 5).
                return d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
            }
        }
        return raw is null ? null : Clone(raw);
    };

    private static Func<JsonNode?, JsonNode?> ParseNumber(bool integer) => raw =>
    {
        var s = AsString(raw);
        if (s != null)
        {
            if (integer && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return JsonValue.Create(l);
            if (!integer && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return JsonValue.Create(d);
        }
        return raw is null ? null : Clone(raw); // leave unparseable for the validator to reject
    };

    private static readonly Func<JsonNode?, JsonNode?> CoerceBool = raw => raw is null ? null : Clone(raw);

    private static readonly Func<JsonNode?, JsonNode?> CloneArray = raw => raw is null ? null : Clone(raw);

    private static JsonNode? MapArray(JsonNode? raw, Func<string, string> mapOne)
    {
        if (raw is not JsonArray arr) return raw is null ? null : Clone(raw);
        var result = new JsonArray();
        foreach (var item in arr)
        {
            var s = AsString(item);
            result.Add(s != null ? JsonValue.Create(mapOne(s)) : (item is null ? null : Clone(item)));
        }
        return result;
    }

    // -- Schema helpers -------------------------------------------------------

    private static void ApplyCommon(JsonObject schema, DesiredField f)
    {
        if (f.Title != null) schema["title"] = f.Title;
        if (f.Description != null) schema["description"] = f.Description;
    }

    private static void ApplyDefault(JsonObject schema, DesiredField f, JsonNode? def)
    {
        if (!f.IsDecision && def != null)
            schema["default"] = Clone(def);
    }

    private static JsonArray OneOf(IReadOnlyList<EnumChoice> choices)
    {
        var arr = new JsonArray();
        foreach (var c in choices)
        {
            var entry = new JsonObject { ["const"] = c.Value };
            if (c.Title != null) entry["title"] = c.Title;
            arr.Add(entry);
        }
        return arr;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static IReadOnlyList<EnumChoice> RequireChoices(DesiredField f)
        => f.Choices is { Count: > 0 } c
            ? c
            : throw new ArgumentException($"Enum field '{f.Name}' must declare at least one choice.");

    private static string? AsString(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode Clone(JsonNode n) => JsonNode.Parse(n.ToJsonString())!;
}
