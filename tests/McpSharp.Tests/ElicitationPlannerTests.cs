// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Elicitation;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// The planner is a pure single-shot schema rewriter; each fallback rung
/// is tested as a pure function (no I/O): F1 titles→labels, F5 bool→Yes/No,
/// F6 number→string, F4 default pre-population (never on a decision field).
/// </summary>
public class ElicitationPlannerTests
{
    private static ClientFormSupport Caps(bool titles = true, bool arrays = true,
        bool numbers = true, bool booleans = true) => new()
    {
        Modes = ElicitationCapabilities.Parse(new JsonObject()),
        Titles = titles,
        Arrays = arrays,
        Numbers = numbers,
        Booleans = booleans,
    };

    private static readonly ElicitationPlanner Planner = new();

    [Fact]
    public void EnumSingle_TitlesSupported_EmitsOneOf()
    {
        var field = new DesiredField
        {
            Name = "color",
            Kind = FieldKind.EnumSingle,
            Choices = new[] { new EnumChoice("#FF0000", "Red"), new EnumChoice("#00FF00", "Green") },
        };
        var plan = Planner.Rewrite(field, Caps(titles: true));

        Assert.Equal("none", plan.FallbackUsed);
        Assert.NotNull(plan.Schema["oneOf"]);
        // Identity remap returns the const value unchanged.
        Assert.Equal("#FF0000", plan.Remap(JsonValue.Create("#FF0000"))!.GetValue<string>());
    }

    [Fact]
    public void F1_EnumSingleTitles_Degrades_To_LabelEnum_And_RemapsBack()
    {
        var field = new DesiredField
        {
            Name = "color",
            Kind = FieldKind.EnumSingle,
            Choices = new[] { new EnumChoice("#FF0000", "Red"), new EnumChoice("#00FF00", "Green") },
        };
        var plan = Planner.Rewrite(field, Caps(titles: false));

        Assert.Equal("F1", plan.FallbackUsed);
        Assert.Null(plan.Schema["oneOf"]);
        var labels = plan.Schema["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Red", "Green" }, labels);

        // The chosen title maps back to the underlying value.
        Assert.Equal("#FF0000", plan.Remap(JsonValue.Create("Red"))!.GetValue<string>());
    }

    [Fact]
    public void F5_Boolean_Degrades_To_YesNo_And_RemapsBack()
    {
        var field = new DesiredField { Name = "enabled", Kind = FieldKind.Boolean };
        var plan = Planner.Rewrite(field, Caps(booleans: false));

        Assert.Equal("F5", plan.FallbackUsed);
        Assert.Equal(new[] { "Yes", "No" },
            plan.Schema["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());

        Assert.True(plan.Remap(JsonValue.Create("Yes"))!.GetValue<bool>());
        Assert.False(plan.Remap(JsonValue.Create("No"))!.GetValue<bool>());
    }

    [Fact]
    public void F6_Number_Degrades_To_String_And_ParsesBack()
    {
        var field = new DesiredField { Name = "n", Kind = FieldKind.Number };
        var plan = Planner.Rewrite(field, Caps(numbers: false));

        Assert.Equal("F6", plan.FallbackUsed);
        Assert.Equal("string", plan.Schema["type"]!.GetValue<string>());
        Assert.NotNull(plan.Schema["pattern"]);

        Assert.Equal(42.5, plan.Remap(JsonValue.Create("42.5"))!.GetValue<double>());
    }

    [Fact]
    public void F6_Integer_Degrades_To_String_And_ParsesBack()
    {
        var field = new DesiredField { Name = "n", Kind = FieldKind.Integer };
        var plan = Planner.Rewrite(field, Caps(numbers: false));

        Assert.Equal(7L, plan.Remap(JsonValue.Create("7"))!.GetValue<long>());
    }

    [Fact]
    public void PlanString_DateFormat_AddsFormatHintDescription()
    {
        var field = new DesiredField { Name = "d", Kind = FieldKind.String, Format = StringFormat.Date };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("date", plan.Schema["format"]!.GetValue<string>());
        Assert.Contains("YYYY-MM-DD", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void F4_Default_AppliedTo_NonDecisionField_WhenOmitted()
    {
        var field = new DesiredField
        {
            Name = "scope",
            Kind = FieldKind.String,
            Default = JsonValue.Create("session"),
        };
        var plan = Planner.Rewrite(field, Caps());

        Assert.Equal("session", plan.Schema["default"]!.GetValue<string>());
        // Omitted (null) => default applied.
        Assert.Equal("session", plan.Remap(null)!.GetValue<string>());
        // Present => kept.
        Assert.Equal("permanent", plan.Remap(JsonValue.Create("permanent"))!.GetValue<string>());
    }

    [Fact]
    public void F4_Default_NeverAppliedTo_DecisionField()
    {
        var field = new DesiredField
        {
            Name = "action",
            Kind = FieldKind.EnumSingle,
            IsDecision = true,
            Default = JsonValue.Create("Allow"),
            Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
        };
        var plan = Planner.Rewrite(field, Caps());

        // The decision field schema must NOT carry a default...
        Assert.Null(plan.Schema["default"]);
        // ...and an omitted decision stays omitted (never defaulted to an allow).
        Assert.Null(plan.Remap(null));
    }

    [Fact]
    public void F1_EnumMultiTitles_Degrades_Items_To_LabelEnum_And_RemapsBack()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a", "Alpha"), new EnumChoice("b", "Beta") },
        };
        var plan = Planner.Rewrite(field, Caps(titles: false));

        Assert.Equal("F1", plan.FallbackUsed);
        Assert.Equal(new[] { "Alpha", "Beta" },
            plan.Schema["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());

        var remapped = plan.Remap(new JsonArray("Alpha", "Beta"))!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "a", "b" }, remapped);
    }

    [Fact]
    public void EnumMulti_ArraysUnsupported_Throws_DeferringToDriver() // F2 is driver-owned
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a") },
        };
        Assert.Throws<InvalidOperationException>(() => Planner.Rewrite(field, Caps(arrays: false)));
    }

    [Fact]
    public void Planner_IsPure_NoSchemaSharingAcrossCalls()
    {
        var field = new DesiredField { Name = "x", Kind = FieldKind.String };
        var a = Planner.Rewrite(field, Caps());
        var b = Planner.Rewrite(field, Caps());
        Assert.NotSame(a.Schema, b.Schema);
    }

    // -- Multi-enum description hint ------------------------------------------

    [Fact]
    public void EnumMulti_NoDescription_NoConstraints_InjectsDefaultHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b") },
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Select one or more", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_NoDescription_MinItems_InjectsMinHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c") },
            MinItems = 2,
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Select 2 or more", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_NoDescription_MaxItems_InjectsMaxHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c") },
            MaxItems = 2,
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Select up to 2", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_NoDescription_BothConstraints_InjectsRangeHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c"), new EnumChoice("d") },
            MinItems = 1,
            MaxItems = 3,
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Select 1-3", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_WithDescription_PreservesCallerDescription()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b") },
            Description = "Pick your favorites",
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Pick your favorites", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_F1TitleFallback_NoDescription_InjectsHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a", "Alpha"), new EnumChoice("b", "Beta") },
        };
        var plan = Planner.Rewrite(field, Caps(titles: false));
        Assert.Equal("F1", plan.FallbackUsed);
        Assert.Equal("Select one or more", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_TitlesSupported_NoDescription_InjectsHint()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a", "Alpha"), new EnumChoice("b", "Beta") },
        };
        var plan = Planner.Rewrite(field, Caps(titles: true));
        Assert.Equal("none", plan.FallbackUsed);
        Assert.Equal("Select one or more", plan.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_DefaultAndHint_Coexist()
    {
        var field = new DesiredField
        {
            Name = "tags",
            Kind = FieldKind.EnumMulti,
            Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c") },
            Default = new JsonArray("a"),
            MinItems = 1,
        };
        var plan = Planner.Rewrite(field, Caps());
        Assert.Equal("Select 1 or more", plan.Schema["description"]!.GetValue<string>());
        Assert.NotNull(plan.Schema["default"]);
        Assert.Equal("a", plan.Schema["default"]![0]!.GetValue<string>());
    }
}
