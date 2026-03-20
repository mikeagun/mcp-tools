// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tool for querying VM system information.
/// </summary>
public static class VmInfoTools
{
    public static void Register(McpServer server, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_vm_info",
            Description = "Get OS build info, disk space, memory, and system state from a VM. Fails if a command is running on the session — wait for it to complete first.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                },
                ["required"] = new JsonArray("session_id"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var cmd = @"
                    $os = Get-CimInstance Win32_OperatingSystem
                    $disk = Get-CimInstance Win32_LogicalDisk -Filter ""DriveType=3"" | Select-Object -First 1
                    [PSCustomObject]@{
                        Hostname = $env:COMPUTERNAME
                        OSVersion = $os.Version
                        OSBuild = $os.BuildNumber
                        OSCaption = $os.Caption
                        TotalMemoryMB = [math]::Round($os.TotalVisibleMemorySize / 1024)
                        FreeMemoryMB = [math]::Round($os.FreePhysicalMemory / 1024)
                        DiskTotalGB = [math]::Round($disk.Size / 1GB)
                        DiskFreeGB = [math]::Round($disk.FreeSpace / 1GB)
                        Uptime = ((Get-Date) - $os.LastBootUpTime).ToString()
                    } | ConvertTo-Json -Depth 2
                ";

                var (output, errors) = sessionManager.ExecuteOnVmSync(sessionId, cmd);
                var errorText = string.Join("\n", errors.Where(e => !string.IsNullOrWhiteSpace(e)));
                var jsonText = string.Join("\n", output);

                // Parse the JSON output into structured response.
                try
                {
                    if (!string.IsNullOrEmpty(errorText))
                        throw new InvalidOperationException(errorText);
                    var parsed = JsonNode.Parse(jsonText);
                    if (parsed != null)
                        return parsed.AsObject();
                }
                catch (InvalidOperationException) { throw; }
                catch { /* fall through to raw output */ }

                var result = new JsonObject { ["output"] = jsonText };
                if (!string.IsNullOrEmpty(errorText))
                    result["error"] = errorText;
                return result;
            },
        });
    }
}
