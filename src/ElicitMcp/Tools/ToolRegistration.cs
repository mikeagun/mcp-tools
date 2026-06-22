// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using McpSharp;

namespace ElicitMcp.Tools;

/// <summary>
/// Central registration for elicit-mcp tools.
///
/// Production (always): the agent-facing <c>request_decision</c> / <c>request_input</c>
/// tools and the no-prompt <c>report_capabilities</c> — a minimal 3-tool surface.
///
/// Demo mode (ELICIT_MCP_DEMO_MODE=1): adds the parameterized <c>elicit_demo</c>
/// conformance tool and the <c>run_conformance</c> batch runner. These are gated to
/// keep the production surface small and avoid accidental prompt storms.
/// </summary>
public static class ToolRegistration
{
    public static void RegisterAll(McpServer server, bool demoMode = false)
    {
        // Always-on production surface.
        AgentTools.Register(server);
        HarnessTools.RegisterCapabilities(server, demoMode);

        // Demo / conformance surface.
        if (demoMode)
        {
            DemoTools.Register(server);
            HarnessTools.RegisterConformance(server);
        }
    }
}
