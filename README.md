# MCP Tools

A collection of MCP (Model Context Protocol) servers and libraries for AI-assisted development workflows. All servers communicate via JSON-RPC 2.0 over stdio and are built on the shared **McpSharp** protocol library.

## Projects

| Project | Description |
|---------|-------------|
| [McpSharp](src/McpSharp/README.md) | Lightweight MCP server library for .NET — JSON-RPC 2.0 transport, tool/resource/prompt registry |
| [HyperVMcp](src/HyperVMcp/README.md) | Hyper-V VM management — lifecycle, remote execution, file transfers, diagnostics |
| [CiDebugMcp](src/CiDebugMcp/README.md) | CI/CD failure investigation — log search, failure triage, artifact downloads, binary analysis |
| [MsBuildMcp](src/MsBuildMcp/README.md) | MSBuild project exploration — solution/project evaluation, dependency graphs, build execution |

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with C++ workload (for MsBuildMcp)
- Hyper-V enabled (for HyperVMcp, Windows only)

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test    # 540 tests across all projects
```

| Project | Tests |
|---------|-------|
| McpSharp | 61 |
| HyperVMcp | 188 |
| CiDebugMcp | 135 |
| MsBuildMcp | 156 |

## Publishing

Each server can be published independently:

```bash
dotnet publish src/HyperVMcp -c Release -o publish/hyperv-mcp
dotnet publish src/CiDebugMcp -c Release -o publish/ci-debug-mcp
dotnet publish src/MsBuildMcp -c Release -o publish/msbuild-mcp
```

## Repository Structure

```
mcp-tools/
├── mcp-tools.sln               # Unified solution (all projects)
├── src/
│   ├── McpSharp/                # Shared MCP protocol library
│   ├── HyperVMcp/               # Hyper-V MCP server
│   ├── CiDebugMcp/              # CI debug MCP server
│   └── MsBuildMcp/              # MSBuild MCP server
├── tests/
│   ├── McpSharp.Tests/
│   ├── HyperVMcp.Tests/
│   ├── CiDebugMcp.Tests/
│   └── MsBuildMcp.Tests/
├── docs/                        # Guides (creating a new server, etc.)
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

To disable all confirmation prompts, set `"mode": "unrestricted"` (HyperVMcp) or pre-approve all builds in the policy file (MsBuildMcp). See each server's README for details.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

## Security

See [SECURITY.md](SECURITY.md) for vulnerability reporting.

## License

[MIT](LICENSE)
