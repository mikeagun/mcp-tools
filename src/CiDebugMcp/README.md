# ci-debug-mcp

MCP server for CI/CD failure investigation — search logs, triage failures, download artifacts, and analyze binary dependencies.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- CI provider authentication (see [Authentication](#authentication) below)

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test    # 135 tests
```

## Tools

| Tool | Purpose |
|------|---------|
| `get_ci_failures` | Unified entry point: find CI failures by PR, run, branch, SHA, or URL. Control verbosity with `detail` level. Returns structured errors, failure classification, test summaries, and hints. |
| `search_job_logs` | Regex search within a job's full log with context lines and parsed error info. Step-scoped via `step_name` or `step_number`. |
| `get_step_logs` | Get one step's log output by name or number. Use `pattern` to center around matches. Defaults to the last failed step. |
| `download_artifact` | Download CI build artifacts. Supports parallel async downloads, selective ZIP extraction, and PR-based resolution. |
| `analyze_binary_deps` | DLL dependency analysis via dumpbin, optional baseline comparison. |
| `update_binary_baselines` | Regenerate dependency baseline files from build output. Auto-discovers mappings from `scripts_dir`. |

## Quick Start

```bash
dotnet publish src/CiDebugMcp -c Release -o publish/ci-debug-mcp
```

Add to your MCP client configuration (e.g., `~/.copilot/mcp-config.json` for GitHub Copilot CLI):

```json
{
  "mcpServers": {
    "ci-debug": {
      "command": "C:\\path\\to\\publish\\ci-debug-mcp\\ci-debug-mcp.exe",
      "env": { "GITHUB_TOKEN": "${env:GITHUB_TOKEN}" }
    }
  }
}
```

Authentication is resolved automatically from Git Credential Manager, GitHub CLI, or Azure CLI. Set `GITHUB_TOKEN` or `AZURE_DEVOPS_PAT` env vars to override. See [Authentication](#authentication) for the full priority chain.

## `get_ci_failures` Examples

```
# Why is my PR red?
get_ci_failures(pr=5078, repo="microsoft/ebpf-for-windows")

# Same thing from a URL
get_ci_failures(url="https://github.com/microsoft/ebpf-for-windows/pull/5078")

# Is CI/CD flaky on main? (last 10 runs, summary only)
get_ci_failures(branch="main", workflow="CI/CD", count=10, detail="summary")

# What failed on this specific run?
get_ci_failures(run_id=22644372005, repo="microsoft/ebpf-for-windows")

# Deep dive with more errors
get_ci_failures(pr=5078, detail="errors", max_errors=50)
```

## Authentication

The server resolves authentication lazily (on first API call) from multiple sources in priority order.

### GitHub

1. **`GITHUB_TOKEN` env var** — explicit token (highest priority, recommended for CI)
2. **`GH_TOKEN` env var** — alternative env var
3. **Git Credential Manager** — works with MSFT SSO, AAD, OAuth (`gho_` tokens). Auto-disambiguates multi-account workstations via remote URL username.
4. **GitHub CLI** — `gh auth token` (if `gh` is installed and authenticated)

### Azure DevOps

1. **`AZURE_DEVOPS_PAT` env var** — Personal Access Token (Basic auth)
2. **`SYSTEM_ACCESSTOKEN` env var** — auto-injected in ADO pipelines (Bearer auth)
3. **Azure CLI** — `az account get-access-token` (Bearer auth, requires `az login`)
4. **Git Credential Manager** — for `dev.azure.com` (auto-detects PAT vs AAD token)

Set `ADO_ORG_URL` and `ADO_PROJECT` env vars to configure the ADO provider, or pass an ADO URL directly to any tool.

For private repositories, authentication is required for all operations.

## Design Principles

- **One tool to start** — `get_ci_failures` accepts whatever context the agent has
- **`detail` controls verbosity** — `"summary"` for health checks, `"jobs"` for patterns, `"errors"` for debugging
- **`max_errors` caps token budget** — prevents 500-error builds from flooding context
- **`max_failures` caps failure entries** — default 10, increase for large runs
- **`max_test_names` caps test name lists** — default 20, with total count when exceeded
- **No pagination** — bounded results + search + range-based log access
- **Hints included** — every failure carries the exact next tool call to drill deeper
- **Failure classification** — `failure_type` field: `build`, `test`, `dependency`, `infra`, `unknown`
- **PR correlation** — `changed_files` and `in_changed_files` help distinguish new regressions from pre-existing failures
- **Test-aware** — Extracts test case names, test summaries, and framework detection (Catch2, gtest, pytest, jest, xunit)
