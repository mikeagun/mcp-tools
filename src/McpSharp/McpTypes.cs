// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp;

/// <summary>
/// Metadata and handler for a single MCP tool.
/// </summary>
public sealed class ToolInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public required Func<JsonObject, JsonNode?> Handler { get; set; }
}

/// <summary>
/// Metadata for a browsable MCP resource.
/// </summary>
public sealed class ResourceInfo
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string MimeType { get; init; } = "application/json";
    public required Func<JsonNode?> Reader { get; set; }
}

/// <summary>
/// Metadata for an MCP prompt template.
/// </summary>
public sealed class PromptInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public List<PromptArgument> Arguments { get; init; } = [];
    public required Func<JsonObject, JsonArray> Handler { get; set; }
}

public sealed class PromptArgument
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// Result of an elicitation request sent from the server to the client.
/// The client prompts the user and returns their response.
/// </summary>
public sealed class ElicitationResult
{
    public required ElicitationAction Action { get; init; }

    /// <summary>
    /// User-provided data matching the requested schema. Only present when Action is Accept.
    /// </summary>
    public JsonObject? Content { get; init; }
}

/// <summary>
/// Elicitation delivery mode (MCP 2025-11-25). Form mode collects structured
/// input via a requested schema; URL mode directs the user to an out-of-band URL.
/// </summary>
public enum ElicitationMode
{
    /// <summary>Form mode — structured input matching a requested schema.</summary>
    Form,
    /// <summary>URL mode — out-of-band interaction at a server-provided URL.</summary>
    Url,
}

/// <summary>
/// Parsed client elicitation capability. Distinguishes which elicitation modes
/// the client advertised at initialization so the server can gate features and
/// never send a mode the client did not declare.
/// </summary>
public sealed class ElicitationCapabilities
{
    /// <summary>The client advertised the <c>elicitation</c> capability.</summary>
    public bool Supported { get; init; }

    /// <summary>The client supports form mode.</summary>
    public bool Form { get; init; }

    /// <summary>The client supports URL mode.</summary>
    public bool Url { get; init; }

    /// <summary>No elicitation capability — the client did not advertise it.</summary>
    public static readonly ElicitationCapabilities None = new();

    /// <summary>Whether the given mode was advertised by the client.</summary>
    public bool Supports(ElicitationMode mode) => mode switch
    {
        ElicitationMode.Form => Form,
        ElicitationMode.Url => Url,
        _ => false,
    };

    /// <summary>
    /// Parse the client's <c>capabilities.elicitation</c> node.
    /// Absent ⇒ unsupported. Present with no <c>form</c>/<c>url</c> keys
    /// (e.g. an empty object) ⇒ form mode only, for backwards compatibility.
    /// Present with explicit keys ⇒ honor exactly those modes.
    /// </summary>
    public static ElicitationCapabilities Parse(JsonNode? elicitationCap)
    {
        if (elicitationCap is null)
            return None;

        var obj = elicitationCap as JsonObject;
        bool hasForm = obj is not null && obj.ContainsKey("form");
        bool hasUrl = obj is not null && obj.ContainsKey("url");

        // Capability present but no explicit modes ⇒ form mode only.
        if (!hasForm && !hasUrl)
            return new ElicitationCapabilities { Supported = true, Form = true, Url = false };

        return new ElicitationCapabilities { Supported = true, Form = hasForm, Url = hasUrl };
    }
}

/// <summary>
/// User response action for an elicitation request.
/// </summary>
public enum ElicitationAction
{
    /// <summary>User approved and submitted data.</summary>
    Accept,
    /// <summary>User explicitly declined the request.</summary>
    Decline,
    /// <summary>User dismissed without choosing (e.g. pressed Escape).</summary>
    Cancel,
    /// <summary>Server-side timeout — user didn't respond in time.</summary>
    Timeout,
}

/// <summary>
/// Thrown when authentication fails and requires human intervention.
/// Caught by McpServer to produce a structured error that tells the agent to STOP,
/// or to prompt the user via elicitation if supported.
/// </summary>
public class AuthenticationException : Exception
{
    /// <summary>The provider that failed (e.g., "GitHub", "ADO").</summary>
    public string Provider { get; }

    /// <summary>Human-readable remediation steps.</summary>
    public string Remediation { get; }

    /// <summary>
    /// Optional callback to reset cached auth state before a retry attempt.
    /// Called by McpServer when the user chooses to retry after re-authenticating.
    /// </summary>
    public Action? ResetAuth { get; init; }

    public AuthenticationException(string provider, string message, string remediation)
        : base(message)
    {
        Provider = provider;
        Remediation = remediation;
    }

    public AuthenticationException(string provider, string message, string remediation, Exception innerException)
        : base(message, innerException)
    {
        Provider = provider;
        Remediation = remediation;
    }
}
