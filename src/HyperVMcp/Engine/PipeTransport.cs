// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace HyperVMcp.Engine;

/// <summary>
/// Named pipe transport for frontend↔backend IPC when using Verb="runas" elevation
/// (which cannot redirect stdin/stdout). Uses two unidirectional pipes to avoid
/// concurrent read/write issues on a single duplex pipe.
///
/// The frontend creates both pipe servers, the elevated backend connects as a client.
///
/// Security layers:
/// 1. Random GUID pipe name — not predictable
/// 2. PipeOptions.CurrentUserOnly — OS-level ACL restricts to current user
/// 3. maxNumberOfServerInstances=1 — prevents pipe server squatting
/// 4. GetNamedPipeClientProcessId — verifies connecting client is the launched backend (kernel PID)
/// </summary>
public sealed class PipeTransport : IDisposable
{
    private readonly NamedPipeServerStream _sendPipe;
    private readonly NamedPipeServerStream _recvPipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>Base pipe name — backend appends "-send" and "-recv" suffixes.</summary>
    public string PipeName { get; }

    public StreamReader Reader => _reader ?? throw new InvalidOperationException("Not connected.");
    public StreamWriter Writer => _writer ?? throw new InvalidOperationException("Not connected.");

    public PipeTransport()
    {
        PipeName = $"hyperv-mcp-{Guid.NewGuid():N}";

        // Frontend sends on the "send" pipe, backend reads from it.
        _sendPipe = NamedPipeServerStreamAcl.Create(
            pipeName: PipeName + "-send",
            direction: PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 0,
            outBufferSize: 4096,
            pipeSecurity: null);

        // Frontend receives on the "recv" pipe, backend writes to it.
        _recvPipe = NamedPipeServerStreamAcl.Create(
            pipeName: PipeName + "-recv",
            direction: PipeDirection.In,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096,
            outBufferSize: 0,
            pipeSecurity: null);
    }

    /// <summary>
    /// Wait for the backend to connect to both pipes, then verify its PID.
    /// </summary>
    public async Task WaitForConnectionAsync(int expectedPid, int timeoutMs = 30_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);

        // Backend connects to both pipes.
        await Task.WhenAll(
            _sendPipe.WaitForConnectionAsync(cts.Token),
            _recvPipe.WaitForConnectionAsync(cts.Token));

        // Verify PID on both pipes.
        VerifyClientPid(_sendPipe, expectedPid);
        VerifyClientPid(_recvPipe, expectedPid);

        _writer = new StreamWriter(_sendPipe) { AutoFlush = true };
        _reader = new StreamReader(_recvPipe);
    }

    private static void VerifyClientPid(NamedPipeServerStream pipe, int expectedPid)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(
                pipe.SafePipeHandle.DangerousGetHandle(), out uint clientPid))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to verify pipe client identity (Win32 error {error}).");
        }

        if (clientPid != (uint)expectedPid)
        {
            throw new InvalidOperationException(
                $"Pipe client PID {clientPid} does not match expected backend PID {expectedPid}. " +
                "Connection rejected for security.");
        }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch (ObjectDisposedException) { }
        try { _reader?.Dispose(); } catch (ObjectDisposedException) { }
        _sendPipe.Dispose();
        _recvPipe.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);
    }
}

/// <summary>
/// Named pipe client used by the elevated backend to connect to the frontend's pipe servers.
/// Connects to two unidirectional pipes: one for reading (frontend's send pipe) and one
/// for writing (frontend's recv pipe).
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly NamedPipeClientStream _readPipe;
    private readonly NamedPipeClientStream _writePipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public StreamReader Reader => _reader ?? throw new InvalidOperationException("Not connected.");
    public StreamWriter Writer => _writer ?? throw new InvalidOperationException("Not connected.");

    public PipeClient(string pipeName)
    {
        // Backend reads from the frontend's send pipe.
        _readPipe = new NamedPipeClientStream(".", pipeName + "-send", PipeDirection.In);
        // Backend writes to the frontend's recv pipe.
        _writePipe = new NamedPipeClientStream(".", pipeName + "-recv", PipeDirection.Out);
    }

    /// <summary>
    /// Connect to both of the frontend's pipe servers.
    /// </summary>
    public void Connect(int timeoutMs = 10_000)
    {
        // Connect to both pipes (order doesn't matter since frontend WaitForConnectionAsync
        // runs both in parallel).
        _readPipe.Connect(timeoutMs);
        _writePipe.Connect(timeoutMs);
        _reader = new StreamReader(_readPipe);
        _writer = new StreamWriter(_writePipe) { AutoFlush = true };
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch (ObjectDisposedException) { }
        try { _reader?.Dispose(); } catch (ObjectDisposedException) { }
        _readPipe.Dispose();
        _writePipe.Dispose();
    }
}
