using MsBuildMcp.Engine;
using MsBuildMcp.Policy;
using MsBuildMcp.Prompts;
using McpSharp;
using McpSharp.Policy;
using MsBuildMcp.Resources;
using MsBuildMcp.Tools;
using System.Text.Json.Nodes;

namespace MsBuildMcp;

public static class Program
{
    public static void Main(string[] cmdArgs)
    {
        // MSBuild Locator must be called before any Microsoft.Build types are loaded.
        ProjectEngine.EnsureMsBuildRegistered();

        var solutionEngine = new SolutionEngine();
        var projectEngine = new ProjectEngine();
        var buildManager = new BuildManager();
        var server = new McpServer("msbuild-mcp");

        // Register tools, resources, prompts
        ToolRegistration.RegisterAll(server, solutionEngine, projectEngine, buildManager);
        ResourceRegistration.RegisterAll(server, solutionEngine);
        PromptRegistration.RegisterAll(server, solutionEngine, projectEngine);

        // Load guardrail policy.
        var policyPath = GetPolicyArg(cmdArgs);
        var classifier = new MsBuildToolClassifier();
        var matcher = new MsBuildRuleMatcher();
        var optionGenerator = new MsBuildOptionGenerator();
        var policy = PolicyEngine.Load<MsBuildPolicyConfig>(
            classifier, matcher,
            explicitPath: policyPath,
            envVarName: "MSBUILD_MCP_POLICY",
            defaultFileName: "msbuild-mcp-policy.json");

        Console.Error.WriteLine("msbuild-mcp: server started");

        // Stdio transport
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var transport = new McpTransport(input, output);
        server.Transport = transport;

        transport.Run((method, parameters) =>
            PolicyDispatch.Dispatch(method, parameters, server, policy, optionGenerator,
                preValidator: (toolName, args) => PreValidateBuild(toolName, args, policy),
                argsEnricher: (toolName, args) => EnrichArgs(toolName, args, buildManager)));

        Console.Error.WriteLine("msbuild-mcp: server stopped");
    }

    /// <summary>
    /// Inject context into args before policy evaluation.
    /// For cancel_build: resolve the active build's sln_path so policy rules can scope by solution.
    /// </summary>
    internal static void EnrichArgs(string toolName, JsonObject args, BuildManager buildManager)
    {
        if (toolName == "cancel_build")
        {
            var buildId = args["build_id"]?.GetValue<string>();
            var slnPath = buildManager.GetCurrentBuildSolution(buildId);
            if (slnPath != null)
                args["sln_path"] = slnPath;
        }
    }

    /// <summary>
    /// Pre-validate build calls against hard constraints before policy evaluation.
    /// Returns an error response if constraints are violated, null to continue.
    /// </summary>
    internal static JsonNode? PreValidateBuild(string toolName, JsonObject args, PolicyEngine policy)
    {
        if (toolName != "build")
            return null;

        var constraints = (policy.Config as MsBuildPolicyConfig)?.BuildConstraints;
        if (constraints == null)
            return null;

        if (constraints.RequireTargets)
        {
            var targets = args["targets"]?.GetValue<string>();
            if (string.IsNullOrEmpty(targets))
                return PolicyDispatch.ErrorContent(new JsonObject
                {
                    ["status"] = "error",
                    ["reason"] = "Build targets required by policy. Use list_projects to discover available targets.",
                });
        }

        if (constraints.AllowedConfigurations is { Count: > 0 } allowed)
        {
            var buildConfig = args["configuration"]?.GetValue<string>() ?? "Debug";
            if (!allowed.Any(c => string.Equals(c, buildConfig, StringComparison.OrdinalIgnoreCase)))
                return PolicyDispatch.ErrorContent(new JsonObject
                {
                    ["status"] = "error",
                    ["reason"] = $"Configuration '{buildConfig}' is not allowed by policy. Allowed: {string.Join(", ", allowed)}",
                });
        }

        if (!constraints.AllowRestore)
        {
            var restore = args["restore"]?.GetValue<bool>() ?? false;
            if (restore)
                return PolicyDispatch.ErrorContent(new JsonObject
                {
                    ["status"] = "error",
                    ["reason"] = "NuGet restore is not allowed by policy. Build without restore=true.",
                });
        }

        return null;
    }

    private static string? GetPolicyArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--policy")
                return args[i + 1];
        }
        return null;
    }
}
