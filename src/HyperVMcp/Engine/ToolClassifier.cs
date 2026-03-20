// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using McpSharp.Policy;

namespace HyperVMcp.Engine;

/// <summary>
/// Static classification of tools by risk tier and category.
/// </summary>
public static class ToolClassifier
{
    /// <summary>
    /// Tool categories for grouping related tools in approval suggestions.
    /// </summary>
    public static class Categories
    {
        public const string VmLifecycle = "vm_lifecycle";
        public const string VmSession = "vm_session";
        public const string VmCommand = "vm_command";
        public const string FileTransfer = "file_transfer";
        public const string ServiceManagement = "service_management";
        public const string ProcessManagement = "process_management";
        public const string ReadOnly = "read_only";
    }

    private sealed record ToolMeta(RiskTier Tier, string Category);

    private static readonly Dictionary<string, ToolMeta> ToolRegistry = new()
    {
        // Read-only tools
        ["list_vms"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["list_sessions"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["get_command_status"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["get_command_output"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["search_command_output"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["get_services"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["get_vm_info"] = new(RiskTier.ReadOnly, Categories.ReadOnly),
        ["list_vm_files"] = new(RiskTier.ReadOnly, Categories.ReadOnly),

        // Session tools
        ["connect_vm"] = new(RiskTier.Moderate, Categories.VmSession),
        ["disconnect_vm"] = new(RiskTier.Session, Categories.VmSession),
        ["reconnect_vm"] = new(RiskTier.Session, Categories.VmSession),
        ["set_env"] = new(RiskTier.Session, Categories.VmSession),

        // Moderate tools
        ["start_vm"] = new(RiskTier.Moderate, Categories.VmLifecycle),
        ["checkpoint_vm"] = new(RiskTier.Moderate, Categories.VmLifecycle),
        ["invoke_command"] = new(RiskTier.Moderate, Categories.VmCommand),
        ["run_script"] = new(RiskTier.Moderate, Categories.VmCommand),
        ["copy_to_vm"] = new(RiskTier.Moderate, Categories.FileTransfer),
        ["copy_from_vm"] = new(RiskTier.Moderate, Categories.FileTransfer),
        ["manage_service"] = new(RiskTier.Moderate, Categories.ServiceManagement),

        // Destructive tools
        ["stop_vm"] = new(RiskTier.Destructive, Categories.VmLifecycle),
        ["restart_vm"] = new(RiskTier.Destructive, Categories.VmLifecycle),
        ["restore_vm"] = new(RiskTier.Destructive, Categories.VmLifecycle),
        ["kill_process"] = new(RiskTier.Destructive, Categories.ProcessManagement),
        ["cancel_command"] = new(RiskTier.Destructive, Categories.VmCommand),
        ["free_command_output"] = new(RiskTier.Destructive, Categories.ReadOnly),
        ["save_command_output"] = new(RiskTier.Destructive, Categories.VmCommand),
    };

    /// <summary>
    /// Get the risk tier for a tool. Unknown tools default to Destructive (safe default).
    /// </summary>
    public static RiskTier GetRiskTier(string toolName)
        => ToolRegistry.TryGetValue(toolName, out var meta) ? meta.Tier : RiskTier.Destructive;

    /// <summary>
    /// Get the category for a tool. Unknown tools return null.
    /// </summary>
    public static string? GetCategory(string toolName)
        => ToolRegistry.TryGetValue(toolName, out var meta) ? meta.Category : null;

    /// <summary>
    /// Get all tool names in a given category.
    /// </summary>
    public static IReadOnlyList<string> GetToolsInCategory(string category)
        => ToolRegistry.Where(kv => kv.Value.Category == category)
            .Select(kv => kv.Key).ToList();

    /// <summary>
    /// Whether a tool is registered (known to the classifier).
    /// </summary>
    public static bool IsKnown(string toolName) => ToolRegistry.ContainsKey(toolName);

    /// <summary>
    /// Determine the default policy decision for a tool based on its risk tier and the policy mode.
    /// </summary>
    public static PolicyDecision DefaultDecision(RiskTier tier, PolicyMode mode) => mode switch
    {
        PolicyMode.Unrestricted => PolicyDecision.Allow,
        PolicyMode.Standard => tier switch
        {
            RiskTier.ReadOnly => PolicyDecision.Allow,
            RiskTier.Session => PolicyDecision.Allow,
            RiskTier.Moderate => PolicyDecision.Confirm,
            RiskTier.Destructive => PolicyDecision.Confirm,
            _ => PolicyDecision.Confirm,
        },
        PolicyMode.Restricted => tier switch
        {
            RiskTier.ReadOnly => PolicyDecision.Allow,
            _ => PolicyDecision.Confirm,
        },
        PolicyMode.Lockdown => tier switch
        {
            RiskTier.ReadOnly => PolicyDecision.Allow,
            _ => PolicyDecision.Deny,
        },
        _ => PolicyDecision.Confirm,
    };
}
