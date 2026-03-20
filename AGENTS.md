# AI Agent Instructions for mcp-tools

This document provides guidance for AI agents working with this repository.

## Repository Overview

This repository contains a shared **MCP (Model Context Protocol)** library and three MCP servers for AI-assisted development:

| Project | Purpose | Tests |
|---------|---------|-------|
| **McpSharp** (`src/McpSharp/`) | Shared MCP protocol library — JSON-RPC 2.0, tool/resource/prompt registry | 63 |
| **HyperVMcp** (`src/HyperVMcp/`) | Hyper-V VM management, remote execution, file transfers | 40 |
| **CiDebugMcp** (`src/CiDebugMcp/`) | CI/CD failure investigation, log analysis, artifact downloads | 135 |
| **MsBuildMcp** (`src/MsBuildMcp/`) | MSBuild solution/project evaluation, dependency graphs, builds | 92 |

**Tech stack**: C# / .NET 9 / JSON-RPC 2.0 over stdio

## Essential Documents

1. **[.github/copilot-instructions.md](.github/copilot-instructions.md)** — Detailed architecture, design decisions, and pitfalls for all projects
2. **[README.md](README.md)** — Top-level overview, build/test/publish commands, configuration
3. **[CONTRIBUTING.md](CONTRIBUTING.md)** — Development setup, code style, PR guidelines
4. Per-project READMEs: [McpSharp](src/McpSharp/README.md), [HyperVMcp](src/HyperVMcp/README.md), [CiDebugMcp](src/CiDebugMcp/README.md), [MsBuildMcp](src/MsBuildMcp/README.md)

## Quick Start

```bash
dotnet build                              # Build all projects
dotnet test                               # Run all tests (must all pass)
dotnet run --project src/MsBuildMcp       # Run a specific server
```

## What Agents Can Help With

### ✅ Appropriate Tasks
- Adding new MCP tools to any server
- Fixing bugs in protocol handling, project evaluation, or command execution
- Improving test coverage across any project
- Adding support for new CI providers (CiDebugMcp) or project types (MsBuildMcp)
- Improving error handling and diagnostics
- Documentation updates

### ⚠️ Be Careful With
- **McpSharp changes** affect all three servers — run the full test suite
- `ProjectEngine.EnsureMsBuildRegistered()` — MSBuild registration is sensitive
- `BuildManager.DiscoverVsToolchain()` — VS discovery and vcvarsall environment capture
- `ElevatedBackend.cs` — Frontend/backend protocol for Hyper-V privilege escalation
- `McpTransport` framing auto-detection — must handle both Content-Length and NDJSON
- Thread safety in `BuildManager` and `CommandRunner`

## Key Invariants

- All tests must pass after changes (`dotnet test`)
- `dotnet build` must produce zero warnings (where `TreatWarningsAsErrors` is set)
- Tool count assertions in test files must be updated when adding/removing tools
- Tests must be self-contained (no hardcoded paths, no external dependencies)
- The dual MSBuild strategy in MsBuildMcp must be preserved (dotnet SDK for evaluation + VS MSBuild for builds)

## Architecture Summary

```
McpSharp (shared library)
    ↑ project reference
    ├── HyperVMcp  (Hyper-V server)
    ├── CiDebugMcp (CI debug server)
    └── MsBuildMcp (MSBuild server)
```

Each server follows the same pattern:
```
Protocol Layer → McpServer (dispatch) → Tools (handlers) → Engine (domain logic)
```
