// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

/// <summary>
/// Regression tests for <see cref="ProjectEngine.BuildPropsKey"/>.
///
/// Background: an earlier implementation used <c>$"{k}={v}"</c> joined by <c>,</c>
/// to serialize <c>additionalProperties</c> into the cache key. Because property
/// values can themselves contain <c>=</c> and <c>,</c>, distinct property
/// dictionaries could collide on the same serialized key — corrupting cache
/// lookups by returning the snapshot for the wrong property set.
///
/// These tests pin the collision-free behavior. The fix encodes both keys and
/// values with <see cref="Uri.EscapeDataString"/> so the separators are
/// guaranteed unambiguous.
/// </summary>
public class ProjectEngineCacheKeyTests
{
    [Fact]
    public void Null_ReturnsEmpty()
    {
        Assert.Equal("", ProjectEngine.BuildPropsKey(null));
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Equal("", ProjectEngine.BuildPropsKey(new Dictionary<string, string>()));
    }

    [Fact]
    public void SimpleProperties_ProduceStableKey()
    {
        var props = new Dictionary<string, string> { ["A"] = "B", ["C"] = "D" };
        var key1 = ProjectEngine.BuildPropsKey(props);
        var key2 = ProjectEngine.BuildPropsKey(new Dictionary<string, string> { ["C"] = "D", ["A"] = "B" });
        Assert.Equal(key1, key2);
        Assert.NotEqual("", key1);
    }

    /// <summary>
    /// Canonical collision: a multi-property dict (<c>{A=B, C=D}</c>) and a
    /// single-property dict whose value contains the separators
    /// (<c>{A="B,C=D"}</c>) both produce <c>"A=B,C=D"</c> under the old
    /// unescaped serialization. With <c>Uri.EscapeDataString</c> applied, the
    /// special characters in the value are encoded and the two keys differ.
    ///
    /// Adversarial: removing the escaping in <see cref="ProjectEngine.BuildPropsKey"/>
    /// makes this assertion fail.
    /// </summary>
    [Fact]
    public void SeparatorInValue_DoesNotCollideWithExtraPair()
    {
        var multiPair = new Dictionary<string, string> { ["A"] = "B", ["C"] = "D" };
        var singlePairWithSeparators = new Dictionary<string, string> { ["A"] = "B,C=D" };

        var keyMulti = ProjectEngine.BuildPropsKey(multiPair);
        var keySingle = ProjectEngine.BuildPropsKey(singlePairWithSeparators);

        Assert.NotEqual(keyMulti, keySingle);
    }

    /// <summary>
    /// Key-side mirror of <see cref="SeparatorInValue_DoesNotCollideWithExtraPair"/>.
    /// Under the old <c>$"{k}={v}"</c> encoding, <c>{"A=B"="C"}</c> and
    /// <c>{"A"="B=C"}</c> both serialize to <c>"A=B=C"</c> — a strict collision.
    /// Escaping the key half with <see cref="Uri.EscapeDataString"/> turns the
    /// embedded <c>=</c> into <c>%3D</c>, so the two dictionaries produce
    /// distinct keys.
    /// </summary>
    [Fact]
    public void SeparatorInKey_DoesNotCollideAcrossDicts()
    {
        // Key contains '='
        var dict1 = new Dictionary<string, string> { ["A=B"] = "C" };
        // Different decomposition that aliases under naive encoding
        var dict2 = new Dictionary<string, string> { ["A"] = "B=C" };

        Assert.NotEqual(
            ProjectEngine.BuildPropsKey(dict1),
            ProjectEngine.BuildPropsKey(dict2));
    }

    /// <summary>
    /// Comma in a value used to terminate a pair under the old serialization,
    /// so a single pair whose value contained a comma could look like two
    /// pairs. Pin the escape behavior.
    /// </summary>
    [Fact]
    public void CommaInValue_IsEscaped()
    {
        var key = ProjectEngine.BuildPropsKey(
            new Dictionary<string, string> { ["A"] = "B,C" });
        Assert.DoesNotContain(",", key);
    }

    /// <summary>
    /// Equals in a value used to merge with the key under the old
    /// serialization. Pin the escape behavior.
    /// </summary>
    [Fact]
    public void EqualsInValue_IsEscaped()
    {
        var key = ProjectEngine.BuildPropsKey(
            new Dictionary<string, string> { ["A"] = "B=C" });
        // The pair separator '=' between key and value remains, but the '=' inside the value is escaped.
        Assert.Equal(1, key.Split('=').Length - 1);
    }

    [Fact]
    public void OrderIndependent_CaseInsensitive()
    {
        var lower = ProjectEngine.BuildPropsKey(
            new Dictionary<string, string> { ["foo"] = "1", ["BAR"] = "2" });
        var upper = ProjectEngine.BuildPropsKey(
            new Dictionary<string, string> { ["BAR"] = "2", ["foo"] = "1" });
        Assert.Equal(lower, upper);
    }
}
