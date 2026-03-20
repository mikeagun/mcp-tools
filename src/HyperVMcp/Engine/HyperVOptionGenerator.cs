// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Policy;
using McpOption = McpSharp.Policy.ElicitationOption;
using McpRule = McpSharp.Policy.ApprovalRule;
using McpEvaluation = McpSharp.Policy.PolicyEvaluation;

namespace HyperVMcp.Engine;

/// <summary>
/// Generates elicitation options for blocked tool calls using McpSharp shared types.
/// Generates elicitation options for blocked tool calls using McpSharp shared types.
/// </summary>
public sealed class HyperVOptionGenerator : IOptionGenerator
{
    private static readonly List<string> PowerCycleTools = ["start_vm", "stop_vm", "restart_vm"];
    private static readonly List<string> CopyTools = ["copy_to_vm", "copy_from_vm"];

    private readonly HyperVToolClassifier _classifier;

    public HyperVOptionGenerator(HyperVToolClassifier classifier)
    {
        _classifier = classifier;
    }

    public List<McpOption> Generate(
        string toolName, JsonObject args, McpEvaluation evaluation)
    {
        var context = _classifier.ExtractContext(toolName, args);
        var trigger = evaluation.GetTrigger() ?? ConfirmTrigger.ToolRiskTier;
        var riskTier = evaluation.GetRiskTier();

        var options = new List<McpOption>
        {
            new()
            {
                Label = "Allow once",
                Persistence = McpSharp.Policy.ApprovalPersistence.Once,
                Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
            },
        };

        switch (trigger)
        {
            case ConfirmTrigger.ToolRiskTier:
            case ConfirmTrigger.ToolOverride:
                AddVmToolOptions(options, toolName, context);
                break;

            case ConfirmTrigger.CommandPattern:
                AddCommandOptions(options, toolName, context);
                break;

            case ConfirmTrigger.HostPath:
                AddHostPathOptions(options, toolName, context);
                break;

            case ConfirmTrigger.BulkVmCount:
                // Bulk: no auto-approve options, just allow/deny once.
                break;
        }

        // "Allow everything on VM" — session only, available when VM context exists (skip for bulk operations).
        if (trigger != ConfirmTrigger.BulkVmCount && context.VmNames is { Count: > 0 })
        {
            var vmName = context.VmNames[0];
            options.Add(new()
            {
                Label = $"Allow everything on {vmName} (this session)",
                Persistence = McpSharp.Policy.ApprovalPersistence.Session,
                Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
                Rule = HyperVRuleMatcher.BuildVmRule(["*"], vmName),
            });
        }

        // Deny once — always present.
        options.Add(new()
        {
            Label = "Deny once",
            Persistence = McpSharp.Policy.ApprovalPersistence.Once,
            Polarity = McpSharp.Policy.ApprovalPolarity.Deny,
        });

        // Destructive tools with VM context: session deny + permanent deny.
        if (riskTier == RiskTier.Destructive && context.VmNames is { Count: > 0 })
        {
            var vmName = context.VmNames[0];
            options.Add(new()
            {
                Label = $"Deny {toolName} on {vmName} (this session)",
                Persistence = McpSharp.Policy.ApprovalPersistence.Session,
                Polarity = McpSharp.Policy.ApprovalPolarity.Deny,
                Rule = HyperVRuleMatcher.BuildVmRule([toolName], vmName),
            });
            options.Add(new()
            {
                Label = $"Always deny {toolName} on {vmName} (permanently)",
                Persistence = McpSharp.Policy.ApprovalPersistence.Permanent,
                Polarity = McpSharp.Policy.ApprovalPolarity.Deny,
                Rule = HyperVRuleMatcher.BuildVmRule([toolName], vmName),
            });
        }

        return options;
    }

    // -- VM lifecycle / tool-tier options ---------------------------------

    private static void AddVmToolOptions(
        List<McpOption> options,
        string toolName,
        ToolCallContext context)
    {
        var vmNames = context.VmNames;
        if (vmNames == null || vmNames.Count == 0)
            return; // No VM context — only Allow once / Deny once.

        var vmName = vmNames[0];

        // Exact tool + exact VM: session + permanent.
        AddSessionPermanentPair(options, $"Allow {toolName} on {vmName}",
            HyperVRuleMatcher.BuildVmRule([toolName], vmName));

        // Power-cycle group: permanent only (session covered by exact tool above).
        if (PowerCycleTools.Contains(toolName))
        {
            options.Add(new()
            {
                Label = $"Allow start/stop/restart on {vmName} (permanently)",
                Persistence = McpSharp.Policy.ApprovalPersistence.Permanent,
                Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
                Rule = HyperVRuleMatcher.BuildVmRule(PowerCycleTools.ToList(), vmName),
            });
        }
    }

    // -- Command options -------------------------------------------------

    private static void AddCommandOptions(
        List<McpOption> options,
        string toolName,
        ToolCallContext context)
    {
        if (context.Command == null) return;

        var analysis = CommandAnalyzer.Analyze(context.Command);
        if (analysis.CommandNames.Count == 0) return;

        var hasVm = context.VmNames is { Count: > 0 };
        var vmName = hasVm ? context.VmNames![0] : null;

        if (analysis.CommandNames.Count == 1)
        {
            var cmdName = analysis.CommandNames[0];

            // Command + first-arg (most specific).
            if (analysis.FirstArgument != null)
            {
                var prefix = $"{cmdName} {analysis.FirstArgument}";
                if (hasVm)
                {
                    AddSessionPermanentPair(options,
                        $"Allow \"{prefix}\" commands on {vmName}",
                        HyperVRuleMatcher.BuildCommandRule(["*"], vmName!, [prefix]));
                }
                else
                {
                    AddSessionOnly(options, $"Allow \"{prefix}\" commands",
                        HyperVRuleMatcher.BuildCommandRule(null, null, [prefix]));
                }
            }

            // Command name only.
            if (hasVm)
            {
                AddSessionPermanentPair(options,
                    $"Allow \"{cmdName}\" commands on {vmName}",
                    HyperVRuleMatcher.BuildCommandRule(["*"], vmName!, [cmdName]));
            }
            else
            {
                AddSessionOnly(options, $"Allow \"{cmdName}\" commands",
                    HyperVRuleMatcher.BuildCommandRule(null, null, [cmdName]));
            }
        }
        else
        {
            // Multi-statement: combined + first command alone.
            var names = analysis.CommandNames;
            var combinedLabel = "Allow \"" + string.Join("\" and \"", names) + "\" commands";

            if (hasVm)
            {
                AddSessionPermanentPair(options,
                    $"{combinedLabel} on {vmName}",
                    HyperVRuleMatcher.BuildCommandRule(["*"], vmName!, names.ToList()));
                AddSessionPermanentPair(options,
                    $"Allow \"{names[0]}\" commands on {vmName}",
                    HyperVRuleMatcher.BuildCommandRule(["*"], vmName!, [names[0]]));
            }
            else
            {
                AddSessionOnly(options, combinedLabel,
                    HyperVRuleMatcher.BuildCommandRule(null, null, names.ToList()));
                AddSessionOnly(options, $"Allow \"{names[0]}\" commands",
                    HyperVRuleMatcher.BuildCommandRule(null, null, [names[0]]));
            }
        }
    }

    // -- Host path / file transfer options --------------------------------

    private static void AddHostPathOptions(
        List<McpOption> options,
        string toolName,
        ToolCallContext context)
    {
        // Path-scoped option.
        if (context.HostPath != null)
        {
            var directories = ExtractParentDirectories(context.HostPath);
            if (directories.Count > 0)
            {
                var dir = directories[0];
                var globPath = dir.TrimEnd('\\') + "\\**";
                var verb = toolName == "copy_to_vm" ? "from" : "to";
                AddSessionPermanentPair(options, $"Allow {toolName} {verb} {dir}",
                    HyperVRuleMatcher.BuildHostPathRule([toolName], null, [globPath]));
            }
        }

        // Directional tool + VM (e.g., "Allow copy_to_vm on test-vm").
        if (context.VmNames is { Count: > 0 })
        {
            var vmName = context.VmNames[0];
            AddSessionPermanentPair(options, $"Allow {toolName} on {vmName}",
                HyperVRuleMatcher.BuildVmRule([toolName], vmName));
        }
    }

    // -- Helpers ----------------------------------------------------------

    private static void AddSessionPermanentPair(
        List<McpOption> options, string baseLabel, McpRule rule)
    {
        options.Add(new()
        {
            Label = $"{baseLabel} (this session)",
            Persistence = McpSharp.Policy.ApprovalPersistence.Session,
            Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
            Rule = rule,
        });
        options.Add(new()
        {
            Label = $"{baseLabel} (permanently)",
            Persistence = McpSharp.Policy.ApprovalPersistence.Permanent,
            Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
            Rule = rule,
        });
    }

    private static void AddSessionOnly(
        List<McpOption> options, string label, McpRule rule)
    {
        options.Add(new()
        {
            Label = $"{label} (this session)",
            Persistence = McpSharp.Policy.ApprovalPersistence.Session,
            Polarity = McpSharp.Policy.ApprovalPolarity.Allow,
            Rule = rule,
        });
    }

    /// <summary>
    /// Extract parent directory paths, most-specific first. Stops at drive root.
    /// </summary>
    public static IReadOnlyList<string> ExtractParentDirectories(string path)
    {
        var directories = new List<string>();
        var dir = Path.GetDirectoryName(path);

        while (!string.IsNullOrEmpty(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            directories.Add(dir.EndsWith('\\') ? dir : dir + "\\");
            dir = parent;
        }

        return directories;
    }
}
