// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using CiDebugMcp.Engine;

namespace CiDebugMcp.Tools;

/// <summary>
/// Converts provider-agnostic CiFailureReport to the same JSON format
/// as the existing GitHub tool output, so ADO and GitHub produce identical schemas.
/// </summary>
public static class FailureReportFormatter
{
    /// <summary>
    /// Convert a CiFailureReport to the JSON format expected by get_ci_failures callers.
    /// </summary>
    public static JsonObject Format(CiFailureReport report, string? providerName = null,
        int maxTestNames = 20, int maxFailures = 10)
    {
        var failures = new JsonArray();
        int totalFailures = report.Failures.Length;
        foreach (var f in report.Failures.Take(maxFailures))
        {
            var entry = new JsonObject
            {
                ["job"] = f.Name,
                ["job_id"] = f.JobId,
                ["conclusion"] = f.Conclusion,
            };

            if (f.BuildId != null)
                entry["run_id"] = f.BuildId;

            if (f.FailureType != null)
                entry["failure_type"] = f.FailureType;

            if (f.Diagnosis != null)
                entry["diagnosis"] = f.Diagnosis;

            if (f.FailedStep != null)
            {
                var stepInfo = new JsonObject
                {
                    ["name"] = f.FailedStep.Name,
                    ["source"] = f.FailedStep.Source ?? "api",
                };
                // Only include step number if it's usable (not a timeline index)
                if (f.FailedStep.Source != "timeline")
                    stepInfo["number"] = f.FailedStep.Number;
                entry["failed_step"] = stepInfo;
            }

            if (f.Errors.Length > 0)
            {
                var stepsArr = new JsonArray();
                var stepObj = new JsonObject { ["number"] = 1, ["name"] = f.Name };
                var errorsArr = new JsonArray();
                foreach (var err in f.Errors)
                {
                    if (err.Code != null)
                    {
                        errorsArr.Add(new JsonObject
                        {
                            ["raw"] = err.Raw,
                            ["code"] = err.Code,
                            ["message"] = err.Message,
                            ["file"] = err.File,
                            ["source_line"] = err.SourceLine,
                        });
                    }
                    else
                    {
                        errorsArr.Add(err.Raw);
                    }
                }
                stepObj["errors"] = errorsArr;
                stepsArr.Add(stepObj);
                entry["failed_steps"] = stepsArr;
            }

            if (f.TestSummary != null)
            {
                entry["test_summary"] = new JsonObject
                {
                    ["framework"] = f.TestSummary.Framework,
                    ["total"] = f.TestSummary.Total,
                    ["passed"] = f.TestSummary.Passed,
                    ["failed"] = f.TestSummary.Failed,
                    ["summary_line"] = f.TestSummary.SummaryLine,
                };
            }

            if (f.FailedTestNames != null)
            {
                var names = f.FailedTestNames;
                entry["failed_test_names"] = new JsonArray(
                    names.Take(maxTestNames).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
                if (names.Length > maxTestNames)
                    entry["failed_test_names_total"] = names.Length;
            }

            if (f.Hint != null) entry["hint"] = f.Hint;
            if (f.HintSearch != null) entry["hint_search"] = f.HintSearch;
            if (f.DiagnosticPath != null) entry["diagnostic_path"] = f.DiagnosticPath;

            // ADO: structured next_action + step_name guidance
            if (providerName == "ado")
            {
                // If this is a trigger build with child builds, point to child
                if (f.HintSearch != null && f.HintSearch.Contains("get_ci_failures"))
                {
                    entry["next_action"] = new JsonObject
                    {
                        ["tool"] = "get_ci_failures",
                        ["params"] = new JsonObject { ["url"] = f.HintSearch.Split("url='").ElementAtOrDefault(1)?.TrimEnd(')', '\'') ?? f.HintSearch },
                    };
                    entry["note"] = "Trigger build — drill into the child build for actual failure details.";
                }
                else
                {
                    entry["next_action"] = new JsonObject
                    {
                        ["tool"] = "search_job_logs",
                        ["params"] = new JsonObject
                        {
                            ["job_id"] = f.JobId,
                            ["pattern"] = "error|FAIL",
                        },
                    };
                    entry["note"] = "For ADO, use step_name (not step_number) when drilling into logs.";
                }
            }

            if (f.AvailableSteps != null)
            {
                var stepsInfoArr = new JsonArray();
                foreach (var s in f.AvailableSteps)
                {
                    var si = new JsonObject
                    {
                        ["number"] = s.Number,
                        ["name"] = s.Name,
                    };
                    if (s.Lines.HasValue) si["lines"] = s.Lines.Value;
                    if (s.HasErrors) si["has_errors"] = true;
                    if (s.Type != null) si["type"] = s.Type;
                    stepsInfoArr.Add(si);
                }
                entry["available_steps"] = stepsInfoArr;
            }

            failures.Add(entry);
        }

        var cancelledArr = new JsonArray();
        foreach (var c in report.Cancelled)
            cancelledArr.Add(new JsonObject { ["name"] = c.Name, ["job_id"] = c.JobId });

        var pendingArr = new JsonArray();
        foreach (var p in report.Pending)
            pendingArr.Add(new JsonObject { ["name"] = p.Name, ["status"] = p.Status });

        var result = new JsonObject
        {
            ["scope"] = report.Scope,
            ["provider"] = providerName ?? "unknown",
            ["summary"] = new JsonObject
            {
                ["total"] = report.Summary.Total,
                ["passed"] = report.Summary.Passed,
                ["failed"] = report.Summary.Failed,
                ["skipped"] = report.Summary.Skipped,
                ["cancelled"] = report.Summary.Cancelled,
                ["pending"] = report.Summary.Pending,
            },
            ["failures"] = failures,
        };

        if (cancelledArr.Count > 0) result["cancelled"] = cancelledArr;
        if (pendingArr.Count > 0) result["pending"] = pendingArr;
        if (totalFailures > maxFailures)
            result["additional_failures"] = totalFailures - maxFailures;
        if (report.ChangedFiles != null)
            result["changed_files"] = new JsonArray(
                report.ChangedFiles.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        if (report.BaseBranch != null) result["base_branch"] = report.BaseBranch;

        return result;
    }
}
