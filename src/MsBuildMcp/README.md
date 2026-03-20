# MSBuild MCP Server

An [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) server that gives LLM agents structured access to MSBuild solutions, projects, dependency graphs, and build execution. Designed for AI-assisted C++ and .NET development workflows.

## What It Does

Instead of having LLM agents parse `.vcxproj` XML with regex or grep through raw MSBuild output, this server provides **13 structured tools** that use the official `Microsoft.Build` evaluation APIs. Agents get:

- **Correct project evaluation** — handles property inheritance, conditional PropertyGroups, Directory.Build.props, import chains, and NuGet-generated targets
- **Dependency graph analysis** — project reference DAG with topological build order
- **Async build execution** — start builds, poll for progress, get structured errors, cancel
- **Cross-project queries** — find properties, packages, or files across entire solutions

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with C++ workload (for C++ project evaluation and building)

### Install

```bash
dotnet build
```

### Configure

Add to your MCP client configuration (e.g., `~/.copilot/mcp-config.json` for GitHub Copilot CLI):

```json
{
  "mcpServers": {
    "msbuild": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\src\\MsBuildMcp"]
    }
  }
}
```

Or with a published executable:

```bash
dotnet publish src/MsBuildMcp -c Release -o publish/msbuild-mcp
```

```json
{
  "mcpServers": {
    "msbuild": {
      "command": "C:\\path\\to\\publish\\msbuild-mcp\\msbuild-mcp.exe"
    }
  }
}
```

## Tools

### Solution & Project Exploration

| Tool | Description |
|------|-------------|
| `list_projects` | List all projects in a `.sln` with solution folders, configs, and `/t:` target syntax |
| `get_project_details` | Evaluate a `.vcxproj`/`.csproj` — properties, items, references, packages |
| `get_project_items` | Get source files and compiler/linker settings (ItemDefinitionGroup metadata) |
| `get_project_imports` | Show the `.props`/`.targets` import chain with conditions |
| `find_project_for_file` | Find which project(s) compile a given source file |

### Dependency Analysis

| Tool | Description |
|------|-------------|
| `get_dependency_graph` | Project reference DAG with topological build order (JSON or Mermaid) |

### Build Execution

| Tool | Description |
|------|-------------|
| `build` | Start MSBuild, wait up to `timeout` seconds, return structured progress/errors |
| `get_build_status` | Poll a running build — blocks until completion or new errors |
| `cancel_build` | Cancel the current build, return partial results |
| `parse_build_output` | Parse raw MSBuild text into structured errors/warnings |

### Cross-Project Queries

| Tool | Description |
|------|-------------|
| `find_property` | Search MSBuild properties across all projects (exact name or regex) |
| `find_packages` | Find NuGet packages across projects with version conflict detection |
| `diff_configurations` | Compare Debug vs Release (properties, compiler/linker settings, items) |

## Guardrails

The `build` and `cancel_build` tools require user approval before execution. The server uses [MCP elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) to prompt the user directly through the client UI — the agent never sees the approval flow.

### Two-Prompt Approval

Build approval uses two sequential prompts to keep choices simple:

**Prompt 1 — Scope** (what to allow):
```
Approve build: Solution=C:\myrepo\MySolution.sln Targets=tools\bpf2c
```
Options: Allow once · Allow builds of bpf2c on MySolution.sln · Allow all builds on MySolution.sln · Allow all builds · Deny

**Prompt 2 — Persistence** (only if a rule-based option was chosen):
```
Save "Allow builds of bpf2c on MySolution.sln" as: (Solution=C:\myrepo\MySolution.sln, Targets=tools\bpf2c)
```
Options: This session only · Permanently

"Allow once" and "Deny" resolve in one click — no second prompt.

Options adapt to context:
- **Targeted builds** get target-scoped options ("Allow builds of bpf2c on X")
- **Non-default configurations** add config-qualified options ("Allow Release builds of bpf2c on X")
- **Full solution builds** only show solution-level options

All query/analysis tools (11 of 13) are read-only and require no approval.

### Build Constraints

Optional hard limits in `msbuild-mcp-policy.json`:

```json
{
  "build_constraints": {
    "require_targets": true,
    "allowed_configurations": ["Debug", "NativeOnlyDebug"],
    "allow_restore": false
  }
}
```

| Constraint | Default | Effect |
|------------|---------|--------|
| `require_targets` | `false` | Reject builds without explicit targets (prevents accidental full-solution builds) |
| `allowed_configurations` | any | Reject builds with unlisted configurations |
| `allow_restore` | `true` | Reject builds with `restore=true` |

Constraint violations fail immediately with actionable error messages — no approval prompt is shown.

### Approval Rules

Permanent approvals saved to `msbuild-mcp-policy.json` support three constraint dimensions:

| Dimension | Matching | Example |
|-----------|----------|---------|
| `sln_path` | Case-insensitive normalized path | `"C:\\git\\project\\project.sln"` |
| `targets` | Subset — approved targets cover any subset of them | `["tools\\bpf2c", "tests\\t"]` |
| `configuration` | Case-insensitive exact match | `"Release"` |

A rule without a constraint dimension matches any value for that dimension. For example, a rule with `sln_path` + `targets` but no `configuration` approves any configuration for those targets.

### Disabling Confirmation

To skip all build approval prompts, pre-approve builds in the policy file:

```json
{
  "user_rules": [
    {
      "rule": { "tools": ["build"] }
    },
    {
      "rule": { "tools": ["cancel_build"] }
    }
  ]
}
```

This auto-approves all builds and cancels with no prompts. For per-solution approval:

```json
{
  "user_rules": [
    {
      "rule": {
        "tools": ["build"],
        "sln_path": "C:\\git\\myproject\\myproject.sln"
      }
    }
  ]
}
```

### Policy File Configuration

The server looks for a policy file in this order:
1. `--policy <path>` CLI argument
2. `MSBUILD_MCP_POLICY` environment variable
3. `msbuild-mcp-policy.json` next to the executable

The policy file is auto-created when you select permanent approval options via elicitation.

**Policy file reference:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `build_constraints.require_targets` | bool | `false` | Reject builds without explicit targets |
| `build_constraints.allowed_configurations` | string[] | any | Only allow listed configurations |
| `build_constraints.allow_restore` | bool | `true` | Allow NuGet restore during builds |
| `user_rules` | array | [] | Approval rules (auto-populated, editable manually) |
| `deny_rules` | array | [] | Denial rules (auto-populated, editable manually) |

### Managing Approval Rules

Permanent approvals are stored in `user_rules`. These are auto-populated by elicitation or can be manually written:

```json
{
  "user_rules": [
    {
      "added": "2026-03-18T10:00:00Z",
      "reason": "User approved via elicitation: build",
      "rule": {
        "tools": ["build"],
        "sln_path": "c:\\git\\myproject\\myproject.sln",
        "targets": ["tools\\bpf2c", "tests\\bpf2c_tests"]
      }
    }
  ]
}
```

Rules support these constraint fields (all optional, combined with AND):
- `tools` — tool names (`["build"]` or `["cancel_build"]`)
- `sln_path` — normalized solution path (case-insensitive matching)
- `targets` — list of approved targets (subset matching: approving `[A, B]` allows building just `A`)
- `configuration` — approved configuration (case-insensitive, e.g. `"Release"`)

**Evaluation order**: deny rules → permanent rules → session rules → default (build/cancel_build require confirmation).

**To reset all approvals**: delete the policy file or clear the `user_rules` array.

## Prompts

The server includes 5 prompt templates for guided agent workflows:

| Prompt | Purpose |
|--------|---------|
| `diagnose-build-failure` | Parse errors, identify root cause vs cascade, suggest fix order |
| `what-to-build` | Determine minimal MSBuild targets from changed files |
| `impact-analysis` | Trace file change impact through the dependency graph |
| `resolve-nuget-issue` | Diagnose NuGet restore failures |
| `explain-build-config` | Explain evaluated project properties for a configuration |

## Examples

### List projects and find one

```
Agent: Call list_projects with sln_path="C:\myrepo\MySolution.sln" and filter="Api"
→ Returns 3 matching projects with their /t: target paths
```

### Build a specific project

```
Agent: Call build with sln_path="...", targets="libs\ApiLib", timeout=60
→ Returns: { status: "succeeded", elapsed_ms: 32700, error_count: 0, warning_count: 4 }
```

### Find what depends on a library

```
Agent: Call get_dependency_graph with include=["ApiLib"]
→ Returns focused subgraph: ApiLib + all transitive dependencies
```

### Compare Debug vs Release

```
Agent: Call diff_configurations with config_a="Debug", config_b="Release"
→ Returns property diffs (Optimization, LinkTimeCodeGeneration, etc.)
   and ItemDefinition diffs (compiler flags, linker settings)
```

## Architecture

```
src/MsBuildMcp/
├── Program.cs              # Entry point with policy wiring
├── Engine/
│   ├── SolutionEngine.cs   # .sln parser
│   ├── ProjectEngine.cs    # Microsoft.Build evaluation with caching
│   ├── DependencyGraph.cs  # DAG with topological sort
│   ├── BuildRunner.cs      # Async MSBuild process management
│   └── ErrorParser.cs      # MSBuild output → structured diagnostics
├── Policy/
│   ├── MsBuildPolicyConfig.cs    # Build constraints (require_targets, etc.)
│   ├── MsBuildToolClassifier.cs  # build/cancel_build require confirmation
│   ├── MsBuildOptionGenerator.cs # Two-prompt scope + persistence options
│   └── MsBuildRuleMatcher.cs     # Solution/target/config constraint matching
├── Tools/                  # MCP tool registrations
├── Prompts/                # MCP prompt templates
└── Resources/              # MCP resource providers
```

Tests are located at `tests/MsBuildMcp.Tests/` (144 unit, integration, and E2E tests).

### Key Design Decisions

**Evaluation over parsing**: Never regex-parse `.vcxproj` XML. Uses `Microsoft.Build.Evaluation.Project` which correctly handles property inheritance, conditional evaluation, import chains, and NuGet targets.

**Dual MSBuild strategy**: The in-process evaluation API uses the .NET SDK's MSBuild (with working SDK resolvers for .NET 9) plus Visual Studio's `VCTargetsPath` (for C++ project support). The build subprocess uses Visual Studio's `MSBuild.exe` directly via `vswhere` discovery.

**Async build model**: Builds run in a background process with streaming output parsing. The `build` tool returns early on new errors (so agents can cancel and fix without waiting). A ring buffer retains the last 1000 output lines.

**Stateless tools, cached engine**: Each tool call is stateless, but the engine layer caches evaluated projects keyed by `(path, config, platform, mtime)`.

## Copilot Skill & VS Code Prompt

A user-level Copilot skill (`~/.copilot/skills/msbuild/SKILL.md`) teaches agents the build workflow: discover solution → determine targets → build → handle errors → iterate. Install it to enable the `msbuild` skill in Copilot CLI sessions.

A VS Code prompt template (`prompts/build.prompt.md`) provides a lightweight wrapper that defers to the skill. Copy it to your repo's `.github/prompts/` directory to enable the `build` prompt in VS Code Copilot Chat:

```bash
cp prompts/build.prompt.md <your-repo>/.github/prompts/build.prompt.md
```

## Platform Support

Currently **Windows only** — requires Visual Studio for C++ project evaluation and build execution. The core solution/project parsing could work cross-platform for .NET SDK projects, but C++ evaluation requires `VCTargetsPath` from a Visual Studio installation.

## Development

```bash
# Build
dotnet build

# Test
dotnet test

# Run locally
dotnet run --project src/MsBuildMcp
```

### Testing

The test suite includes:
- **Protocol tests** — MCP JSON-RPC dispatch, NDJSON transport
- **Engine tests** — solution parsing, error parsing, dependency graph algorithms
- **Integration tests** — full tool registration and dispatch
- **Feature tests** — self-contained tests with synthetic solutions (no external dependencies)
- **Policy tests** — rule matching (targets, config, subset), option generation, pre-validation, escalation flows

## Security

This server runs locally and has the same trust boundary as your shell. It can:
- Read any `.sln`/`.vcxproj`/`.csproj` file accessible to the current user
- Execute MSBuild builds with the current user's permissions
- Access environment variables for VS/MSBuild discovery

Build execution requires explicit user approval through MCP elicitation. Query and analysis tools are read-only and require no approval.

It does **not**:
- Make network requests (except NuGet restore during builds, if enabled)
- Send telemetry
- Store credentials

## License

[MIT](LICENSE)
