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
    private readonly string _exeDir;
    private readonly string _userDir;
    private readonly PolicyFileLocator _locator;

    public PolicyEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"policy-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        // Stand-ins for the executable directory and %LOCALAPPDATA%\mcp-tools so
        // tests never touch the real install directory or user profile. The user
        // directory is intentionally not created up front.
        _exeDir = Path.Combine(_tempDir, "exe");
        Directory.CreateDirectory(_exeDir);
        _userDir = Path.Combine(_tempDir, "localappdata", PolicyFileLocator.UserDirectoryName);
        _locator = new PolicyFileLocator(_exeDir, _userDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // -- Policy file location -----------------------------------------

    [Fact]
    public void SaveRule_CreatesPerUserFile_WhenPolicyFilePathNull()
    {
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: null, defaultFileName: "my-server-policy.json",
            fileLocator: _locator);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        // Saved under the (newly created) per-user directory with the custom
        // default name — not next to the exe, and not as "policy.json".
        var expectedPath = Path.Combine(_userDir, "my-server-policy.json");
        Assert.True(File.Exists(expectedPath), $"Expected policy file at {expectedPath}");
        Assert.Empty(Directory.GetFiles(_exeDir));

        var json = File.ReadAllText(expectedPath);
        var config = JsonSerializer.Deserialize<PolicyConfig>(json, PolicyConfig.JsonOptions);
        Assert.NotNull(config?.UserRules);
        Assert.Single(config!.UserRules!);
    }

    [Fact]
    public void SaveRule_UsesExistingFileNextToExe_WhenPolicyFilePathNull()
    {
        var exeFile = Path.Combine(_exeDir, "my-server-policy.json");
        File.WriteAllText(exeFile, "{}");
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: null, defaultFileName: "my-server-policy.json",
            fileLocator: _locator);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        Assert.Single(JsonNode.Parse(File.ReadAllText(exeFile))!["user_rules"]!.AsArray());
        Assert.False(Directory.Exists(_userDir));
    }

    [Fact]
    public void SaveRule_UsesExplicitPath_WhenProvided()
    {
        var policyFile = Path.Combine(_tempDir, "explicit.json");
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile, defaultFileName: "should-not-use.json",
            fileLocator: _locator);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        Assert.True(File.Exists(policyFile));
        Assert.False(File.Exists(Path.Combine(_exeDir, "should-not-use.json")));
        Assert.False(Directory.Exists(_userDir));
    }

    [Fact]
    public void SaveRule_CreatesMissingDirectoryOfExplicitPath()
    {
        var policyFile = Path.Combine(_tempDir, "nested", "dir", "policy.json");
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            policyFilePath: policyFile);

        engine.SaveRuleToPolicy(
            new ApprovalRule { Tools = ["build"] }, "test");

        Assert.True(File.Exists(policyFile));
    }

    [Fact]
    public void Resolve_FallsBackToPerUserPath_WhenNoFileNextToExe()
    {
        Assert.Equal(
            Path.Combine(_userDir, "my-server-policy.json"),
            _locator.Resolve(null, null, "my-server-policy.json"));
    }

    [Fact]
    public void Resolve_PrefersExistingFileNextToExe_OverPerUserFile()
    {
        var exeFile = Path.Combine(_exeDir, "my-server-policy.json");
        File.WriteAllText(exeFile, "{}");
        Directory.CreateDirectory(_userDir);
        File.WriteAllText(Path.Combine(_userDir, "my-server-policy.json"), "{}");

        Assert.Equal(exeFile, _locator.Resolve(null, null, "my-server-policy.json"));
    }

    [Fact]
    public void Resolve_ExplicitPathAndEnvVar_TakePrecedence()
    {
        File.WriteAllText(Path.Combine(_exeDir, "my-server-policy.json"), "{}");
        var envVar = $"MCPSHARP_TEST_POLICY_{Guid.NewGuid():N}";
        var envPath = Path.Combine(_tempDir, "from-env.json");
        var explicitPath = Path.Combine(_tempDir, "explicit.json");
        Environment.SetEnvironmentVariable(envVar, envPath);
        try
        {
            // Neither file needs to exist: an explicit or env path is always used.
            Assert.Equal(explicitPath, _locator.Resolve(explicitPath, envVar, "my-server-policy.json"));
            Assert.Equal(envPath, _locator.Resolve(null, envVar, "my-server-policy.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_IgnoresEmptyExplicitPathAndEnvVar(string empty)
    {
        var envVar = $"MCPSHARP_TEST_POLICY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, empty);
        try
        {
            Assert.Equal(
                Path.Combine(_userDir, "my-server-policy.json"),
                _locator.Resolve(empty, envVar, "my-server-policy.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Theory]
    [InlineData("my-server", "my-server-policy.json")]
    [InlineData(null, "policy.json")]
    [InlineData("", "policy.json")]
    public void DefaultFileNameFor_UsesEntryAssemblyName(string? assemblyName, string expected)
    {
        Assert.Equal(expected, PolicyEngine.DefaultFileNameFor(assemblyName));
    }

    [Fact]
    public void Load_NullDefaultFileName_UsesPerServerFileName()
    {
        var engine = PolicyEngine.Load<PolicyConfig>(
            new AllowAllClassifier(), new MatchAllMatcher(), fileLocator: _locator);

        var expectedName = PolicyEngine.DefaultFileNameFor(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name);
        Assert.Equal(Path.Combine(_userDir, expectedName), engine.PolicyFilePath);
    }

    [Fact]
    public void SaveRule_NullDefaultFileName_UsesPerServerFileName()
    {
        var engine = new PolicyEngine(
            new PolicyConfig(), new AllowAllClassifier(), new MatchAllMatcher(),
            fileLocator: _locator);

        engine.SaveRuleToPolicy(new ApprovalRule { Tools = ["build"] }, "test");

        Assert.True(File.Exists(Path.Combine(_userDir, PolicyEngine.DefaultFileName)));
    }

    [Fact]
    public void Load_NoFile_SavesToPerUserPath()
    {
        var engine = PolicyEngine.Load<PolicyConfig>(
            new AllowAllClassifier(), new MatchAllMatcher(),
            defaultFileName: "my-server-policy.json", fileLocator: _locator);

        var expectedPath = Path.Combine(_userDir, "my-server-policy.json");
        Assert.Equal(expectedPath, engine.PolicyFilePath);
        Assert.Null(engine.Config.UserRules);

        engine.SaveRuleToPolicy(new ApprovalRule { Tools = ["build"] }, "test");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Load_ReadsPerUserFile()
    {
        Directory.CreateDirectory(_userDir);
        var userFile = Path.Combine(_userDir, "my-server-policy.json");
        File.WriteAllText(userFile, JsonSerializer.Serialize(new PolicyConfig
        {
            UserRules = [new UserRule { Rule = new ApprovalRule { Tools = ["build"] } }],
        }, PolicyConfig.JsonOptions));

        var engine = PolicyEngine.Load<PolicyConfig>(
            new AllowAllClassifier(), new MatchAllMatcher(),
            defaultFileName: "my-server-policy.json", fileLocator: _locator);

        Assert.Equal(userFile, engine.PolicyFilePath);
        Assert.Single(engine.Config.UserRules!);
    }

    [Fact]
    public void Load_ExistingFileNextToExe_IsReadAndSavedTo()
    {
        var exeFile = Path.Combine(_exeDir, "my-server-policy.json");
        File.WriteAllText(exeFile, JsonSerializer.Serialize(new PolicyConfig
        {
            UserRules = [new UserRule { Rule = new ApprovalRule { Tools = ["build"] } }],
        }, PolicyConfig.JsonOptions));

        var engine = PolicyEngine.Load<PolicyConfig>(
            new AllowAllClassifier(), new MatchAllMatcher(),
            defaultFileName: "my-server-policy.json", fileLocator: _locator);

        Assert.Equal(exeFile, engine.PolicyFilePath);
        Assert.Single(engine.Config.UserRules!);

        engine.SaveRuleToPolicy(new ApprovalRule { Tools = ["test"] }, "test");
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(exeFile))!["user_rules"]!.AsArray().Count);
        Assert.False(Directory.Exists(_userDir));
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
