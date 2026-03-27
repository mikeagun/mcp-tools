---
name: msbuild
description: >
  Build MSBuild solutions and projects using the msbuild MCP server.
  Use when asked to build, compile, rebuild, restore, clean, or analyze build
  configurations for any C++ or .NET solution. Provides targeted builds, dependency
  analysis, and structured error handling via MCP tools.
---

# MSBuild Build Skill

Build MSBuild solutions using the `msbuild` MCP server for structured project discovery,
targeted builds, dependency analysis, and error parsing.

## When to Use

- User asks to build, compile, rebuild, or clean a project or solution
- User asks to build a specific component after making changes
- User asks what needs rebuilding after editing files
- After making code changes that need to be compiled
- Diagnosing build failures or configuration issues

## Tools

- `msbuild` MCP tools: `list_projects`, `build`, `get_build_status`, `cancel_build`,
  `find_project_for_file`, `get_dependency_graph`, `get_project_details`,
  `get_project_items`, `get_project_imports`, `find_property`, `find_packages`,
  `diff_configurations`, `parse_build_output`
- `run_in_terminal`: Execute MSBuild commands and capture build output
- `get_errors`: Retrieve compilation errors and warnings from VS Code
- `read_file`: Examine source files and build configuration

**Key principle:** Always use the `msbuild` MCP server tools instead of constructing
MSBuild commands manually. The MCP server handles VS toolchain discovery, async polling,
and structured error parsing.

## Prerequisites

The `msbuild` MCP server must be configured. Run `list_projects` first to verify
connectivity and discover the solution structure.

## Workflow

### Step 1: Discover the Solution

Find the `.sln` file in the current working directory or its ancestors. Then discover
the project structure:

```
Call: list_projects(sln_path="<path to .sln>")
```

This returns all projects with their MSBuild `/t:` target syntax, solution folders,
and configurations. **Save the solution path — you'll need it for every subsequent call.**

### Step 2: Determine What to Build

**Option A: User specified a component** — Look up the target name from `list_projects`.
The `target` field gives the exact `/t:` syntax (e.g., `drivers\EbpfCore`).

**Option B: User changed files** — Use `find_project_for_file` to identify affected projects:

```
Call: find_project_for_file(sln_path="...", file_path="<changed file>")
```

Then optionally check what else depends on those projects:

```
Call: get_dependency_graph(sln_path="...", include=["<affected project>"])
```

This returns the affected project plus all its transitive dependencies — the minimal
set that needs rebuilding.

**Option C: Full solution build** — Omit the `targets` parameter in the build call.

### Step 3: Build

```
Call: build(
  sln_path="<path>",
  targets="<target1>,<target2>",   # Omit for full solution
  configuration="Debug",            # Default
  platform="x64",                   # Default
  timeout=30,                        # Wait up to 30s for initial results
  restore=false                      # Set true if NuGet packages may be missing
)
```

**Key behaviors:**
- Returns early if a build **error** is detected (so you can cancel and fix immediately)
- If the build takes longer than `timeout`, returns current progress — poll with `get_build_status`
- If a build is already running, polls it instead of starting a new one
- Only one build runs at a time — collision detection reports conflicts

### Step 4: Poll Long Builds

If `build` returns with `status: "running"`:

```
Call: get_build_status(timeout=30)
```

Repeat until `status` is `succeeded` or `failed`. Each poll waits up to `timeout` seconds
for the build to finish or for new errors to appear.

### Step 5: Handle Errors

If the build fails, errors are structured in the response:

```json
{
  "errors": [
    { "file": "src/foo.cpp", "line": 42, "code": "C2039", "message": "...", "project": "..." }
  ]
}
```

**Error triage strategy:**
1. Look for **root cause errors** — often the first error in the first project that failed
2. Cascade errors (later errors caused by the first) can be ignored initially
3. Fix root cause errors, then rebuild just the affected targets
4. For header errors, dependents may also need rebuilding

If the raw output is needed:

```
Call: get_build_status(include_output=true, output_lines=100)
```

### Step 6: Cancel If Needed

```
Call: cancel_build()
```

Returns partial results with errors collected so far.

## Targeted Build Strategy

**Always prefer targeted builds over full solution builds.** For a 90-project solution,
a full build can take 5+ minutes. Building just the changed project takes seconds.

### Translating User Intent to Targets

| User Says | What to Do |
|-----------|-----------|
| "build it" / "compile" | Build projects affected by recent changes (use `find_project_for_file`) |
| "build the API" / "build EbpfCore" | Look up target from `list_projects`, filter by name |
| "rebuild everything" | Full solution build (no targets) |
| "build and test" | Build the component + its test project |
| "clean build" | Use additional_args="/t:Clean" then build, or rebuild from scratch |

### Finding the Right Target

Use `list_projects` with `filter` to search:

```
Call: list_projects(sln_path="...", filter="bpf2c")
→ Returns: [{ name: "bpf2c", target: "tools\\bpf2c", ... }]
```

The `target` field is the exact string to pass to `build(targets=...)`.

For multiple targets, comma-separate them: `"tools\\bpf2c,tests\\bpf2c_tests"`.

## Configuration Selection

Use `Debug|x64` unless the user specifies otherwise. Run `list_projects` to see the
available configurations for the solution — they vary by project (e.g., some solutions
have `Release`, `NativeOnlyDebug`, or custom configurations).

## Advanced: Build Analysis with MCP Tools

Beyond building, the MCP server provides project intelligence:

- **`get_project_details`** — Examine project properties, compiler settings, references
- **`diff_configurations`** — Compare Debug vs Release settings
- **`find_property`** — Search for a property across all projects (e.g., `SpectreMitigation`)
- **`find_packages`** — Find NuGet packages and version conflicts
- **`get_project_imports`** — Trace property inheritance through the import chain
- **`get_dependency_graph`** — Full project reference DAG with build order

Use these to answer questions like "why does this project use Spectre mitigation?"
or "what depends on EbpfApi?" without manual `.vcxproj` spelunking.

## Common Issues

| Problem | Solution |
|---------|----------|
| "MSB4057: target does not exist" | Wrong target syntax — check `list_projects` for correct path |
| "MSB4019: imported project not found" | Missing NuGet restore — set `restore=true` |
| Build collision (different targets) | Cancel current build first, or wait for it to finish |
| Very slow build | Use targeted builds instead of full solution |
| "LINK : error LNK..." | Linker error — check `get_project_details` for AdditionalDependencies |

## Integration with build-fix Skill

When the build fails, you can:
1. Get structured errors from the build result
2. Fix the root cause error(s) in code
3. Rebuild just the affected target(s) — not the full solution
4. Repeat until clean

This targeted rebuild loop is much faster than full-solution rebuilds.
