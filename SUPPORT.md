# Support

## How to Get Help

This project uses [GitHub Issues](https://github.com/microsoft/mcp-tools/issues) for bug reports and feature requests.

### Before Filing an Issue

1. Check existing [open issues](https://github.com/microsoft/mcp-tools/issues) to avoid duplicates
2. Include the information requested in the issue template
3. Provide minimal reproduction steps if reporting a bug

### Diagnostics

The MCP server writes diagnostic messages to stderr. When reporting issues, include the stderr output which shows:
- MSBuild registration details
- VCTargetsPath discovery
- Transport framing mode
- Any evaluation errors

To capture stderr when running via an MCP client, check your client's log files or run the server directly:

```bash
dotnet run --project src/MsBuildMcp 2>msbuild-mcp.log
```

## Microsoft Support Policy

Support for this project is limited to the resources listed above.
