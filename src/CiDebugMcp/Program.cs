using CiDebugMcp.Engine;
using McpSharp;
using CiDebugMcp.Tools;

namespace CiDebugMcp;

public static class Program
{
    public static void Main()
    {
        var cache = new LogCache();
        var github = new GitHubClient(cache);
        var binaryAnalyzer = new BinaryAnalyzer();
        var downloadManager = new DownloadManager(github);
        var resolver = new CiProviderResolver(github, cache);
        var server = new McpServer("ci-debug-mcp");

        // Register tools with provider resolver for GitHub + ADO support
        ToolRegistration.RegisterAll(server, github, binaryAnalyzer, downloadManager, resolver);

        Console.Error.WriteLine("ci-debug-mcp: server started");

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var transport = new McpTransport(input, output, "ci-debug-mcp");

        transport.Run((method, parameters) => server.Dispatch(method, parameters));

        Console.Error.WriteLine("ci-debug-mcp: server stopped");
    }
}
