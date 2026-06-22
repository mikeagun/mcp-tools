// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace McpSharp.Elicitation;

/// <summary>
/// Server-side validation of accepted elicitation content against the constraints
/// the server requested. Only
/// fields that declare constraints (or membership for enums) are checked — plain
/// unconstrained fields pass untouched so existing flows are undisturbed.
/// </summary>
public static class FormValidator
{
    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// Validate one field's logical (already-remapped) value. Returns true with a
    /// null error when valid, or false with a human-readable reason when invalid.
    /// </summary>
    public static bool Validate(DesiredField field, JsonNode? value, out string? error)
    {
        error = null;

        if (value is null)
        {
            if (field.Required)
            {
                error = $"'{field.Name}' is required.";
                return false;
            }
            return true;
        }

        return field.Kind switch
        {
            FieldKind.String => ValidateString(field, value, out error),
            FieldKind.Number => ValidateNumber(field, value, integer: false, out error),
            FieldKind.Integer => ValidateNumber(field, value, integer: true, out error),
            FieldKind.Boolean => ValidateBool(field, value, out error),
            FieldKind.EnumSingle => ValidateEnumSingle(field, value, out error),
            FieldKind.EnumMulti => ValidateEnumMulti(field, value, out error),
            _ => true,
        };
    }

    private static bool ValidateString(DesiredField f, JsonNode value, out string? error)
    {
        error = null;
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s))
        {
            error = $"'{f.Name}' must be a string.";
            return false;
        }

        if (f.MinLength != null && s.Length < f.MinLength)
        { error = $"'{f.Name}' must be at least {f.MinLength} characters."; return false; }
        if (f.MaxLength != null && s.Length > f.MaxLength)
        { error = $"'{f.Name}' must be at most {f.MaxLength} characters."; return false; }
        if (f.Pattern != null && !Regex.IsMatch(s, f.Pattern))
        { error = $"'{f.Name}' does not match the required pattern."; return false; }

        if (f.Format != null && !ValidateFormat(f.Format.Value, s))
        { error = $"'{f.Name}' is not a valid {FormSchemaBuilder.FormatToString(f.Format.Value)}."; return false; }

        return true;
    }

    private static bool ValidateFormat(StringFormat format, string s) => format switch
    {
        StringFormat.Email => EmailRegex.IsMatch(s),
        StringFormat.Uri => Uri.TryCreate(s, UriKind.Absolute, out _),
        StringFormat.Date => DateTime.TryParseExact(
            s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        StringFormat.DateTime => DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => true,
    };

    private static bool ValidateNumber(DesiredField f, JsonNode value, bool integer, out string? error)
    {
        error = null;
        if (!ElicitationResult.TryToDouble(value, out var d))
        {
            error = $"'{f.Name}' must be a {(integer ? "integer" : "number")}.";
            return false;
        }
        if (integer && d != Math.Floor(d))
        { error = $"'{f.Name}' must be an integer."; return false; }
        if (f.Minimum != null && d < f.Minimum)
        { error = $"'{f.Name}' must be >= {f.Minimum}."; return false; }
        if (f.Maximum != null && d > f.Maximum)
        { error = $"'{f.Name}' must be <= {f.Maximum}."; return false; }
        return true;
    }

    private static bool ValidateBool(DesiredField f, JsonNode value, out string? error)
    {
        error = null;
        if (value is JsonValue v && v.TryGetValue<bool>(out _))
            return true;
        error = $"'{f.Name}' must be a boolean.";
        return false;
    }

    private static bool ValidateEnumSingle(DesiredField f, JsonNode value, out string? error)
    {
        error = null;
        var s = AsString(value);
        var allowed = f.Choices?.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (s == null || allowed == null || !allowed.Contains(s))
        {
            error = $"'{f.Name}' must be one of the offered choices.";
            return false;
        }
        return true;
    }

    private static bool ValidateEnumMulti(DesiredField f, JsonNode value, out string? error)
    {
        error = null;
        if (value is not JsonArray arr)
        {
            error = $"'{f.Name}' must be an array.";
            return false;
        }
        if (f.MinItems != null && arr.Count < f.MinItems)
        { error = $"'{f.Name}' requires at least {f.MinItems} selection(s)."; return false; }
        if (f.MaxItems != null && arr.Count > f.MaxItems)
        { error = $"'{f.Name}' allows at most {f.MaxItems} selection(s)."; return false; }

        var allowed = f.Choices?.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (allowed != null)
        {
            foreach (var item in arr)
            {
                var s = AsString(item);
                if (s == null || !allowed.Contains(s))
                {
                    error = $"'{f.Name}' contains an invalid selection.";
                    return false;
                }
            }
        }
        return true;
    }

    private static string? AsString(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
