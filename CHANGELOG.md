# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-03-03

### Added

- **13 MCP tools** for MSBuild solution/project analysis and build execution:
  - `list_projects` — List projects with solution folders, configs, and `/t:` target syntax
  - `get_project_details` — Evaluate project properties, items, references, packages
  - `get_project_items` — Get source files and compiler/linker settings (ItemDefinitionGroup)
  - `get_project_imports` — Show `.props`/`.targets` import chain with conditions
  - `find_project_for_file` — Find which project(s) compile a source file
  - `get_dependency_graph` — Project reference DAG with topological sort (JSON/Mermaid)
  - `find_property` — Search properties across projects (exact or regex)
  - `find_packages` — NuGet package inventory with version conflict detection
  - `diff_configurations` — Compare Debug vs Release configurations
  - `build` — Async MSBuild execution with timeout-based polling
  - `get_build_status` — Poll running builds, block until completion or errors
  - `cancel_build` — Cancel builds with partial result collection
  - `parse_build_output` — Parse raw MSBuild text into structured diagnostics
- **5 MCP prompts** for guided agent workflows:
  - `diagnose-build-failure`, `what-to-build`, `impact-analysis`, `resolve-nuget-issue`, `explain-build-config`
- MCP protocol implementation (JSON-RPC 2.0, Content-Length + NDJSON framing)
- Microsoft.Build evaluation engine with mtime-based caching
- Visual Studio toolchain discovery via `vswhere` + `vcvarsall`
- Central Package Management (CPM) version resolution
- Solution-level `ProjectDependencies` parsing
- Support for C++ (`.vcxproj`), C# (`.csproj`), and WiX (`.wixproj`) projects
- 48 unit and integration tests
- GitHub Actions CI/CD pipeline
