// Copyright (c) MsBuildMcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Policy;

namespace MsBuildMcp.Policy;

/// <summary>
/// Tool classifier for msbuild-mcp. Only build/cancel_build require confirmation.
/// </summary>
public sealed class MsBuildToolClassifier : IToolClassifier
{
    public PolicyDecision Classify(string toolName, JsonObject args)
    {
        return toolName switch
        {
            "build" => PolicyDecision.Confirm,
            "cancel_build" => PolicyDecision.Confirm,
            _ => PolicyDecision.Allow,
        };
    }
}
