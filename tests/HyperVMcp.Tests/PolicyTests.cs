// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using HyperVMcp.Tools;
using McpSharp;
using McpSharp.Policy;
using Xunit;

namespace HyperVMcp.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Tool Classifier Tests
// ═══════════════════════════════════════════════════════════════════════

public class ToolClassifierTests
{
    [Theory]
    [InlineData("list_vms", RiskTier.ReadOnly)]
    [InlineData("get_services", RiskTier.ReadOnly)]
    [InlineData("connect_vm", RiskTier.Moderate)]
    [InlineData("disconnect_vm", RiskTier.Session)]
    [InlineData("set_env", RiskTier.Session)]
    [InlineData("invoke_command", RiskTier.Moderate)]
    [InlineData("copy_to_vm", RiskTier.Moderate)]
    [InlineData("stop_vm", RiskTier.Destructive)]
    [InlineData("kill_process", RiskTier.Destructive)]
    [InlineData("restore_vm", RiskTier.Destructive)]
    public void GetRiskTier_KnownTools(string toolName, RiskTier expected)
    {
        Assert.Equal(expected, ToolClassifier.GetRiskTier(toolName));
    }

    [Fact]
    public void GetRiskTier_UnknownTool_ReturnsDestructive()
    {
        Assert.Equal(RiskTier.Destructive, ToolClassifier.GetRiskTier("unknown_tool"));
    }

    [Fact]
    public void GetCategory_VmLifecycle()
    {
        Assert.Equal("vm_lifecycle", ToolClassifier.GetCategory("stop_vm"));
        Assert.Equal("vm_lifecycle", ToolClassifier.GetCategory("start_vm"));
    }

    [Fact]
    public void GetToolsInCategory_VmLifecycle()
    {
        var tools = ToolClassifier.GetToolsInCategory("vm_lifecycle");
        Assert.Contains("stop_vm", tools);
        Assert.Contains("start_vm", tools);
        Assert.Contains("restart_vm", tools);
        Assert.Contains("restore_vm", tools);
        Assert.Contains("checkpoint_vm", tools);
        Assert.DoesNotContain("invoke_command", tools);
    }

    [Theory]
    [InlineData(RiskTier.ReadOnly, PolicyMode.Standard, McpSharp.Policy.PolicyDecision.Allow)]
    [InlineData(RiskTier.Session, PolicyMode.Standard, McpSharp.Policy.PolicyDecision.Allow)]
    [InlineData(RiskTier.Moderate, PolicyMode.Standard, McpSharp.Policy.PolicyDecision.Confirm)]
    [InlineData(RiskTier.Destructive, PolicyMode.Standard, McpSharp.Policy.PolicyDecision.Confirm)]
    [InlineData(RiskTier.ReadOnly, PolicyMode.Restricted, McpSharp.Policy.PolicyDecision.Allow)]
    [InlineData(RiskTier.Moderate, PolicyMode.Restricted, McpSharp.Policy.PolicyDecision.Confirm)]
    [InlineData(RiskTier.Destructive, PolicyMode.Lockdown, McpSharp.Policy.PolicyDecision.Deny)]
    [InlineData(RiskTier.Destructive, PolicyMode.Unrestricted, McpSharp.Policy.PolicyDecision.Allow)]
    public void DefaultDecision_ByTierAndMode(RiskTier tier, PolicyMode mode, McpSharp.Policy.PolicyDecision expected)
    {
        Assert.Equal(expected, ToolClassifier.DefaultDecision(tier, mode));
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Command Analyzer Tests (PowerShell parser-based)
// ═══════════════════════════════════════════════════════════════════════

public class CommandAnalyzerTests
{
    [Fact]
    public void SimpleCommand_SingleName()
    {
        var result = CommandAnalyzer.Analyze("Get-Process");
        Assert.Single(result.CommandNames);
        Assert.Equal("Get-Process", result.CommandNames[0]);
        Assert.Equal(1, result.StatementCount);
        Assert.False(result.HasPipeline);
    }

    [Fact]
    public void CommandWithArgs_ExtractsFirstArg()
    {
        var result = CommandAnalyzer.Analyze(@"Remove-Item C:\logs -Recurse -Force");
        Assert.Single(result.CommandNames);
        Assert.Equal("Remove-Item", result.CommandNames[0]);
        Assert.Equal(@"C:\logs", result.FirstArgument);
    }

    [Fact]
    public void CommandWithFlagBeforeArg_SkipsFlag()
    {
        var result = CommandAnalyzer.Analyze("Stop-Service -Name eBPFSvc");
        Assert.Equal("Stop-Service", result.CommandNames[0]);
        // -Name is a parameter, eBPFSvc is its value (skipped), so no firstArg
    }

    [Fact]
    public void ChainedCommands_DetectsMultipleStatements()
    {
        var result = CommandAnalyzer.Analyze("Remove-Item C:\\logs; Start-Process notepad");
        Assert.Equal(2, result.CommandNames.Count);
        Assert.Contains("Remove-Item", result.CommandNames);
        Assert.Contains("Start-Process", result.CommandNames);
        Assert.Equal(2, result.StatementCount);
    }

    [Fact]
    public void PipelineCommand_DetectsPipeline()
    {
        var result = CommandAnalyzer.Analyze("Get-Process | Stop-Process");
        Assert.Equal(2, result.CommandNames.Count);
        Assert.Contains("Get-Process", result.CommandNames);
        Assert.Contains("Stop-Process", result.CommandNames);
        Assert.True(result.HasPipeline);
    }

    [Fact]
    public void QuotedSemicolon_NotTreatedAsChaining()
    {
        var result = CommandAnalyzer.Analyze("Remove-Item 'file;name.txt'");
        Assert.Equal(1, result.StatementCount);
        Assert.Single(result.CommandNames);
        Assert.Equal("Remove-Item", result.CommandNames[0]);
    }

    [Fact]
    public void AllCommandsApproved_SimpleMatch()
    {
        Assert.True(CommandAnalyzer.AllCommandsApproved(
            "Remove-Item C:\\logs", ["Remove-Item"]));
    }

    [Fact]
    public void AllCommandsApproved_RejectsChainWithUnapprovedCommand()
    {
        // Remove-Item is approved, but Start-Process is not.
        Assert.False(CommandAnalyzer.AllCommandsApproved(
            "Remove-Item foo; Start-Process bad.exe", ["Remove-Item"]));
    }

    [Fact]
    public void AllCommandsApproved_AcceptsChainWhenAllApproved()
    {
        Assert.True(CommandAnalyzer.AllCommandsApproved(
            "Remove-Item foo; Stop-Service svc",
            ["Remove-Item", "Stop-Service"]));
    }

    [Fact]
    public void AllCommandsApproved_RejectsPipeWithUnapproved()
    {
        Assert.False(CommandAnalyzer.AllCommandsApproved(
            "Get-Process | Stop-Process", ["Get-Process"]));
    }

    [Fact]
    public void AllCommandsApproved_CaseInsensitive()
    {
        Assert.True(CommandAnalyzer.AllCommandsApproved(
            "remove-item C:\\logs", ["Remove-Item"]));
    }

    [Fact]
    public void EmptyCommand_NoNames()
    {
        var result = CommandAnalyzer.Analyze("");
        Assert.Empty(result.CommandNames);
    }

    [Fact]
    public void TypeMethodInvocation_ExtractsTypeName()
    {
        var result = CommandAnalyzer.Analyze("[math]::Round((Get-PSDrive C).Free / 1GB, 2)");
        Assert.Contains("[math]::Round", result.CommandNames);
    }

    [Fact]
    public void TypeMethodInvocation_DateTimeNow()
    {
        var result = CommandAnalyzer.Analyze("[datetime]::Now.ToString('yyyy-MM-dd')");
        Assert.Contains("[datetime]::Now", result.CommandNames);
    }

    [Fact]
    public void TypeMethodInvocation_VariableAssignment()
    {
        // $x = [math]::Round(5.5) — starts with $, not a type invocation at top level.
        // The assignment wraps the expression but [math]::Round should still be found.
        var result = CommandAnalyzer.Analyze("$x = [math]::Round(5.5)");
        Assert.Contains("[math]::Round", result.CommandNames);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Parent Directory Extraction Tests
// ═══════════════════════════════════════════════════════════════════════

public class HostPathTests
{
    [Fact]
    public void ExtractParentDirectories_NestedPath()
    {
        var result = HyperVOptionGenerator.ExtractParentDirectories(
            @"C:\Users\me\Desktop\file.txt");
        Assert.Contains(@"C:\Users\me\Desktop\", result);
        Assert.Contains(@"C:\Users\me\", result);
        Assert.Contains(@"C:\Users\", result);
        Assert.DoesNotContain(@"C:\", result); // Stops at drive root.
    }

    [Fact]
    public void ExtractParentDirectories_ShallowPath()
    {
        var result = HyperVOptionGenerator.ExtractParentDirectories(@"C:\temp\file.txt");
        Assert.Single(result);
        Assert.Equal(@"C:\temp\", result[0]);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════
// Approval Rule Matching Tests
// ═══════════════════════════════════════════════════════════════════════

public class ApprovalRuleTests
{
    private static (PolicyEngine engine, HyperVToolClassifier classifier, HyperVRuleMatcher matcher, HyperVOptionGenerator optionGen) CreatePolicy(HyperVPolicyConfig? config = null)
    {
        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var engine = new PolicyEngine(config, classifier, matcher);
        return (engine, classifier, matcher, optionGen);
    }

    /// <summary>
    /// Full rule matching: checks Tools (like PolicyEngine does) then delegates to matcher.
    /// </summary>
    private static bool RuleMatches(HyperVRuleMatcher matcher, McpSharp.Policy.ApprovalRule rule, string toolName, JsonObject args)
    {
        if (rule.Tools != null && rule.Tools.Count > 0)
            if (!rule.Tools.Contains("*") && !rule.Tools.Contains(toolName))
                return false;
        return matcher.Matches(rule, toolName, args);
    }

    [Fact]
    public void Matches_ToolFilter()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "test-1");
        var args = new JsonObject { ["vm_name"] = "test-1" };

        Assert.True(RuleMatches(matcher, rule, "stop_vm", args));
        Assert.False(RuleMatches(matcher, rule, "start_vm", args));
    }

    [Fact]
    public void Matches_Wildcard_AllTools()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildVmRule(["*"], "test-vm");
        var args = new JsonObject { ["vm_name"] = "test-vm" };
        Assert.True(RuleMatches(matcher, rule, "stop_vm", args));
        Assert.True(RuleMatches(matcher, rule, "anything", args));
    }

    [Fact]
    public void Matches_VmPattern()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Tools = ["stop_vm"],
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_pattern"] = ToJsonElement("test-*"),
            },
        };

        Assert.True(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
        Assert.True(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-web-01" }));
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "prod-db" }));
    }

    [Fact]
    public void Matches_ExactVmNames()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "test-db");

        Assert.True(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-other" }));
    }

    [Fact]
    public void Matches_CommandPrefix()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["git"]);

        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "git commit -m fix" }));
        Assert.False(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item foo" }));
    }

    [Fact]
    public void Matches_CommandPrefix_RejectsChainedCommands()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["Remove-Item"]);

        // Simple command — matches.
        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item C:\\logs -Recurse" }));

        // Chained with semicolon — rejected (Start-Process not approved).
        Assert.False(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item C:\\logs; Start-Process malware.exe" }));

        // Chained with pipe — rejected (Out-Null not approved).
        Assert.False(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item C:\\logs | Out-Null" }));

        // Semicolons inside quotes — allowed (single statement).
        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item 'file;name.txt'" }));
    }

    [Fact]
    public void Matches_CommandPrefix_ChainedAllApproved()
    {
        var (_, _, matcher, _) = CreatePolicy();
        // Both commands are in the approved set — should match.
        var rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["Remove-Item", "Stop-Service"]);
        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item C:\\logs; Stop-Service svc" }));
    }

    [Fact]
    public void Matches_CommandPrefix_CaseInsensitive()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["Remove-Item"]);
        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "remove-item C:\\logs" }));
    }

    [Fact]
    public void Matches_HostPath_Glob()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildHostPathRule(["copy_from_vm"], null, [@"C:\temp\**"]);

        Assert.True(RuleMatches(matcher, rule, "copy_from_vm",
            new JsonObject { ["session_id"] = "s-1", ["source"] = "/tmp/f", ["destination"] = @"C:\temp\output\file.txt" }));
        Assert.False(RuleMatches(matcher, rule, "copy_from_vm",
            new JsonObject { ["session_id"] = "s-1", ["source"] = "/tmp/f", ["destination"] = @"C:\Users\secret.txt" }));
    }

    [Fact]
    public void Matches_BulkVms()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Constraints = new Dictionary<string, JsonElement>
            {
                ["max_bulk_vms"] = ToJsonElement(5),
            },
        };

        Assert.True(RuleMatches(matcher, rule, "stop_vm",
            new JsonObject { ["vm_name"] = new JsonArray("a", "b", "c", "d", "e") }));
        Assert.False(RuleMatches(matcher, rule, "stop_vm",
            new JsonObject { ["vm_name"] = new JsonArray("a", "b", "c", "d", "e", "f") }));
    }

    [Fact]
    public void Matches_CombinedAndSemantics()
    {
        var (_, _, matcher, _) = CreatePolicy();
        // Tools AND VmPattern must both match.
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Tools = ["stop_vm"],
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_pattern"] = ToJsonElement("test-*"),
            },
        };

        Assert.True(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
        Assert.False(RuleMatches(matcher, rule, "start_vm", new JsonObject { ["vm_name"] = "test-db" })); // Wrong tool.
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "prod-db" })); // Wrong VM.
    }

    [Fact]
    public void GlobMatch_StarWithinSegment()
    {
        Assert.True(GlobMatcher.Match("test-*", "test-db"));
        Assert.True(GlobMatcher.Match("test-*", "test-web-01"));
        Assert.False(GlobMatcher.Match("test-*", "prod-db"));
    }

    [Fact]
    public void GlobMatch_DoubleStarAcrossPaths()
    {
        Assert.True(GlobMatcher.Match(@"C:\temp\**", @"C:\temp\sub\deep\file.txt"));
        Assert.False(GlobMatcher.Match(@"C:\temp\**", @"C:\users\file.txt"));
    }

    [Fact]
    public void VmNames_WithoutTools_DoesNotMatch()
    {
        // A rule with VmNames but no Tools should never match — prevents
        // accidentally approving all tools on a VM.
        var (_, _, matcher, _) = CreatePolicy();
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_names"] = ToJsonElement(new[] { "test-db" }),
            },
        };
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
        Assert.False(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "test", ["vm_name"] = "test-db" }));
    }

    [Fact]
    public void VmPattern_WithoutTools_DoesNotMatch()
    {
        var (_, _, matcher, _) = CreatePolicy();
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_pattern"] = ToJsonElement("test-*"),
            },
        };
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
    }

    [Fact]
    public void BuildVmRule_WithoutTools_HasEmptyTools()
    {
        // Building a VM rule without tools is prevented by the API requiring tools parameter.
        // Verify that BuildVmRule always sets tools.
        var rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "test-db");
        Assert.Contains("stop_vm", rule.Tools!);
        Assert.NotNull(rule.Constraints);
        Assert.True(rule.Constraints!.ContainsKey("vm_names"));
    }

    [Fact]
    public void BuildVmRule_VmPatternNotTools_DoesNotMatch()
    {
        // Verify that a rule with vm_pattern but no tools doesn't match.
        var (_, _, matcher, _) = CreatePolicy();
        var rule = new McpSharp.Policy.ApprovalRule
        {
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_pattern"] = ToJsonElement("test-*"),
            },
        };
        Assert.False(RuleMatches(matcher, rule, "stop_vm", new JsonObject { ["vm_name"] = "test-db" }));
    }

    [Fact]
    public void BuildVmRule_VmWithTools_Succeeds()
    {
        var rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "test-db");
        Assert.Contains("stop_vm", rule.Tools!);
        Assert.NotNull(rule.Constraints);
    }

    [Fact]
    public void CommandPrefixes_WithoutTools_StillMatches()
    {
        // Command prefix rules don't need tools — they scope by command content.
        var (_, _, matcher, _) = CreatePolicy();
        var rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["git"]);
        Assert.True(RuleMatches(matcher, rule, "invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "git status" }));
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Policy Evaluation Tests
// ═══════════════════════════════════════════════════════════════════════

public class PolicyEvaluationTests
{
    private static (PolicyEngine engine, HyperVToolClassifier classifier, HyperVRuleMatcher matcher, HyperVOptionGenerator optionGen) CreatePolicy(HyperVPolicyConfig? config = null)
    {
        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var engine = new PolicyEngine(config, classifier, matcher);
        return (engine, classifier, matcher, optionGen);
    }

    [Fact]
    public void Unrestricted_AllowsEverything()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig { Mode = PolicyMode.Unrestricted });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "prod-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsReadOnly()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("list_vms", new JsonObject());
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_GetProcess()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Get-Process",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_Hostname()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "hostname",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsDotnet_NotInDefaults()
    {
        // dotnet was removed from default allowed patterns (dotnet run executes code).
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "dotnet build -c Release",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_GitStatus()
    {
        // git status is a read-only subcommand — allowed by default.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "git status",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_GitDiff()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "git diff HEAD~1",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsGitCommit()
    {
        // git commit is a write operation — not in the allowed subcommands.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "git commit -m fix",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsGitPush()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "git push origin main",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsMsiexec()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "msiexec /i C:\\build\\package.msi /qn",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsCd()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "cd C:\\temp",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsOutFile()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Out-File C:\\temp\\log.txt",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsUnknownCommand()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Set-NetIPAddress -InterfaceIndex 5 -IPAddress 10.0.0.1",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Contains("not in allowed list", result.Reason);
    }

    [Fact]
    public void Standard_ConfirmsUnknownNativeCommand()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "netsh advfirewall set allprofiles state off",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_FormatTable()
    {
        // Format-Table should be allowed (not confused with Format-Volume which is a warn pattern).
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Get-Process | Format-Table Name, Id",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_VariableAccess()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "$env:COMPUTERNAME",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_MathTypeAccelerator()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "[math]::Round((Get-PSDrive C).Free / 1GB, 2)",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_AllowsSafeCommand_DateTimeTypeAccelerator()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "[datetime]::Now.ToString('yyyy-MM-dd')",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsUnsafe_SystemIOFile()
    {
        // Dangerous .NET types should NOT be in the allowlist.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "[System.IO.File]::Delete('C:\\important.txt')",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_BlocksStopComputer()
    {
        // Stop-Computer should be blocked — use stop_vm MCP tool instead.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Stop-Computer -Force",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void Standard_WarnsFormatVolume()
    {
        // Format-Volume was moved from blocked to warn — user can approve if needed.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Format-Volume -DriveLetter D",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Standard_WarnsRemoveVM()
    {
        // Remove-VM was moved from blocked to warn — user can approve if needed.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Remove-VM -Name test-vm -Force",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void Unrestricted_AllowsUnknownCommand()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig { Mode = PolicyMode.Unrestricted });
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Set-NetIPAddress -InterfaceIndex 5",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void Standard_ConfirmsDestructive()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Equal(ConfirmTrigger.ToolRiskTier, result.GetTrigger());
    }

    [Fact]
    public void ConnectVm_RespectsVmBlocklist()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            VmBlocklist = ["production-*"],
        });
        var result = engine.Evaluate("connect_vm", new JsonObject { ["vm_name"] = "production-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void ConnectVm_ComputerName_RespectsBlocklist()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            VmBlocklist = ["10.0.0.*"],
        });
        var result = engine.Evaluate("connect_vm", new JsonObject { ["computer_name"] = "10.0.0.5" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void ManageService_StartRequiresConfirmation()
    {
        // All Moderate tools require confirmation in Standard mode.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("manage_service", new JsonObject
        {
            ["session_id"] = "s-1",
            ["name"] = "eBPFSvc",
            ["action"] = "start",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void ManageService_StopRequiresConfirmation()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("manage_service", new JsonObject
        {
            ["session_id"] = "s-1",
            ["name"] = "eBPFSvc",
            ["action"] = "stop",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void ManageService_RestartRequiresConfirmation()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("manage_service", new JsonObject
        {
            ["session_id"] = "s-1",
            ["name"] = "eBPFSvc",
            ["action"] = "restart",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void RunScript_CheckedAgainstPatterns()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            BlockedCommandPatterns = ["malware\\.ps1"],
        });
        var result = engine.Evaluate("run_script", new JsonObject
        {
            ["session_id"] = "s-1",
            ["script_path"] = @"C:\tools\malware.ps1",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void ToolOverride_Allow()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            ToolOverrides = new() { ["stop_vm"] = "allow" },
        });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void ToolOverride_Deny()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            ToolOverrides = new() { ["invoke_command"] = "deny" },
        });
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "anything",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void BlockedCommandPattern_Deny()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            BlockedCommandPatterns = ["Format-Volume"],
        });
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Format-Volume -DriveLetter C",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
        Assert.Equal(ConfirmTrigger.CommandPattern, result.GetTrigger());
    }

    [Fact]
    public void WarnCommandPattern_Confirm()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            WarnCommandPatterns = ["Remove-Item"],
        });
        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Remove-Item C:\\logs -Recurse",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Equal(ConfirmTrigger.CommandPattern, result.GetTrigger());
    }

    [Fact]
    public void VmBlocklist_Deny()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            VmBlocklist = ["production-*"],
        });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "production-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, result.Decision);
    }

    [Fact]
    public void VmAllowlist_Allows()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            VmAllowlist = ["test-*"],
        });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void BulkVmCount_ExceedsLimit()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig { MaxBulkVms = 2 });
        var result = engine.Evaluate("stop_vm", new JsonObject
        {
            ["vm_name"] = new JsonArray("a", "b", "c"),
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Equal(ConfirmTrigger.BulkVmCount, result.GetTrigger());
    }

    [Fact]
    public void UserRules_MatchOverridesDefault()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            UserRules =
            [
                new McpSharp.Policy.UserRule
                {
                    Rule = new McpSharp.Policy.ApprovalRule
                    {
                        Tools = ["stop_vm"],
                        Constraints = new Dictionary<string, JsonElement>
                        {
                            ["vm_pattern"] = ToJsonElement("test-*"),
                        },
                    },
                },
            ],
        });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void SessionApproval_MatchOverridesDefault()
    {
        var (engine, _, _, _) = CreatePolicy();
        engine.RegisterSessionApproval(new McpSharp.Policy.ApprovalRule
        {
            Tools = ["stop_vm"],
            Constraints = new Dictionary<string, JsonElement>
            {
                ["vm_pattern"] = ToJsonElement("test-*"),
            },
        });
        var result = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void SessionApproval_CommandPrefix()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            WarnCommandPatterns = ["Remove-Item"],
        });
        engine.RegisterSessionApproval(HyperVRuleMatcher.BuildCommandRule(null, null, ["Remove-Item"]));

        var result = engine.Evaluate("invoke_command", new JsonObject
        {
            ["session_id"] = "s-1",
            ["command"] = "Remove-Item C:\\logs",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void HostPathRestriction_OutsideAllowed()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            HostPathRestrictions = new HostPathRestrictions
            {
                CopyFromVmDestAllow = [@"C:\temp\**"],
            },
        });
        var result = engine.Evaluate("copy_from_vm", new JsonObject
        {
            ["session_id"] = "s-1",
            ["source"] = "/tmp/file",
            ["destination"] = @"C:\Users\secret.txt",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Equal(ConfirmTrigger.HostPath, result.GetTrigger());
    }

    [Fact]
    public void HostPathRestriction_InsideAllowed()
    {
        var (engine, _, _, _) = CreatePolicy(new HyperVPolicyConfig
        {
            HostPathRestrictions = new HostPathRestrictions
            {
                CopyFromVmDestAllow = [@"C:\temp\**"],
            },
        });
        var result = engine.Evaluate("copy_from_vm", new JsonObject
        {
            ["session_id"] = "s-1",
            ["source"] = "/tmp/file",
            ["destination"] = @"C:\temp\output\file.txt",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Allow, result.Decision);
    }

    [Fact]
    public void HostPath_NoRestrictions_StillConfirms()
    {
        // With no path restrictions configured, file transfers require confirmation.
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("copy_from_vm", new JsonObject
        {
            ["session_id"] = "s-1",
            ["source"] = "/tmp/file",
            ["destination"] = @"C:\anywhere\file.txt",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
        Assert.Equal(ConfirmTrigger.HostPath, result.GetTrigger());
    }

    [Fact]
    public void ConnectVm_RequiresConfirmation()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("connect_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void StartVm_RequiresConfirmation()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("start_vm", new JsonObject { ["vm_name"] = "test-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    [Fact]
    public void CheckpointVm_RequiresConfirmation()
    {
        var (engine, _, _, _) = CreatePolicy();
        var result = engine.Evaluate("checkpoint_vm", new JsonObject
        {
            ["vm_name"] = "test-db",
            ["checkpoint_name"] = "snap1",
        });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Confirm, result.Decision);
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}

public class SuggestionGenerationTests
{
    private static (PolicyEngine engine, HyperVToolClassifier classifier, HyperVRuleMatcher matcher, HyperVOptionGenerator optionGen) CreatePolicy(HyperVPolicyConfig? config = null)
    {
        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var engine = new PolicyEngine(config, classifier, matcher);
        return (engine, classifier, matcher, optionGen);
    }

    [Fact]
    public void VmTool_GeneratesSessionAndPermanentPairs()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("stop_vm",
            new JsonObject { ["vm_name"] = "test-db" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.ToolRiskTier, riskTier: RiskTier.Destructive));

        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains(options, o => o.Label == "Deny once");
        Assert.Contains(options, o => o.Label == "Allow stop_vm on test-db (this session)");
        Assert.Contains(options, o => o.Label == "Allow stop_vm on test-db (permanently)");
        // Power-cycle group is permanent only.
        Assert.Contains(options, o => o.Label == "Allow start/stop/restart on test-db (permanently)");
        Assert.DoesNotContain(options, o => o.Label == "Allow start/stop/restart on test-db (this session)");
        // Everything on VM — session only.
        Assert.Contains(options, o => o.Label == "Allow everything on test-db (this session)");
        // Destructive deny options.
        Assert.Contains(options, o => o.Label == "Deny stop_vm on test-db (this session)"
            && o.Polarity == McpSharp.Policy.ApprovalPolarity.Deny);
        Assert.Contains(options, o => o.Label == "Always deny stop_vm on test-db (permanently)"
            && o.Polarity == McpSharp.Policy.ApprovalPolarity.Deny);
    }

    [Fact]
    public void VmTool_NonPowerCycleTool_NoGroupOption()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("restore_vm",
            new JsonObject { ["vm_name"] = "test-db" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.ToolRiskTier, riskTier: RiskTier.Destructive));

        Assert.Contains(options, o => o.Label.Contains("restore_vm on test-db"));
        Assert.DoesNotContain(options, o => o.Label.Contains("start/stop/restart"));
    }

    [Fact]
    public void CommandPattern_SimpleCommand_GeneratesPrefixSuggestions()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = @"Remove-Item C:\logs -Recurse" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.CommandPattern));

        Assert.Equal("Allow once", options[0].Label);
        // Without VM context, command options are session-only.
        Assert.Contains(options, o => o.Label.Contains(@"""Remove-Item C:\logs""") && o.Label.Contains("session"));
        Assert.Contains(options, o => o.Label.Contains(@"""Remove-Item""") && o.Label.Contains("session"));
        // No permanent without VM scope.
        Assert.DoesNotContain(options, o => o.Label.Contains("permanently"));
    }

    [Fact]
    public void CommandPattern_ChainedCommand_OffersAllNames()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("invoke_command",
            new JsonObject { ["session_id"] = "s-1", ["command"] = "Remove-Item C:\\logs; Stop-Service eBPFSvc" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.CommandPattern));

        Assert.Contains(options, o => o.Label.Contains("Remove-Item") && o.Label.Contains("Stop-Service"));
        Assert.Contains(options, o => o.Label.Contains(@"""Remove-Item""") && !o.Label.Contains("Stop-Service"));
    }

    [Fact]
    public void HostPath_GeneratesPathAndDirectionalVmOptions()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("copy_from_vm",
            new JsonObject { ["session_id"] = "s-1", ["destination"] = @"C:\Users\me\Desktop\file.txt", ["vm_name"] = "test-vm" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.HostPath));

        Assert.Contains(options, o => o.Label.Contains("Desktop"));
        // Directional tool+VM option (not "all copies").
        Assert.Contains(options, o => o.Label.Contains("copy_from_vm on test-vm"));
        Assert.DoesNotContain(options, o => o.Label.Contains("all copies"));
        // Everything on VM.
        Assert.Contains(options, o => o.Label.Contains("everything on test-vm"));
    }

    [Fact]
    public void BulkVm_OnlyAllowAndDenyOnce()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var options = optionGen.Generate("stop_vm",
            new JsonObject { ["vm_name"] = new JsonArray("a", "b", "c", "d") },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.BulkVmCount));

        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains(options, o => o.Label == "Deny once");
        // No count-based auto-approve options.
        Assert.DoesNotContain(options, o => o.Label.Contains("VMs"));
        // With VM names in args, an "everything on {vm}" session option is also generated.
        Assert.True(options.Count <= 3);
    }

    [Fact]
    public void CancelCommand_WithResolver_GeneratesSessionAndPermanentPairs()
    {
        var (_, classifier, _, optionGen) = CreatePolicy();
        classifier.CommandSessionResolver = commandId =>
            commandId == "cmd-42" ? "s-1" : null;
        classifier.SessionVmResolver = sessionId =>
            sessionId == "s-1" ? "test-vm" : null;

        var evaluation = classifier.Evaluate("cancel_command",
            new JsonObject { ["command_id"] = "cmd-42" });

        var options = optionGen.Generate("cancel_command",
            new JsonObject { ["command_id"] = "cmd-42" },
            evaluation);

        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains(options, o => o.Label == "Allow cancel_command on test-vm (this session)");
        Assert.Contains(options, o => o.Label == "Allow cancel_command on test-vm (permanently)");
        Assert.Contains(options, o => o.Label == "Allow everything on test-vm (this session)");
        // Destructive deny options.
        Assert.Contains(options, o => o.Label == "Deny cancel_command on test-vm (this session)"
            && o.Polarity == McpSharp.Policy.ApprovalPolarity.Deny);
    }

    [Fact]
    public void CancelCommand_WithoutResolver_OnlyAllowAndDenyOnce()
    {
        var (_, _, _, optionGen) = CreatePolicy();

        var options = optionGen.Generate("cancel_command",
            new JsonObject { ["command_id"] = "cmd-42" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.ToolRiskTier, riskTier: RiskTier.Destructive));

        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains(options, o => o.Label == "Deny once");
        // No session/permanent options without VM context.
        Assert.DoesNotContain(options, o => o.Label.Contains("session"));
        Assert.DoesNotContain(options, o => o.Label.Contains("permanently"));
    }

    [Fact]
    public void DenyRules_EvaluatedBeforeAllowRules()
    {
        var config = new HyperVPolicyConfig
        {
            DenyRules = [new McpSharp.Policy.UserRule { Rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "prod-db") }],
            UserRules = [new McpSharp.Policy.UserRule { Rule = HyperVRuleMatcher.BuildVmRule(["stop_vm"], "prod-db") }],
        };
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var engine = new PolicyEngine(config, classifier, matcher);

        var eval = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "prod-db" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, eval.Decision);
        Assert.Contains("deny rule", eval.Reason);
    }

    [Fact]
    public void SessionDenial_BlocksSubsequentCalls()
    {
        var (engine, _, _, _) = CreatePolicy();
        engine.RegisterSessionDenial(HyperVRuleMatcher.BuildVmRule(["stop_vm"], "test-vm"));

        var eval = engine.Evaluate("stop_vm", new JsonObject { ["vm_name"] = "test-vm" });
        Assert.Equal(McpSharp.Policy.PolicyDecision.Deny, eval.Decision);
    }
}
public class PolicyPersistenceTests
{
    [Fact]
    public void SaveRuleToPolicy_CreatesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"policy-test-{Guid.NewGuid()}.json");
        try
        {
            // Write initial config.
            File.WriteAllText(tempFile,
                JsonSerializer.Serialize(new HyperVPolicyConfig(), McpSharp.Policy.PolicyConfig.JsonOptions));

            var config = new HyperVPolicyConfig();
            var classifier = new HyperVToolClassifier(config);
            var matcher = new HyperVRuleMatcher(classifier);
            var engine = new PolicyEngine(config, classifier, matcher, tempFile);
            engine.SaveRuleToPolicy(
                new McpSharp.Policy.ApprovalRule
                {
                    Tools = ["stop_vm"],
                    Constraints = new Dictionary<string, JsonElement>
                    {
                        ["vm_pattern"] = ToJsonElement("test-*"),
                    },
                },
                "test reason");

            // Read back and verify.
            var json = File.ReadAllText(tempFile);
            var deserialized = JsonSerializer.Deserialize<HyperVPolicyConfig>(json, McpSharp.Policy.PolicyConfig.JsonOptions)!;
            Assert.NotNull(deserialized.UserRules);
            Assert.Single(deserialized.UserRules);
            Assert.Equal("test reason", deserialized.UserRules[0].Reason);
            Assert.NotNull(deserialized.UserRules[0].Rule);
            Assert.Contains("stop_vm", deserialized.UserRules[0].Rule!.Tools!);
            Assert.Equal(JsonValueKind.String, deserialized.UserRules[0].Rule!.Constraints!["vm_pattern"].ValueKind);
            Assert.Equal("test-*", deserialized.UserRules[0].Rule!.Constraints!["vm_pattern"].GetString());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void SaveRuleToPolicy_PreservesExistingRules()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"policy-test-{Guid.NewGuid()}.json");
        try
        {
            var initial = new HyperVPolicyConfig
            {
                UserRules =
                [
                    new McpSharp.Policy.UserRule
                    {
                        Reason = "existing rule",
                        Rule = HyperVRuleMatcher.BuildCommandRule(null, null, ["git"]),
                    },
                ],
            };
            File.WriteAllText(tempFile, JsonSerializer.Serialize(initial, McpSharp.Policy.PolicyConfig.JsonOptions));

            var classifier = new HyperVToolClassifier(initial);
            var matcher = new HyperVRuleMatcher(classifier);
            var engine = new PolicyEngine(initial, classifier, matcher, tempFile);
            engine.SaveRuleToPolicy(
                new McpSharp.Policy.ApprovalRule { Tools = ["stop_vm"] },
                "new rule");

            var json = File.ReadAllText(tempFile);
            var deserialized = JsonSerializer.Deserialize<HyperVPolicyConfig>(json, McpSharp.Policy.PolicyConfig.JsonOptions)!;
            Assert.Equal(2, deserialized.UserRules!.Count);
            Assert.Equal("existing rule", deserialized.UserRules[0].Reason);
            Assert.Equal("new rule", deserialized.UserRules[1].Reason);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_DefaultsToStandard()
    {
        var config = new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var engine = PolicyEngine.Load<HyperVPolicyConfig>(classifier, matcher);
        Assert.Equal(PolicyMode.Standard, ((HyperVPolicyConfig)engine.Config).Mode);
    }

    [Fact]
    public void ParseApprovalRule_AllFields()
    {
        // Deserialize a JSON object directly to McpSharp.Policy.ApprovalRule.
        var json = new JsonObject
        {
            ["tools"] = new JsonArray("stop_vm", "start_vm"),
            ["vm_pattern"] = "test-*",
            ["command_prefixes"] = new JsonArray("git"),
            ["host_paths"] = new JsonArray(@"C:\temp\**"),
            ["max_bulk_vms"] = 5,
        };

        var rule = JsonSerializer.Deserialize<McpSharp.Policy.ApprovalRule>(
            json.ToJsonString(), McpSharp.Policy.PolicyConfig.JsonOptions)!;
        Assert.Equal(2, rule.Tools!.Count);
        Assert.Equal("test-*", rule.Constraints!["vm_pattern"].GetString());
        Assert.Equal("git", rule.Constraints["command_prefixes"].EnumerateArray().First().GetString());
        Assert.Equal(@"C:\temp\**", rule.Constraints["host_paths"].EnumerateArray().First().GetString());
        Assert.Equal(5, rule.Constraints["max_bulk_vms"].GetInt32());
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Dispatch Wrapper Integration Tests
// ═══════════════════════════════════════════════════════════════════════

public class DispatchWrapperTests
{
    private static (McpServer server, PolicyEngine policy, HyperVOptionGenerator optionGen) CreateTestSetup(HyperVPolicyConfig? config = null)
    {
        var server = new McpServer("test");
        // Register a simple mock tool to test dispatch interception.
        server.RegisterTool(new ToolInfo
        {
            Name = "stop_vm",
            Description = "Test tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = args => new JsonObject { ["mock"] = "ok", ["vm_name"] = args["vm_name"]?.GetValue<string>() },
        });
        server.RegisterTool(new ToolInfo
        {
            Name = "list_vms",
            Description = "Test read-only tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonObject { ["vms"] = new JsonArray() },
        });
        server.RegisterTool(new ToolInfo
        {
            Name = "invoke_command",
            Description = "Test command tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = args => new JsonObject { ["output"] = args["command"]?.GetValue<string>() },
        });

        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var policy = new PolicyEngine(config, classifier, matcher);
        return (server, policy, optionGen);
    }

    [Fact]
    public void NonToolsCall_PassesThrough()
    {
        var (server, policy, optionGen) = CreateTestSetup();
        var result = PolicyDispatch.Dispatch("tools/list", null, server, policy, optionGen);
        Assert.NotNull(result);
        var tools = result!["tools"]?.AsArray();
        Assert.NotNull(tools);
        Assert.True(tools!.Count > 0);
    }

    [Fact]
    public void AuthorizeRequest_ToolPresent()
    {
        var (server, policy, optionGen) = CreateTestSetup();
        var result = PolicyDispatch.Dispatch("tools/list", null, server, policy, optionGen);
        var tools = result!["tools"]!.AsArray();
        var toolNames = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
    }

    [Fact]
    public void ReadOnlyTool_AllowedInStandard()
    {
        var (server, policy, optionGen) = CreateTestSetup();
        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject { ["name"] = "list_vms", ["arguments"] = new JsonObject() },
            server, policy, optionGen);

        // Should succeed (mock tool returns vms array).
        var text = result?["content"]?.AsArray()?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.DoesNotContain("confirmation_required", text);
        Assert.DoesNotContain("\"status\":\"denied\"", text);
        Assert.Contains("vms", text);
    }

    [Fact]
    public void DestructiveTool_DeniedWithoutElicitation()
    {
        // Without transport/elicitation, blocked tools get denied.
        var (server, policy, optionGen) = CreateTestSetup();
        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-db" },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("denied", text);
    }

    [Fact]
    public void Unrestricted_AllowsDestructive()
    {
        var (server, policy, optionGen) = CreateTestSetup(new HyperVPolicyConfig { Mode = PolicyMode.Unrestricted });
        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-db" },
            },
            server, policy, optionGen);

        // Should NOT be a policy block.
        var text = result?["content"]?.AsArray()?[0]?["text"]?.GetValue<string>() ?? "";
        Assert.DoesNotContain("confirmation_required", text);
        Assert.Contains("mock", text);
    }

    [Fact]
    public void WarnCommandPattern_DeniedWithoutElicitation()
    {
        var (server, policy, optionGen) = CreateTestSetup(new HyperVPolicyConfig
        {
            WarnCommandPatterns = ["Remove-Item"],
        });

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "invoke_command",
                ["arguments"] = new JsonObject
                {
                    ["session_id"] = "s-1",
                    ["command"] = "Remove-Item C:\\logs -Recurse",
                },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("denied", text);
    }
}

// ── Elicitation tests ───────────────────────────────────────

public class ElicitationSchemaTests
{
    private static (PolicyEngine engine, HyperVToolClassifier classifier, HyperVRuleMatcher matcher, HyperVOptionGenerator optionGen) CreatePolicy(HyperVPolicyConfig? config = null)
    {
        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var engine = new PolicyEngine(config, classifier, matcher);
        return (engine, classifier, matcher, optionGen);
    }

    [Fact]
    public void BuildElicitationSchema_ContainsAllSuggestionLabels()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var suggestions = optionGen.Generate(
            "stop_vm",
            new JsonObject { ["vm_name"] = "test-vm" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.ToolRiskTier,
                category: "vm_lifecycle"));

        var schema = McpSharp.Policy.PolicyDispatch.BuildElicitationSchema(
            "stop_vm",
            new JsonObject { ["vm_name"] = "test-vm" },
            PolicyEvaluationExtensions.BuildEvaluation(McpSharp.Policy.PolicyDecision.Confirm, "test"),
            suggestions,
            out var message);

        // Schema has action property with enum.
        var action = schema["properties"]!["action"]!;
        var enumValues = action["enum"]!.AsArray();

        // Should have one entry per suggestion plus "Deny".
        Assert.Equal(suggestions.Count, enumValues.Count);
        Assert.Equal("Allow once", enumValues[0]!.GetValue<string>());
        Assert.Equal("Deny once", enumValues[^1]!.GetValue<string>());

        // No enumNames — labels are used directly as enum values.
        Assert.Null(action["enumNames"]);

        // Message contains tool name and reason.
        Assert.Contains("stop_vm", message);
    }

    [Fact]
    public void BuildElicitationSchema_IncludesSavePermanently_WhenPermanentSuggestionsExist()
    {
        var (_, _, _, optionGen) = CreatePolicy();
        var suggestions = optionGen.Generate(
            "start_vm",
            new JsonObject { ["vm_name"] = "dev-vm" },
            PolicyEvaluationExtensions.BuildEvaluation(
                McpSharp.Policy.PolicyDecision.Confirm, "test",
                trigger: ConfirmTrigger.ToolRiskTier));

        var schema = McpSharp.Policy.PolicyDispatch.BuildElicitationSchema(
            "start_vm", new JsonObject(),
            PolicyEvaluationExtensions.BuildEvaluation(McpSharp.Policy.PolicyDecision.Confirm, "t"),
            suggestions, out _);

        // save_permanently was removed — persistence is encoded in labels.
        Assert.Null(schema["properties"]!["save_permanently"]);
    }

    [Fact]
    public void BuildElicitationSchema_NoSavePermanently_WhenOnlySingleScope()
    {
        // Simulate suggestions with only "single" scope.
        var suggestions = new List<McpSharp.Policy.ElicitationOption>
        {
            new() { Label = "Allow once", Persistence = McpSharp.Policy.ApprovalPersistence.Once, Polarity = McpSharp.Policy.ApprovalPolarity.Allow },
        };

        var schema = McpSharp.Policy.PolicyDispatch.BuildElicitationSchema(
            "test_tool", new JsonObject(),
            PolicyEvaluationExtensions.BuildEvaluation(McpSharp.Policy.PolicyDecision.Confirm, "t"),
            suggestions, out _);
    }

    [Fact]
    public void BuildElicitationSchema_MessageIncludesCommand()
    {
        var suggestions = new List<McpSharp.Policy.ElicitationOption>
        {
            new() { Label = "Allow once", Persistence = McpSharp.Policy.ApprovalPersistence.Once, Polarity = McpSharp.Policy.ApprovalPolarity.Allow },
        };

        var schema = McpSharp.Policy.PolicyDispatch.BuildElicitationSchema(
            "invoke_command",
            new JsonObject { ["command"] = "Get-Process -Name svc*" },
            PolicyEvaluationExtensions.BuildEvaluation(McpSharp.Policy.PolicyDecision.Confirm, "Unknown command"),
            suggestions, out var message);

        Assert.Contains("Get-Process -Name svc*", message);
        Assert.Contains("invoke_command", message);
    }
}

public class ElicitationDispatchTests
{
    private static (McpServer server, PolicyEngine policy, HyperVOptionGenerator optionGen) CreateElicitationSetup(MemoryStream input, MemoryStream output, HyperVPolicyConfig? config = null)
    {
        var transport = new McpTransport(input, output, "test");

        // Trigger NDJSON framing.
        var dummy = System.Text.Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("test");
        server.Transport = transport;

        // Simulate initialize with elicitation capability.
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        // Register mock tools.
        server.RegisterTool(new ToolInfo
        {
            Name = "stop_vm",
            Description = "Test tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = args => new JsonObject { ["stopped"] = true, ["vm_name"] = args["vm_name"]?.GetValue<string>() },
        });
        server.RegisterTool(new ToolInfo
        {
            Name = "list_vms",
            Description = "Read-only tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ => new JsonObject { ["vms"] = new JsonArray() },
        });

        config ??= new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var policy = new PolicyEngine(config, classifier, matcher);
        return (server, policy, optionGen);
    }

    private static void WriteResponse(MemoryStream input, string id, string action, JsonObject? content = null)
    {
        var result = new JsonObject { ["action"] = action };
        if (content != null)
            result["content"] = JsonNode.Parse(content.ToJsonString());

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        input.Write(bytes);
        input.Position = 0;
    }

    [Fact]
    public void Elicitation_UserApproves_ToolExecutes()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        // Pre-load user approval response.
        WriteResponse(input, "s-1", "accept", new JsonObject { ["action"] = "Allow once" });

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        // Should have executed the tool (not returned an error).
        Assert.NotNull(result);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        Assert.Equal(true, parsed["stopped"]?.GetValue<bool>());
    }

    [Fact]
    public void Elicitation_UserDenies_ReturnsError()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        WriteResponse(input, "s-1", "accept", new JsonObject { ["action"] = "Deny once" });

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("denied", text.ToLowerInvariant());
    }

    [Fact]
    public void Elicitation_UserDeclines_ReturnsError()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        WriteResponse(input, "s-1", "decline");

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("declined", text.ToLowerInvariant());
    }

    [Fact]
    public void Elicitation_UserCancels_ReturnsError()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        WriteResponse(input, "s-1", "cancel");

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text2 = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("cancelled", text2.ToLowerInvariant());
    }

    [Fact]
    public void Elicitation_FreeformFeedback_SurfacedInResponse()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        // Simulate user typing a freeform response (not one of the predefined options).
        WriteResponse(input, "s-1", "accept",
            new JsonObject { ["action"] = "Please connect to the VM first" });

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        // The user's feedback should be visible in the response with distinct status.
        Assert.Contains("Please connect to the VM first", text);
        Assert.Contains("user_feedback", text);
        Assert.Contains("IMPORTANT", text);
        // Status should be "user_feedback", not "denied".
        var parsed = JsonNode.Parse(text)!;
        Assert.Equal("user_feedback", parsed["status"]?.GetValue<string>());
    }

    [Fact]
    public void Elicitation_AlwaysAllowOption_RegistersRule()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        // Select option_1 (Always allow stop_vm on test-vm) with save_permanently=false (session only).
        WriteResponse(input, "s-1", "accept", new JsonObject
        {
            ["action"] = "Allow stop_vm on test-vm (this session)",
            ["save_permanently"] = false,
        });

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        // Tool should have executed.
        var text = result!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed = JsonNode.Parse(text)!;
        Assert.Equal(true, parsed["stopped"]?.GetValue<bool>());

        // The session approval should now allow stop_vm on test-vm without elicitation.
        // Reset streams for a second call.
        input.SetLength(0);
        output.SetLength(0);

        var result2 = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        // Should succeed without elicitation (no response needed in input stream).
        var text2 = result2!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        var parsed2 = JsonNode.Parse(text2)!;
        Assert.Equal(true, parsed2["stopped"]?.GetValue<bool>());
    }

    [Fact]
    public void Elicitation_ReadOnlyTool_NoElicitationNeeded()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        // Read-only tool should pass through without elicitation.
        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject { ["name"] = "list_vms" },
            server, policy, optionGen);

        Assert.NotNull(result);
        Assert.Null(result["isError"]);

        // No elicitation request should have been sent.
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Elicitation_SendsElicitationRequest_WithCorrectSchema()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, policy, optionGen) = CreateElicitationSetup(input, output);

        // Pre-load approval.
        WriteResponse(input, "s-1", "accept", new JsonObject { ["action"] = "Allow once" });

        McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        // Verify the elicitation request.
        output.Position = 0;
        var raw = System.Text.Encoding.UTF8.GetString(output.ToArray());
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var request = JsonNode.Parse(lines[0])!;

        Assert.Equal("elicitation/create", request["method"]?.GetValue<string>());
        Assert.Equal("s-1", request["id"]?.GetValue<string>());

        var @params = request["params"]!;
        Assert.Contains("stop_vm", @params["message"]!.GetValue<string>());

        var schema = @params["requestedSchema"]!;
        var actionProp = schema["properties"]!["action"]!;
        Assert.NotNull(actionProp["enum"]);

        // Should have a deny option.
        var enumVals = actionProp["enum"]!.AsArray();
        Assert.Contains(enumVals, v => v!.GetValue<string>() == "Deny once");
    }

    [Fact]
    public void Elicitation_DeniedWithoutElicitation()
    {
        // Create server without elicitation capability.
        var server = new McpServer("test");
        server.Dispatch("initialize", null); // No elicitation capability.
        server.RegisterTool(new ToolInfo
        {
            Name = "stop_vm",
            Description = "Test tool",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = args => new JsonObject { ["stopped"] = true },
        });
        var config = new HyperVPolicyConfig();
        var classifier = new HyperVToolClassifier(config);
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGen = new HyperVOptionGenerator(classifier);
        var policy = new PolicyEngine(config, classifier, matcher);

        var result = McpSharp.Policy.PolicyDispatch.Dispatch("tools/call",
            new JsonObject
            {
                ["name"] = "stop_vm",
                ["arguments"] = new JsonObject { ["vm_name"] = "test-vm" },
            },
            server, policy, optionGen);

        // Without elicitation, tool call gets denied.
        Assert.True(result!["isError"]?.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("denied", text);
    }
}