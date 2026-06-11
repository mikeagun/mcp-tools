// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpSharp.Policy;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// Tests for PolicyEngine persistence: filename resolution, config type
/// preservation, and cross-session rule matching.
/// </summary>
public class PolicyEngineTests : IDisposable
{
    private readonly string _tempDir;

    public PolicyEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"policy-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // -- Filename mismatch fix ----------------------------------------

    [Fact]
    public void SaveRule_UsesDefaultFileName_WhenPolicyFilePathNull()
    {
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: null, defaultFileName: "my-server-policy.json");

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        // File should be created with the custom default name, not "policy.json".
        var expectedPath = Path.Combine(AppContext.BaseDirectory, "my-server-policy.json");
        try
        {
            Assert.True(File.Exists(expectedPath),
                $"Expected policy file at {expectedPath}");

            var json = File.ReadAllText(expectedPath);
            var config = JsonSerializer.Deserialize<PolicyConfig>(json, PolicyConfig.JsonOptions);
            Assert.NotNull(config?.UserRules);
            Assert.Single(config!.UserRules!);
        }
        finally
        {
            // Clean up the file created next to the exe.
            if (File.Exists(expectedPath)) File.Delete(expectedPath);
            var tmpPath = expectedPath + ".tmp";
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    [Fact]
    public void SaveRule_UsesExplicitPath_WhenProvided()
    {
        var policyFile = Path.Combine(_tempDir, "explicit.json");
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile, defaultFileName: "should-not-use.json");

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        Assert.True(File.Exists(policyFile));
        Assert.False(File.Exists(
            Path.Combine(AppContext.BaseDirectory, "should-not-use.json")));
    }

    // -- Config type preservation -------------------------------------

    [Fact]
    public void SaveRule_PreservesUnknownFieldsInFile()
    {
        // Write a config with server-specific fields.
        var policyFile = Path.Combine(_tempDir, "policy.json");
        var initial = new JsonObject
        {
            ["build_constraints"] = new JsonObject
            {
                ["require_targets"] = true,
            },
            ["vm_allowlist"] = new JsonArray("test-vm"),
        };
        File.WriteAllText(policyFile,
            initial.ToJsonString(PolicyConfig.JsonOptions));

        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        // Read back and verify server-specific fields are preserved.
        var json = File.ReadAllText(policyFile);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.NotNull(root["build_constraints"]);
        Assert.True(root["build_constraints"]!["require_targets"]!.GetValue<bool>());
        Assert.NotNull(root["vm_allowlist"]);
        Assert.Equal("test-vm", root["vm_allowlist"]![0]!.GetValue<string>());

        // User rules should also be present.
        Assert.NotNull(root["user_rules"]);
        Assert.Single(root["user_rules"]!.AsArray());
    }

    [Fact]
    public void SaveRule_PreservesConfigTypeInMemory()
    {
        var policyFile = Path.Combine(_tempDir, "policy.json");
        var serverConfig = new TestServerConfig { CustomField = "preserved" };

        var engine = new PolicyEngine(
            serverConfig, new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        // In-memory config should still be the server-specific type.
        Assert.IsType<TestServerConfig>(engine.Config);
        Assert.Equal("preserved", ((TestServerConfig)engine.Config).CustomField);

        // And the rule should be in the config.
        Assert.NotNull(engine.Config.UserRules);
        Assert.Single(engine.Config.UserRules);
    }

    // -- Cross-session persistence -------------------------------------------

    [Fact]
    public void PermanentRule_SurvivesNewEngineInstance()
    {
        var policyFile = Path.Combine(_tempDir, "test-policy.json");

        // Session 1: save a permanent rule.
        var engine1 = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile);

        var rule = new ApprovalRule
        {
            Tools = ["build"],
            Constraints = new Dictionary<string, JsonElement>
            {
                ["sln_path"] = JsonSerializer.SerializeToElement("c:\\test.sln"),
            },
        };
        engine1.SaveRuleToPolicy(rule, "permanent approval");

        // Session 2: load from same file, verify rule matches.
        var engine2 = PolicyEngine.Load<PolicyConfig>(
            new AllowAllClassifier(), new MatchAllMatcher(),
            explicitPath: policyFile);

        var evaluation = engine2.Evaluate("build", new JsonObject());
        Assert.Equal(PolicyDecision.Allow, evaluation.Decision);
    }

    [Fact]
    public void SaveRule_AccumulatesMultipleRules()
    {
        var policyFile = Path.Combine(_tempDir, "policy.json");
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "rule 1");
        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["test"] }, "rule 2");
        engine.SaveDenyRuleToPolicy(
            new ApprovalRule { Tools = ["dangerous"] }, "deny rule");

        // Verify file has all rules.
        var json = File.ReadAllText(policyFile);
        var config = JsonSerializer.Deserialize<PolicyConfig>(json, PolicyConfig.JsonOptions)!;
        Assert.Equal(2, config.UserRules!.Count);
        Assert.NotNull(config.DenyRules);
        Assert.Single(config.DenyRules);

        // Verify in-memory config matches.
        Assert.Equal(2, engine.Config.UserRules!.Count);
        Assert.NotNull(engine.Config.DenyRules);
        Assert.Single(engine.Config.DenyRules);
    }

    // -- Helpers --------------------------------------------------------------

    private sealed class AllowAllClassifier : IToolClassifier
    {
        public PolicyDecision Classify(string toolName, JsonObject args)
            => PolicyDecision.Confirm;
    }

    private sealed class MatchAllMatcher : IRuleMatcher
    {
        public bool Matches(ApprovalRule rule, string toolName, JsonObject args)
            => true;
    }

    private sealed class TestServerConfig : PolicyConfig
    {
        [JsonPropertyName("custom_field")]
        public string? CustomField { get; set; }
    }
}
