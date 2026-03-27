---
name: ci-debug
description: >
  Debug CI/CD failures using the ci-debug-mcp server. Use when asked to investigate
  CI failures, search build logs, triage test failures, or analyze binary dependencies.
  Supports GitHub Actions and Azure DevOps.
---

# CI Debug Skill

Debug CI/CD failures using the `ci-debug-mcp` MCP server for structured log search,
failure triage, artifact downloads, and binary dependency analysis.

## When to Use

- User asks why CI is failing on a PR or branch
- User asks to investigate a specific CI run or job
- User asks about flaky tests or recurring failures
- Triaging build vs test vs infrastructure failures
- Analyzing binary dependencies or DLL mismatches

## Tools

- `ci-debug-mcp` tools: `get_ci_failures`, `search_job_logs`, `get_step_logs`,
  `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
- Supports GitHub Actions and Azure DevOps (Azure Pipelines)
- Complements VCS tools (use github-mcp-server or ADO API for PR metadata, commit details, issue linking)

## Workflow

### Step 1: Start from Whatever You Have

Use `get_ci_failures` with any combination of context:

```
get_ci_failures(pr=5078, repo="microsoft/ebpf-for-windows")
get_ci_failures(url="https://github.com/.../pull/5078")
get_ci_failures(url="https://dev.azure.com/org/project/_build/results?buildId=123")
get_ci_failures(run_id=22644372005, repo="microsoft/ebpf-for-windows")
get_ci_failures(branch="main", workflow="CI/CD")
get_ci_failures(owner="microsoft", repo="ebpf-for-windows")
```

### Step 2: Control Verbosity with `detail`

- `"summary"` — pass/fail/cancelled counts + workflow names for failures (fast, for health checks)
- `"jobs"` — which jobs failed and which step (no log download)
- `"errors"` (default) — download logs and extract real error lines

### Step 3: Follow the Hints

Every failure includes actionable hints:
- `hint`: `get_step_logs(job_id=X, step_number=N)` — best next step for drill-down
- `hint_search`: `search_job_logs(job_id=X, pattern='...')` — targeted pattern search

### Step 4: Deep Dive

Use `get_step_logs` for the full step output (range-based, defaults to tail of failed step).
Use `search_job_logs` only when you need to find something specific across the whole log.

### Step 5: Binary Dependencies

Use `analyze_binary_deps` to compare DLL dependencies against baselines.
Use `update_binary_baselines` to regenerate baseline files after intentional changes.

### Step 6: Cross-Reference PRs

Use `github-mcp-server` `list_pull_requests` to get open PRs, then
`get_ci_failures(pr=N, detail="summary")` for each. For "what's broken in CI overall?",
use `get_ci_failures(owner, repo, count=5, detail="jobs")` — no PR needed.

## Tips

- `get_ci_failures` at `detail="errors"` extracts real errors (compiler errors, test failures,
  mismatches) — not just `##[error]` annotations. You often don't need follow-up calls.
- `get_step_logs` defaults to 50 lines (the tail of the failed step). Pass `max_lines=200`
  for more context, or use `pattern` to center the window around a specific match.
- For health checks: `get_ci_failures(branch="main", count=10, detail="summary")`
- Setup/checkout boilerplate is filtered from `search_job_logs` by default.
- `max_errors=20` (default) prevents 500-error builds from flooding your context.
- `max_failures=10` (default) caps failure entries; `max_test_names=20` caps test name lists.
  Increase these when you need full details on large runs.

## Failure Classification

The `failure_type` field in responses routes your strategy:

| Type | Meaning | Next Action |
|------|---------|-------------|
| `build` | Compiler/linker error | Find the error in the log, fix source code |
| `test` | Test assertion failure | Find the failing test, check if it's a regression |
| `dependency` | Missing package or tool | Check NuGet restore, SDK versions |
| `infra` | CI infrastructure issue | Usually transient — retry or skip |
| `unknown` | Unclassified | Drill into logs manually |
