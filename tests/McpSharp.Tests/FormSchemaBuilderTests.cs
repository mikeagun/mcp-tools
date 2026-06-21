// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp.Elicitation;
using Xunit;

namespace McpSharp.Tests;

/// <summary>
/// FormSchemaBuilder emits every primitive/enum/array construct, enforces the
/// flat-object guardrail, and refuses sensitive fields.
/// </summary>
public class FormSchemaBuilderTests
{
    [Fact]
    public void String_WithAllConstraints_EmitsThem()
    {
        var schema = new FormSchemaBuilder()
            .String("email", required: true, title: "Email", description: "Your email",
                minLength: 3, maxLength: 100, pattern: ".+@.+", format: StringFormat.Email,
                @default: "a@b.com")
            .Build();

        var p = schema["properties"]!["email"]!;
        Assert.Equal("string", p["type"]!.GetValue<string>());
        Assert.Equal("Email", p["title"]!.GetValue<string>());
        Assert.Equal("Your email", p["description"]!.GetValue<string>());
        Assert.Equal(3, p["minLength"]!.GetValue<int>());
        Assert.Equal(100, p["maxLength"]!.GetValue<int>());
        Assert.Equal(".+@.+", p["pattern"]!.GetValue<string>());
        Assert.Equal("email", p["format"]!.GetValue<string>());
        Assert.Equal("a@b.com", p["default"]!.GetValue<string>());
        Assert.Contains("email", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData(StringFormat.Email, "email")]
    [InlineData(StringFormat.Uri, "uri")]
    [InlineData(StringFormat.Date, "date")]
    [InlineData(StringFormat.DateTime, "date-time")]
    public void String_Format_SerializesCorrectly(StringFormat format, string expected)
    {
        var schema = new FormSchemaBuilder().String("f", format: format).Build();
        Assert.Equal(expected, schema["properties"]!["f"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void String_DateFormat_AutoAddsFormatHint_WhenNoDescription()
    {
        var schema = new FormSchemaBuilder().String("d", format: StringFormat.Date).Build();
        Assert.Contains("YYYY-MM-DD", schema["properties"]!["d"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void String_DateTimeFormat_AutoAddsFormatHint()
    {
        var schema = new FormSchemaBuilder().String("ts", format: StringFormat.DateTime).Build();
        Assert.Contains("ISO 8601", schema["properties"]!["ts"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void String_Format_DoesNotOverrideExplicitDescription()
    {
        var schema = new FormSchemaBuilder()
            .String("d", format: StringFormat.Date, description: "Birth date").Build();
        Assert.Equal("Birth date", schema["properties"]!["d"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void String_EmailFormat_NoAutoHint() // email/uri shapes are self-evident
    {
        var schema = new FormSchemaBuilder().String("e", format: StringFormat.Email).Build();
        Assert.Null(schema["properties"]!["e"]!["description"]);
    }

    [Fact]
    public void Integer_WithMinMaxDefault()
    {
        var schema = new FormSchemaBuilder()
            .Number("count", integer: true, minimum: 1, maximum: 10, @default: 5)
            .Build();
        var p = schema["properties"]!["count"]!;
        Assert.Equal("integer", p["type"]!.GetValue<string>());
        Assert.Equal(1, p["minimum"]!.GetValue<int>());
        Assert.Equal(10, p["maximum"]!.GetValue<int>());
        Assert.Equal(5, p["default"]!.GetValue<int>());
    }

    [Fact]
    public void Number_IsDouble()
    {
        var schema = new FormSchemaBuilder().Number("ratio", minimum: 0.5).Build();
        Assert.Equal("number", schema["properties"]!["ratio"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Boolean_WithDefault()
    {
        var schema = new FormSchemaBuilder().Boolean("enabled", @default: true).Build();
        var p = schema["properties"]!["enabled"]!;
        Assert.Equal("boolean", p["type"]!.GetValue<string>());
        Assert.True(p["default"]!.GetValue<bool>());
    }

    [Fact]
    public void EnumSingle_NoTitles_EmitsEnum()
    {
        var schema = new FormSchemaBuilder()
            .EnumSingle("color", new[] { new EnumChoice("red"), new EnumChoice("green") })
            .Build();
        var p = schema["properties"]!["color"]!;
        Assert.Equal("string", p["type"]!.GetValue<string>());
        Assert.Null(p["oneOf"]);
        var en = p["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "red", "green" }, en);
    }

    [Fact]
    public void EnumSingle_WithTitles_EmitsOneOf()
    {
        var schema = new FormSchemaBuilder()
            .EnumSingle("color", new[]
            {
                new EnumChoice("#FF0000", "Red"),
                new EnumChoice("#00FF00", "Green"),
            })
            .Build();
        var p = schema["properties"]!["color"]!;
        Assert.Equal("string", p["type"]!.GetValue<string>());
        Assert.Null(p["enum"]);
        var oneOf = p["oneOf"]!.AsArray();
        Assert.Equal("#FF0000", oneOf[0]!["const"]!.GetValue<string>());
        Assert.Equal("Red", oneOf[0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void EnumMulti_NoTitles_EmitsItemsEnum()
    {
        var schema = new FormSchemaBuilder()
            .EnumMulti("tags", new[] { new EnumChoice("a"), new EnumChoice("b") },
                minItems: 1, maxItems: 2)
            .Build();
        var p = schema["properties"]!["tags"]!;
        Assert.Equal("array", p["type"]!.GetValue<string>());
        Assert.Equal("string", p["items"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "a", "b" },
            p["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
        Assert.Equal(1, p["minItems"]!.GetValue<int>());
        Assert.Equal(2, p["maxItems"]!.GetValue<int>());
    }

    [Fact]
    public void EnumMulti_WithTitles_EmitsItemsAnyOf()
    {
        var schema = new FormSchemaBuilder()
            .EnumMulti("tags", new[] { new EnumChoice("a", "Alpha"), new EnumChoice("b", "Beta") })
            .Build();
        var items = schema["properties"]!["tags"]!["items"]!;
        var anyOf = items["anyOf"]!.AsArray();
        Assert.Equal("a", anyOf[0]!["const"]!.GetValue<string>());
        Assert.Equal("Alpha", anyOf[0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void Build_NestedObject_Throws()
    {
        var builder = new FormSchemaBuilder()
            .AddField("nested", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["x"] = new JsonObject { ["type"] = "string" } },
            });
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("nested objects are not allowed", ex.Message);
    }

    [Fact]
    public void Build_ArrayOfObjects_Throws()
    {
        var builder = new FormSchemaBuilder()
            .AddField("rows", new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
            });
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("api_key")]
    [InlineData("apiKey")]
    [InlineData("access_token")]
    [InlineData("github_secret")]
    [InlineData("credential")]
    [InlineData("card_number")]
    public void String_SensitiveFieldName_Throws(string name)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FormSchemaBuilder().String(name));
    }

    [Fact]
    public void String_SensitiveFlag_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FormSchemaBuilder().String("answer", sensitive: true));
    }

    [Fact]
    public void IsSensitiveFieldName_Predicate()
    {
        Assert.True(FormSchemaBuilder.IsSensitiveFieldName("user_password"));
        Assert.True(FormSchemaBuilder.IsSensitiveFieldName("APIKEY"));
        Assert.False(FormSchemaBuilder.IsSensitiveFieldName("username"));
        Assert.False(FormSchemaBuilder.IsSensitiveFieldName("color"));
    }

    [Fact]
    public void Build_NoFields_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new FormSchemaBuilder().Build());
    }

    [Fact]
    public void Add_DuplicateName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FormSchemaBuilder().String("x").String("x"));
    }
}
