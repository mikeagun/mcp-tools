# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **McpSharp** — Shared MCP protocol library:
  - JSON-RPC 2.0 dispatch with Content-Length and NDJSON framing auto-detection
  - Tool, resource, and prompt registry with handler pattern
  - MCP elicitation support (server-initiated prompts to the client)
  - Elicitation capability parsing (`ElicitationCapabilities`/`ElicitationMode`): mode-aware gating so the server never sends an elicitation mode the client did not advertise
  - Concurrent dispatch mode for blocking tools
  - Policy framework (`McpSharp.Policy`): `PolicyEngine`, `PolicyDispatch`, `IToolClassifier`, `IRuleMatcher`, `IOptionGenerator`
  - Early response buffering for sequential elicitation calls
  - Full form-mode elicitation engine (`McpSharp.Elicitation`, MCP `2025-11-25`): `FormSchemaBuilder` (typed primitive/enum/array construct emitters, flat-object guard, sensitive-field guard), pure single-shot `ElicitationPlanner` (F1/F4/F5/F6 rewrites), `ElicitationDriver` (F2/F3/F8 orchestration with decision-field deny-safety), `FormValidator` (server-side validation), and typed `ElicitationResult` accessors
  - `ElicitationCapabilities` parsed at `initialize` (form/url modes; empty object = form-only) with structural mode-guarding so the server never sends a mode the client did not advertise

- **HyperVMcp** — Hyper-V VM management server (26 tools):
  - VM lifecycle: `list_vms`, `start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`, `restore_vm`
  - Sessions: `connect_vm`, `disconnect_vm`, `reconnect_vm`, `list_sessions`
  - Commands: `invoke_command`, `get_command_status`, `cancel_command`, `run_script`
  - Output: `search_command_output`, `get_command_output`, `save_command_output`, `free_command_output`
  - Files: `copy_to_vm`, `copy_from_vm`, `list_vm_files`
  - Diagnostics: `get_vm_info`, `get_services`, `manage_service`, `kill_process`, `set_env`
  - Elevated backend pattern (lazy UAC via `sudo`)
  - Policy-based guardrails with MCP elicitation

- **CiDebugMcp** — CI/CD failure investigation server (6 tools):
  - `get_ci_failures`, `search_job_logs`, `get_step_logs`, `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
  - GitHub Actions and Azure DevOps support
  - Lazy multi-source authentication (env vars, GCM, CLI)
  - Agent-first API with progressive disclosure and failure classification

- **MsBuildMcp** — MSBuild project exploration server (17 tools, 5 prompts):
  - Project exploration: `list_projects`, `get_project_details`, `get_project_items`, `get_project_imports`, `find_project_for_file`
  - Dependency analysis: `get_dependency_graph`
  - Build execution: `build`, `publish`, `get_build_status`, `cancel_build`, `parse_build_output`, `search_build_output`, `get_build_output`, `save_build_output`
  - Cross-project queries: `find_property`, `find_packages`, `diff_configurations`
  - Prompts: `diagnose-build-failure`, `what-to-build`, `impact-analysis`, `resolve-nuget-issue`, `explain-build-config`
  - Dual MSBuild strategy (dotnet SDK evaluation + VS MSBuild builds)
  - Visual Studio version selection for side-by-side installs (e.g. VS 2022 + VS 2026): `--vs <selector>` / `MSBUILD_MCP_VS` accept `latest`, a product year (`2022`), a version prefix (`17.14`), a version range (`[17,18)`), or an installation path; C++ evaluation and builds use the same selected installation, and build status reports it as `visual_studio`
  - `publish` uses `dotnet` CLI directly (not VS MSBuild) for .NET SDK projects
  - Policy-based guardrails with MCP elicitation

- **ElicitMcp** — Dual-purpose elicitation server with a deliberately small surface:
  - Production tools (always registered): `request_decision`, `request_input` (also covers free-text feedback), and the no-prompt `report_capabilities`
  - Demo-mode tools (`ELICIT_MCP_DEMO_MODE=1`): a single parameterized `elicit_demo(case, simulate_caps?)` covering every construct/format/enum/action/fallback rung/safety check, plus a `run_conformance` batch runner
  - Decision-field deny-safety and sensitive-field guard preserved; URL mode not implemented
  - Built on the `McpSharp.Elicitation` engine; no policy/guardrail layer (interactive only)

### Fixed

- **CiDebugMcp** — Authentication-failure retries prompt the user via elicitation (the server wires `McpServer.Transport`) instead of failing straight to a STOP error.
- **McpSharp** — The two-prompt persistence follow-up (`PolicyDispatch`) honors the configured elicitation timeout rather than blocking indefinitely.
- **MsBuildMcp** — C++ project evaluation no longer fails when Visual Studio 2026 is installed: `VCTargetsPath` is now the highest complete `MSBuild\Microsoft\VC\vNNN` directory (VS 2026 uses `v180`; its `v170` folder is an incomplete stub) instead of a hard-coded `v170`.
- **MsBuildMcp** — Visual Studio Build Tools and prerelease installations are now discovered (stable installations are still preferred), and a failing `vcvarsall.bat` is reported instead of silently starting a build without the VC environment.
