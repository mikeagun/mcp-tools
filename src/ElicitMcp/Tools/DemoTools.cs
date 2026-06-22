// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp;
using McpSharp.Elicitation;

namespace ElicitMcp.Tools;

/// <summary>
/// The validation purpose of elicit-mcp, collapsed into a single parameterized tool.
/// <c>elicit_demo(case, ...)</c> triggers one named construct/variant (every primitive,
/// format, enum, multi-select, response action, fallback rung, and safety check) and
/// returns a machine-checkable conformance record. Each <c>case</c>
/// carries its own forced capability config so a fallback rung fires deterministically
/// even without <c>simulate_caps</c>. Registered only in demo mode.
/// </summary>
internal static class DemoTools
{
    private static readonly EnumChoice[] PlainChoices = { new("red"), new("green"), new("blue") };
    private static readonly EnumChoice[] TitledChoices =
        { new("#FF0000", "Red"), new("#00FF00", "Green"), new("#0000FF", "Blue") };

    private sealed record DemoCase(
        string Feature, Func<DesiredField[]> Fields,
        Func<McpServer, JsonObject, ClientFormSupport>? Support = null,
        int Timeout = 120, bool NoPrompt = false);

    private static readonly Dictionary<string, DemoCase> Cases = BuildCases();

    internal static void Register(McpServer server)
    {
        var caseEnum = new JsonArray();
        foreach (var key in Cases.Keys) caseEnum.Add(key);

        var props = new JsonObject
        {
            ["case"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Which construct/fallback to demonstrate.",
                ["enum"] = caseEnum,
            },
            ["message"] = new JsonObject { ["type"] = "string", ["description"] = "Optional prompt override." },
        };
        ElicitHelpers.AddCommonProps(props); // note + simulate_caps

        server.RegisterTool(new ToolInfo
        {
            Name = "elicit_demo",
            Description = "[demo] Trigger one named elicitation construct/variant and return a conformance record "
                + "{ feature, case, status, fallback_used, decision_made, verdict, values? }. "
                + "The 'sensitive_field_guard' case sends no prompt and returns { guard_fired, verdict }. "
                + "Use 'simulate_caps' to force a fallback rung; fallback_* cases force their own degradation by default.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray("case"),
            },
            Handler = args =>
            {
                var caseName = args["case"]?.GetValue<string>();
                if (caseName == null || !Cases.TryGetValue(caseName, out var c))
                    throw new ArgumentException(
                        $"Unknown case '{caseName}'. Valid cases: {string.Join(", ", Cases.Keys)}.");

                var rec = c.NoPrompt
                    ? SensitiveGuardRecord()
                    : ElicitHelpers.RecordFromOutcome(c.Feature, new ElicitationDriver(server).Run(
                        ElicitHelpers.MessageArg(args, c.Feature),
                        c.Fields(),
                        c.Support != null ? c.Support(server, args) : ElicitHelpers.ResolveSupport(server, args),
                        c.Timeout, maxRetries: 0));
                rec["case"] = caseName;
                if (args["note"]?.GetValue<string>() is { } note) rec["note"] = note;
                return rec;
            },
        });
    }

    // -- sensitive_field_guard (no prompt; distinct return shape) -------------

    private static JsonObject SensitiveGuardRecord()
    {
        try
        {
            new FormSchemaBuilder().String("password").Build();
            return new JsonObject
            {
                ["case"] = "sensitive_field_guard",
                ["guard_fired"] = false,
                ["verdict"] = "fail",
                ["guard_detail"] = "builder did not refuse a sensitive field",
            };
        }
        catch (InvalidOperationException ex)
        {
            return new JsonObject
            {
                ["case"] = "sensitive_field_guard",
                ["guard_fired"] = true,
                ["verdict"] = "pass",
                ["guard_detail"] = ex.Message,
            };
        }
    }

    // -- Case table -----------------------------------------------------------

    private static Dictionary<string, DemoCase> BuildCases()
    {
        var cases = new Dictionary<string, DemoCase>(StringComparer.Ordinal);

        void Add(string name, DemoCase c) => cases[name] = c;
        DesiredField[] One(DesiredField f) => new[] { f };

        // Primitives
        Add("string", new("string",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String, Title = "Text" })));
        Add("string_constrained", new("string_constrained",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String, MinLength = 3, MaxLength = 10, Pattern = "^[a-z]+$" })));
        Add("integer", new("integer",
            () => One(new DesiredField { Name = "n", Kind = FieldKind.Integer, Minimum = 1, Maximum = 10 })));
        Add("number", new("number",
            () => One(new DesiredField { Name = "n", Kind = FieldKind.Number, Minimum = 0, Maximum = 100 })));
        Add("boolean", new("boolean",
            () => One(new DesiredField { Name = "enabled", Kind = FieldKind.Boolean, Default = JsonValue.Create(false) })));

        // Formats & defaults
        Add("email", new("format_email",
            () => One(new DesiredField { Name = "email", Kind = FieldKind.String, Format = StringFormat.Email })));
        Add("uri", new("format_uri",
            () => One(new DesiredField { Name = "uri", Kind = FieldKind.String, Format = StringFormat.Uri })));
        Add("date", new("format_date",
            () => One(new DesiredField { Name = "date", Kind = FieldKind.String, Format = StringFormat.Date })));
        Add("datetime", new("format_datetime",
            () => One(new DesiredField { Name = "ts", Kind = FieldKind.String, Format = StringFormat.DateTime })));
        Add("string_default", new("string_default",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String, Default = JsonValue.Create("preset") })));

        // Enums
        Add("enum", new("enum",
            () => One(Enum("color", PlainChoices))));
        Add("enum_titled", new("enum_titled",
            () => One(Enum("color", TitledChoices))));
        Add("multiselect", new("multiselect",
            () => One(Multi("colors", PlainChoices, 1, 3))));
        Add("multiselect_titled", new("multiselect_titled",
            () => One(Multi("colors", TitledChoices, 1, 3))));
        Add("enum_default", new("enum_default",
            () => One(new DesiredField { Name = "color", Kind = FieldKind.EnumSingle, Choices = PlainChoices, Default = JsonValue.Create("green") })));

        // Response actions
        Add("action_accept", new("action_accept",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String })));
        Add("action_decline", new("action_decline",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String })));
        Add("action_cancel", new("action_cancel",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String })));
        Add("action_timeout", new("action_timeout",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String }), Timeout: 5));

        // Fallbacks — each forces its own degradation by default.
        Add("fallback_titles", new("fallback_titles",
            () => One(Enum("color", TitledChoices)), Support: ForceTitlesOff));
        Add("fallback_multiselect", new("fallback_multiselect",
            () => One(Multi("colors", PlainChoices, 1, 3)), Support: ForceArraysOff));
        Add("fallback_boolean", new("fallback_boolean",
            () => One(new DesiredField { Name = "enabled", Kind = FieldKind.Boolean }), Support: ForceBooleansOff));
        Add("fallback_number", new("fallback_number",
            () => One(new DesiredField { Name = "n", Kind = FieldKind.Number, Minimum = 0 }), Support: ForceNumbersOff));
        Add("fallback_unsupported", new("fallback_unsupported",
            () => One(new DesiredField { Name = "text", Kind = FieldKind.String }), Support: ForceFormOff));
        Add("fallback_feedback", new("fallback_feedback",
            () => new[]
            {
                new DesiredField { Name = "decision", Kind = FieldKind.EnumSingle, IsDecision = true, Required = true,
                    Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") } },
                new DesiredField { Name = "feedback", Kind = FieldKind.String },
            }));

        // Safety checks
        Add("decision_field_safety", new("decision_field_safety",
            () => One(new DesiredField
            {
                Name = "action", Kind = FieldKind.EnumSingle, IsDecision = true,
                Default = JsonValue.Create("Allow"), // a default must NOT cause a grant
                Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") },
            })));
        Add("sensitive_field_guard", new("sensitive_field_guard",
            () => Array.Empty<DesiredField>(), NoPrompt: true));

        return cases;
    }

    // -- Field factories ------------------------------------------------------

    private static DesiredField Enum(string name, EnumChoice[] choices)
        => new() { Name = name, Kind = FieldKind.EnumSingle, Choices = choices };

    private static DesiredField Multi(string name, EnumChoice[] choices, int min, int max)
        => new() { Name = name, Kind = FieldKind.EnumMulti, Choices = choices, MinItems = min, MaxItems = max };

    // -- Forced-fallback support factories ------------------------------------

    private static ClientFormSupport ForceTitlesOff(McpServer s, JsonObject a)
        => HasSim(a) ? ElicitHelpers.ResolveSupport(s, a) : new ClientFormSupport { Modes = s.ElicitationCaps, Titles = false };

    private static ClientFormSupport ForceArraysOff(McpServer s, JsonObject a)
        => HasSim(a) ? ElicitHelpers.ResolveSupport(s, a) : new ClientFormSupport { Modes = s.ElicitationCaps, Arrays = false };

    private static ClientFormSupport ForceBooleansOff(McpServer s, JsonObject a)
        => HasSim(a) ? ElicitHelpers.ResolveSupport(s, a) : new ClientFormSupport { Modes = s.ElicitationCaps, Booleans = false };

    private static ClientFormSupport ForceNumbersOff(McpServer s, JsonObject a)
        => HasSim(a) ? ElicitHelpers.ResolveSupport(s, a) : new ClientFormSupport { Modes = s.ElicitationCaps, Numbers = false };

    private static ClientFormSupport ForceFormOff(McpServer s, JsonObject a)
        => HasSim(a)
            ? ElicitHelpers.ResolveSupport(s, a)
            : new ClientFormSupport { Modes = new ElicitationCapabilities { Supported = false, Form = false, Url = false } };

    private static bool HasSim(JsonObject args) => args["simulate_caps"] is JsonObject;
}
