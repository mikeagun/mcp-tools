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
  - Concurrent dispatch mode for blocking tools
  - Policy framework (`McpSharp.Policy`): `PolicyEngine`, `PolicyDispatch`, `IToolClassifier`, `IRuleMatcher`, `IOptionGenerator`
  - Early response buffering for sequential elicitation calls
  - 61 tests

- **HyperVMcp** — Hyper-V VM management server (25 tools):
  - VM lifecycle: `list_vms`, `start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`, `restore_vm`
  - Sessions: `connect_vm`, `disconnect_vm`, `reconnect_vm`, `list_sessions`
  - Commands: `invoke_command`, `get_command_status`, `cancel_command`, `run_script`
  - Output: `search_command_output`, `get_command_output`, `save_command_output`, `free_command_output`
  - Files: `copy_to_vm`, `copy_from_vm`, `list_vm_files`
  - Diagnostics: `get_vm_info`, `get_services`, `manage_service`, `kill_process`, `set_env`
  - Elevated backend pattern (lazy UAC via `sudo`)
  - Policy-based guardrails with MCP elicitation
  - 188 tests

- **CiDebugMcp** — CI/CD failure investigation server (6 tools):
  - `get_ci_failures`, `search_job_logs`, `get_step_logs`, `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
  - GitHub Actions and Azure DevOps support
  - Lazy multi-source authentication (env vars, GCM, CLI)
  - Agent-first API with progressive disclosure and failure classification
  - 135 tests

- **MsBuildMcp** — MSBuild project exploration server (16 tools, 5 prompts):
  - Project exploration: `list_projects`, `get_project_details`, `get_project_items`, `get_project_imports`, `find_project_for_file`
  - Dependency analysis: `get_dependency_graph`
  - Build execution: `build`, `get_build_status`, `cancel_build`, `parse_build_output`, `search_build_output`, `get_build_output`, `save_build_output`
  - Cross-project queries: `find_property`, `find_packages`, `diff_configurations`
  - Prompts: `diagnose-build-failure`, `what-to-build`, `impact-analysis`, `resolve-nuget-issue`, `explain-build-config`
  - Dual MSBuild strategy (dotnet SDK evaluation + VS MSBuild builds)
  - Policy-based guardrails with MCP elicitation
  - 156 tests
