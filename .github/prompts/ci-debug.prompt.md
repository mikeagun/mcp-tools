---
mode: agent
description: "Debug CI/CD failures — search logs, find errors, analyze binary dependencies."
tools: ["ci-debug-mcp"]
---

## Tools

- `ci-debug-mcp` tools: `get_ci_failures`, `search_job_logs`, `get_step_logs`,
  `download_artifact`, `analyze_binary_deps`, `update_binary_baselines`
- Supports GitHub Actions and Azure DevOps (Azure Pipelines)

## Instructions

Read the full CI debug skill reference before proceeding:

```
.github/skills/ci-debug/SKILL.md
```

Follow the instructions in that file for failure triage, log search, and binary analysis. That file is the authoritative reference for all CI/CD debugging operations.
