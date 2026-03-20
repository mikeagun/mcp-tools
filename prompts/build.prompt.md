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
~/.copilot/skills/msbuild/SKILL.md
```

Follow the workflow in that file:
1. Discover the solution with `list_projects`
2. Determine targets from context (user request, changed files, or full solution)
3. Run a **targeted build** — prefer building specific projects over full solution
4. Handle errors structurally — use the MCP server's parsed error output
5. For build-fix loops, rebuild only affected targets after each fix

**Key principle:** Always use the `msbuild` MCP server tools (`list_projects`, `build`,
`find_project_for_file`, `get_dependency_graph`) instead of constructing MSBuild commands
manually. The MCP server handles VS toolchain discovery, async polling, and error parsing.
