// Copyright (c) MsBuildMcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Policy;

namespace MsBuildMcp.Policy;

/// <summary>
/// Generates elicitation options for msbuild-mcp build approvals.
/// Uses a two-prompt flow: Prompt 1 selects scope, Prompt 2 selects persistence.
/// cancel_build uses a single prompt (inherent session persistence).
/// </summary>
public sealed class MsBuildOptionGenerator : IOptionGenerator
{
    private const string DefaultConfiguration = "Debug";

    public List<ElicitationOption> Generate(
        string toolName, JsonObject args, PolicyEvaluation evaluation)
    {
        var options = new List<ElicitationOption>();

        if (toolName == "build")
            GenerateBuildOptions(options, args);
        else if (toolName == "cancel_build")
            GenerateCancelBuildOptions(options, args);

        return options;
    }

    private static void GenerateBuildOptions(List<ElicitationOption> options, JsonObject args)
    {
        var slnPath = args["sln_path"]?.GetValue<string>();
        var targets = args["targets"]?.GetValue<string>();
        var config = args["configuration"]?.GetValue<string>() ?? DefaultConfiguration;
        var isNonDefaultConfig = !string.Equals(config, DefaultConfiguration,
            StringComparison.OrdinalIgnoreCase);

        var slnName = slnPath != null ? Path.GetFileName(slnPath) : null;
        var normalizedSln = slnPath != null ? MsBuildRuleMatcher.NormalizePath(slnPath) : null;

        // Always starts with "Allow once" (terminal — no Prompt 2).
        options.Add(new()
        {
            Label = "Allow once",
            Persistence = ApprovalPersistence.Once,
            Polarity = ApprovalPolarity.Allow,
        });

        // Target-scoped options (only when targets specified).
        if (!string.IsNullOrEmpty(targets) && normalizedSln != null)
        {
            var targetDisplay = FormatTargets(targets);
            var targetList = MsBuildRuleMatcher.ParseTargetsString(targets).ToList();

            if (isNonDefaultConfig)
            {
                // Config-specific target-scoped: "Allow Release builds of bpf2c on X"
                options.Add(new()
                {
                    Label = $"Allow {config} builds of {targetDisplay} on {slnName}",
                    Polarity = ApprovalPolarity.Allow,
                    Rule = MsBuildRuleMatcher.BuildRule(normalizedSln, targetList, config),
                });
            }

            // Any-config target-scoped: "Allow builds of bpf2c on X"
            options.Add(new()
            {
                Label = $"Allow builds of {targetDisplay} on {slnName}",
                Polarity = ApprovalPolarity.Allow,
                Rule = MsBuildRuleMatcher.BuildRule(normalizedSln, targetList),
            });
        }

        // Solution-scoped options.
        if (normalizedSln != null)
        {
            if (isNonDefaultConfig)
            {
                // Config-specific solution-scoped: "Allow Release builds on X"
                options.Add(new()
                {
                    Label = $"Allow {config} builds on {slnName}",
                    Polarity = ApprovalPolarity.Allow,
                    Rule = MsBuildRuleMatcher.BuildRule(normalizedSln, configuration: config),
                });
            }

            // Any-config solution-scoped: "Allow all builds on X"
            options.Add(new()
            {
                Label = $"Allow all builds on {slnName}",
                Polarity = ApprovalPolarity.Allow,
                Rule = MsBuildRuleMatcher.BuildRule(normalizedSln),
            });
        }

        // Global all builds: "Allow all builds"
        options.Add(new()
        {
            Label = "Allow all builds",
            Polarity = ApprovalPolarity.Allow,
            Rule = new ApprovalRule { Tools = ["build"] },
        });

        // Always ends with "Deny" (terminal — no Prompt 2).
        options.Add(new()
        {
            Label = "Deny",
            Persistence = ApprovalPersistence.Once,
            Polarity = ApprovalPolarity.Deny,
        });
    }

    private static void GenerateCancelBuildOptions(List<ElicitationOption> options, JsonObject args)
    {
        options.Add(new()
        {
            Label = "Allow once",
            Persistence = ApprovalPersistence.Once,
            Polarity = ApprovalPolarity.Allow,
        });

        // Solution-scoped option when sln_path is available (injected by argsEnricher).
        var slnPath = args["sln_path"]?.GetValue<string>();
        if (slnPath != null)
        {
            var slnName = Path.GetFileName(slnPath);
            var normalized = MsBuildRuleMatcher.NormalizePath(slnPath);
            options.Add(new()
            {
                Label = $"Allow cancel_build on {slnName}",
                Polarity = ApprovalPolarity.Allow,
                Rule = new ApprovalRule
                {
                    Tools = ["cancel_build"],
                    Constraints = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["sln_path"] = System.Text.Json.JsonSerializer.SerializeToElement(normalized),
                    },
                },
            });
        }

        options.Add(new()
        {
            Label = "Allow all cancel_build",
            Polarity = ApprovalPolarity.Allow,
            Rule = new ApprovalRule { Tools = ["cancel_build"] },
        });

        options.Add(new()
        {
            Label = "Deny",
            Persistence = ApprovalPersistence.Once,
            Polarity = ApprovalPolarity.Deny,
        });
    }

    /// <summary>Format targets for display: last path component, max 3 shown.</summary>
    internal static string FormatTargets(string targets)
    {
        var parts = targets.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = parts.Select(p => p.Split('\\', '/').Last()).ToArray();
        if (names.Length <= 3)
            return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(3))}, \u2026";
    }
}
