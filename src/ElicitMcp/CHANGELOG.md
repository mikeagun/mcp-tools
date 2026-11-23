# Changelog — ElicitMcp

All notable changes to ElicitMcp (`elicit-mcp`) are documented in this file. ElicitMcp is versioned and released independently; releases are tagged `elicit-mcp-v<version>`.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Dual-purpose elicitation server with a deliberately small surface:
  - Production tools (always registered): `request_decision`, `request_input` (also covers free-text feedback), and the no-prompt `report_capabilities`
  - Demo-mode tools (`ELICIT_MCP_DEMO_MODE=1`): a single parameterized `elicit_demo(case, simulate_caps?)` covering every construct/format/enum/action/fallback rung/safety check, plus a `run_conformance` batch runner
  - Decision-field deny-safety and sensitive-field guard preserved; URL mode not implemented
  - Built on the `McpSharp.Elicitation` engine; no policy/guardrail layer (interactive only)
- Packed as a .NET tool NuGet package, `Mikeagun.McpTools.ElicitMcp`, with framework-dependent `win-x64` and `win-arm64` packages. `initialize` reports the server's own version.

### Changed

- Targets .NET 10 (`net10.0`) instead of .NET 9, and runs on a newer major .NET runtime when .NET 10 is not installed.

### Fixed

- Protocol handling (from McpSharp): requests are decoded as UTF-8, malformed JSON-RPC messages are answered with errors instead of stopping the server, and large requests are read much faster. See the [McpSharp changelog](https://github.com/mikeagun/mcp-tools/blob/main/src/McpSharp/CHANGELOG.md).
