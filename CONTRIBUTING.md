# Contributing to MCP Tools

Thank you for your interest in contributing! This document provides guidelines for contributing to MCP Tools.

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with C++ desktop development workload (for MsBuildMcp)
- Hyper-V enabled on Windows (for HyperVMcp)
- Git

### Development Setup

```bash
git clone https://github.com/microsoft/mcp-tools.git
cd mcp-tools
dotnet build
dotnet test
```

### Running a Server Locally

```bash
dotnet run --project src/MsBuildMcp
dotnet run --project src/CiDebugMcp
dotnet run --project src/HyperVMcp
```

Each server communicates via JSON-RPC 2.0 over stdio. Send an `initialize` request to start:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
```

## Code Structure

- `src/McpSharp/` — Shared MCP protocol library (JSON-RPC 2.0, tool/resource/prompt registry)
- `src/HyperVMcp/` — Hyper-V VM management MCP server
- `src/CiDebugMcp/` — CI/CD failure investigation MCP server
- `src/MsBuildMcp/` — MSBuild project exploration and build MCP server
- `tests/` — Test projects (one per src project)

## Making Changes

### Adding a New Tool to an Existing Server

1. Create or extend a tool class in the appropriate `src/*/Tools/` directory
2. Register it in `ToolRegistration.RegisterAll()`
3. Add tests — both unit tests for the underlying logic and integration tests for MCP dispatch
4. Update tool count assertions in test files (if applicable)
5. Update the project's `README.md` (in `src/*/README.md`)

### Modifying McpSharp (Shared Library)

Changes to `src/McpSharp/` affect all three servers. Ensure all test suites pass:

```bash
dotnet test
```

### Code Style

- Follow existing patterns in the codebase
- Use `System.Text.Json` (not Newtonsoft.Json) for all JSON handling
- Use C# file-scoped namespaces
- Prefer `required` properties with `init` setters for data classes
- Add XML doc comments to public APIs

### Testing

```bash
# Run all tests
dotnet test

# Run tests for a specific project
dotnet test tests/MsBuildMcp.Tests
dotnet test tests/CiDebugMcp.Tests
dotnet test tests/HyperVMcp.Tests
dotnet test tests/McpSharp.Tests
```

All tests must pass before submitting a PR. Tests should be self-contained — they create temporary solutions/projects in `%TEMP%` and clean up after themselves.

## Pull Requests

1. Fork the repository and create a feature branch
2. Make your changes with tests
3. Ensure `dotnet build` produces zero warnings and `dotnet test` passes all tests
4. Submit a pull request with a clear description of the change

### PR Checklist

- [ ] All tests pass (`dotnet test`)
- [ ] No new warnings (`dotnet build`)
- [ ] New tools/features have tests
- [ ] README updated if adding tools or changing behavior
- [ ] Commit messages are clear and descriptive

## Reporting Issues

Use [GitHub Issues](https://github.com/microsoft/mcp-tools/issues) to report bugs or request features. Include:

- MSBuild MCP Server version
- .NET SDK version (`dotnet --version`)
- Visual Studio version
- Steps to reproduce
- Expected vs actual behavior
- Relevant error output (from stderr)

## Code of Conduct

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
