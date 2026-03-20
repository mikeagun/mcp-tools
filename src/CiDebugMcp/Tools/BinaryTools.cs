using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using McpSharp;

namespace CiDebugMcp.Tools;

/// <summary>
/// Tier 4: Binary dependency analysis tools.
/// </summary>
public static class BinaryTools
{
    public static void Register(McpServer server, BinaryAnalyzer analyzer)
    {
        if (!analyzer.IsAvailable)
        {
            Console.Error.WriteLine("ci-debug-mcp: dumpbin.exe not found, binary analysis tools disabled");
            return;
        }

        server.RegisterTool(new ToolInfo
        {
            Name = "analyze_binary_deps",
            Description = "Analyze DLL dependencies of a PE binary using dumpbin. " +
                          "Optionally compare against an expected baseline file." +
                          " Returns: { binary, dependencies[], count, baseline_match?, missing?, extra? }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["binary_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .exe or .dll" },
                    ["baseline_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to expected dependencies .txt file (optional, for comparison)",
                    },
                },
                ["required"] = new JsonArray("binary_path"),
            },
            Handler = args =>
            {
                var binaryPath = args["binary_path"]!.GetValue<string>();
                var baselinePath = args["baseline_path"]?.GetValue<string>();
                var isExe = binaryPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                var deps = analyzer.GetDependencies(binaryPath, isExe);

                var result = new JsonObject
                {
                    ["binary"] = Path.GetFileName(binaryPath),
                    ["dependencies"] = new JsonArray(deps.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                    ["count"] = deps.Length,
                };

                if (baselinePath != null)
                {
                    var (matches, missing, extra) = analyzer.CompareBaseline(binaryPath, baselinePath, isExe);
                    result["baseline_match"] = matches;
                    if (missing.Length > 0)
                        result["missing"] = new JsonArray(missing.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
                    if (extra.Length > 0)
                        result["extra"] = new JsonArray(extra.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
                }

                return result;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "update_binary_baselines",
            Description = "Regenerate DLL dependency baseline files from current build output. " +
                          "Provide binary-to-baseline mappings as a JSON object." +
                          " Returns: { updated: [{binary, baseline, added?, removed?, unchanged?}] }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_dir"] = new JsonObject { ["type"] = "string", ["description"] = "Build output directory (e.g. x64\\Debug)" },
                    ["mappings"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Object mapping binary filename to baseline .txt path. " +
                                          "Example: {\"ebpfapi.dll\": \"scripts/check_deps_ebpfapi.txt\"}",
                    },
                    ["scripts_dir"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Directory containing check_binary_dependencies_*.txt files. " +
                                          "Auto-discovers binary-to-baseline mappings from filenames. " +
                                          "Alternative to providing mappings explicitly.",
                    },
                },
                ["required"] = new JsonArray("build_dir"),
            },
            Handler = args =>
            {
                var buildDir = args["build_dir"]!.GetValue<string>();
                var mappingsNode = args["mappings"];
                var scriptsDir = args["scripts_dir"]?.GetValue<string>();

                JsonObject mappings;
                if (mappingsNode != null)
                {
                    mappings = mappingsNode.AsObject();
                }
                else if (scriptsDir != null)
                {
                    // Auto-discover binary-to-baseline mappings from filenames
                    mappings = new JsonObject();
                    if (Directory.Exists(scriptsDir))
                    {
                        foreach (var file in Directory.GetFiles(scriptsDir, "check_binary_dependencies_*.txt"))
                        {
                            var fileName = Path.GetFileNameWithoutExtension(file);
                            // Format: check_binary_dependencies_{binary}_{config}
                            var prefix = "check_binary_dependencies_";
                            var rest = fileName[prefix.Length..];
                            // Last underscore-separated segment is config; everything before is binary name
                            var lastUnderscore = rest.LastIndexOf('_');
                            if (lastUnderscore > 0)
                            {
                                var binaryPart = rest[..lastUnderscore];
                                // Replace last underscore-separated dot placeholder: ebpfapi_dll → ebpfapi.dll
                                var dotIdx = binaryPart.LastIndexOf('_');
                                if (dotIdx > 0)
                                {
                                    var binaryName = binaryPart[..dotIdx] + "." + binaryPart[(dotIdx + 1)..];
                                    mappings[binaryName] = file;
                                }
                            }
                        }
                    }

                    if (mappings.Count == 0)
                        throw new ArgumentException($"No check_binary_dependencies_*.txt files found in '{scriptsDir}'");
                }
                else
                {
                    throw new ArgumentException("Either 'mappings' or 'scripts_dir' must be provided");
                }

                var updated = new JsonArray();
                foreach (var (binary, baselineNode) in mappings)
                {
                    var baselinePath = baselineNode!.GetValue<string>();
                    var binaryPath = Path.Combine(buildDir, binary);
                    var isExe = binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                    if (!File.Exists(binaryPath))
                    {
                        updated.Add(new JsonObject { ["binary"] = binary, ["error"] = "File not found" });
                        continue;
                    }

                    // Get current deps
                    var deps = analyzer.GetDependencies(binaryPath, isExe);

                    // Read old baseline for diff
                    string[] oldDeps = [];
                    if (File.Exists(baselinePath))
                    {
                        oldDeps = File.ReadAllLines(baselinePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    }

                    // Write new baseline
                    File.WriteAllLines(baselinePath, deps);

                    var added = deps.Except(oldDeps, StringComparer.OrdinalIgnoreCase).ToArray();
                    var removed = oldDeps.Except(deps, StringComparer.OrdinalIgnoreCase).ToArray();

                    var entry = new JsonObject { ["binary"] = binary, ["baseline"] = baselinePath };
                    if (added.Length > 0)
                        entry["added"] = new JsonArray(added.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
                    if (removed.Length > 0)
                        entry["removed"] = new JsonArray(removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
                    if (added.Length == 0 && removed.Length == 0)
                        entry["unchanged"] = true;
                    updated.Add(entry);
                }

                return new JsonObject { ["updated"] = updated };
            },
        });
    }
}
