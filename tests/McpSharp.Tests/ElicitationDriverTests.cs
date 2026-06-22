// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using McpSharp.Elicitation;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// Exercises the driver loop: orchestrates multi-round-trip degradations (F2 multi-select
/// → booleans, F3 re-validate-and-retry) and the no-prompt sentinel (F8), with a
/// scripted MemoryStream stub client (no live client). Enforces decision-field
/// deny-safety: an accept with the decision field omitted is never a grant.
/// </summary>
public class ElicitationDriverTests
{
    private static (McpServer server, MemoryStream input, MemoryStream output) Setup()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var transport = new McpTransport(input, output, "test");

        var dummy = Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        var server = new McpServer("test");
        server.Transport = transport;
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });
        return (server, input, output);
    }

    private static ClientFormSupport FormCaps(bool titles = true, bool arrays = true,
        bool numbers = true, bool booleans = true) => new()
    {
        Modes = ElicitationCapabilities.Parse(new JsonObject()),
        Titles = titles,
        Arrays = arrays,
        Numbers = numbers,
        Booleans = booleans,
    };

    private static void Preload(MemoryStream input, params JsonObject[] responses)
    {
        input.SetLength(0);
        input.Position = 0;
        var sb = new StringBuilder();
        foreach (var r in responses)
            sb.Append(r.ToJsonString()).Append('\n');
        input.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        input.Position = 0;
    }

    private static JsonObject Accept(string id, JsonObject content) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = new JsonObject { ["action"] = "accept", ["content"] = JsonNode.Parse(content.ToJsonString()) },
    };

    private static JsonObject Action(string id, string action) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = new JsonObject { ["action"] = action },
    };

    // -- F8 -------------------------------------------------------------------

    [Fact]
    public void F8_NoFormSupport_ReturnsNoPrompt_WithoutWriting()
    {
        var (server, _, output) = Setup();
        var driver = new ElicitationDriver(server);

        var outcome = driver.Run("msg",
            new[] { new DesiredField { Name = "x", Kind = FieldKind.String } },
            new ClientFormSupport { Modes = ElicitationCapabilities.None });

        Assert.Equal(DriverStatus.NoPrompt, outcome.Status);
        Assert.False(outcome.DecisionMade);
        Assert.Equal(0, output.Length);
    }

    // -- F2 -------------------------------------------------------------------

    [Fact]
    public void F2_MultiSelect_ArraysUnsupported_ExpandsToBooleans_AndRecombines()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // Client selects choices 0 and 2 via booleans.
        Preload(input, Accept("s-1", new JsonObject
        {
            ["tags__0"] = true,
            ["tags__1"] = false,
            ["tags__2"] = true,
        }));

        var outcome = driver.Run("Pick tags",
            new[]
            {
                new DesiredField
                {
                    Name = "tags",
                    Kind = FieldKind.EnumMulti,
                    Choices = new[] { new EnumChoice("a"), new EnumChoice("b"), new EnumChoice("c") },
                },
            },
            FormCaps(arrays: false));

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.Contains("F2", outcome.FallbacksUsed);
        var tags = outcome.Values!["tags"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "a", "c" }, tags);
    }

    // -- F3 -------------------------------------------------------------------

    [Fact]
    public void F3_InvalidThenValid_RetriesAndAccepts()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // First response too short (minLength 5), second is valid.
        Preload(input,
            Accept("s-1", new JsonObject { ["code"] = "ab" }),
            Accept("s-2", new JsonObject { ["code"] = "abcde" }));

        var outcome = driver.Run("Enter code",
            new[]
            {
                new DesiredField { Name = "code", Kind = FieldKind.String, Required = true, MinLength = 5 },
            },
            FormCaps(), maxRetries: 1);

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.Equal("abcde", outcome.Values!["code"]!.GetValue<string>());
    }

    [Fact]
    public void F3_InvalidBeyondRetries_ReturnsInvalid()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        Preload(input, Accept("s-1", new JsonObject { ["code"] = "ab" }));

        var outcome = driver.Run("Enter code",
            new[]
            {
                new DesiredField { Name = "code", Kind = FieldKind.String, Required = true, MinLength = 5 },
            },
            FormCaps(), maxRetries: 0);

        Assert.Equal(DriverStatus.Invalid, outcome.Status);
        Assert.False(outcome.DecisionMade);
    }

    // -- Decision-field deny-safety -----------------------------------

    [Fact]
    public void DecisionField_Omitted_IsNeverGranted()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // Accept, but the (optional) decision field is omitted — must NOT be a grant.
        Preload(input, Accept("s-1", new JsonObject { ["note"] = "looks fine" }));

        var outcome = driver.Run("Approve?",
            new[]
            {
                new DesiredField
                {
                    Name = "action",
                    Kind = FieldKind.EnumSingle,
                    IsDecision = true,
                    Default = JsonValue.Create("Allow"), // even a default must not grant
                    Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
                },
                new DesiredField { Name = "note", Kind = FieldKind.String },
            },
            FormCaps(), maxRetries: 0);

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.False(outcome.DecisionMade);          // omitted decision => deny-safe
        Assert.Null(outcome.Values!["action"]);          // no default fabricated
    }

    [Fact]
    public void DecisionField_Present_IsGranted()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        Preload(input, Accept("s-1", new JsonObject { ["action"] = "Allow" }));

        var outcome = driver.Run("Approve?",
            new[]
            {
                new DesiredField
                {
                    Name = "action",
                    Kind = FieldKind.EnumSingle,
                    IsDecision = true,
                    Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
                },
            },
            FormCaps());

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.True(outcome.DecisionMade);
        Assert.Equal("Allow", outcome.Values!["action"]!.GetValue<string>());
    }

    [Fact]
    public void DecisionField_ExplicitDeny_IsMade_ButValueIsDeny()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // An explicit negative answer is still a decision that was MADE — callers
        // must read the value to learn it was a denial.
        Preload(input, Accept("s-1", new JsonObject { ["action"] = "Deny" }));

        var outcome = driver.Run("Approve?",
            new[]
            {
                new DesiredField
                {
                    Name = "action",
                    Kind = FieldKind.EnumSingle,
                    IsDecision = true,
                    Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
                },
            },
            FormCaps());

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.True(outcome.DecisionMade);                                    // a choice WAS made...
        Assert.Equal("Deny", outcome.Values!["action"]!.GetValue<string>()); // ...and it was a denial
    }

    [Fact]
    public void Integer_NativeNumber_FractionalValue_IsRejected_NotTruncated()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // The client supports native numbers and returns 5.7 for an integer field
        // whose maximum is 5. The fractional value must be rejected, not silently
        // truncated to 5 (which would slip past both the wholeness and range checks).
        Preload(input, Accept("s-1", new JsonObject { ["count"] = 5.7 }));

        var outcome = driver.Run("How many?",
            new[]
            {
                new DesiredField
                {
                    Name = "count",
                    Kind = FieldKind.Integer,
                    Maximum = 5,
                },
            },
            FormCaps(numbers: true), maxRetries: 0);

        Assert.Equal(DriverStatus.Invalid, outcome.Status);
    }

    // -- F1 via the driver ----------------------------------------------------

    [Fact]
    public void F1_TitlesUnsupported_DriverRemapsTitleToValue()
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        // Client returns the displayed title; the driver maps it back to the value.
        Preload(input, Accept("s-1", new JsonObject { ["color"] = "Red" }));

        var outcome = driver.Run("Pick a color",
            new[]
            {
                new DesiredField
                {
                    Name = "color",
                    Kind = FieldKind.EnumSingle,
                    Choices = new[] { new EnumChoice("#FF0000", "Red"), new EnumChoice("#00FF00", "Green") },
                },
            },
            FormCaps(titles: false));

        Assert.Equal(DriverStatus.Accepted, outcome.Status);
        Assert.Contains("F1", outcome.FallbacksUsed);
        Assert.Equal("#FF0000", outcome.Values!["color"]!.GetValue<string>());
    }

    // -- Non-accept actions ---------------------------------------------------

    [Theory]
    [InlineData("decline", DriverStatus.Declined)]
    [InlineData("cancel", DriverStatus.Cancelled)]
    public void NonAccept_IsNeverAGrant(string action, DriverStatus expected)
    {
        var (server, input, _) = Setup();
        var driver = new ElicitationDriver(server);

        Preload(input, Action("s-1", action));

        var outcome = driver.Run("Approve?",
            new[]
            {
                new DesiredField
                {
                    Name = "action",
                    Kind = FieldKind.EnumSingle,
                    IsDecision = true,
                    Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
                },
            },
            FormCaps());

        Assert.Equal(expected, outcome.Status);
        Assert.False(outcome.DecisionMade);
    }
}
