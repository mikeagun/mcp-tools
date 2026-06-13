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

    // -- Volatile publication of UserRules / DenyRules ----------------------

    /// <summary>
    /// Sanity test for the property round-trip on <see cref="PolicyConfig"/>
    /// after the auto-properties were converted to backing-field properties
    /// using <see cref="Volatile.Read{T}(ref T)"/> / <see cref="Volatile.Write{T}(ref T, T)"/>.
    /// </summary>
    [Fact]
    public void PolicyConfig_UserRules_RoundTrips()
    {
        var config = new PolicyConfig();
        Assert.Null(config.UserRules);
        Assert.Null(config.DenyRules);

        var rules = new List<UserRule>
        {
            new() { Rule = new ApprovalRule { Tools = ["a"] } },
            new() { Rule = new ApprovalRule { Tools = ["b"] } },
        };
        config.UserRules = rules;
        Assert.Same(rules, config.UserRules);
        Assert.Equal(2, config.UserRules.Count);

        config.UserRules = null;
        Assert.Null(config.UserRules);
    }

    /// <summary>
    /// JSON serialization must continue to work after the auto-property
    /// conversion. JsonSerializer drives reads/writes through the public
    /// getters/setters, so the volatile semantics are transparent.
    /// </summary>
    [Fact]
    public void PolicyConfig_JsonRoundTrip_PreservesRules()
    {
        var original = new PolicyConfig
        {
            UserRules = [new UserRule { Rule = new ApprovalRule { Tools = ["build"] } }],
            DenyRules = [new UserRule { Rule = new ApprovalRule { Tools = ["dangerous"] } }],
        };

        var json = JsonSerializer.Serialize(original, PolicyConfig.JsonOptions);
        var round = JsonSerializer.Deserialize<PolicyConfig>(json, PolicyConfig.JsonOptions)!;

        Assert.Single(round.UserRules!);
        Assert.Equal("build", round.UserRules![0].Rule!.Tools![0]);
        Assert.Single(round.DenyRules!);
        Assert.Equal("dangerous", round.DenyRules![0].Rule!.Tools![0]);
    }

    /// <summary>
    /// Stress test concurrent SaveRule + Evaluate against the same engine.
    ///
    /// Under the prior `auto-property + volatile _config` design, concurrent
    /// readers could in principle observe an inconsistent list state on a
    /// weak-memory architecture (ARM64) — though x64's stronger memory model
    /// masks the issue. The fix moves the volatile semantics to the
    /// PolicyConfig.UserRules/DenyRules properties so the publication
    /// (build new list off old → swap reference) is paired with proper
    /// acquire/release ordering on both sides.
    ///
    /// On x64 this test catches the BIG defects rather than the subtle
    /// memory-model ones — e.g., if a maintainer "fixed" the property to
    /// mutate the existing list in place (`_userRules.AddRange(value)`),
    /// concurrent readers would observe `InvalidOperationException` mid-
    /// enumeration. Pinning the no-exception invariant prevents that
    /// regression class.
    /// </summary>
    [Fact]
    public async Task SaveRule_ConcurrentWithEvaluate_NeverThrows()
    {
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: Path.Combine(_tempDir, "concurrent.json"));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var readCount = 0;
        var writeCount = 0;
        Exception? failure = null;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    // Evaluate iterates UserRules / DenyRules — any in-place
                    // mutation would surface here as InvalidOperationException
                    // (or NullReferenceException as the adversarial revert
                    // confirmed).
                    engine.Evaluate("build", new JsonObject());
                    Interlocked.Increment(ref readCount);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        })).ToArray();

        var writers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            try
            {
                var i = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (w == 0)
                    {
                        engine.SaveRuleToPolicy(
                            new ApprovalRule { Tools = [$"allow-{i++}"] }, "stress");
                    }
                    else
                    {
                        // Deny rules use the same Volatile-published property
                        // path — cover both branches in one test.
                        engine.SaveDenyRuleToPolicy(
                            new ApprovalRule { Tools = [$"deny-{i++}"] }, "stress");
                    }
                    Interlocked.Increment(ref writeCount);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        })).ToArray();

        await Task.WhenAll(readers.Concat(writers));
        Assert.Null(failure);

        // Sanity: prove the test actually exercised the race rather than
        // silently observing zero activity. Both loops must have run enough
        // iterations that any concurrency bug would have surfaced.
        Assert.True(readCount > 100, $"Readers only ran {readCount} iterations — test starved");
        Assert.True(writeCount > 5, $"Writers only ran {writeCount} iterations — test starved");
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
