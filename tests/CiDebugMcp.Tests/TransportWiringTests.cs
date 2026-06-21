// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using McpSharp;
using Xunit;

namespace CiDebugMcp.Tests;

/// <summary>
/// Regression: CiDebugMcp must set <see cref="McpServer.Transport"/> when
/// wiring the server, otherwise <see cref="McpServer.Elicit"/> returns null and the
/// auth-failure retry elicitation can never fire (every auth failure falls straight
/// through to the STOP error). The defect was a missing
/// <c>server.Transport = transport</c> line in Program.cs.
/// </summary>
public class TransportWiringTests
{
    [Fact]
    public void BuildServer_WiresTransport_EnablingElicitation()
    {
        var (server, transport, _) = Program.BuildServer(new MemoryStream(), new MemoryStream());

        Assert.NotNull(server.Transport);
        Assert.Same(transport, server.Transport);
    }

    [Fact]
    public void WiredServer_OnAuthFailure_FiresElicitation_NotStopError()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        var (server, transport, _) = Program.BuildServer(input, output);

        // Lock NDJSON framing, then clear the streams for the test exchange.
        var dummy = Encoding.UTF8.GetBytes("{\"_\":0}\n");
        input.Write(dummy);
        input.Position = 0;
        transport.ReadMessage();
        input.SetLength(0);
        input.Position = 0;
        output.SetLength(0);
        output.Position = 0;

        // Client advertises elicitation support.
        server.Dispatch("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
        });

        // Register a tool that fails authentication on first call, succeeds on retry.
        int calls = 0;
        bool resetCalled = false;
        server.RegisterTool(new ToolInfo
        {
            Name = "auth_probe",
            Description = "test",
            InputSchema = new JsonObject { ["type"] = "object" },
            Handler = _ =>
            {
                calls++;
                if (calls == 1)
                    throw new AuthenticationException("GitHub", "token expired", "Re-run gh auth login")
                    {
                        ResetAuth = () => resetCalled = true,
                    };
                return new JsonObject { ["ok"] = true };
            },
        });

        // Client responds "retry" to the auth elicitation.
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "s-1",
            ["result"] = new JsonObject
            {
                ["action"] = "accept",
                ["content"] = new JsonObject { ["action"] = "retry" },
            },
        };
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
        input.Write(bytes);
        input.Position = 0;

        var result = server.Dispatch("tools/call", new JsonObject
        {
            ["name"] = "auth_probe",
            ["arguments"] = new JsonObject(),
        });

        // Elicitation fired (transport was wired): an elicitation/create was written...
        output.Position = 0;
        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("elicitation/create", wire);

        // ...the retry path ran (ResetAuth invoked, handler re-executed)...
        Assert.True(resetCalled);
        Assert.Equal(2, calls);

        // ...and the final result is the successful tool output, not a STOP error.
        Assert.Null(result!["isError"]);
        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("\"ok\":true", text);
    }
}
