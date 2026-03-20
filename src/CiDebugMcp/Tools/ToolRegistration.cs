using CiDebugMcp.Engine;
using McpSharp;

namespace CiDebugMcp.Tools;

public static class ToolRegistration
{
    public static void RegisterAll(McpServer server, IGitHubApi github,
        BinaryAnalyzer binaryAnalyzer, DownloadManager downloadManager,
        CiProviderResolver? resolver = null)
    {
        LogTools.Register(server, github, resolver);
        BinaryTools.Register(server, binaryAnalyzer);
        ArtifactTools.Register(server, github, downloadManager, resolver);
    }
}
