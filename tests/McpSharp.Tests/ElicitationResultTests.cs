// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// Typed accessors over an accepted elicitation's content.
/// </summary>
public class ElicitationResultTests
{
    private static ElicitationResult Accepted(JsonObject content)
        => new() { Action = ElicitationAction.Accept, Content = content };

    [Fact]
    public void GetString_ReturnsValue_OrNull()
    {
        var r = Accepted(new JsonObject { ["name"] = "alice", ["n"] = 5 });
        Assert.Equal("alice", r.GetString("name"));
        Assert.Null(r.GetString("missing"));
        Assert.Null(r.GetString("n")); // numeric, not a string
    }

    [Fact]
    public void GetNumber_ReturnsValue_OrNull()
    {
        var r = Accepted(new JsonObject { ["n"] = 42, ["d"] = 3.5, ["s"] = "x" });
        Assert.Equal(42, r.GetNumber("n"));
        Assert.Equal(3.5, r.GetNumber("d"));
        Assert.Null(r.GetNumber("s"));
        Assert.Null(r.GetNumber("missing"));
    }

    [Fact]
    public void GetBool_ReturnsValue_OrNull()
    {
        var r = Accepted(new JsonObject { ["b"] = true, ["s"] = "true" });
        Assert.True(r.GetBool("b"));
        Assert.Null(r.GetBool("s"));
        Assert.Null(r.GetBool("missing"));
    }

    [Fact]
    public void GetArray_ReturnsStrings_OrNull()
    {
        var r = Accepted(new JsonObject { ["tags"] = new JsonArray("a", "b"), ["s"] = "x" });
        Assert.Equal(new[] { "a", "b" }, r.GetArray("tags"));
        Assert.Null(r.GetArray("s"));
        Assert.Null(r.GetArray("missing"));
    }

    [Fact]
    public void TryGet_Typed()
    {
        var r = Accepted(new JsonObject { ["n"] = 7, ["s"] = "hi" });
        Assert.True(r.TryGet<int>("n", out var n));
        Assert.Equal(7, n);
        Assert.True(r.TryGet<string>("s", out var s));
        Assert.Equal("hi", s);
        Assert.False(r.TryGet<int>("missing", out _));
    }

    [Fact]
    public void Accessors_NullContent_ReturnNull()
    {
        var r = new ElicitationResult { Action = ElicitationAction.Decline };
        Assert.Null(r.GetString("x"));
        Assert.Null(r.GetNumber("x"));
        Assert.Null(r.GetBool("x"));
        Assert.Null(r.GetArray("x"));
        Assert.False(r.TryGet<int>("x", out _));
    }
}
