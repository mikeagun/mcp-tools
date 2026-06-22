// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using McpSharp;
using Xunit;

namespace ElicitMcp.Tests;

/// <summary>
/// Registration, dispatch, and scripted-client round-trip tests for elicit-mcp.
/// Production mode exposes a minimal 3-tool surface; demo mode adds the
/// parameterized conformance tools. No live client / network.
/// </summary>
public class ElicitMcpTests
{
    private static readonly string[] ProductionTools =
        { "request_decision", "request_input", "report_capabilities" };

    private static readonly string[] DemoTools =
        { "request_decision", "request_input", "report_capabilities", "elicit_demo", "run_conformance" };

    // -- Registration / mode gating -------------------------------------------

    [Fact]
    public void ProductionMode_Registers_OnlyThreeTools()
    {
        var (server, _) = ElicitMcp.Program.BuildServer(new MemoryStream(), new MemoryStream(), demoMode: false);
        Assert.Equal(ProductionTools.ToHashSet(), ToolNames(server));
    }

    [Fact]
    public void DemoMode_Registers_FiveTools()
    {
        var (server, _) = ElicitMcp.Program.BuildServer(new MemoryStream(), new MemoryStream(), demoMode: true);
        Assert.Equal(DemoTools.ToHashSet(), ToolNames(server));
    }

    [Fact]
    public void ProductionMode_DoesNotExpose_DemoTools()
    {
        var names = ToolNames(ElicitMcp.Program.BuildServer(new MemoryStream(), new MemoryStream()).server);
        Assert.DoesNotContain("elicit_demo", names);
        Assert.DoesNotContain("run_conformance", names);
    }

    [Fact]
    public void Initialize_ReportsServerName()
    {
        var (server, _) = ElicitMcp.Program.BuildServer(new MemoryStream(), new MemoryStream());
        var init = server.Dispatch("initialize", new JsonObject())!;
        Assert.Equal("elicit-mcp", init["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildServer_SetsTransport()
    {
        var (server, transport) = ElicitMcp.Program.BuildServer(new MemoryStream(), new MemoryStream());
        Assert.Same(transport, server.Transport);
    }

    // -- Agent-facing: request_decision ---------------------------------------

    [Fact]
    public void RequestDecision_Accept_Grants()
    {
        var (server, input, _) = Setup();
        Preload(input, Accept("s-1", new JsonObject { ["decision"] = "Allow" }));

        var r = CallTool(server, "request_decision", new JsonObject
        {
            ["message"] = "Approve?",
            ["options"] = new JsonArray("Allow", "Deny"),
        });

        Assert.True(r["decision_made"]!.GetValue<bool>());
        Assert.Equal("Allow", r["decision"]!.GetValue<string>());
    }

    [Fact]
    public void RequestDecision_OmittedDecision_IsNotGranted()
    {
        var (server, input, _) = Setup();
        Preload(input, Accept("s-1", new JsonObject()));

        var r = CallTool(server, "request_decision", new JsonObject
        {
            ["message"] = "Approve?",
            ["options"] = new JsonArray("Allow", "Deny"),
        });

        Assert.False(r["decision_made"]!.GetValue<bool>());
        Assert.Null(r["decision"]);
    }

    // -- Agent-facing: request_input (incl. folded feedback) ------------------

    [Fact]
    public void RequestInput_TypedFields_ReturnsValues()
    {
        var (server, input, _) = Setup();
        Preload(input, Accept("s-1", new JsonObject { ["age"] = 30 }));

        var r = CallTool(server, "request_input", new JsonObject
        {
            ["message"] = "Your age?",
            ["fields"] = new JsonArray(new JsonObject
            {
                ["name"] = "age",
                ["type"] = "integer",
                ["min"] = 1,
                ["max"] = 120,
                ["required"] = true,
            }),
        });

        Assert.Equal("accepted", r["status"]!.GetValue<string>());
        Assert.Equal(30, r["values"]!["age"]!.GetValue<int>());
    }

    [Fact]
    public void RequestInput_AsFreeTextFeedback_ReturnsText()
    {
        var (server, input, _) = Setup();
        Preload(input, Accept("s-1", new JsonObject { ["feedback"] = "great tool" }));

        var r = CallTool(server, "request_input", new JsonObject
        {
            ["message"] = "Thoughts?",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "feedback", ["type"] = "string" }),
        });

        Assert.Equal("great tool", r["values"]!["feedback"]!.GetValue<string>());
    }

    [Fact]
    public void RequestInput_SensitiveFieldName_Refused()
    {
        var (server, input, _) = Setup();
        Preload(input, Accept("s-1", new JsonObject { ["password"] = "x" }));

        var r = CallTool(server, "request_input", new JsonObject
        {
            ["message"] = "Secret?",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "password", ["type"] = "string" }),
        });

        Assert.Equal("refused", r["status"]!.GetValue<string>());
    }

    // -- report_capabilities (always-on) --------------------------------------

    [Fact]
    public void ReportCapabilities_ReflectsAdvertised_AndDemoModeFlag()
    {
        var (server, _, _) = Setup(demoMode: false);
        var r = CallTool(server, "report_capabilities", new JsonObject());
        Assert.True(r["advertised"]!["form"]!.GetValue<bool>());
        Assert.False(r["advertised"]!["url"]!.GetValue<bool>());
        Assert.False(r["demo_mode_active"]!.GetValue<bool>());
    }

    [Fact]
    public void ReportCapabilities_DemoMode_AdvertisesActive()
    {
        var (server, _, _) = Setup(demoMode: true);
        var r = CallTool(server, "report_capabilities", new JsonObject());
        Assert.True(r["demo_mode_active"]!.GetValue<bool>());
    }

    // -- elicit_demo (demo mode) ----------------------------------------------

    [Fact]
    public void ElicitDemo_EnumTitled_EmitsOneOfSchema()
    {
        var (server, input, output) = Setup(demoMode: true);
        Preload(input, Accept("s-1", new JsonObject { ["color"] = "#FF0000" }));

        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "enum_titled" });

        Assert.Equal("enum_titled", r["case"]!.GetValue<string>());
        var sent = FirstElicitation(output);
        Assert.NotNull(sent["params"]!["requestedSchema"]!["properties"]!["color"]!["oneOf"]);
    }

    [Fact]
    public void ElicitDemo_FallbackTitles_ForcesF1()
    {
        var (server, input, output) = Setup(demoMode: true);
        Preload(input, Accept("s-1", new JsonObject { ["color"] = "Red" }));

        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "fallback_titles" });

        var color = FirstElicitation(output)["params"]!["requestedSchema"]!["properties"]!["color"]!;
        Assert.Null(color["oneOf"]);
        Assert.NotNull(color["enum"]);
        Assert.Contains("F1", r["fallback_used"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("#FF0000", r["values"]!["color"]!.GetValue<string>());
    }

    [Fact]
    public void ElicitDemo_FallbackMultiselect_ForcesF2_Recombined()
    {
        var (server, input, _) = Setup(demoMode: true);
        Preload(input, Accept("s-1", new JsonObject
        {
            ["colors__0"] = true,
            ["colors__1"] = false,
            ["colors__2"] = true,
        }));

        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "fallback_multiselect" });

        Assert.Contains("F2", r["fallback_used"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "red", "blue" },
            r["values"]!["colors"]!.AsArray().Select(n => n!.GetValue<string>()).ToList());
    }

    [Fact]
    public void ElicitDemo_FallbackUnsupported_NoPrompt()
    {
        var (server, _, _) = Setup(demoMode: true);
        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "fallback_unsupported" });
        Assert.Equal("no-prompt", r["verdict"]!.GetValue<string>());
    }

    [Fact]
    public void ElicitDemo_DecisionFieldSafety_OmittedDecisionNotGranted()
    {
        var (server, input, _) = Setup(demoMode: true);
        Preload(input, Accept("s-1", new JsonObject()));

        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "decision_field_safety" });
        Assert.False(r["decision_made"]!.GetValue<bool>());
    }

    [Fact]
    public void ElicitDemo_SensitiveFieldGuard_Fires_NoPrompt()
    {
        var (server, _, output) = Setup(demoMode: true);
        var r = CallTool(server, "elicit_demo", new JsonObject { ["case"] = "sensitive_field_guard" });

        Assert.True(r["guard_fired"]!.GetValue<bool>());
        Assert.Equal("pass", r["verdict"]!.GetValue<string>());
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ElicitDemo_SimulateCapsOverride_ForcesBooleanFallback()
    {
        // 'boolean' case has no forced support, so simulate_caps drives the fallback.
        var (server, input, _) = Setup(demoMode: true);
        Preload(input, Accept("s-1", new JsonObject { ["enabled"] = "Yes" }));

        var r = CallTool(server, "elicit_demo", new JsonObject
        {
            ["case"] = "boolean",
            ["simulate_caps"] = new JsonObject { ["booleans"] = false },
        });

        Assert.Contains("F5", r["fallback_used"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(r["values"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ElicitDemo_UnknownCase_Errors()
    {
        var (server, _, _) = Setup(demoMode: true);
        var res = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = "elicit_demo",
            ["arguments"] = new JsonObject { ["case"] = "does_not_exist" },
        })!;
        Assert.True(res["isError"]?.GetValue<bool>());
    }

    // -- run_conformance (demo mode) ------------------------------------------

    [Fact]
    public void RunConformance_NotRegistered_InProductionMode()
    {
        var (server, _, _) = Setup(demoMode: false);
        Assert.DoesNotContain("run_conformance", ToolNames(server));
    }

    [Fact]
    public void RunConformance_DemoMode_RunsSubset()
    {
        var (server, input, _) = Setup(demoMode: true);
        Preload(input,
            Accept("s-1", new JsonObject { ["text"] = "hi" }),
            Accept("s-2", new JsonObject { ["color"] = "red" }),
            Accept("s-3", new JsonObject { ["color"] = "r" }),
            Accept("s-4", new JsonObject { ["enabled"] = true }),
            Accept("s-5", new JsonObject { ["action"] = "Allow" }));

        var r = CallTool(server, "run_conformance", new JsonObject());

        Assert.Equal(5, r["results"]!.AsArray().Count);
        Assert.Equal(5, r["summary"]!["pass"]!.GetValue<int>());
    }

    // -- Helpers --------------------------------------------------------------

    private static HashSet<string> ToolNames(McpServer server)
        => server.Dispatch("tools/list", null)!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToHashSet();

    private static (McpServer server, MemoryStream input, MemoryStream output) Setup(bool demoMode = false)
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, transport) = ElicitMcp.Program.BuildServer(input, output, demoMode);

        var dummy = Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });
        return (server, input, output);
    }

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

    private static JsonNode CallTool(McpServer server, string name, JsonObject args)
    {
        var res = server.Dispatch("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args })!;
        var text = res["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!;
    }

    private static JsonNode FirstElicitation(MemoryStream output)
    {
        output.Position = 0;
        var raw = Encoding.UTF8.GetString(output.ToArray());
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = JsonNode.Parse(line)!;
            if (node["method"]?.GetValue<string>() == "elicitation/create")
                return node;
        }
        throw new Xunit.Sdk.XunitException("No elicitation/create request was written.");
    }
}
