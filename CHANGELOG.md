# Changelog

Each component has its own changelog. The MCP servers are versioned and released independently: a release of a server is tagged `<server>-v<version>` (for example `msbuild-mcp-v0.2.0`) and its release notes come from that server's changelog.

| Component | Changelog | Release tags |
|-----------|-----------|--------------|
| HyperVMcp | [src/HyperVMcp/CHANGELOG.md](src/HyperVMcp/CHANGELOG.md) | `hyperv-mcp-v*` |
| CiDebugMcp | [src/CiDebugMcp/CHANGELOG.md](src/CiDebugMcp/CHANGELOG.md) | `ci-debug-mcp-v*` |
| MsBuildMcp | [src/MsBuildMcp/CHANGELOG.md](src/MsBuildMcp/CHANGELOG.md) | `msbuild-mcp-v*` |
| ElicitMcp | [src/ElicitMcp/CHANGELOG.md](src/ElicitMcp/CHANGELOG.md) | `elicit-mcp-v*` |
| McpSharp (shared library, not released separately) | [src/McpSharp/CHANGELOG.md](src/McpSharp/CHANGELOG.md) | — |

Repository-wide changes (build, CI, and release tooling) that do not change a server's behavior are not recorded in the component changelogs.
