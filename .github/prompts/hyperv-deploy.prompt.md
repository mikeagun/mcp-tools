---
mode: agent
description: "Deploy builds to a Hyper-V VM — file transfer, command execution, service management, and recovery."
tools: ["hyperv-mcp"]
---

## Tools

- `hyperv-mcp` tools: `list_vms`, `start_vm`, `stop_vm`, `restart_vm`, `checkpoint_vm`,
  `restore_vm`, `connect_vm`, `disconnect_vm`, `reconnect_vm`, `invoke_command`,
  `get_command_status`, `cancel_command`, `copy_to_vm`, `copy_from_vm`, `list_vm_files`,
  `get_services`, `manage_service`, `kill_process`, `set_env`, `get_vm_info`,
  `search_command_output`, `get_command_output`

## Instructions

Read the full Hyper-V deploy skill reference before proceeding:

```
.github/skills/hyperv-deploy/SKILL.md
```

Follow the instructions in that file for VM setup, file deployment, command execution, service management, hot-replace, and recovery patterns. That file is the authoritative reference for all Hyper-V VM deployment operations.
