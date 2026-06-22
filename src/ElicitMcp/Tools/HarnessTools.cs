// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using McpSharp;
using McpSharp.Elicitation;

namespace ElicitMcp.Tools;

/// <summary>
/// Harness / reporting tools. <c>report_capabilities</c> is always registered (it sends
/// no prompt and lets an agent adapt before eliciting, and discover whether the demo
/// harness is active). <c>run_conformance</c> is registered only in demo mode — it is
/// the real prompt-storm surface.
/// </summary>
internal static class HarnessTools
{
    /// <summary>Always-on capability reporting (production + demo).</summary>
    internal static void RegisterCapabilities(McpServer server, bool demoMode)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "report_capabilities",
            Description = "Report the elicitation capabilities the client advertised at initialize "
                + "(form/url modes; an empty elicitation object is interpreted as form-only). Sends no prompt. "
                + "Returns: { advertised: { supported, form, url }, empty_object_is_form_only, demo_mode_active }.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                var caps = server.ElicitationCaps;
                return new JsonObject
                {
                    ["advertised"] = new JsonObject
                    {
                        ["supported"] = caps.Supported,
                        ["form"] = caps.Form,
                        ["url"] = caps.Url,
                    },
                    ["empty_object_is_form_only"] = true,
                    // Lets a conformance client discover the harness even when the demo
                    // tools are not registered (set ELICIT_MCP_DEMO_MODE=1 to enable them).
                    ["demo_mode_active"] = demoMode,
                };
            },
        });
    }

    /// <summary>Demo-only conformance runner.</summary>
    internal static void RegisterConformance(McpServer server)
    {
        var props = new JsonObject();
        ElicitHelpers.AddCommonProps(props);

        server.RegisterTool(new ToolInfo
        {
            Name = "run_conformance",
            Description = "[demo] Run a scripted subset of elicitation constructs and emit a single conformance "
                + "report { client, results: [...], summary }. Registered only in demo mode "
                + "(ELICIT_MCP_DEMO_MODE=1) to avoid accidental prompt storms.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = props },
            Handler = args =>
            {
                var support = ElicitHelpers.ResolveSupport(server, args);
                var driver = new ElicitationDriver(server);
                var results = new JsonArray();
                int pass = 0, fail = 0, needsHuman = 0;

                foreach (var item in Subset())
                {
                    var outcome = driver.Run(item.Feature, item.Fields(), support,
                        ElicitHelpers.DefaultTimeoutSeconds, maxRetries: 0);
                    var rec = ElicitHelpers.RecordFromOutcome(item.Feature, outcome);
                    results.Add(rec);
                    switch (rec["verdict"]?.GetValue<string>())
                    {
                        case "pass": pass++; break;
                        case "fail": fail++; break;
                        default: needsHuman++; break;
                    }
                }

                var report = new JsonObject
                {
                    ["client"] = new JsonObject
                    {
                        ["advertised"] = new JsonObject
                        {
                            ["form"] = server.ElicitationCaps.Form,
                            ["url"] = server.ElicitationCaps.Url,
                        },
                    },
                    ["results"] = results,
                    ["summary"] = new JsonObject
                    {
                        ["pass"] = pass,
                        ["fail"] = fail,
                        ["needs_human"] = needsHuman,
                    },
                };
                if (args["note"]?.GetValue<string>() is { } note) report["note"] = note;
                return report;
            },
        });
    }

    private sealed record SubsetItem(string Feature, Func<DesiredField[]> Fields);

    private static IEnumerable<SubsetItem> Subset()
    {
        yield return new("string",
            () => new[] { new DesiredField { Name = "text", Kind = FieldKind.String } });
        yield return new("enum",
            () => new[] { new DesiredField { Name = "color", Kind = FieldKind.EnumSingle, Choices = new[] { new EnumChoice("red"), new EnumChoice("green") } } });
        yield return new("enum_titled",
            () => new[] { new DesiredField { Name = "color", Kind = FieldKind.EnumSingle, Choices = new[] { new EnumChoice("r", "Red"), new EnumChoice("g", "Green") } } });
        yield return new("boolean",
            () => new[] { new DesiredField { Name = "enabled", Kind = FieldKind.Boolean } });
        yield return new("decision_field_safety",
            () => new[] { new DesiredField { Name = "action", Kind = FieldKind.EnumSingle, IsDecision = true, Choices = new[] { new EnumChoice("Allow"), new EnumChoice("Deny") } } });
    }
}
