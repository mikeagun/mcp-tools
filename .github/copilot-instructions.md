# Copilot Instructions for mcp-tools

## Repository Overview

This repository contains a shared MCP protocol library and three MCP servers for AI-assisted development workflows. All projects are .NET 9 and communicate via JSON-RPC 2.0 over stdio.

| Project | Location | Description |
|---------|----------|-------------|
| **McpSharp** | `src/McpSharp/` | Shared MCP protocol library — JSON-RPC 2.0 transport, tool/resource/prompt registry |
| **HyperVMcp** | `src/HyperVMcp/` | Hyper-V VM management — lifecycle, remote execution, file transfers, diagnostics |
| **CiDebugMcp** | `src/CiDebugMcp/` | CI/CD failure investigation — log search, failure triage, artifact downloads |
| **MsBuildMcp** | `src/MsBuildMcp/` | MSBuild project exploration — solution/project evaluation, dependency graphs, builds |

## Quick Reference

| Task | Command |
|------|---------|
| Build all | `dotnet build` |
| Test all | `dotnet test` |
| Run HyperVMcp | `dotnet run --project src/HyperVMcp` |
| Run CiDebugMcp | `dotnet run --project src/CiDebugMcp` |
| Run MsBuildMcp | `dotnet run --project src/MsBuildMcp` |
| Publish HyperVMcp | `dotnet publish src/HyperVMcp -c Release -o publish/hyperv-mcp` |
| Publish CiDebugMcp | `dotnet publish src/CiDebugMcp -c Release -o publish/ci-debug-mcp` |
| Publish MsBuildMcp | `dotnet publish src/MsBuildMcp -c Release -o publish/msbuild-mcp` |

## Dependency Structure

All three servers depend on McpSharp via direct project references (`src/McpSharp/McpSharp.csproj`). Changes to McpSharp affect all servers — run the full test suite after any McpSharp change.

---

# McpSharp

## Architecture

```
src/McpSharp/
├── McpServer.cs      # Registry + JSON-RPC dispatch (tools, resources, prompts)
├── McpTransport.cs   # stdio transport with Content-Length/NDJSON auto-detection
└── McpTypes.cs       # ToolInfo, ResourceInfo, PromptInfo, PromptArgument
```

## Key Design Decisions

### No Default Server Name
`McpServer(string name)` requires a name — no default. This prevents one consumer's name from silently being embedded in another's MCP initialize response.

### Configurable Log Prefix
`McpTransport(stream, stream, string? logPrefix)` writes diagnostics to stderr with the given prefix. Defaults to `"mcp-server"` but consumers should pass their own name.

### Handler Pattern
Tools, resources, and prompts use a `Func<JsonObject, JsonNode?>` handler pattern:

```csharp
server.RegisterTool(new ToolInfo
{
    Name = "my_tool",
    Description = "Does something useful. Returns: { result, count }",
    InputSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["input"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("input"),
    },
    Handler = args =>
    {
        var input = args["input"]!.GetValue<string>();
        return new JsonObject { ["result"] = input.ToUpper(), ["count"] = input.Length };
    },
});
```

McpServer catches handler exceptions and returns them as MCP error responses with `isError: true`.

### Framing Auto-Detection
The transport auto-detects Content-Length vs NDJSON framing from the first non-whitespace byte. `{` → NDJSON, anything else → Content-Length headers. This is locked for the session after first detection.

### JSON-RPC 2.0 Compliance
- Requests with `id` get responses; notifications (no `id`) do not
- Unknown methods throw `InvalidOperationException` → JSON-RPC error code -32603
- `notifications/initialized` and `notifications/cancelled` are silently accepted

## Code Conventions

- **License header**: `// Copyright (c) McpSharp contributors` + `// SPDX-License-Identifier: MIT` on every .cs file
- **Nullable**: `<Nullable>enable</Nullable>` — no nullable warnings allowed
- **No external dependencies**: The library must remain dependency-free (only `System.Text.Json`)
- **Thread safety**: McpTransport is single-threaded (one reader, one writer). McpServer registrations are not thread-safe (register all tools before starting the transport)

## How to Add a New MCP Feature

If the MCP spec adds a new method category (like `logging/` or `sampling/`):

1. Add types to `McpTypes.cs` (e.g., `LoggingInfo`)
2. Add registry + handler to `McpServer.cs` (e.g., `RegisterLogging()`, `HandleLoggingSet()`)
3. Add the method to the `Dispatch` switch expression
4. Add capability to `HandleInitialize()`
5. Add tests in `McpServerTests.cs` covering registration, listing, calling, error paths
6. Add integration round-trip test in `McpTransportTests.cs`

## Testing Conventions

Tests are in `tests/McpSharp.Tests/`:
- **McpServerTests.cs**: Unit tests for each dispatch method. Test success path, error path, null handling, and edge cases (empty registry, missing params, unknown names)
- **McpTransportTests.cs**: Integration tests using `MemoryStream` for stdin/stdout. Test both NDJSON and Content-Length framing, including auto-detection, multi-request, notifications, and error responses
- Tests must not depend on network, filesystem, or external processes
- Use `[Theory]`/`[InlineData]` for parameterized coverage where appropriate

## Common Pitfalls

- **JsonNode cloning**: `JsonNode.Parse(node.ToJsonString())` is used to clone nodes because `JsonNode` can only have one parent. This appears in `HandleToolsList` (schema cloning) and transport (id/result cloning).
- **Content-Length is byte count**: The `Content-Length` header specifies bytes, not characters. Multi-byte UTF-8 content requires `Encoding.UTF8.GetBytes(body).Length`, not `body.Length`.
- **WriteMessage framing**: When framing is `Unknown` (no messages read yet), `WriteMessage` defaults to Content-Length framing.

---

# HyperVMcp

## Architecture

```
src/HyperVMcp/
├── Program.cs                   # Entry point — elevated backend, engine wiring, stdio transport
├── Engine/
│   ├── Types.cs                 # ManagedSession, CommandJob, VmInfo, VmOperationResult, OutputSlice
│   ├── VmManager.cs             # VM lifecycle (start/stop/restart/checkpoint/restore) via elevated backend
│   ├── SessionManager.cs        # PowerShell session management (PSDirect + WinRM), retry logic
│   ├── CommandRunner.cs         # Long-running command execution with async polling and concurrency limits
│   ├── OutputBuffer.cs          # Output retention (full: 50K lines / tail: 1K ring buffer), search, export
│   ├── FileTransferManager.cs   # File upload/download with auto-compression (tar) for large transfers
│   ├── ElevatedBackend.cs       # Elevated subprocess management — JSON line protocol over stdio
│   ├── CredentialStore.cs       # Credential resolution: explicit → Credential Manager → default target
│   └── PsUtils.cs              # PowerShell single-quote escaping and name validation
└── Tools/
    ├── ToolRegistration.cs      # Central registration — calls Register() on all tool groups
    ├── VmTools.cs               # list_vms, start_vm, stop_vm, restart_vm, checkpoint_vm, restore_vm
    ├── SessionTools.cs          # connect_vm, disconnect_vm, reconnect_vm, list_sessions
    ├── CommandTools.cs          # invoke_command, get_command_status, cancel_command, run_script, output tools
    ├── FileTools.cs             # copy_to_vm, copy_from_vm, list_vm_files
    ├── VmInfoTools.cs           # get_vm_info
    ├── ServiceTools.cs          # get_services, manage_service
    ├── ProcessTools.cs          # kill_process
    └── EnvTools.cs              # set_env
```

## Key Design Decisions

### Elevated Backend Pattern
The MCP server runs non-elevated. On first VM operation requiring admin privileges, it spawns `sudo hyperv-mcp.exe --elevated-backend` which triggers a one-time UAC prompt. The backend communicates with the frontend via JSON lines over stdin/stdout. The backend hosts a persistent PowerShell runspace that retains PSSessions across calls.

### Shared Runspace Architecture
The elevated backend uses a single `Runspace` instance. PSSessions created during sync operations (e.g., `connect_vm`) are accessible from async commands (e.g., `invoke_command`) via `Get-PSSession -InstanceId`. This avoids creating duplicate sessions.

### Async Command Model
`invoke_command` starts commands in the background and returns immediately with a `command_id`. The `timeout` parameter controls how long to wait for initial output before returning. Commands continue running independently — agents poll via `get_command_status` with `since_line` for efficient delta updates. Internally, `WaitForNews()` uses `ManualResetEventSlim` for event-based signaling.

### Dual Output Modes
- **full** (default): Retains up to 50,000 lines. Supports `search_command_output`, `get_command_output` with ranges, and `save_command_output`.
- **tail**: Ring buffer of last 1,000 lines. Lightweight for commands where only recent output matters.
- **save_to**: Optionally streams all output to disk as lines arrive, independent of retention mode.

### Output Formats
The `output_format` parameter on `invoke_command` controls PowerShell formatting:
- `"none"` (default): Raw streaming, no buffering
- `"text"`: Pipes through `Out-String -Width 500` (buffers all output for tabular rendering)
- `"json"`: Pipes through `ConvertTo-Json -Depth 5 -Compress`

### Connection Modes
- **PSDirect**: Local Hyper-V VMs via VM name. Uses `New-PSSession -VMName`.
- **WinRM**: Remote machines via IP or hostname. Uses `New-PSSession -ComputerName`.

Connection retries up to 10 times with 3-second intervals for transient PSDirect errors.

### Credential Resolution
Priority order:
1. Explicit `username` + `password` parameters (highest priority)
2. Windows Credential Manager target lookup (default target: `"TEST_VM"`)
3. Results cached in-memory for the process lifetime

### Bulk Parallel Operations
VM lifecycle tools (`start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`, `restore_vm`) accept a single VM name or an array. Each operation runs as a parallel `Task` with individual success/failure reporting per VM.

### File Transfer Compression
Files ≥ 10 MB or directories are automatically compressed via `tar` before transfer and extracted on the destination. This significantly reduces transfer time over PSDirect/WinRM sessions. The threshold is configurable via `FileTransferManager.CompressionThresholdBytes`.

## How to Add a New Tool

1. Create or extend a tool class in `src/HyperVMcp/Tools/`
2. Register with `server.RegisterTool(new ToolInfo { Name, Description, InputSchema, Handler })` in a `Register()` method
3. Wire into `ToolRegistration.RegisterAll()` if it's a new class
4. Use `PsUtils.PsEscape()` for any user-provided strings embedded in PowerShell scripts
5. Use `PsUtils.ValidateName()` for VM names, service names, and other identifiers
6. Add tests in `BasicTests.cs`
7. Update `src/HyperVMcp/README.md` tool tables

## Testing Conventions

- **TypesTests**: Unit tests for `CommandJob` behavior — output buffering, status transitions, snapshot generation, `WaitForNews` semantics
- **ToolRegistrationTests**: Verifies all tools register without error, checks minimum tool count (≥24), and validates `initialize` response
- Tests must not depend on Hyper-V VMs being available — only test internal logic and registration
- All tests must pass in a standard CI environment without elevated privileges

## Important Files

| File | Why It Matters |
|------|---------------|
| `ElevatedBackend.cs` | Frontend/backend communication protocol. Two code paths: `ElevatedBackend` (frontend, sends requests) and `ElevatedBackendHost` (backend, executes PowerShell). JSON line protocol with streaming output/error/done events. |
| `SessionManager.cs` | Session lifecycle, retry logic for transient errors, command execution with output format handling. The `IsTransientError()` method detects 8+ error patterns for PSDirect resilience. |
| `CommandRunner.cs` | Command lifecycle management. Enforces max 5 concurrent commands per session. Handles both VM commands (wrapped in `Invoke-Command`) and host-side transfers. |
| `OutputBuffer.cs` | Dual-mode output retention with search, range queries, and disk streaming. The `Search()` method supports regex with context lines. `Free()` releases memory while preserving metadata. |
| `Types.cs` | All domain types. `CommandJob` is the most complex — manages output buffering, status transitions, and event-based polling via `WaitForNews()`. |

## Common Pitfalls

- **PowerShell escaping**: `PsEscape()` only handles single-quoted strings (`'` → `''`). It also strips `\r`, `\n`, `\0` to prevent syntax breakage. Never use double-quoted strings for user input in scripts.
- **Session InstanceId**: PSSessions are tracked by `InstanceId` (GUID), not by name. This is what enables cross-runspace access between sync and async operations in the elevated backend.
- **Output format buffering**: `"text"` format pipes through `Out-String` which buffers ALL output until the command completes. For long-running commands that need streaming output, use `"none"` format.
- **PATH replacement**: `set_env` with `PATH` replaces the entire value. To extend PATH, use `invoke_command` with `$env:PATH += ';C:\new\path'` instead.
- **Compression threshold**: Auto-compression triggers on aggregate size of all source files. A directory of many small files totaling > 10 MB will be compressed even though individual files are small.
- **Concurrent command limit**: `CommandRunner.MaxCommandsPerSession` defaults to 5. Starting a 6th command on the same session throws an exception. Completed commands don't count toward the limit.

---

# CiDebugMcp

## Architecture

```
src/CiDebugMcp/
├── Program.cs                    # Entry point — creates engines, registers tools, runs transport
├── Engine/
│   ├── IGitHubApi.cs             # Interface for testability (implemented by GitHubClient)
│   ├── ICiProvider.cs            # Provider-agnostic CI interface + shared data types
│   ├── GitHubClient.cs           # GitHub API client with lazy multi-source auth
│   ├── GitHubCiProvider.cs       # GitHub Actions implementation of ICiProvider
│   ├── AdoCiProvider.cs          # Azure DevOps implementation of ICiProvider
│   ├── CiProviderResolver.cs     # URL-based provider resolution (GitHub vs ADO)
│   ├── LogCache.cs               # In-memory TTL cache for parsed job logs
│   ├── LogParser.cs              # Step parsing, error extraction, test summary detection
│   ├── BinaryAnalyzer.cs         # PE dependency analysis via dumpbin
│   ├── DownloadManager.cs        # Concurrent download pool with artifact dedup
│   └── DownloadJob.cs            # Async HTTP→disk streaming with ZIP extraction
└── Tools/
    ├── ToolRegistration.cs       # Wires all tool groups
    ├── LogTools.cs               # get_ci_failures, search_job_logs, get_step_logs
    ├── FailureReportFormatter.cs # CiFailureReport → JSON serialization
    ├── ArtifactTools.cs          # download_artifact
    └── BinaryTools.cs            # analyze_binary_deps, update_binary_baselines
```

## Key Design Decisions

### Agent-First API Design
Every design choice optimizes for LLM agent consumption:
- **No pagination** — bounded results with `max_errors`, `max_results`, range-based log access
- **Progressive disclosure** — `detail: "summary"` → `"jobs"` → `"errors"` controls verbosity
- **Hints in responses** — every failure includes the exact next tool call to drill deeper
- **Failure classification** — `failure_type` field routes agent strategy (build→find compiler error, test→find regression, infra→skip/retry)

### Authentication Strategy
Auth resolves lazily on first API call (not at startup) to avoid GCM popups during server initialization.

**GitHub**: `GITHUB_TOKEN` env → `GH_TOKEN` env → Git Credential Manager (supports MSFT SSO) → `gh` CLI.

**Azure DevOps**: `AZURE_DEVOPS_PAT` env (Basic) → `SYSTEM_ACCESSTOKEN` env (Bearer, auto-injected in ADO pipelines) → Azure CLI `az account get-access-token` (Bearer) → Git Credential Manager for `dev.azure.com`.

### Error Extraction Pipeline
`EnrichWithLogErrorsAsync` in LogTools.cs is the core extraction pipeline:
1. Download job log → parse into steps
2. Scan steps inclusively: resolved step first, then all `##[error]`-annotated steps, then longest non-setup steps
3. For each step: `ExtractMeaningfulErrors` with full-step scan (5x `maxErrors` internal cap, truncate after dedup)
4. If nothing found: try `ExtractTestSummary` from step tails
5. Last resort: fall back to API step metadata + `available_steps` for the agent

### Step Identity Mismatch
CI provider step names (YAML `name:` field in GitHub Actions, task `displayName` in ADO) differ from log step headers (command text). Step numbers may also differ (GitHub Actions API includes setup/post steps; logs don't). Therefore:
- **Never use API step names in hints** — use log step names or omit
- `ResolveLogStep()` uses a 5-strategy cascade (exact → substring → word overlap → positional → last with errors) but hints prefer `step_number` from logs
- `failed_step` objects from API include `source: "api"` flag

### Download Architecture
`DownloadJob` streams HTTP→disk in 80KB chunks with periodic progress signals. `DownloadManager` provides a concurrent download pool with artifact_id dedup and 1-hour TTL cleanup. The `timeout` param on `download_artifact` controls poll-wait only — downloads continue in background independently.

## How to Add a New Tool

1. Create or extend a tool class in `src/CiDebugMcp/Tools/`
2. Register with `server.RegisterTool(new ToolInfo { Name, Description, InputSchema, Handler })`
3. Wire into `ToolRegistration.RegisterAll()`
4. **Description must document the response schema** — agents discover fields from descriptions, e.g.: `"Returns: { job_id, status, errors: [{raw, code?, message?}], hint }"`
5. **Include `repo` in all hint strings** — use `StepLogsHint()` / `SearchLogsHint()` helpers
6. Add tests using `FakeGitHubApi` for orchestration, direct calls for pure logic
7. Update `src/CiDebugMcp/README.md` tool table and `prompts/ci-debug.prompt.md`

## Testing Conventions

- **LogParserTests / TestExtractionTests**: Pure logic tests with constructed string arrays. No mocking needed.
- **CiFailuresTests / LogToolsTests**: Use `FakeGitHubApi` (canned JSON responses keyed by API path, longest-match routing). Register tools on a real `McpServer`, dispatch via `tools/call`.
- **DownloadJobTests**: Use `StaticFileHandler` (HttpMessageHandler returning file content) to test ZIP extraction without network.
- **LogCacheTests**: Direct cache CRUD.
- `CallTool()` helper checks `isError` and fails with the error message if the tool threw.
- Numeric params (`run_id`, `job_id`) must be cast to `(long)` in test `JsonObject` args — `GetValue<long>()` rejects `Int32`.

## Important Files

| File | Why It Matters |
|------|---------------|
| `LogTools.cs` | ~1700 lines. Contains all CI failure orchestration, hint generation, error extraction pipeline, diagnosis synthesis. |
| `LogParser.cs` | ~600 lines. All regex patterns, step parsing, error classification, test name/summary extraction. Changes here affect what agents see. |
| `GitHubClient.cs` | Auth resolution chain, log caching, API wrappers. The `CreateAuthenticatedClient()` method creates per-download HTTP clients. |
| `FakeGitHubApi.cs` | Test infrastructure. Uses longest-match path routing — overlapping keys like `/pulls/42` and `/pulls/42/files` must both be registered; the longer key wins. |

## Common Pitfalls

- **`IsInSetupBlock` is aggressive**: It treats ALL content inside `##[group]` blocks as "in setup". This means `search_job_logs` with `include_setup=false` (default) filters lines inside any group. Use `step_name` or `include_setup=true` when searching build/test steps.
- **MSBuild prefix in errors**: Lines like `113>file.cpp(10): error C2084` have an MSBuild project-number prefix. `TryParseError` strips it via `MsbuildPrefixRegex` before parsing. The dedup key uses the cleaned file path.
- **`ExtractMeaningfulErrors` scan cap**: The scan loop uses `scanCap = max(maxErrors * 5, 100)` to ensure deep errors are found. The output is truncated to `maxErrors` after dedup.
- **`TestFailureRegex` tightened**: Uses `FAILED:` (with colon) not bare `FAIL` — the bare pattern matched ProcDump `FAIL-FAST` output.

---

# MsBuildMcp

## Architecture

```
src/MsBuildMcp/
├── Program.cs              # Entry point — MSBuild registration, engine wiring, stdio transport
├── Engine/
│   ├── SolutionEngine.cs   # .sln regex parser with mtime caching
│   ├── SolutionTypes.cs    # SolutionInfo, SolutionProject, SolutionConfig
│   ├── ProjectEngine.cs    # Microsoft.Build evaluation API wrapper with mtime caching
│   ├── ProjectTypes.cs     # ProjectSnapshot, ProjectReference, PackageReference, ProjectImport
│   ├── DependencyGraph.cs  # Project reference DAG, topological sort (Kahn's algorithm)
│   ├── BuildRunner.cs      # BuildJob (streaming parser) + BuildManager (lifecycle)
│   ├── BuildTypes.cs       # BuildStatus, BuildCollision data types
│   ├── OutputBuffer.cs     # Build output retention, search, range queries, and export
│   └── ErrorParser.cs      # MSBuild output → structured BuildDiagnostic objects
├── Tools/
│   ├── ToolRegistration.cs # Wires all tool groups into McpServer
│   ├── SolutionTools.cs    # list_projects
│   ├── ProjectTools.cs     # get_project_details, find_project_for_file, get_project_items, get_project_imports
│   ├── DependencyTools.cs  # get_dependency_graph
│   ├── BuildTools.cs       # build, get_build_status, cancel_build, parse_build_output, search/get/save_build_output
│   └── QueryTools.cs       # find_packages, find_property, diff_configurations
├── Prompts/
│   └── PromptRegistration.cs  # 5 MCP prompt templates
└── Resources/
    └── ResourceRegistration.cs  # Dynamic solution resource provider
```

## Key Design Decisions

### MSBuild Registration Strategy
The server uses a **dual MSBuild strategy**:
- **In-process evaluation**: Uses `MSBuildLocator.RegisterDefaults()` (dotnet SDK MSBuild) which has working .NET 9 SDK resolvers. VCTargetsPath from Visual Studio is set as an env var for C++ project support.
- **Build subprocess**: Uses VS `MSBuild.exe` directly (discovered via `vswhere` + `vcvarsall` for the full C++ build environment).

This split exists because VS MSBuild's SDK resolver DLLs target .NET Framework and can't load in a .NET 9 host. See `ProjectEngine.EnsureMsBuildRegistered()` and `BuildManager.DiscoverVsToolchain()`.

### Evaluation over Parsing
Never regex-parse `.vcxproj` XML directly. Always use `Microsoft.Build.Evaluation.Project` for correct handling of property inheritance, conditional evaluation, import chains, and NuGet targets.

### Stateless Tools, Cached Engine
Each MCP tool call is stateless (no session between calls), but the engine layer caches evaluated projects keyed by `(path, config, platform, mtime)`. See `ProjectEngine._cache`.

### Environment Variable Poisoning
The build subprocess must strip `MSBUILD_EXE_PATH`, `MSBuildSDKsPath`, `MSBuildExtensionsPath`, and `DOTNET_HOST_PATH` from its environment. These are injected by the dotnet host and would poison VS MSBuild's NuGet SDK resolver. See `BuildManager.CreateProcessStartInfo()`.

## How to Add a New Tool

1. Create or extend a tool class in `src/MsBuildMcp/Tools/`
2. Register it with `server.RegisterTool(new ToolInfo { Name, Description, InputSchema, Handler })` in a `Register()` method. Handler signature: `Func<JsonObject, JsonNode?>`.
3. Wire it into `ToolRegistration.RegisterAll()` if it's a new class
4. Update the `ExactToolSet` test assertion in `FeatureTests.cs` — this test verifies that every registered tool name is known, preventing accidental tool renames or removals
5. Add unit tests for the underlying logic and integration tests for MCP dispatch
6. Update `src/MsBuildMcp/README.md` tool reference table and `CHANGELOG.md`

Exceptions thrown by tool handlers are caught by McpServer and returned as MCP error responses with `isError: true`.

## How to Add a New Prompt

1. Add a registration method in `PromptRegistration.cs`
2. Call it from `RegisterAll()`
3. Add a test in `IntegrationTests.cs` or `FeatureTests.cs`
4. Document in `src/MsBuildMcp/README.md`

## Testing Conventions

- **SolutionEngineTests / DependencyGraphTests**: Create temp dirs and synthetic `.sln`/`.vcxproj` files, clean up via `IDisposable`
- **FeatureTests**: Creates a full temporary solution with 4 `.csproj` projects (CoreLib, DataLib, WebApp, Tests) to test tools end-to-end
- **IntegrationTests**: Tests tool/prompt registration counts and dispatch against the real `mcp-tools.sln`
- Tests must be self-contained — no hardcoded paths, no external dependencies
- All tests must pass on `windows-latest` in GitHub Actions CI

## Important Files

| File | Why It Matters |
|------|---------------|
| `ProjectEngine.cs` | Most complex file. MSBuild locator setup, evaluation with caching, property/item/import extraction. |
| `BuildRunner.cs` | Async build management. `BuildJob` (streaming parser, ring buffer, error events) + `BuildManager` (lifecycle, collision detection). |
| `ProjectTools.cs` | Project tool handlers + `SnapshotToJson` serializer with optional property filtering. |
| `QueryTools.cs` | Cross-solution query tools. `find_property` supports exact name and regex with result limiting. |

## Common Pitfalls

- **SolutionDir**: MSBuild normally sets this from the `.sln` context. Standalone `.vcxproj` evaluation requires setting it as a global property. `ProjectEngine.InferSolutionDir()` walks parent dirs to find a `.sln`.
- **ItemDefinitionGroup**: ClCompile/Link/Lib/Midl metadata is only extracted for C++ projects (projects with `ClCompile` items). WiX and C# projects skip this to avoid spurious inherited settings.
- **Central Package Management (CPM)**: When `PackageReference` has no Version, check `PackageVersion` items from `Directory.Packages.props`. See `ExtractPackageReferences()`.
- **Build collision**: Only one build runs at a time. If a build is already running, the `build` tool polls it instead of starting a new one, and reports a `collision` if targets differ.
