---
mode: agent
description: "Build the project — entire solution or specific components. Use when asked to build, compile, rebuild, restore, or clean."
tools: ["msbuild"]
---

## Tools

- `run_in_terminal`: Execute MSBuild commands and capture build output
- `get_errors`: Retrieve compilation errors and warnings from VS Code
- `read_file`: Examine source files and build configuration
- `msbuild` MCP tools: `list_projects`, `build`, `get_build_status`, `cancel_build`, `find_project_for_file`, `get_dependency_graph`

## Instructions

Read the full MSBuild skill reference before proceeding:

```
.github/skills/msbuild/SKILL.md
```

Follow the instructions in that file for solution discovery, targeted builds, error handling, and build analysis. That file is the authoritative reference for all MSBuild build operations.
