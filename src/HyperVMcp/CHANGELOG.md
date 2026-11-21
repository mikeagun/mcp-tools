# Changelog — HyperVMcp

All notable changes to HyperVMcp (`hyperv-mcp`) are documented in this file. HyperVMcp is versioned and released independently; releases are tagged `hyperv-mcp-v<version>`.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Hyper-V VM management server (26 tools):
  - VM lifecycle: `list_vms`, `start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`, `restore_vm`
  - Sessions: `connect_vm`, `disconnect_vm`, `reconnect_vm`, `list_sessions`
  - Commands: `invoke_command`, `get_command_status`, `cancel_command`, `run_script`
  - Output: `search_command_output`, `get_command_output`, `save_command_output`, `free_command_output`
  - Files: `copy_to_vm`, `copy_from_vm`, `list_vm_files`
  - Diagnostics: `get_vm_info`, `get_services`, `manage_service`, `kill_process`, `set_env`
- Elevated backend pattern (lazy UAC via `sudo`)
- Policy-based guardrails with MCP elicitation
- Packed as a .NET tool NuGet package, `Mikeagun.McpTools.HyperVMcp`, with framework-dependent `win-x64` and `win-arm64` packages. `initialize` reports the server's own version.

### Changed

- Targets .NET 10 (`net10.0`, previously `net9.0-windows`) and declares Windows support with `SupportedOSPlatform("windows")`. Runs on a newer major .NET runtime when .NET 10 is not installed.
- `Microsoft.PowerShell.SDK` is updated to 7.6.10.
- Policy rules approved via elicitation are saved to `%LOCALAPPDATA%\mcp-tools\hyperv-mcp-policy.json` unless a policy file is configured (`--policy` or `HYPERV_MCP_POLICY`) or `hyperv-mcp-policy.json` already exists next to the executable. Previously they were saved next to the executable, where an upgrade or reinstall that replaced the install directory lost them, and saving failed if that directory was not writable. That per-user file is also read at startup.

### Fixed

- Policy prompts that offer to save a rule honor the configured elicitation timeout rather than blocking indefinitely.
- Protocol handling (from McpSharp): requests are decoded as UTF-8, malformed JSON-RPC messages are answered with errors instead of stopping the server, and large requests are read much faster. See the [McpSharp changelog](https://github.com/mikeagun/mcp-tools/blob/main/src/McpSharp/CHANGELOG.md).
