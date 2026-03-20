# Hyper-V MCP Server

An MCP (Model Context Protocol) server for managing Hyper-V virtual machines and executing remote commands via PowerShell sessions. Designed for AI-assisted VM testing, deployment, and diagnostics workflows.

## What It Does

Provides **26 structured tools** for VM lifecycle management, remote command execution, file transfers, and system diagnostics — all accessible to LLM agents via the MCP protocol over stdio. Key capabilities:

- **VM lifecycle** — start, stop, restart, checkpoint, and restore VMs (with parallel bulk operations)
- **Remote execution** — run PowerShell commands on VMs via PSDirect or WinRM sessions with streaming output
- **File transfers** — upload/download files with automatic compression for large transfers
- **Output management** — searchable output buffers, delta polling, and disk streaming
- **System diagnostics** — query OS info, services, processes, and environment variables
- **Guardrails** — policy-based approval system with MCP elicitation for user confirmation

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Windows)
- Hyper-V enabled on the host
- `sudo` available for elevated operations (Windows 11+ or [gsudo](https://github.com/gerardog/gsudo))
- VM credentials stored in Windows Credential Manager (or provided explicitly)

### Credential Setup

```powershell
Install-Module CredentialManager -Force
New-StoredCredential -Target "TEST_VM" -UserName "Administrator" -Password "your-password"
```

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test    # 164 tests
```

## Quick Start

```bash
dotnet publish src/HyperVMcp -c Release -o publish
```

Add to your MCP client configuration:

```json
{
  "mcpServers": {
    "hyperv": {
      "command": "C:\\path\\to\\publish\\hyperv-mcp.exe"
    }
  }
}
```

With a custom policy file:

```json
{
  "mcpServers": {
    "hyperv": {
      "command": "C:\\path\\to\\publish\\hyperv-mcp.exe",
      "args": ["--policy", "C:\\path\\to\\hyperv-mcp-policy.json"]
    }
  }
}
```

## Tools

### VM Lifecycle

| Tool | Description |
|------|-------------|
| `list_vms` | List Hyper-V VMs with state, CPU, memory, and checkpoint info |
| `start_vm` | Start one or more VMs; optionally wait for heartbeat + Guest Services |
| `stop_vm` | Stop one or more VMs (force by default) |
| `restart_vm` | Restart VMs with optional session reconnect |
| `checkpoint_vm` | Create named checkpoint on one or more VMs |
| `restore_vm` | Restore VMs to a named checkpoint (default: "baseline") |

### Session Management

| Tool | Description |
|------|-------------|
| `connect_vm` | Establish persistent PowerShell session (PSDirect or WinRM) |
| `disconnect_vm` | Close a session and release resources |
| `reconnect_vm` | Refresh a broken or stale session |
| `list_sessions` | List all active sessions with status |

### Command Execution

| Tool | Description |
|------|-------------|
| `invoke_command` | Execute PowerShell on a VM with timeout and streaming output |
| `get_command_status` | Poll a running command for new output or completion |
| `cancel_command` | Cancel a running command |
| `run_script` | Execute a `.ps1` script file on a VM |

### Output Management

| Tool | Description |
|------|-------------|
| `search_command_output` | Regex search over a command's retained output |
| `get_command_output` | View output by line range, tail, or centered on a pattern match |
| `save_command_output` | Write a command's output to a file on the host |
| `free_command_output` | Release output buffer to free memory |

### File Transfer

| Tool | Description |
|------|-------------|
| `copy_to_vm` | Upload files/directories to a VM (auto-compresses large transfers) |
| `copy_from_vm` | Download files from a VM to the host |
| `list_vm_files` | List files and directories on a VM with summary stats |

### System Information

| Tool | Description |
|------|-------------|
| `get_vm_info` | Get OS build, disk space, memory, and uptime from a VM |
| `get_services` | Query service status on a VM |
| `manage_service` | Start, stop, or restart a service on a VM |
| `kill_process` | Terminate a process on a VM by PID or name |
| `set_env` | Set environment variables for a session (injected into all subsequent commands) |

## Guardrails & Policy

All state-modifying tools require user approval before execution. The server uses [MCP elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) to prompt the user directly through the MCP client — the agent never sees the approval flow.

### How It Works

When an agent calls a tool that requires confirmation (e.g., `start_vm`, `invoke_command`), the server pauses and asks the user through the client UI. The user sees options like:

```
Approve stop_vm: VM=test-vm
```

Session approvals last until the server restarts. Permanent approvals are saved to the policy file.

### Tool Risk Tiers

| Tier | Tools | Default (Standard mode) |
|------|-------|------------------------|
| **ReadOnly** | list_vms, get_services, get_vm_info, list_sessions, get_command_status, get_command_output, search_command_output, list_vm_files | Always allowed |
| **Session** | disconnect_vm, reconnect_vm, set_env | Always allowed |
| **Moderate** | connect_vm, start_vm, checkpoint_vm, invoke_command, run_script, copy_to_vm, copy_from_vm, manage_service | Requires confirmation |
| **Destructive** | stop_vm, restart_vm, restore_vm, kill_process, cancel_command, free_command_output, save_command_output | Requires confirmation |

### Command Pattern Defaults

Commands executed via `invoke_command` are checked against pattern lists:

- **Blocked** (always denied): `Format-Volume`, `Clear-Disk`, `Remove-VM`, `Stop-Computer`, etc.
- **Warned** (requires confirmation): `Remove-Item`, `Stop-Service`, `Invoke-WebRequest`, `Start-Process`, etc.
- **Allowed** (auto-approved): `Get-*`, `Test-*`, `Select-*`, `hostname`, `ipconfig`, read-only `git` subcommands, etc.
- **Unknown** commands not matching any pattern require confirmation.

### Policy File

The server looks for a policy file in this order:
1. `--policy <path>` CLI argument
2. `HYPERV_MCP_POLICY` environment variable
3. `hyperv-mcp-policy.json` next to the executable

The policy file is auto-created when you select permanent approval or denial options via elicitation.

### Disabling Confirmation

If your MCP client already validates every tool call, or you want full agent autonomy:

```json
{
  "mode": "unrestricted"
}
```

This skips all policy checks — every tool call is auto-approved, including destructive operations and blocked command patterns. No prompts will appear.

For programmatic/CI use where an agent should be able to self-approve, set `approve_flag_mode`:

```json
{
  "mode": "standard",
  "approve_flag_mode": true
}
```

This allows agents to add `"approve": true` to any tool call's arguments to bypass policy checks for that call.

### Policy File Reference

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `mode` | string | `"standard"` | `unrestricted` (no checks), `standard` (default), `restricted` (read-only only), `lockdown` (explicit allows only) |
| `vm_allowlist` | string[] | null | Glob patterns — matching VMs skip confirmation |
| `vm_blocklist` | string[] | null | Glob patterns — matching VMs are always denied |
| `max_bulk_vms` | int | 3 | Maximum VMs in a single bulk operation before requiring confirmation |
| `blocked_command_patterns` | string[] | (built-in) | Regex patterns for commands that are always denied |
| `warn_command_patterns` | string[] | (built-in) | Regex patterns for commands requiring confirmation |
| `allowed_command_patterns` | string[] | (built-in) | Regex patterns for commands auto-approved |
| `host_path_restrictions` | object | null | Glob allow-lists for `copy_to_vm` source, `copy_from_vm` destination, and `save_to` paths |
| `tool_overrides` | object | null | Per-tool decision override: `{"start_vm": "allow", "restore_vm": "deny"}` |
| `approve_flag_mode` | bool | false | Allow agents to self-approve with `"approve": true` in tool arguments |
| `user_rules` | array | [] | Approval rules (auto-populated by elicitation, editable manually) |
| `deny_rules` | array | [] | Denial rules (auto-populated by elicitation, editable manually) |

Example policy for a test automation environment:

```json
{
  "mode": "standard",
  "vm_allowlist": ["test-*", "ci-*"],
  "vm_blocklist": ["prod-*"],
  "max_bulk_vms": 10,
  "host_path_restrictions": {
    "copy_to_vm_source_allow": ["C:\\builds\\**"],
    "copy_from_vm_dest_allow": ["C:\\test-output\\**"]
  }
}
```

### Managing Approval Rules

Permanent approvals and denials are stored in the `user_rules` and `deny_rules` arrays. These are auto-populated when you select permanent options in elicitation prompts:

```json
{
  "user_rules": [
    {
      "added": "2026-03-15T10:30:00Z",
      "reason": "User approved via elicitation: stop_vm",
      "rule": {
        "tools": ["stop_vm"],
        "vm_names": ["test-vm"]
      }
    }
  ]
}
```

Rules support these constraint fields (all optional, combined with AND):
- `tools` — tool names to match, or `["*"]` for all tools
- `vm_names` — exact VM names (OR matching within the list)
- `vm_pattern` — glob pattern for VM names (e.g., `"test-*"`)
- `command_prefixes` — approved command prefixes (e.g., `["Get-Service", "git"]`)
- `host_paths` — glob patterns for allowed host paths
- `max_bulk_vms` — override the bulk VM limit for this rule

**Evaluation order**: deny rules → session denials → user rules → session approvals → tool overrides → VM filters → command patterns → default decision.

**To reset all approvals**: delete the policy file (or clear the `user_rules` and `deny_rules` arrays).

**To remove a specific approval**: edit the policy file and remove the entry from the `user_rules` array.

### Timeout Capping

All wait-style timeout parameters are capped at **45 seconds** to prevent MCP transport timeouts. The `hard_timeout` parameter (backend kill timer) is not capped. If a requested timeout exceeds 45 seconds, the response includes a `timeout_clamped` warning.

## Architecture

```
src/HyperVMcp/
├── Program.cs                   # Entry point — elevated backend, policy wiring, stdio transport
├── Engine/
│   ├── Types.cs                 # ManagedSession, CommandJob, VmInfo, OutputSlice, etc.
│   ├── VmManager.cs             # VM lifecycle operations via elevated backend
│   ├── SessionManager.cs        # PowerShell session management (PSDirect + WinRM)
│   ├── CommandRunner.cs         # Long-running command execution with async polling
│   ├── OutputBuffer.cs          # Output streaming, retention (full/tail), search, and export
│   ├── FileTransferManager.cs   # File upload/download with auto-compression
│   ├── ElevatedBackend.cs       # Elevated subprocess management via sudo
│   ├── CredentialStore.cs       # Credential resolution from Windows Credential Manager
│   ├── PsUtils.cs              # PowerShell escaping and name validation
│   ├── PolicyTypes.cs          # Policy enums, rules, configuration types
│   ├── ToolClassifier.cs       # Tool risk tier classification
│   ├── HyperVToolClassifier.cs     # Policy evaluation (IToolClassifier)
│   ├── HyperVRuleMatcher.cs        # Rule constraint matching (IRuleMatcher)
│   ├── HyperVOptionGenerator.cs    # Approval option generation (IOptionGenerator)
│   ├── HyperVPolicyConfig.cs       # Policy configuration extending shared PolicyConfig
│   ├── PolicyEvaluationExtensions.cs # Type-safe metadata access for evaluations
│   ├── CommandAnalyzer.cs      # PowerShell AST-based command analysis
│   └── TimeoutHelper.cs        # Timeout capping (max 45 seconds)
└── Tools/
    ├── ToolRegistration.cs      # Central registration for all tool groups
    ├── VmTools.cs               # VM lifecycle tools
    ├── SessionTools.cs          # Session management tools
    ├── CommandTools.cs          # Command execution tools
    ├── FileTools.cs             # File transfer tools
    ├── VmInfoTools.cs           # System information tools
    ├── ServiceTools.cs          # Service management tools
    ├── ProcessTools.cs          # Process management tools
    └── EnvTools.cs              # Environment variable tools
```

Tests are located at `tests/HyperVMcp.Tests/`.

### Key Design Decisions

**Elevated backend pattern**: The MCP server runs as a non-elevated process. Hyper-V operations requiring administrator privileges are delegated to a separate elevated subprocess spawned lazily via `sudo`. This avoids running the entire MCP server elevated and defers the UAC prompt until the first admin-requiring call.

**Shared runspace**: The elevated backend uses a single persistent PowerShell runspace. PSSessions created during sync operations (e.g., `connect_vm`) remain accessible from async commands via `Get-PSSession -InstanceId`, enabling session reuse across the sync/async boundary.

**Dual output modes**: Commands support `full` mode (retains up to 50K lines, searchable, supports range queries) or `tail` mode (ring buffer of last 1K lines, lightweight). The `save_to` parameter streams all output to disk as lines arrive, independent of retention mode.

**Async command model**: Commands return immediately with a `command_id`. Agents poll via `get_command_status` with optional `since_line` for delta updates. Event-based signaling (`WaitForNews`) detects new output or completion without busy-polling.

**Connection retry logic**: PSDirect connections retry up to 10 times (3-second intervals) to handle VM boot windows. WinRM defaults to 2 retries. Credential errors are capped at 3 retries. The `max_retries` parameter on `connect_vm` overrides the defaults.

**Elicitation-only approval**: User approval uses MCP elicitation — the server prompts the user directly through the client UI. The agent never sees approval options, preventing autonomous bypass. Session and permanent approval rules eliminate repeat prompts.

## Platform Support

**Windows only** — requires Hyper-V and PowerShell 7.x (via Microsoft.PowerShell.SDK). The elevated backend uses Windows `sudo` for privilege escalation.

## Security

This server manages Hyper-V VMs with the current user's permissions (elevated via `sudo` on demand). It can:
- Start, stop, snapshot, and restore any Hyper-V VM on the host
- Execute arbitrary PowerShell commands on connected VMs
- Transfer files between host and VMs
- Read credentials from Windows Credential Manager

All state-modifying operations require explicit user approval through MCP elicitation in Standard mode. A policy file controls which operations are allowed, warned, or blocked.

It does **not**:
- Make network requests (all VM communication is local via PSDirect or WinRM)
- Send telemetry
- Store credentials (reads from Credential Manager, caches in-memory only)
- Allow agents to bypass approval (elicitation goes directly to the user)

## License

[MIT](LICENSE)
