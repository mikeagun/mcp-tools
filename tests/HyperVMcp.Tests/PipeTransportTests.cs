// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using Xunit;

namespace HyperVMcp.Tests;

public class PipeTransportTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    [Fact]
    public async Task PipeTransport_RoundTrip_JsonLineProtocol()
    {
        // Create server (frontend side).
        using var transport = new PipeTransport();
        var pipeName = transport.PipeName;

        Assert.StartsWith("hyperv-mcp-", pipeName);

        // Simulate backend connecting.
        var clientTask = Task.Run(async () =>
        {
            using var client = new PipeClient(pipeName);
            client.Connect(5000);

            // Send ready signal.
            var ready = new JsonObject { ["id"] = "ready", ["status"] = "ok" };
            client.Writer.WriteLine(ready.ToJsonString());

            // Read a request.
            var line = await client.Reader.ReadLineAsync();
            var request = JsonNode.Parse(line!)!.AsObject();
            Assert.Equal("r-1", request["id"]!.GetValue<string>());
            Assert.Equal("Get-VM", request["script"]!.GetValue<string>());

            // Send response.
            var response = new JsonObject
            {
                ["id"] = "r-1",
                ["status"] = "ok",
                ["output"] = new JsonArray("test-output")
            };
            client.Writer.WriteLine(response.ToJsonString());
        });

        // Server side: wait for connection (same process = PID match).
        await transport.WaitForConnectionAsync(Environment.ProcessId, 10_000);

        // Read ready signal.
        var readyLine = await transport.Reader.ReadLineAsync();
        var readyMsg = JsonNode.Parse(readyLine!)!.AsObject();
        Assert.Equal("ready", readyMsg["id"]!.GetValue<string>());
        Assert.Equal("ok", readyMsg["status"]!.GetValue<string>());

        // Send request.
        var req = new JsonObject { ["id"] = "r-1", ["script"] = "Get-VM" };
        transport.Writer.WriteLine(req.ToJsonString());

        // Read response.
        var respLine = await transport.Reader.ReadLineAsync();
        var resp = JsonNode.Parse(respLine!)!.AsObject();
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.Equal("test-output", resp["output"]![0]!.GetValue<string>());

        await clientTask;
    }

    [Fact]
    public async Task PipeTransport_PidVerification_RejectsWrongPid()
    {
        using var transport = new PipeTransport();

        // Connect from same process but verify against wrong PID.
        var clientTask = Task.Run(() =>
        {
            try
            {
                using var client = new PipeClient(transport.PipeName);
                client.Connect(5000);
                // Keep alive while server checks PID.
                Thread.Sleep(2000);
            }
            catch { /* server may disconnect us */ }
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            // Verify against a PID that doesn't match our process.
            await transport.WaitForConnectionAsync(99999, 10_000);
        });

        Assert.Contains("does not match", ex.Message);
        await clientTask;
    }

    [Fact]
    public async Task PipeTransport_PidVerification_AcceptsCorrectPid()
    {
        using var transport = new PipeTransport();

        var clientTask = Task.Run(() =>
        {
            using var client = new PipeClient(transport.PipeName);
            client.Connect(5000);
        });

        // Should succeed with our own PID.
        await transport.WaitForConnectionAsync(Environment.ProcessId, 10_000);

        await clientTask;
    }

    [Fact]
    public async Task PipeTransport_DiagMessages_CanBeInterleavedWithResponses()
    {
        using var transport = new PipeTransport();

        var clientTask = Task.Run(async () =>
        {
            using var client = new PipeClient(transport.PipeName);
            client.Connect(5000);

            // Send ready.
            client.Writer.WriteLine(new JsonObject { ["id"] = "ready", ["status"] = "ok" }.ToJsonString());

            // Read a request.
            await client.Reader.ReadLineAsync();

            // Send a diag message first.
            client.Writer.WriteLine(new JsonObject { ["type"] = "diag", ["line"] = "Executing: whoami" }.ToJsonString());

            // Then send the actual response.
            client.Writer.WriteLine(new JsonObject
            {
                ["id"] = "r-1",
                ["status"] = "ok",
                ["output"] = new JsonArray("result")
            }.ToJsonString());
        });

        await transport.WaitForConnectionAsync(Environment.ProcessId, 10_000);

        // Read ready.
        await transport.Reader.ReadLineAsync();

        // Send request.
        transport.Writer.WriteLine(new JsonObject { ["id"] = "r-1", ["script"] = "whoami" }.ToJsonString());

        // Read first message — should be diag.
        var msg1 = JsonNode.Parse((await transport.Reader.ReadLineAsync())!)!.AsObject();
        Assert.Equal("diag", msg1["type"]!.GetValue<string>());
        Assert.Equal("Executing: whoami", msg1["line"]!.GetValue<string>());

        // Read second message — should be the response.
        var msg2 = JsonNode.Parse((await transport.Reader.ReadLineAsync())!)!.AsObject();
        Assert.Equal("r-1", msg2["id"]!.GetValue<string>());
        Assert.Equal("ok", msg2["status"]!.GetValue<string>());

        await clientTask;
    }

    [Fact]
    public void PipeTransport_UniquePipeNames()
    {
        using var t1 = new PipeTransport();
        using var t2 = new PipeTransport();
        Assert.NotEqual(t1.PipeName, t2.PipeName);
    }

    [Fact]
    public void PipeTransport_MaxInstances_PreventsDuplicateServer()
    {
        using var transport = new PipeTransport();

        // Attempting to create another server with the same send pipe name should fail.
        Assert.Throws<IOException>(() =>
        {
            using var duplicate = NamedPipeServerStreamAcl.Create(
                pipeName: transport.PipeName + "-send",
                direction: PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                transmissionMode: PipeTransmissionMode.Byte,
                options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 0,
                outBufferSize: 4096,
                pipeSecurity: null);
        });
    }

    [Fact]
    public void PipeClient_ConnectTimeout_ThrowsOnNoServer()
    {
        var fakePipeName = $"hyperv-mcp-{Guid.NewGuid():N}";
        using var client = new PipeClient(fakePipeName);

        // Should timeout since no server exists.
        Assert.Throws<TimeoutException>(() => client.Connect(500));
    }

    [Fact]
    public async Task PipeTransport_GetNamedPipeClientProcessId_ReturnsCorrectPid()
    {
        var pipeName = $"hyperv-mcp-test-{Guid.NewGuid():N}";

        using var server = NamedPipeServerStreamAcl.Create(
            pipeName: pipeName,
            direction: PipeDirection.In,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 0,
            pipeSecurity: null);

        var clientTask = Task.Run(async () =>
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            await client.ConnectAsync(5000);
            await Task.Delay(2000); // Keep alive while server checks PID.
        });

        await server.WaitForConnectionAsync();

        var result = GetNamedPipeClientProcessId(
            server.SafePipeHandle.DangerousGetHandle(), out uint clientPid);

        Assert.True(result, "GetNamedPipeClientProcessId should succeed");
        Assert.Equal((uint)Environment.ProcessId, clientPid);

        await clientTask;
    }
}
