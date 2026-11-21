# Changelog — CiDebugMcp

All notable changes to CiDebugMcp (`ci-debug-mcp`) are documented in this file. CiDebugMcp is versioned and released independently; releases are tagged `ci-debug-mcp-v<version>`.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- CI/CD failure investigation server (6 tools): `get_ci_failures`, `search_job_logs`, `get_step_logs`, `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
- GitHub Actions and Azure DevOps support
- Lazy multi-source authentication (env vars, GCM, CLI)
- Agent-first API with progressive disclosure and failure classification
- Packed as a .NET tool NuGet package, `Mikeagun.McpTools.CiDebugMcp`, with framework-dependent `win-x64` and `win-arm64` packages. `initialize` reports the server's own version.

### Changed

- Targets .NET 10 (`net10.0`) instead of .NET 9, and runs on a newer major .NET runtime when .NET 10 is not installed.

### Fixed

- Authentication-failure retries prompt the user via elicitation (the server wires `McpServer.Transport`) instead of failing straight to a STOP error.
- `analyze_binary_deps` and `update_binary_baselines` find `dumpbin.exe` on ARM64 hosts that only have the native ARM64 MSVC tools installed. Previously, only the x64 `dumpbin.exe` was found.
- Protocol handling (from McpSharp): requests are decoded as UTF-8, malformed JSON-RPC messages are answered with errors instead of stopping the server, and large requests are read much faster. See the [McpSharp changelog](https://github.com/mikeagun/mcp-tools/blob/main/src/McpSharp/CHANGELOG.md).
