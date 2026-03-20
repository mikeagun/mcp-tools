---
mode: agent
description: "Debug CI/CD failures — search logs, find errors, analyze binary dependencies."
tools: ["ci-debug-mcp"]
---

## Tools

- `ci-debug-mcp` tools: `get_ci_failures`, `search_job_logs`, `get_step_logs`,
  `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
- Supports GitHub Actions and Azure DevOps (Azure Pipelines)
- Complements VCS tools (use github-mcp-server or ADO API for PR metadata, commit details, issue linking)

## Workflow

1. **Start from whatever you have:** Use `get_ci_failures` with any combination of:
   - `pr=5078` — checks on PR head commit
   - `url="https://github.com/.../pull/5078"` — auto-parses GitHub URLs
   - `url="https://dev.azure.com/org/project/_build/results?buildId=123"` — auto-parses ADO URLs
   - `run_id=22644372005` — specific run
   - `branch="main", workflow="CI/CD"` — latest on branch
   - Just `owner` + `repo` — repo-wide recent failures

2. **Control verbosity with `detail`:**
   - `"summary"` — pass/fail/cancelled counts + workflow names for failures (fast, for health checks)
   - `"jobs"` — which jobs failed and which step (no log download)
   - `"errors"` (default) — download logs and extract real error lines

3. **Follow the hints:** Every failure includes:
   - `hint`: `get_step_logs(job_id=X, step_number=N)` — best next step
   - `hint_search`: `search_job_logs(job_id=X, pattern='...')` — targeted pattern

4. **Deep dive:** Use `get_step_logs` for the full step output (range-based, defaults to tail).
   Use `search_job_logs` only when you need to find something specific across the whole log.

5. **Binary deps:** Use `analyze_binary_deps` to compare DLL dependencies against baselines.
   Use `update_binary_baselines` to regenerate baseline files after intentional changes.

6. **Which PRs are failing?** Use `github-mcp-server` `list_pull_requests` to get open PRs,
   then `get_ci_failures(pr=N, detail="summary")` for each. For "what's broken in CI overall?",
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
