// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using HyperVMcp.Tools;
using McpSharp;
using McpSharp.Policy;

namespace HyperVMcp;

public static class Program
{
    public static void Main(string[] args)
    {
        // If launched as the elevated backend subprocess, run the backend loop.
        if (args.Length > 0 && args[0] == "--elevated-backend")
        {
            // Parse required --pipe argument.
            string? pipeName = null;
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == "--pipe")
                {
                    pipeName = args[i + 1];
                    break;
                }
            }
            if (pipeName == null)
            {
                Console.Error.WriteLine("hyperv-mcp: --elevated-backend requires --pipe <name>");
                Environment.Exit(1);
                return;
            }
            ElevatedBackendHost.Run(pipeName);
            return;
        }

        // Parse --policy <path> argument.
        string? policyPath = null;
        int? toolTimeout = null;
        int? userTimeout = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--policy")
                policyPath = args[i + 1];
            else if (args[i] == "--tool-timeout")
                toolTimeout = int.Parse(args[i + 1]);
            else if (args[i] == "--user-timeout")
                userTimeout = int.Parse(args[i + 1]);
        }

        // Normal MCP server mode — starts non-elevated.
        RunServer(policyPath, toolTimeout, userTimeout);
    }

    private static void RunServer(string? policyPath, int? toolTimeout, int? userTimeout)
    {
        // Load HyperV policy config first (needed to configure backend timeout).
        var (hvConfig, resolvedPolicyPath) = LoadHyperVConfig(policyPath);

        // Command-line overrides take precedence over policy file.
        if (toolTimeout.HasValue)
            hvConfig.BackendTimeoutSeconds = toolTimeout.Value;
        if (userTimeout.HasValue)
            hvConfig.ConfirmationTimeoutSeconds = userTimeout.Value;

        var effectiveToolTimeout = hvConfig.BackendTimeoutSeconds;
        var effectiveUserTimeout = hvConfig.ConfirmationTimeoutSeconds;

        // Initialize engines.
        var elevatedBackend = new ElevatedBackend
        {
            DefaultTimeoutMs = effectiveToolTimeout * 1000,
        };
        var credentialStore = new CredentialStore();
        var sessionManager = new SessionManager(credentialStore, elevatedBackend);
        var vmManager = new VmManager(elevatedBackend);
        var commandRunner = new CommandRunner(sessionManager);
        var fileTransferManager = new FileTransferManager(sessionManager, commandRunner);

        // Wire session busy-check (avoids circular dependency between SessionManager and CommandRunner).
        sessionManager.ActiveCommandChecker = commandRunner.GetActiveCommand;

        // Create shared policy components.
        var classifier = new HyperVToolClassifier(hvConfig);
        classifier.SessionVmResolver = sessionId =>
        {
            try { return sessionManager.GetSession(sessionId).VmName; }
            catch { return null; }
        };
        var matcher = new HyperVRuleMatcher(classifier);
        var optionGenerator = new HyperVOptionGenerator(classifier);
        var policy = new PolicyEngine(hvConfig, classifier, matcher, resolvedPolicyPath);

        // Create MCP server and register tools.
        var server = new McpServer("hyperv-mcp");
        ToolRegistration.RegisterAll(server, sessionManager, vmManager, commandRunner, fileTransferManager);

        // Bind to stdio and enter event loop.
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var transport = new McpTransport(input, output, "hyperv-mcp");
        server.Transport = transport;

        Console.Error.WriteLine($"hyperv-mcp: server started (tool-timeout={effectiveToolTimeout}s, user-timeout={effectiveUserTimeout}s)");

        try
        {
            // Wrap dispatch with policy guardrails.
            transport.Run((method, parameters) =>
            {
                // ApproveFlagMode bypass: strip approve flag and allow without policy check.
                if (method == "tools/call" && hvConfig.ApproveFlagMode)
                {
                    var approveArgs = parameters?["arguments"]?.AsObject();
                    if (approveArgs != null && approveArgs.ContainsKey("approve")
                        && approveArgs["approve"]?.GetValue<bool>() == true)
                    {
                        approveArgs.Remove("approve");
                        return server.Dispatch(method, parameters);
                    }
                }

                return PolicyDispatch.Dispatch(method, parameters, server, policy, optionGenerator,
                    preValidator: (toolName, args) => PreValidateSession(toolName, args, classifier),
                    elicitationTimeoutSeconds: effectiveUserTimeout);
            });
        }
        finally
        {
            Console.Error.WriteLine("hyperv-mcp: server stopping");
            commandRunner.Dispose();
            sessionManager.Dispose();
            elevatedBackend.Dispose();
            Console.Error.WriteLine("hyperv-mcp: server stopped");
        }
    }

    // -- Helpers ----------------------------------------------------------

    static JsonNode? PreValidateSession(string toolName, JsonObject args, HyperVToolClassifier classifier)
    {
        // connect_vm creates a new session — session_id is an optional custom ID, not a reference.
        if (toolName == "connect_vm") return null;

        var sessionId = args["session_id"]?.GetValue<string>();
        if (sessionId != null && classifier.SessionVmResolver != null
            && classifier.SessionVmResolver(sessionId) == null)
        {
            return PolicyDispatch.ErrorContent(new JsonObject
            {
                ["status"] = "error",
                ["tool"] = toolName,
                ["reason"] = $"Session '{sessionId}' not found. Use connect_vm to create a session first.",
            });
        }
        return null;
    }

    static (HyperVPolicyConfig config, string? resolvedPath) LoadHyperVConfig(string? explicitPath)
    {
        var path = explicitPath
            ?? Environment.GetEnvironmentVariable("HYPERV_MCP_POLICY")
            ?? FindPolicyFileNextToExe();

        if (path != null && File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<HyperVPolicyConfig>(json, HyperVPolicyConfig.HyperVJsonOptions)
                    ?? new HyperVPolicyConfig();
                Console.Error.WriteLine($"hyperv-mcp: loaded policy from {path} (mode={config.Mode})");
                return (config, path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hyperv-mcp: failed to load policy from {path}: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine("hyperv-mcp: no policy file found, using default standard policy");
        }
        return (new HyperVPolicyConfig(), path);
    }

    static string? FindPolicyFileNextToExe()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "hyperv-mcp-policy.json");
        return File.Exists(candidate) ? candidate : null;
    }
}
