// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using ElicitMcp.Tools;
using McpSharp;

namespace ElicitMcp;

public static class Program
{
    public static void Main()
    {
        // The prompt-storm-prone conformance runner is gated behind an
        // explicit demo-mode flag so it can never fire by accident.
        bool demoMode = Environment.GetEnvironmentVariable("ELICIT_MCP_DEMO_MODE") is "1" or "true";

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var (server, transport) = BuildServer(input, output, demoMode);

        Console.Error.WriteLine($"elicit-mcp: server started (demo_mode={demoMode})");

        // Concurrent dispatch lets blocking, user-interactive elicitation tools run
        // without starving the request loop.
        transport.Run((method, parameters) => server.Dispatch(method, parameters), concurrent: true);

        Console.Error.WriteLine("elicit-mcp: server stopped");
    }

    /// <summary>
    /// Wire up the server, tools, and transport over the given streams. Sets
    /// <see cref="McpServer.Transport"/> so the server-initiated elicitation tools
    /// can prompt the user. Does not start the transport. Exposed for testing.
    /// </summary>
    public static (McpServer server, McpTransport transport) BuildServer(
        Stream input, Stream output, bool demoMode = false)
    {
        var server = new McpServer("elicit-mcp");
        ToolRegistration.RegisterAll(server, demoMode);

        var transport = new McpTransport(input, output, "elicit-mcp");
        server.Transport = transport; // enable server-initiated elicitation

        return (server, transport);
    }
}
