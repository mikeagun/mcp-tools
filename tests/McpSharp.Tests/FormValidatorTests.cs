// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Elicitation;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// Server-side validation of accepted content against the
/// requested constraints (the F3 rung). Only constraint-bearing fields are
/// checked; plain enum/string flows are unaffected.
/// </summary>
public class FormValidatorTests
{
    private static bool Validate(DesiredField f, JsonNode? v)
        => FormValidator.Validate(f, v, out _);

    [Fact]
    public void Required_Missing_Invalid()
    {
        var f = new DesiredField { Name = "x", Kind = FieldKind.String, Required = true };
        Assert.False(Validate(f, null));
    }

    [Fact]
    public void Optional_Missing_Valid()
    {
        var f = new DesiredField { Name = "x", Kind = FieldKind.String };
        Assert.True(Validate(f, null));
    }

    [Theory]
    [InlineData("a@b.com", true)]
    [InlineData("not-an-email", false)]
    public void StringFormat_Email(string value, bool ok)
    {
        var f = new DesiredField { Name = "e", Kind = FieldKind.String, Format = StringFormat.Email };
        Assert.Equal(ok, Validate(f, JsonValue.Create(value)));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("nope", false)]
    public void StringFormat_Uri(string value, bool ok)
    {
        var f = new DesiredField { Name = "u", Kind = FieldKind.String, Format = StringFormat.Uri };
        Assert.Equal(ok, Validate(f, JsonValue.Create(value)));
    }

    [Theory]
    [InlineData("2026-06-21", true)]
    [InlineData("2026-13-99", false)]
    [InlineData("not-a-date", false)]
    public void StringFormat_Date(string value, bool ok)
    {
        var f = new DesiredField { Name = "d", Kind = FieldKind.String, Format = StringFormat.Date };
        Assert.Equal(ok, Validate(f, JsonValue.Create(value)));
    }

    [Fact]
    public void String_LengthAndPattern()
    {
        var f = new DesiredField
        {
            Name = "s", Kind = FieldKind.String,
            MinLength = 2, MaxLength = 4, Pattern = "^[a-z]+$",
        };
        Assert.True(Validate(f, JsonValue.Create("abc")));
        Assert.False(Validate(f, JsonValue.Create("a")));     // too short
        Assert.False(Validate(f, JsonValue.Create("abcde"))); // too long
        Assert.False(Validate(f, JsonValue.Create("AB")));    // pattern
    }

    [Fact]
    public void Integer_RangeAndWholeness()
    {
        var f = new DesiredField { Name = "n", Kind = FieldKind.Integer, Minimum = 1, Maximum = 10 };
        Assert.True(Validate(f, JsonValue.Create(5)));
        Assert.False(Validate(f, JsonValue.Create(0)));    // below min
        Assert.False(Validate(f, JsonValue.Create(11)));   // above max
        Assert.False(Validate(f, JsonValue.Create(2.5)));  // not integer
    }

    [Fact]
    public void EnumSingle_MembershipEnforced()
    {
        var f = new DesiredField
        {
            Name = "c", Kind = FieldKind.EnumSingle,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b") },
        };
        Assert.True(Validate(f, JsonValue.Create("a")));
        Assert.False(Validate(f, JsonValue.Create("z")));
    }

    [Fact]
    public void EnumMulti_ItemsAndCounts()
    {
        var f = new DesiredField
        {
            Name = "t", Kind = FieldKind.EnumMulti, MinItems = 1, MaxItems = 2,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c") },
        };
        Assert.True(Validate(f, new JsonArray("a", "b")));
        Assert.False(Validate(f, new JsonArray()));              // below minItems
        Assert.False(Validate(f, new JsonArray("a", "b", "c"))); // above maxItems
        Assert.False(Validate(f, new JsonArray("a", "z")));      // invalid member
    }

    [Fact]
    public void UnconstrainedString_AlwaysValid()
    {
        var f = new DesiredField { Name = "s", Kind = FieldKind.String };
        Assert.True(Validate(f, JsonValue.Create("anything at all")));
    }
}
