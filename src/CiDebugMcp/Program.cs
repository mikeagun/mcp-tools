// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using CiDebugMcp.Engine;
using McpSharp;
using CiDebugMcp.Tools;

namespace CiDebugMcp;

public static class Program
{
    public static void Main()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var (server, transport, github) = BuildServer(input, output);

        github.WarmAuth(); // Start auth resolution in background to avoid first-call timeout

        Console.Error.WriteLine("ci-debug-mcp: server started");

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        Console.Error.WriteLine("ci-debug-mcp: server stopped");
    }

    /// <summary>
    /// Wire up the server, tools, and transport over the given streams.
    /// Sets <see cref="McpServer.Transport"/> so server-initiated elicitation
    /// (e.g. the auth-failure retry prompt) is enabled. Does not start the
    /// transport or warm auth — callers do that. Exposed for testing.
    /// </summary>
    internal static (McpServer server, McpTransport transport, GitHubClient github) BuildServer(
        Stream input, Stream output)
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var binaryAnalyzer = new BinaryAnalyzer();
        var downloadManager = new DownloadManager(github);
        var resolver = new CiProviderResolver(github, cache);
        var server = new McpServer("ci-debug-mcp");

        // Register tools with provider resolver for GitHub + ADO support
        ToolRegistration.RegisterAll(server, github, binaryAnalyzer, downloadManager, resolver);

        var transport = new McpTransport(input, output, "ci-debug-mcp");
        server.Transport = transport; // enable server-initiated elicitation (e.g. auth-failure retry prompts)

        return (server, transport, github);
    }
}
