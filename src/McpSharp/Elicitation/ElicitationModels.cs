// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp.Elicitation;

/// <summary>The logical kind of a desired form field.</summary>
public enum FieldKind
{
    /// <summary>String input.</summary>
    String,
    /// <summary>Number input.</summary>
    Number,
    /// <summary>Integer input.</summary>
    Integer,
    /// <summary>Boolean input.</summary>
    Boolean,
    /// <summary>Single-select enum.</summary>
    EnumSingle,
    /// <summary>Multi-select array.</summary>
    EnumMulti,
}

/// <summary>
/// A single logical form field the server wants to collect, independent of how
/// it is finally rendered on the wire. The <see cref="ElicitationPlanner"/> rewrites
/// it into the richest construct the client supports plus a value-remap.
/// </summary>
public sealed class DesiredField
{
    /// <summary>Property name in the response content.</summary>
    public required string Name { get; init; }

    /// <summary>The logical kind of input.</summary>
    public required FieldKind Kind { get; init; }

    /// <summary>Whether the field is required.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// Whether this is the decision/approval field. A decision field is NEVER
    /// defaulted or fabricated to an allow — an omitted decision stays a deny.
    /// <see cref="Default"/> is ignored for decision fields.
    /// </summary>
    public bool IsDecision { get; init; }

    /// <summary>Optional display title.</summary>
    public string? Title { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    // String constraints.
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public string? Pattern { get; init; }
    public StringFormat? Format { get; init; }

    // Numeric constraints.
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }

    // Enum choices.
    public IReadOnlyList<EnumChoice>? Choices { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }

    /// <summary>
    /// Optional default value. Applied only to non-decision fields when
    /// the client omits the field (F4). Ignored when <see cref="IsDecision"/> is true.
    /// </summary>
    public JsonNode? Default { get; init; }
}

/// <summary>
/// Client form-feature support used to drive deterministic degradations. The MCP
/// capability negotiation only exposes form/url <em>modes</em>; the
/// finer-grained feature flags here are server assumptions (default: full support)
/// that callers — notably the conformance harness — can reduce to exercise each
/// fallback rung deterministically without a live client.
/// </summary>
public sealed class ClientFormSupport
{
    /// <summary>Advertised elicitation modes.</summary>
    public ElicitationCapabilities Modes { get; init; } = ElicitationCapabilities.None;

    /// <summary>Client renders <c>oneOf</c>/<c>anyOf</c> titles. Drives F1.</summary>
    public bool Titles { get; init; } = true;

    /// <summary>Client supports multi-select arrays. Drives F2.</summary>
    public bool Arrays { get; init; } = true;

    /// <summary>Client supports native numeric input. Drives F6.</summary>
    public bool Numbers { get; init; } = true;

    /// <summary>Client supports native boolean input. Drives F5.</summary>
    public bool Booleans { get; init; } = true;

    /// <summary>Full form support over the given advertised modes.</summary>
    public static ClientFormSupport Full(ElicitationCapabilities modes) => new() { Modes = modes };
}

/// <summary>
/// The result of planning one <see cref="DesiredField"/>: the wire property schema
/// to emit, a remap from the client's returned value back to the caller's logical
/// value, and which fallback rung (if any) was applied.
/// </summary>
/// <param name="Schema">The wire property schema for this field.</param>
/// <param name="Remap">Maps the client's returned value back to the logical value.</param>
/// <param name="FallbackUsed">The fallback rung applied: "none", "F1", "F5", "F6".</param>
public sealed record PlannedField(JsonObject Schema, Func<JsonNode?, JsonNode?> Remap, string FallbackUsed);
