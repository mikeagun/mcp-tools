# Changelog — MsBuildMcp

All notable changes to MsBuildMcp (`msbuild-mcp`) are documented in this file. MsBuildMcp is versioned and released independently; releases are tagged `msbuild-mcp-v<version>`.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- MSBuild project exploration server (18 tools, 5 prompts):
  - Project exploration: `list_projects`, `get_project_details`, `get_project_items`, `get_project_imports`, `find_project_for_file`
  - Dependency analysis: `get_dependency_graph`
  - Build execution: `build`, `publish`, `get_build_status`, `cancel_build`, `parse_build_output`, `search_build_output`, `get_build_output`, `save_build_output`
  - Cross-project queries: `find_property`, `find_packages`, `diff_configurations`
  - Prompts: `diagnose-build-failure`, `what-to-build`, `impact-analysis`, `resolve-nuget-issue`, `explain-build-config`
- Dual MSBuild strategy (dotnet SDK evaluation + VS MSBuild builds)
- Visual Studio version selection for side-by-side installs (e.g. VS 2022 + VS 2026): `--vs <selector>` / `MSBUILD_MCP_VS` accept `latest`, a product year (`2022`), a version prefix (`17.14`), a version range (`[17,18)`), or an installation path; C++ evaluation and builds use the same selected installation, and build status reports it as `visual_studio`
- Per-call Visual Studio selection: optional `vs` argument on `build` and the project-evaluation tools (and the `what-to-build` / `explain-build-config` prompts), plus `list_vs_installations`; a running build with a different installation is reported as a collision
- `publish` uses `dotnet` CLI directly (not VS MSBuild) for .NET SDK projects
- Policy-based guardrails with MCP elicitation
- Packed as a .NET tool NuGet package, `Mikeagun.McpTools.MsBuildMcp`, with framework-dependent `win-x64` and `win-arm64` packages. `initialize` reports the server's own version.

### Changed

- Targets .NET 10 (`net10.0`) instead of .NET 9, and always runs on the newest installed .NET runtime (`LatestMajor`) so it can evaluate projects with a newer .NET SDK.
- `System.Configuration.ConfigurationManager` is updated to 10.0.15.

### Fixed

- C++ project evaluation no longer fails when Visual Studio 2026 is installed: `VCTargetsPath` is now the highest complete `MSBuild\Microsoft\VC\vNNN` directory (VS 2026 uses `v180`; its `v170` folder is an incomplete stub) instead of a hard-coded `v170`.
- Visual Studio Build Tools and prerelease installations are now discovered (stable installations are still preferred), and a failing `vcvarsall.bat` is reported instead of silently starting a build without the VC environment.
- A completed build stays queryable by `build_id` (`get_build_status`) after a newer build or publish replaces it, even if it finished without being polled. Repeatedly polling a completed build no longer fills the recent-builds history with copies of it, evicting older builds.
- `elapsed_ms` of a completed build is now its duration; previously it kept counting from the build's start time until the status was read.
- Builds on ARM64 hosts use the native ARM64 `MSBuild.exe` and `vcvarsall arm64` environment. If the ARM64 C++ build tools aren't installed, builds use the x64 tools. Previously, the x64 tools were always used.
- Policy prompts that offer to save a rule honor the configured elicitation timeout rather than blocking indefinitely.
- Protocol handling (from McpSharp): requests are decoded as UTF-8, malformed JSON-RPC messages are answered with errors instead of stopping the server, and large requests are read much faster. See the [McpSharp changelog](https://github.com/mikeagun/mcp-tools/blob/main/src/McpSharp/CHANGELOG.md).
