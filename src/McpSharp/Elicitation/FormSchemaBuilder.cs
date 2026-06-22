// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace McpSharp.Elicitation;

/// <summary>
/// String <c>format</c> values permitted by form-mode elicitation.
/// </summary>
public enum StringFormat
{
    /// <summary><c>email</c></summary>
    Email,
    /// <summary><c>uri</c></summary>
    Uri,
    /// <summary><c>date</c></summary>
    Date,
    /// <summary><c>date-time</c></summary>
    DateTime,
}

/// <summary>
/// A single enum choice. <see cref="Title"/> is optional display text; when any
/// choice in a set carries a title the set is emitted as <c>oneOf</c>/<c>anyOf</c>
/// of <c>{ const, title }</c>, otherwise as a plain <c>enum</c>.
/// </summary>
public readonly record struct EnumChoice(string Value, string? Title = null);

/// <summary>
/// Typed builder for form-mode <c>requestedSchema</c> objects.
/// Emits a flat object with primitive properties only and refuses to
/// emit known-sensitive fields in form mode — sensitive data must use
/// URL mode.
/// </summary>
public sealed class FormSchemaBuilder
{
    private readonly List<(string Name, JsonObject Schema, bool Required)> _fields = new();

    // Field names that must never be solicited via form mode (collected as plaintext).
    private static readonly Regex SensitiveName = new(
        @"(password|passwd|passphrase|passcode|pwd|secret|token|api[_-]?key|apikey|credential|private[_-]?key|session[_-]?key|payment|credit[_-]?card|card[_-]?number|cardnumber|cvv|cvc|\bpin\b|\bssn\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Whether a field name is considered sensitive and must not be solicited via
    /// form mode. Sensitive data must use URL mode instead.
    /// </summary>
    public static bool IsSensitiveFieldName(string name) => SensitiveName.IsMatch(name);

    // -- String ---------------------------------------------------

    /// <summary>Add a string property.</summary>
    /// <param name="sensitive">
    /// Caller-declared sensitivity. When true (or the name matches a known
    /// sensitive pattern) the field is refused; use URL mode instead.
    /// </param>
    public FormSchemaBuilder String(string name, bool required = false,
        string? title = null, string? description = null,
        int? minLength = null, int? maxLength = null,
        string? pattern = null, StringFormat? format = null,
        string? @default = null, bool sensitive = false)
    {
        GuardSensitive(name, sensitive);

        var schema = new JsonObject { ["type"] = "string" };
        if (title != null) schema["title"] = title;
        if (description != null) schema["description"] = description;
        if (minLength != null) schema["minLength"] = minLength;
        if (maxLength != null) schema["maxLength"] = maxLength;
        if (pattern != null) schema["pattern"] = pattern;
        if (format != null)
        {
            schema["format"] = FormatToString(format.Value);
            // Surface a format hint for text-only clients when the caller gave none.
            if (description == null && DefaultFormatHint(format.Value) is { } hint)
                schema["description"] = hint;
        }
        if (@default != null) schema["default"] = @default;

        return Add(name, schema, required);
    }

    // -- Number / integer --------------------------------------------

    /// <summary>Add a number or integer property.</summary>
    public FormSchemaBuilder Number(string name, bool required = false, bool integer = false,
        double? minimum = null, double? maximum = null, double? @default = null,
        string? title = null, string? description = null)
    {
        GuardSensitive(name, false);

        var schema = new JsonObject { ["type"] = integer ? "integer" : "number" };
        if (title != null) schema["title"] = title;
        if (description != null) schema["description"] = description;
        if (minimum != null) schema["minimum"] = minimum;
        if (maximum != null) schema["maximum"] = maximum;
        if (@default != null)
            schema["default"] = integer ? (JsonNode)(long)@default.Value : @default.Value;

        return Add(name, schema, required);
    }

    // -- Boolean -----------------------------------------------------

    /// <summary>Add a boolean property.</summary>
    public FormSchemaBuilder Boolean(string name, bool required = false,
        bool? @default = null, string? title = null, string? description = null)
    {
        GuardSensitive(name, false);

        var schema = new JsonObject { ["type"] = "boolean" };
        if (title != null) schema["title"] = title;
        if (description != null) schema["description"] = description;
        if (@default != null) schema["default"] = @default.Value;

        return Add(name, schema, required);
    }

    // -- Single-select enum ---------------------------------------

    /// <summary>
    /// Add a single-select enum. Emits a plain <c>enum</c> when no choice carries
    /// a title and <c>oneOf</c>/<c>const</c>/<c>title</c> when any does.
    /// </summary>
    public FormSchemaBuilder EnumSingle(string name, IReadOnlyList<EnumChoice> choices,
        bool required = false, string? @default = null,
        string? title = null, string? description = null)
    {
        GuardSensitive(name, false);
        if (choices.Count == 0)
            throw new ArgumentException($"Enum '{name}' must have at least one choice.", nameof(choices));

        var schema = new JsonObject { ["type"] = "string" };
        if (title != null) schema["title"] = title;
        if (description != null) schema["description"] = description;

        if (choices.Any(c => c.Title != null))
        {
            var oneOf = new JsonArray();
            foreach (var c in choices)
            {
                var entry = new JsonObject { ["const"] = c.Value };
                if (c.Title != null) entry["title"] = c.Title;
                oneOf.Add(entry);
            }
            schema["oneOf"] = oneOf;
        }
        else
        {
            var en = new JsonArray();
            foreach (var c in choices) en.Add(c.Value);
            schema["enum"] = en;
        }

        if (@default != null) schema["default"] = @default;
        return Add(name, schema, required);
    }

    // -- Multi-select enum ----------------------------------------

    /// <summary>
    /// Add a multi-select array. Emits <c>items.enum</c> when no choice carries a
    /// title and <c>items.anyOf</c> of <c>{ const, title }</c> when any
    /// does.
    /// </summary>
    public FormSchemaBuilder EnumMulti(string name, IReadOnlyList<EnumChoice> choices,
        int? minItems = null, int? maxItems = null,
        IReadOnlyList<string>? @default = null, bool required = false,
        string? title = null, string? description = null)
    {
        GuardSensitive(name, false);
        if (choices.Count == 0)
            throw new ArgumentException($"Enum '{name}' must have at least one choice.", nameof(choices));

        JsonObject items;
        if (choices.Any(c => c.Title != null))
        {
            var anyOf = new JsonArray();
            foreach (var c in choices)
            {
                var entry = new JsonObject { ["const"] = c.Value };
                if (c.Title != null) entry["title"] = c.Title;
                anyOf.Add(entry);
            }
            items = new JsonObject { ["anyOf"] = anyOf };
        }
        else
        {
            var en = new JsonArray();
            foreach (var c in choices) en.Add(c.Value);
            items = new JsonObject { ["type"] = "string", ["enum"] = en };
        }

        var schema = new JsonObject { ["type"] = "array", ["items"] = items };
        if (title != null) schema["title"] = title;
        if (description != null) schema["description"] = description;
        if (minItems != null) schema["minItems"] = minItems;
        if (maxItems != null) schema["maxItems"] = maxItems;
        if (@default != null)
        {
            var def = new JsonArray();
            foreach (var d in @default) def.Add(d);
            schema["default"] = def;
        }

        return Add(name, schema, required);
    }

    // -- Low-level field add --------------------------------------------------

    /// <summary>
    /// Add a pre-built field schema. The schema MUST describe a primitive,
    /// enum, or array-of-primitive property — nesting is rejected by
    /// <see cref="Build"/>. Sensitive field names are refused.
    /// </summary>
    public FormSchemaBuilder AddField(string name, JsonObject fieldSchema, bool required = false)
    {
        GuardSensitive(name, false);
        return Add(name, fieldSchema, required);
    }

    private FormSchemaBuilder Add(string name, JsonObject schema, bool required)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Field name must be non-empty.", nameof(name));
        if (_fields.Any(f => f.Name == name))
            throw new ArgumentException($"Duplicate field name '{name}'.", nameof(name));
        _fields.Add((name, schema, required));
        return this;
    }

    private static void GuardSensitive(string name, bool sensitive)
    {
        if (sensitive || IsSensitiveFieldName(name))
            throw new InvalidOperationException(
                $"Field '{name}' is sensitive and must not be solicited via form mode; " +
                "use URL mode for credentials, tokens, or payment data.");
    }

    // -- Build ----------------------------------------------------

    /// <summary>
    /// Emit the flat-object <c>requestedSchema</c>. Throws if any field
    /// is not a flat primitive/enum/array-of-primitive property.
    /// </summary>
    public JsonObject Build()
    {
        if (_fields.Count == 0)
            throw new InvalidOperationException("Form schema must have at least one field.");

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, schema, isRequired) in _fields)
        {
            ValidateFlat(name, schema);
            // Clone so the same field schema cannot be parented twice.
            properties[name] = JsonNode.Parse(schema.ToJsonString());
            if (isRequired) required.Add(name);
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static readonly HashSet<string> PrimitiveTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean" };

    private static void ValidateFlat(string name, JsonObject schema)
    {
        var type = schema["type"]?.GetValue<string>();

        // Single-select enum-with-titles is a string + oneOf — primitive.
        if (type == "string" || PrimitiveTypes.Contains(type ?? ""))
            return;

        if (type == "array")
        {
            var items = schema["items"] as JsonObject
                ?? throw new InvalidOperationException(
                    $"Field '{name}': array must declare primitive 'items'.");
            var itemType = items["type"]?.GetValue<string>();
            // items.enum (primitive) or items.anyOf of const/title — both flat.
            if (items.ContainsKey("anyOf") || (itemType != null && PrimitiveTypes.Contains(itemType)))
                return;
            throw new InvalidOperationException(
                $"Field '{name}': array items must be primitive or const choices.");
        }

        throw new InvalidOperationException(
            $"Field '{name}': nested objects are not allowed in form mode.");
    }

    internal static string FormatToString(StringFormat format) => format switch
    {
        StringFormat.Email => "email",
        StringFormat.Uri => "uri",
        StringFormat.Date => "date",
        StringFormat.DateTime => "date-time",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// A human-readable format hint used as the field <c>description</c> when the
    /// caller supplies none, so clients that render a plain text box (no date picker)
    /// still show the user what format to type. Returns null for formats whose shape
    /// is self-evident (email/uri).
    /// </summary>
    internal static string? DefaultFormatHint(StringFormat format) => format switch
    {
        StringFormat.Date => "Format: YYYY-MM-DD",
        StringFormat.DateTime => "Format: YYYY-MM-DDTHH:MM (ISO 8601)",
        _ => null,
    };
}
