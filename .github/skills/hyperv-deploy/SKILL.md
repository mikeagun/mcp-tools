---
name: hyperv-deploy
description: >
  Deploy builds to a Hyper-V VM and run commands using the hyperv-mcp server.
  Use as a reference for VM setup, file deployment, command execution, service
  management, hot-replace, and recovery patterns. Project-specific skills
  (vm-test-ebpf, vm-test-cnc) build on this foundation.
---

# Hyper-V Deploy Skill

Generic reference for deploying builds to Hyper-V VMs and running remote commands
via the **hyperv-mcp** MCP server. Project-specific skills should reference this
for VM operations guidance instead of repeating it.

## When to Use

- Deploying build output to a test VM
- Running tests or commands on a VM
- Hot-replacing binaries (drivers, DLLs, executables) without VM restore
- Iterative fix-and-test cycles (rebuild → copy → re-run)
- Recovering from stuck services, hung processes, or inconsistent state

## Tools

### VM Lifecycle
`list_vms`, `start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`, `restore_vm`

### Session Management
`connect_vm`, `disconnect_vm`, `reconnect_vm`, `list_sessions`

### Command Execution
`invoke_command`, `get_command_status`, `cancel_command`, `run_script`

### Output Management
`search_command_output`, `get_command_output`, `save_command_output`, `free_command_output`

### File Transfer
`copy_to_vm`, `copy_from_vm`, `list_vm_files`

### Diagnostics
`get_vm_info`, `get_services`, `manage_service`, `kill_process`, `set_env`

---

## MCP Conventions

### Timeouts and Polling

- **`initial_wait`** (≤45s): How long to wait for first output. The command keeps running
  after this — it is NOT a kill timer.
- **`timeout`**: Hard kill timer. Set this for commands that might hang indefinitely.
- **Poll pattern**: Start with `initial_wait=30`, then loop:
  ```
  get_command_status(command_id="<id>", timeout=45)
  ```
  Repeat until `status` is `completed` or `failed`.
- Never exceed 45 seconds for `timeout` on any MCP tool call (the MCP protocol limit).
  For long commands, rely on the poll pattern.

### Output Formats

- Default is **raw streaming** (`output_format="none"`). Do NOT pipe through `Out-String`
  manually — the server handles formatting.
- Use `output_format="text"` only for `Format-Table` / `Format-List` rendering (buffers
  all output until command completes).
- Use `output_format="json"` for structured data (`ConvertTo-Json`).

### Output Retention

- Default is **full** (up to 50K lines, searchable with `search_command_output`).
- Use `retention="tail"` for long-running commands (>2 min) unless you need to search
  the full output later. This keeps only the last 1K lines and saves memory.

### File Transfers

- Always verify source paths exist before calling `copy_to_vm` (`Test-Path` on host).
- Always use `copy_to_vm` / `copy_from_vm` — never `Copy-Item` (it doesn't work over
  PSDirect sessions managed by the MCP server).
- Large transfers (≥10 MB) are auto-compressed via tar.

### Working Directory

- `Set-Location` does NOT persist across `invoke_command` calls. Use the
  `working_directory` parameter instead.

### Environment Variables

- Use `set_env` to set variables that persist for the session. These are injected into
  all subsequent `invoke_command` calls automatically.
- **Never use `set_env` for PATH** — it replaces the entire value, removing System32
  and breaking `netsh`, `sc.exe`, etc. To extend PATH:
  ```
  invoke_command(session_id="<id>", command="$env:PATH += ';C:\\new\\path'")
  ```
- After VM reboot, `set_env` variables are lost — call `set_env` again on the new session.

### Execution Policy

If the VM has a restrictive policy, prefix script commands with:
```
Set-ExecutionPolicy Bypass -Scope Process -Force;
```

---

## VM Setup

### Step 1: Find and Prepare the VM

```
list_vms(name_filter="<pattern>")
```

To start from a clean state, restore a checkpoint:
```
restore_vm(vm_name="<name>", checkpoint_name="<checkpoint>", wait_for_ready=true)
```

If the VM is off:
```
start_vm(vm_name="<name>", wait_for_ready=true)
```

### Step 2: Connect

```
connect_vm(vm_name="<name>", credential_target="TEST_VM")  → session_id
```

Credentials resolve from: explicit params → Credential Manager target → default `TEST_VM`.

If `connect_vm` fails with transient PSDirect errors, it retries automatically (up to
10 attempts, 3s intervals). If it still fails, reboot and retry:
```
restart_vm(vm_name="<name>", wait_for_ready=true)
connect_vm(vm_name="<name>", credential_target="TEST_VM")
```

### Step 3: Set Session Environment (optional)

```
set_env(session_id="<id>", variables={"MY_VAR": "value"})
```

---

## File Deployment Patterns

### Selective Copy (fast — skip build intermediates)

Use globs to copy only what's needed for testing, skipping `.lib`, `.pdb`, `.obj` files:
```
copy_to_vm(session_id="<id>",
    source=["<build>\\*.exe", "<build>\\*.dll", "<build>\\*.sys"],
    destination="C:\\Deploy")
```

### Full Directory Copy (when PDBs are needed for debugging)

```
copy_to_vm(session_id="<id>",
    source="<build_dir>",
    destination="C:\\")
```

This is slower (~1 GB = ~3 min) but includes PDB files for crash analysis.

### Compressed Transfer

Force compression for directories with many small files:
```
copy_to_vm(session_id="<id>",
    source="<dir>",
    destination="C:\\Deploy",
    compress=true)
```

### Create Target Directory First

```
invoke_command(session_id="<id>",
    command="New-Item -ItemType Directory -Path C:\\Deploy -Force | Out-Null")
```

### Poll Large Transfers

If `copy_to_vm` returns `status="running"`:
```
get_command_status(command_id="<id>", timeout=45, include_output=false)
```

---

## Command Execution & Output

### Run a Command

```
invoke_command(session_id="<id>",
    command="<command>",
    working_directory="C:\\Deploy",
    initial_wait=30,
    timeout=300)
```

### Poll Long-Running Commands

```
get_command_status(command_id="<id>", timeout=45)
```

Repeat until `status` is `completed`. Use `since_line` for efficient delta updates:
```
get_command_status(command_id="<id>", timeout=45, since_line=<last_line>)
```

### Search Output for Failures

```
search_command_output(command_id="<id>", pattern="FAILED|Error|failed")
```

### View Context Around a Match

```
get_command_output(command_id="<id>", around_line=<N>, max_lines=30)
```

### Cancel a Stuck Command

```
cancel_command(command_id="<id>")
```

If the process is stuck beyond cancellation:
```
kill_process(session_id="<id>", name="<process_name>")
```

---

## Service Management

### Check Service Status

```
get_services(session_id="<id>", names=["ServiceA", "ServiceB"])
```

Expected: Status=4 means Running.

### Start / Stop / Restart

```
manage_service(session_id="<id>", name="ServiceName", action="start")
manage_service(session_id="<id>", name="ServiceName", action="stop")
manage_service(session_id="<id>", name="ServiceName", action="restart")
```

### Dependency Order

When stopping multiple services, stop in **reverse dependency order** (dependents first,
then the service they depend on). Start in **forward order**.

Example (where ServiceB depends on ServiceA):
```
# Stop (reverse):
manage_service(session_id="<id>", name="ServiceB", action="stop")
manage_service(session_id="<id>", name="ServiceA", action="stop")

# Start (forward):
manage_service(session_id="<id>", name="ServiceA", action="start")
manage_service(session_id="<id>", name="ServiceB", action="start")
```

### STOP_PENDING Recovery

If a service gets stuck in STOP_PENDING, a process is holding handles. Kill the blocking
process to release them:
```
kill_process(session_id="<id>", name="<blocking_process>")
manage_service(session_id="<id>", name="ServiceName", action="stop")
```

### PATH After Service Install

MSI installers often add to Machine PATH, but the current session won't see it until
reboot. Read the current PATH, then extend it:
```
invoke_command(session_id="<id>", command="$env:PATH")
# Use the output to extend:
set_env(session_id="<id>", variables={"PATH": "<current_path>;C:\\new\\install\\path"})
```

**Exception**: After a VM reboot, Machine PATH is correct — no extension needed.

---

## Component Hot-Replace

Always prefer hot-replacing components over VM restore. This avoids the 30-60s restore
cycle and preserves any setup state.

### Unlocked Files (executables, user-mode DLLs)

Just copy — no restart needed. Tests load fresh copies on next run:
```
copy_to_vm(session_id="<id>",
    source="<build>\\my_test.exe",
    destination="C:\\Deploy\\")
```

### Locked Files (kernel drivers, in-use DLLs)

Stop the service holding the file, copy, restart:
```
manage_service(session_id="<id>", name="MyDriver", action="stop")
copy_to_vm(session_id="<id>",
    source="<build>\\MyDriver.sys",
    destination="C:\\install\\drivers\\")
manage_service(session_id="<id>", name="MyDriver", action="start")
```

### Full Stack Replacement

When replacing multiple components, stop all in reverse dependency order, copy everything,
then start in forward order. Errors from stopping already-stopped services are expected —
ignore them.

### Re-Registration After Driver Updates

Some systems require re-registering metadata after driver replacement (e.g., eBPF program
type stores). Project-specific skills document their registration steps.

---

## Iterative Fix-and-Test Cycle

The fastest development loop — rebuild only what changed, copy only the changed binary:

```
# 1. Rebuild just the affected project (~30s incremental):
build(sln_path="...", targets="<project>", configuration="Release")

# 2. Copy only the changed binary (~5-10s):
copy_to_vm(session_id="<id>",
    source="<build>\\changed_binary.exe",
    destination="C:\\Deploy\\")

# 3. Re-run the test:
invoke_command(session_id="<id>",
    command=".\\changed_binary.exe <args>",
    working_directory="C:\\Deploy",
    initial_wait=30, timeout=120)
```

For locked files (drivers, in-use DLLs), add stop/start around the copy step.

---

## Recovery Escalation

When things go wrong, escalate through these tiers:

### Tier 1: Cancel and Kill

```
cancel_command(command_id="<id>")
kill_process(session_id="<id>", name="<process>")
```

### Tier 2: Check and Clean Up Leaked State

Some systems (e.g., eBPF) leave kernel state after crashes. Check for and clean up
leaked resources before running more tests. Project-specific skills document what
to check and how to clean up.

### Tier 3: Reboot

Reboot clears all runtime state — drivers unloaded, services return to boot config.
Use when kernel state is inconsistent, services are stuck, or unexplained failures
occur after hot-replace.

```
restart_vm(vm_name="<name>", session_id="<id>")
```

**After reboot**, the old session is dead. You must reconnect:
```
disconnect_vm(session_id="<id>")
connect_vm(vm_name="<name>", credential_target="TEST_VM")
set_env(session_id="<id>", variables={...})  # re-set any session env vars
```

Do NOT use `reconnect_vm` after a reboot — it only works for broken sessions on a
running VM.

### Tier 4: VM Restore (last resort)

**Ask the user before restoring** — the broken state may indicate a bug worth investigating.

```
restore_vm(vm_name="<name>", checkpoint_name="<checkpoint>", session_id="<id>")
```

---

## Important Notes

- **Elevation**: The hyperv-mcp server defers UAC until the first admin tool is called.
  No upfront elevation needed.
- **Test signing**: VMs running test-signed drivers must have `bcdedit -set TESTSIGNING ON`
  and secure boot disabled.
- **Debug CRT**: Debug builds may need `ucrtbased.dll` and `vcruntime140d.dll` copied to
  the VM's `System32` directory.
- **Parallel VMs**: Multiple `connect_vm` calls create independent sessions. Useful for
  running different test groups on different VMs simultaneously.
- **MSI install verification**: Do not trust MSI exit codes — always verify by checking
  that expected services are running with `get_services`.
- **Collecting crash dumps**: On failure, copy dumps from the VM:
  ```
  copy_from_vm(session_id="<id>",
      source=["C:\\Windows\\Minidump\\*.dmp", "C:\\Dumps\\*.dmp"],
      destination=".\\CrashDumps")
  ```
