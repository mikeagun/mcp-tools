# MCP Tools

A collection of MCP (Model Context Protocol) servers and libraries for AI-assisted development workflows. All servers communicate via JSON-RPC 2.0 over stdio and are built on the shared **McpSharp** protocol library.

## Projects

| Project | Description |
|---------|-------------|
| [McpSharp](src/McpSharp/README.md) | Lightweight MCP server library for .NET — JSON-RPC 2.0 transport, tool/resource/prompt registry |
| [HyperVMcp](src/HyperVMcp/README.md) | Hyper-V VM management — lifecycle, remote execution, file transfers, diagnostics |
| [CiDebugMcp](src/CiDebugMcp/README.md) | CI/CD failure investigation — log search, failure triage, artifact downloads, binary analysis |
| [MsBuildMcp](src/MsBuildMcp/README.md) | MSBuild project exploration — solution/project evaluation, dependency graphs, build execution |
| [ElicitMcp](src/ElicitMcp/README.md) | MCP elicitation — agent-facing decision/input/feedback tools plus a form-mode conformance/showcase harness |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Visual Studio 2022 or later](https://visualstudio.microsoft.com/) with C++ workload (for MsBuildMcp)
- Hyper-V enabled (for HyperVMcp, Windows only)

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test                        # all projects
dotnet test tests/McpSharp.Tests   # a single project
```

## Publishing

Each server can be published independently:

```bash
dotnet publish src/HyperVMcp -c Release -o publish/hyperv-mcp
dotnet publish src/CiDebugMcp -c Release -o publish/ci-debug-mcp
dotnet publish src/MsBuildMcp -c Release -o publish/msbuild-mcp
dotnet publish src/ElicitMcp -c Release -o publish/elicit-mcp
```

## Packaging and Releases

Each MCP server is also packed as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) NuGet package (`Mikeagun.McpTools.HyperVMcp`, `.CiDebugMcp`, `.MsBuildMcp`, `.ElicitMcp`). Each server produces a framework-dependent package per runtime (`win-x64`, `win-arm64`) plus a platform-agnostic package that points to them:

```bash
dotnet pack src/HyperVMcp -c Release -o artifacts/packages
# Mikeagun.McpTools.HyperVMcp.<version>.nupkg
# Mikeagun.McpTools.HyperVMcp.win-x64.<version>.nupkg
# Mikeagun.McpTools.HyperVMcp.win-arm64.<version>.nupkg
```

The packages require the .NET 10 runtime or later. Install a downloaded package from a local folder with `dotnet tool install`, or run it directly with `dnx` (.NET 10 SDK or later):

```bash
dotnet tool install --global Mikeagun.McpTools.HyperVMcp --add-source <folder>
dnx Mikeagun.McpTools.HyperVMcp --source <folder> --yes
```

`scripts/Test-ToolPackage.ps1` extracts the packages for a runtime, starts each server, and checks that its MCP `initialize` response reports the expected server name and the package version. CI runs it for `win-x64` and `win-arm64` on every build.

### Versioning and releases

The servers are versioned and released independently. Each server's version is the `<Version>` in its project file, and its changes are recorded in its own `CHANGELOG.md` (see [CHANGELOG.md](CHANGELOG.md) for the list).

To release a server:

1. In a pull request, set the server's `<Version>` and rename the `## [Unreleased]` heading in its `CHANGELOG.md` to `## [<version>] - <date>` (adding a new empty `## [Unreleased]` above it). Several servers can be released from one pull request.
2. After it merges, push a tag `<server>-v<version>` on the merge commit for each server, for example `msbuild-mcp-v0.2.0`. Push each tag with its own command (`git push origin msbuild-mcp-v0.2.0`): GitHub starts no workflow runs when more than three tags are pushed at once.
3. The [Release workflow](.github/workflows/release.yml) checks that the tag matches the server's `<Version>` and changelog, builds, tests, packs, smoke-tests the `win-x64` and `win-arm64` packages, and creates a GitHub Release named `<server> <version>` with that changelog section as its notes and the packages attached.

Running the Release workflow manually on a branch packs one or all servers as `<version>-ci.<run>` prerelease packages, available as a workflow artifact. Running it manually on a release tag (selecting the tag as the workflow's ref) releases that tag's server, ignoring the server choice — use this if a tag push did not start a release. `scripts/Get-ReleaseInfo.ps1` lists the servers and versions, and validates a release tag locally (`./scripts/Get-ReleaseInfo.ps1 -Tag msbuild-mcp-v0.2.0`).

## Repository Structure

```
mcp-tools/
├── mcp-tools.sln               # Unified solution (all projects)
├── src/
│   ├── McpSharp/                # Shared MCP protocol library
│   ├── HyperVMcp/               # Hyper-V MCP server
│   ├── CiDebugMcp/              # CI debug MCP server
│   ├── MsBuildMcp/              # MSBuild MCP server
│   └── ElicitMcp/               # Elicitation MCP server
├── tests/
│   ├── McpSharp.Tests/
│   ├── HyperVMcp.Tests/
│   ├── CiDebugMcp.Tests/
│   ├── MsBuildMcp.Tests/
│   └── ElicitMcp.Tests/
├── docs/                        # Guides (creating a new server, etc.)
├── scripts/                     # Release tooling (package smoke test)
├── Directory.Build.props        # Shared package metadata
├── .github/
│   ├── skills/                  # Copilot skill definitions
│   ├── prompts/                 # VS Code prompt wrappers
│   └── workflows/               # CI/CD pipelines
└── publish/                     # Pre-built server binaries
```

## Configuration

Add servers to your MCP client configuration (e.g., `~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "hyperv": {
      "command": "C:\\path\\to\\publish\\hyperv-mcp\\hyperv-mcp.exe"
    },
    "ci-debug": {
      "command": "C:\\path\\to\\publish\\ci-debug-mcp\\ci-debug-mcp.exe"
    },
    "msbuild": {
      "command": "C:\\path\\to\\publish\\msbuild-mcp\\msbuild-mcp.exe"
    },
    "elicit": {
      "command": "C:\\path\\to\\publish\\elicit-mcp\\elicit-mcp.exe"
    }
  }
}
```

### CiDebugMcp Authentication

CiDebugMcp resolves authentication lazily on first API call from multiple sources in priority order:

**GitHub**: `GITHUB_TOKEN` env var → `GH_TOKEN` env var → Git Credential Manager (supports SSO/AAD/OAuth) → GitHub CLI (`gh auth token`)

**Azure DevOps**: `AZURE_DEVOPS_PAT` env var → `SYSTEM_ACCESSTOKEN` env var → Azure CLI (`az account get-access-token`) → Git Credential Manager

No explicit configuration is needed if Git Credential Manager or the GitHub/Azure CLIs are already authenticated. See the [CiDebugMcp README](src/CiDebugMcp/README.md#authentication) for details.

## Guardrails

HyperVMcp and MsBuildMcp include policy-based guardrails that require user approval for state-modifying operations. The servers use [MCP elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) to prompt the user directly — the agent never sees the approval flow.

- **HyperVMcp**: VM lifecycle, command execution, and file transfers require confirmation. Commands are analyzed against pattern lists (blocked/warned/allowed). See the [HyperVMcp guardrails docs](src/HyperVMcp/README.md#guardrails--policy).
- **MsBuildMcp**: Build and cancel operations require confirmation with target/configuration-scoped approval. See the [MsBuildMcp guardrails docs](src/MsBuildMcp/README.md#guardrails).
- **CiDebugMcp**: All tools are read-only and require no approval.
- **ElicitMcp**: Interactive elicitation only (it asks the user for decisions, input, or feedback on the agent's behalf, and provides a form-mode conformance harness). It has no state-modifying operations and no guardrail layer.

To disable all confirmation prompts, set `"mode": "unrestricted"` (HyperVMcp) or pre-approve all builds in the policy file (MsBuildMcp). See each server's README for details.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

## Security

See [SECURITY.md](SECURITY.md) for vulnerability reporting.

## License

[MIT](LICENSE)
