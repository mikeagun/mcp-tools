// Copyright (c) MsBuildMcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using McpSharp.Policy;

namespace MsBuildMcp.Policy;

/// <summary>
/// Policy configuration for msbuild-mcp. Extends PolicyConfig with build-specific constraints.
/// </summary>
public sealed class MsBuildPolicyConfig : PolicyConfig
{
    [JsonPropertyName("build_constraints")]
    public BuildConstraints? BuildConstraints { get; set; }
}

/// <summary>
/// Hard constraints on build operations. Violations fail immediately with an actionable
/// error — no elicitation prompt is shown. Use for structural limits that should never
/// be overridden at runtime.
/// </summary>
public sealed class BuildConstraints
{
    /// <summary>
    /// If true, all build calls must specify targets. Prevents full-solution builds.
    /// </summary>
    [JsonPropertyName("require_targets")]
    public bool RequireTargets { get; set; }

    /// <summary>
    /// If set, only these configurations are allowed (case-insensitive).
    /// Builds with other configurations fail immediately.
    /// </summary>
    [JsonPropertyName("allowed_configurations")]
    public List<string>? AllowedConfigurations { get; set; }

    /// <summary>
    /// If false, builds with restore=true are rejected.
    /// </summary>
    [JsonPropertyName("allow_restore")]
    public bool AllowRestore { get; set; } = true;
}
