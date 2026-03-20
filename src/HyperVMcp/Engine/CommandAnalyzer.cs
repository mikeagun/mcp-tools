// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Management.Automation.Language;

namespace HyperVMcp.Engine;

/// <summary>
/// Structured analysis of a PowerShell command string using the real PowerShell parser.
/// Used for policy evaluation (detecting multi-statement commands) and suggestion
/// generation (extracting command names for auto-approve options).
/// </summary>
public sealed record CommandAnalysis(
    /// <summary>Command names from each statement/pipeline element (e.g., ["Remove-Item", "Start-Process"]).</summary>
    IReadOnlyList<string> CommandNames,

    /// <summary>Number of statements (1 = simple, 2+ = chained with ; or newline).</summary>
    int StatementCount,

    /// <summary>True if any statement uses a pipeline (|).</summary>
    bool HasPipeline,

    /// <summary>First argument of the first command (e.g., "C:\logs" from "Remove-Item C:\logs").</summary>
    string? FirstArgument
);

/// <summary>
/// Analyzes PowerShell command strings using System.Management.Automation.Language.Parser.
/// Provides structured command information for policy evaluation and auto-approve suggestions.
/// </summary>
public static class CommandAnalyzer
{
    /// <summary>
    /// Parse a command string and extract structured information.
    /// Uses the real PowerShell parser — correctly handles quoting, subexpressions,
    /// here-strings, and all PowerShell syntax.
    /// </summary>
    public static CommandAnalysis Analyze(string command)
    {
        var ast = Parser.ParseInput(command, out _, out _);

        var commandNames = new List<string>();
        var statementCount = 0;
        var hasPipeline = false;
        string? firstArg = null;
        var isFirstCommand = true;

        var statements = ast.EndBlock?.Statements;
        if (statements == null)
            return new CommandAnalysis([], 0, false, null);

        foreach (var stmt in statements)
        {
            statementCount++;

            if (stmt is PipelineAst pipeline)
            {
                if (pipeline.PipelineElements.Count > 1)
                    hasPipeline = true;

                foreach (var element in pipeline.PipelineElements)
                {
                    if (element is CommandAst cmdAst)
                    {
                        var name = cmdAst.GetCommandName();
                        if (name != null)
                            commandNames.Add(name);

                        // Extract first argument from the first command.
                        if (isFirstCommand && cmdAst.CommandElements.Count > 1)
                        {
                            // Get the string representation, skipping parameters (which start with -).
                            for (var i = 1; i < cmdAst.CommandElements.Count; i++)
                            {
                                var elem = cmdAst.CommandElements[i];
                                if (elem is CommandParameterAst)
                                    continue; // Skip -Recurse, -Force, etc.
                                // Also skip the value after a parameter (e.g., -m "message").
                                if (i > 1 && cmdAst.CommandElements[i - 1] is CommandParameterAst)
                                    continue;
                                firstArg = elem.Extent.Text;
                                break;
                            }
                        }
                        isFirstCommand = false;
                    }
                }
            }
        }

        // Global pass: find .NET type method invocations anywhere in the AST.
        // This catches assignments ($x = [math]::Round(...)), nested expressions, etc.
        ExtractTypeMethodNames(ast, commandNames);

        return new CommandAnalysis(
            commandNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            statementCount,
            hasPipeline,
            firstArg);
    }

    /// <summary>
    /// Extract type names from static method/property access (e.g., [math]::Round → "[math]::Round").
    /// Walks the entire AST to find InvokeMemberExpressionAst and MemberExpressionAst nodes
    /// that reference type expressions, including chained access like [datetime]::Now.ToString().
    /// </summary>
    private static void ExtractTypeMethodNames(Ast ast, List<string> commandNames)
    {
        // Find static method invocations and property access on .NET types.
        // Handles: [math]::Round(...), [datetime]::Now, [datetime]::Now.ToString(...)
        foreach (var node in ast.FindAll(n => n is MemberExpressionAst, searchNestedScriptBlocks: false))
        {
            if (node is not MemberExpressionAst member || !member.Static)
                continue;

            // Direct type access: [math]::Round, [datetime]::Now
            if (member.Expression is TypeExpressionAst typeExpr)
            {
                var typeName = typeExpr.TypeName.Name;
                var memberName = member.Member is StringConstantExpressionAst str ? str.Value : null;
                if (memberName != null)
                    commandNames.Add($"[{typeName}]::{memberName}");
            }
        }
    }

    /// <summary>
    /// Check if all command names in a command string are covered by the approved set.
    /// For multi-statement commands, EVERY command must be approved.
    /// Returns false if any command is not in the approved set.
    /// </summary>
    public static bool AllCommandsApproved(string command, IReadOnlyList<string> approvedPrefixes)
    {
        var analysis = Analyze(command);

        if (analysis.CommandNames.Count == 0)
            return false;

        // Every command name in the input must match at least one approved prefix.
        return analysis.CommandNames.All(name =>
            approvedPrefixes.Any(prefix =>
            {
                // Exact command name match.
                if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Prefix match: "git commit" starts with approved prefix "git".
                // Must be at word boundary.
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.Length > prefix.Length
                    && name[prefix.Length] == ' ')
                    return true;

                return false;
            }));
    }
}
