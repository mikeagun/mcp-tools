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
