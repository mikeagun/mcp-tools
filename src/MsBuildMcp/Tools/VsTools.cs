// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

/// <summary>
/// Visual Studio selection: the shared <c>vs</c> tool argument and <c>list_vs_installations</c>.
/// </summary>
public static class VsTools
{
    /// <summary>Schema for the optional <c>vs</c> argument shared by build and evaluation tools.</summary>
    internal static JsonObject VsArgSchema(string usedFor) => new()
    {
        ["type"] = "string",
        ["description"] = $"Visual Studio installation to use for {usedFor}: 'latest', a product year " +
                          "('2022', '2026'), a version prefix ('17', '17.14'), a version range ('[17,18)'), " +
                          "or an installation path from list_vs_installations. Omit to use the server default.",
    };

    /// <summary>Read the optional <c>vs</c> argument (null when absent or blank).</summary>
    internal static string? GetVsArg(JsonObject args) =>
        args["vs"]?.GetValue<string>() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public static void Register(McpServer server, VsToolchainProvider vs)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "list_vs_installations",
            Description = "List the Visual Studio installations (discovered via vswhere) that can be selected " +
                          "with the 'vs' parameter of build and project-evaluation tools, and which one is " +
                          "the server default. Returns: { default_selector, default?, default_error?, " +
                          "installations: [{ display_name, version, path, year?, prerelease?, is_default, " +
                          "has_cpp_targets }] }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["required"] = new JsonArray(),
            },
            Handler = _ => ListInstallations(vs),
        });
    }

    internal static JsonObject ListInstallations(VsToolchainProvider vs)
    {
        var result = new JsonObject { ["default_selector"] = vs.DefaultSpec ?? "latest" };

        VsInstance? defaultInstance = null;
        try
        {
            defaultInstance = vs.ResolveInstance();
            result["default"] = defaultInstance.ToJson();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            result["default_error"] = ex.Message;
        }

        var installations = new JsonArray();
        foreach (var i in vs.Locator.Instances)
        {
            var obj = i.ToJson();
            obj["is_default"] = defaultInstance != null && string.Equals(
                i.InstallationPath, defaultInstance.InstallationPath, StringComparison.OrdinalIgnoreCase);
            obj["has_cpp_targets"] = i.VcTargetsPath != null;
            installations.Add(obj);
        }
        result["installations"] = installations;
        return result;
    }
}
