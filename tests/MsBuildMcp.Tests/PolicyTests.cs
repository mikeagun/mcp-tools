// Copyright (c) MsBuildMcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using McpSharp.Policy;
using MsBuildMcp.Policy;
using Xunit;
using static MsBuildMcp.Tests.PolicyTestHelpers;

namespace MsBuildMcp.Tests;

// =============================================================================
// MsBuildRuleMatcher: targets + configuration constraint matching
// =============================================================================

public class RuleMatcherTargetTests
{
    private readonly MsBuildRuleMatcher _matcher = new();

    [Fact]
    public void NoConstraints_MatchesAnything()
    {
        var rule = new ApprovalRule { Tools = ["build"] };
        var args = BuildArgs("C:\\sln.sln", targets: "A");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void SlnOnly_MatchesAnyTargets()
    {
        var rule = MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"));
        var args = BuildArgs("C:\\sln.sln", targets: "A,B,C");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void SlnOnly_MatchesNoTargets()
    {
        var rule = MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"));
        var args = BuildArgs("C:\\sln.sln");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_ExactMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"]);
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_SubsetMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c", "tests\\bpf2c_tests"]);
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_FullSetMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c", "tests\\bpf2c_tests"]);
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c,tests\\bpf2c_tests");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_SupersetDoesNotMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"]);
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c,drivers\\EbpfCore");
        Assert.False(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_DisjointDoesNotMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"]);
        var args = BuildArgs("C:\\sln.sln", targets: "drivers\\EbpfCore");
        Assert.False(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_FullSolutionBuildDoesNotMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"]);
        var args = BuildArgs("C:\\sln.sln"); // no targets = full solution
        Assert.False(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetConstraint_CaseInsensitive()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"]);
        var args = BuildArgs("C:\\sln.sln", targets: "Tools\\BPF2C");
        Assert.True(_matcher.Matches(rule, "build", args));
    }
}

public class RuleMatcherConfigTests
{
    private readonly MsBuildRuleMatcher _matcher = new();

    [Fact]
    public void NoConfigConstraint_MatchesAnyConfig()
    {
        var rule = MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"));
        var args = BuildArgs("C:\\sln.sln", config: "Release");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void ConfigConstraint_ExactMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), configuration: "Release");
        var args = BuildArgs("C:\\sln.sln", config: "Release");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void ConfigConstraint_CaseInsensitive()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), configuration: "release");
        var args = BuildArgs("C:\\sln.sln", config: "Release");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void ConfigConstraint_DefaultDebugWhenOmitted()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), configuration: "Debug");
        var args = BuildArgs("C:\\sln.sln"); // no config = defaults to Debug
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void ConfigConstraint_MismatchRejects()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), configuration: "Release");
        var args = BuildArgs("C:\\sln.sln", config: "Debug");
        Assert.False(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetPlusConfig_BothMustMatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"], "Release");
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c", config: "Release");
        Assert.True(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetPlusConfig_TargetMatchConfigMismatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"], "Release");
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c", config: "Debug");
        Assert.False(_matcher.Matches(rule, "build", args));
    }

    [Fact]
    public void TargetPlusConfig_ConfigMatchTargetMismatch()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            Norm("C:\\sln.sln"), ["tools\\bpf2c"], "Release");
        var args = BuildArgs("C:\\sln.sln", targets: "drivers\\EbpfCore", config: "Release");
        Assert.False(_matcher.Matches(rule, "build", args));
    }
}

// =============================================================================
// MsBuildOptionGenerator: context-dependent scope options
// =============================================================================

public class OptionGeneratorTests
{
    private readonly MsBuildOptionGenerator _gen = new();
    private static readonly PolicyEvaluation DefaultEval = new()
    {
        Decision = PolicyDecision.Confirm,
        Reason = "Tool 'build' requires confirmation",
    };

    [Fact]
    public void Case1_TargetedDefaultConfig()
    {
        var args = BuildArgs("C:\\project\\project.sln", targets: "tools\\bpf2c");
        var options = _gen.Generate("build", args, DefaultEval);

        Assert.Equal(5, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains("builds of bpf2c on project.sln", options[1].Label);
        Assert.Contains("all builds on project.sln", options[2].Label);
        Assert.Equal("Allow all builds", options[3].Label);
        Assert.Equal("Deny", options[4].Label);

        // Terminal options have explicit persistence
        Assert.Equal(ApprovalPersistence.Once, options[0].Persistence);
        Assert.Equal(ApprovalPersistence.Once, options[4].Persistence);

        // Scope options have null persistence (triggers Prompt 2)
        Assert.Null(options[1].Persistence);
        Assert.Null(options[2].Persistence);
        Assert.Null(options[3].Persistence);
    }

    [Fact]
    public void Case2_TargetedNonDefaultConfig()
    {
        var args = BuildArgs("C:\\project\\project.sln",
            targets: "tools\\bpf2c", config: "Release");
        var options = _gen.Generate("build", args, DefaultEval);

        Assert.Equal(7, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains("Release builds of bpf2c on project.sln", options[1].Label);
        Assert.Contains("builds of bpf2c on project.sln", options[2].Label);
        Assert.DoesNotContain("Release", options[2].Label); // any-config
        Assert.Contains("Release builds on project.sln", options[3].Label);
        Assert.Contains("all builds on project.sln", options[4].Label);
        Assert.Equal("Allow all builds", options[5].Label);
        Assert.Equal("Deny", options[6].Label);
    }

    [Fact]
    public void Case3_FullSolutionDefaultConfig()
    {
        var args = BuildArgs("C:\\project\\project.sln");
        var options = _gen.Generate("build", args, DefaultEval);

        Assert.Equal(4, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains("all builds on project.sln", options[1].Label);
        Assert.Equal("Allow all builds", options[2].Label);
        Assert.Equal("Deny", options[3].Label);
    }

    [Fact]
    public void Case4_FullSolutionNonDefaultConfig()
    {
        var args = BuildArgs("C:\\project\\project.sln", config: "Release");
        var options = _gen.Generate("build", args, DefaultEval);

        Assert.Equal(5, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains("Release builds on project.sln", options[1].Label);
        Assert.Contains("all builds on project.sln", options[2].Label);
        Assert.Equal("Allow all builds", options[3].Label);
        Assert.Equal("Deny", options[4].Label);
    }

    [Fact]
    public void CancelBuild_NoContext_GlobalOnly()
    {
        var args = new JsonObject();
        var options = _gen.Generate("cancel_build", args, DefaultEval);

        Assert.Equal(3, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Equal("Allow all cancel_build", options[1].Label);
        Assert.Equal("Deny", options[2].Label);

        // Terminal options have explicit persistence
        Assert.Equal(ApprovalPersistence.Once, options[0].Persistence);
        Assert.Equal(ApprovalPersistence.Once, options[2].Persistence);

        // Scope option has null persistence (triggers Prompt 2)
        Assert.Null(options[1].Persistence);
        Assert.NotNull(options[1].Rule);
    }

    [Fact]
    public void CancelBuild_WithSolution_ShowsSolutionScoped()
    {
        var args = new JsonObject { ["sln_path"] = "C:\\project\\project.sln" };
        var options = _gen.Generate("cancel_build", args, DefaultEval);

        Assert.Equal(4, options.Count);
        Assert.Equal("Allow once", options[0].Label);
        Assert.Contains("cancel_build on project.sln", options[1].Label);
        Assert.Equal("Allow all cancel_build", options[2].Label);
        Assert.Equal("Deny", options[3].Label);

        // Solution-scoped and global scope options have null persistence
        Assert.Null(options[1].Persistence);
        Assert.Null(options[2].Persistence);

        // Solution-scoped rule has sln_path constraint
        Assert.NotNull(options[1].Rule?.Constraints);
        Assert.True(options[1].Rule!.Constraints!.ContainsKey("sln_path"));
    }

    [Fact]
    public void ScopeOptions_HaveRules()
    {
        var args = BuildArgs("C:\\project\\project.sln", targets: "tools\\bpf2c");
        var options = _gen.Generate("build", args, DefaultEval);

        Assert.Null(options[0].Rule); // Allow once — no rule
        Assert.NotNull(options[1].Rule); // target-scoped — has rule
        Assert.NotNull(options[2].Rule); // solution-scoped — has rule
        Assert.NotNull(options[3].Rule); // global — has rule
        Assert.Null(options[4].Rule); // Deny — no rule
    }

    [Fact]
    public void MultiTargets_DisplaysTruncated()
    {
        var targets = "a\\A,b\\B,c\\C,d\\D";
        var display = MsBuildOptionGenerator.FormatTargets(targets);
        Assert.Contains("A, B, C", display);
        Assert.Contains("\u2026", display); // ellipsis
        Assert.DoesNotContain("D", display);
    }

    [Fact]
    public void MultiTargets_ThreeOrFewer_NoTruncation()
    {
        var targets = "tools\\bpf2c,tests\\bpf2c_tests";
        var display = MsBuildOptionGenerator.FormatTargets(targets);
        Assert.Equal("bpf2c, bpf2c_tests", display);
    }
}

// =============================================================================
// Pre-validation: build constraints
// =============================================================================

public class PreValidationTests
{
    [Fact]
    public void RequireTargets_RejectsNoTargets()
    {
        var policy = CreatePolicy(new BuildConstraints { RequireTargets = true });
        var args = BuildArgs("C:\\sln.sln");
        var result = Program.PreValidateBuild("build", args, policy);

        Assert.NotNull(result);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("targets required", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireTargets_AllowsWithTargets()
    {
        var policy = CreatePolicy(new BuildConstraints { RequireTargets = true });
        var args = BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c");
        var result = Program.PreValidateBuild("build", args, policy);
        Assert.Null(result);
    }

    [Fact]
    public void AllowedConfigs_RejectsDisallowed()
    {
        var policy = CreatePolicy(new BuildConstraints
        {
            AllowedConfigurations = ["Debug", "NativeOnlyDebug"]
        });
        var args = BuildArgs("C:\\sln.sln", config: "Release");
        var result = Program.PreValidateBuild("build", args, policy);

        Assert.NotNull(result);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Release", text);
        Assert.Contains("not allowed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowedConfigs_AllowsListed()
    {
        var policy = CreatePolicy(new BuildConstraints
        {
            AllowedConfigurations = ["Debug", "Release"]
        });
        var args = BuildArgs("C:\\sln.sln", config: "Release");
        Assert.Null(Program.PreValidateBuild("build", args, policy));
    }

    [Fact]
    public void AllowedConfigs_CaseInsensitive()
    {
        var policy = CreatePolicy(new BuildConstraints
        {
            AllowedConfigurations = ["debug"]
        });
        var args = BuildArgs("C:\\sln.sln", config: "Debug");
        Assert.Null(Program.PreValidateBuild("build", args, policy));
    }

    [Fact]
    public void AllowedConfigs_DefaultDebugWhenOmitted()
    {
        var policy = CreatePolicy(new BuildConstraints
        {
            AllowedConfigurations = ["Debug"]
        });
        var args = BuildArgs("C:\\sln.sln"); // no config = Debug
        Assert.Null(Program.PreValidateBuild("build", args, policy));
    }

    [Fact]
    public void AllowRestore_RejectsRestore()
    {
        var policy = CreatePolicy(new BuildConstraints { AllowRestore = false });
        var args = BuildArgs("C:\\sln.sln");
        args["restore"] = true;
        var result = Program.PreValidateBuild("build", args, policy);

        Assert.NotNull(result);
        var text = result!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("restore", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowRestore_AllowsWhenFalse()
    {
        var policy = CreatePolicy(new BuildConstraints { AllowRestore = false });
        var args = BuildArgs("C:\\sln.sln"); // restore defaults to false
        Assert.Null(Program.PreValidateBuild("build", args, policy));
    }

    [Fact]
    public void NoConstraints_AllowsEverything()
    {
        var policy = CreatePolicy(null);
        var args = BuildArgs("C:\\sln.sln");
        Assert.Null(Program.PreValidateBuild("build", args, policy));
    }

    [Fact]
    public void NonBuildTool_Skipped()
    {
        var policy = CreatePolicy(new BuildConstraints { RequireTargets = true });
        var args = new JsonObject();
        Assert.Null(Program.PreValidateBuild("list_projects", args, policy));
    }

    private static PolicyEngine CreatePolicy(BuildConstraints? constraints)
    {
        var config = new MsBuildPolicyConfig { BuildConstraints = constraints };
        return new PolicyEngine(config, new MsBuildToolClassifier(),
            new MsBuildRuleMatcher());
    }
}

// =============================================================================
// Policy engine integration: escalation flows
// =============================================================================

public class PolicyEscalationTests
{
    [Fact]
    public void TargetScopedRule_MatchesSubset()
    {
        var engine = CreateEngine();
        engine.RegisterSessionApproval(
            MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"), ["tools\\bpf2c", "tests\\t"]));

        // Exact match
        var eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", targets: "tools\\bpf2c"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // Subset match
        eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", targets: "tests\\t"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // Different target — still needs confirmation
        eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", targets: "drivers\\X"));
        Assert.Equal(PolicyDecision.Confirm, eval.Decision);
    }

    [Fact]
    public void SolutionScopedRule_MatchesAllTargets()
    {
        var engine = CreateEngine();
        engine.RegisterSessionApproval(
            MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln")));

        var eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", targets: "anything\\here"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // Full solution build also matches
        eval = engine.Evaluate("build", BuildArgs("C:\\sln.sln"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // Different solution still needs confirmation
        eval = engine.Evaluate("build",
            BuildArgs("C:\\other.sln", targets: "A"));
        Assert.Equal(PolicyDecision.Confirm, eval.Decision);
    }

    [Fact]
    public void ConfigScopedRule_OnlyMatchesThatConfig()
    {
        var engine = CreateEngine();
        engine.RegisterSessionApproval(
            MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"), configuration: "Release"));

        var eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", config: "Release"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // Debug doesn't match
        eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", config: "Debug"));
        Assert.Equal(PolicyDecision.Confirm, eval.Decision);
    }

    [Fact]
    public void GlobalRule_MatchesEverything()
    {
        var engine = CreateEngine();
        engine.RegisterSessionApproval(new ApprovalRule { Tools = ["build"] });

        var eval = engine.Evaluate("build",
            BuildArgs("C:\\any.sln", targets: "anything", config: "Release"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);
    }

    [Fact]
    public void BroaderRuleSubsumesNarrower()
    {
        var engine = CreateEngine();
        // Narrow: Release builds of bpf2c
        engine.RegisterSessionApproval(
            MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln"), ["tools\\bpf2c"], "Release"));
        // Broad: all builds on sln
        engine.RegisterSessionApproval(
            MsBuildRuleMatcher.BuildRule(Norm("C:\\sln.sln")));

        // Debug build of different target — matched by broad rule
        var eval = engine.Evaluate("build",
            BuildArgs("C:\\sln.sln", targets: "drivers\\X", config: "Debug"));
        Assert.Equal(PolicyDecision.Allow, eval.Decision);
    }

    private static PolicyEngine CreateEngine()
    {
        return new PolicyEngine(new MsBuildPolicyConfig(),
            new MsBuildToolClassifier(), new MsBuildRuleMatcher());
    }
}

public class CancelBuildPolicyTests
{
    [Fact]
    public void CancelBuild_SolutionScopedRule_Matches()
    {
        var engine = CreateEngine();
        var rule = new ApprovalRule
        {
            Tools = ["cancel_build"],
            Constraints = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["sln_path"] = System.Text.Json.JsonSerializer.SerializeToElement(
                    MsBuildRuleMatcher.NormalizePath("C:\\sln.sln")),
            },
        };
        engine.RegisterSessionApproval(rule);

        // cancel_build with matching enriched sln_path
        var args = new JsonObject { ["sln_path"] = "C:\\sln.sln" };
        var eval = engine.Evaluate("cancel_build", args);
        Assert.Equal(PolicyDecision.Allow, eval.Decision);

        // cancel_build with different sln_path
        var args2 = new JsonObject { ["sln_path"] = "C:\\other.sln" };
        var eval2 = engine.Evaluate("cancel_build", args2);
        Assert.Equal(PolicyDecision.Confirm, eval2.Decision);
    }

    [Fact]
    public void CancelBuild_GlobalRule_MatchesAny()
    {
        var engine = CreateEngine();
        engine.RegisterSessionApproval(new ApprovalRule { Tools = ["cancel_build"] });

        var args = new JsonObject { ["sln_path"] = "C:\\any.sln" };
        Assert.Equal(PolicyDecision.Allow, engine.Evaluate("cancel_build", args).Decision);

        var args2 = new JsonObject();
        Assert.Equal(PolicyDecision.Allow, engine.Evaluate("cancel_build", args2).Decision);
    }

    private static PolicyEngine CreateEngine()
    {
        return new PolicyEngine(new MsBuildPolicyConfig(),
            new MsBuildToolClassifier(), new MsBuildRuleMatcher());
    }
}

// =============================================================================
// MsBuildPolicyConfig serialization
// =============================================================================

public class PolicyConfigSerializationTests
{
    [Fact]
    public void RoundTrip_WithConstraints()
    {
        var config = new MsBuildPolicyConfig
        {
            BuildConstraints = new BuildConstraints
            {
                RequireTargets = true,
                AllowedConfigurations = ["Debug", "Release"],
                AllowRestore = false,
            },
        };

        var json = JsonSerializer.Serialize(config, PolicyConfig.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MsBuildPolicyConfig>(
            json, PolicyConfig.JsonOptions);

        Assert.NotNull(deserialized?.BuildConstraints);
        Assert.True(deserialized!.BuildConstraints!.RequireTargets);
        Assert.Equal(2, deserialized.BuildConstraints.AllowedConfigurations!.Count);
        Assert.False(deserialized.BuildConstraints.AllowRestore);
    }

    [Fact]
    public void RoundTrip_NoConstraints()
    {
        var config = new MsBuildPolicyConfig();
        var json = JsonSerializer.Serialize(config, PolicyConfig.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MsBuildPolicyConfig>(
            json, PolicyConfig.JsonOptions);

        Assert.Null(deserialized?.BuildConstraints);
    }

    [Fact]
    public void TargetRule_RoundTrip()
    {
        var rule = MsBuildRuleMatcher.BuildRule(
            "c:\\project\\test.sln", ["tools\\bpf2c", "tests\\t"], "Release");
        var json = JsonSerializer.Serialize(rule, PolicyConfig.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ApprovalRule>(
            json, PolicyConfig.JsonOptions);

        Assert.NotNull(deserialized?.Constraints);
        Assert.True(deserialized!.Constraints!.ContainsKey("sln_path"));
        Assert.True(deserialized.Constraints.ContainsKey("targets"));
        Assert.True(deserialized.Constraints.ContainsKey("configuration"));
    }
}

// =============================================================================
// Helpers
// =============================================================================

internal static class PolicyTestHelpers
{
    public static JsonObject BuildArgs(string slnPath,
        string? targets = null, string? config = null)
    {
        var args = new JsonObject { ["sln_path"] = slnPath };
        if (targets != null) args["targets"] = targets;
        if (config != null) args["configuration"] = config;
        return args;
    }

    public static string Norm(string path) => MsBuildRuleMatcher.NormalizePath(path);
}
