// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// Central tool registration — wires all tool groups to the MCP server.
/// </summary>
public static class ToolRegistration
{
    public static void RegisterAll(
        McpServer server,
        Engine.SessionManager sessionManager,
        Engine.VmManager vmManager,
        Engine.CommandRunner commandRunner,
        Engine.FileTransferManager fileTransferManager)
    {
        SessionTools.Register(server, sessionManager);
        VmTools.Register(server, vmManager, sessionManager);
        CommandTools.Register(server, commandRunner);
        CommandTools.RegisterOutputTools(server, commandRunner);
        FileTools.Register(server, fileTransferManager);
        ServiceTools.Register(server, sessionManager);
        VmInfoTools.Register(server, sessionManager);
        ProcessTools.Register(server, sessionManager);
        EnvTools.Register(server, sessionManager);
    }
}
