# Changelog — McpSharp

All notable changes to McpSharp, the shared MCP protocol library, are documented in this file. McpSharp is compiled into each MCP server and is not released separately; changes that affect a server's behavior are also recorded in that server's changelog.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- JSON-RPC 2.0 dispatch with Content-Length and NDJSON framing auto-detection
- Tool, resource, and prompt registry with handler pattern
- MCP elicitation support (server-initiated prompts to the client)
- Elicitation capability parsing (`ElicitationCapabilities`/`ElicitationMode`): mode-aware gating so the server never sends an elicitation mode the client did not advertise
- Concurrent dispatch mode for blocking tools
- Policy framework (`McpSharp.Policy`): `PolicyEngine`, `PolicyDispatch`, `IToolClassifier`, `IRuleMatcher`, `IOptionGenerator`
- Early response buffering for sequential elicitation calls
- Full form-mode elicitation engine (`McpSharp.Elicitation`, MCP `2025-11-25`): `FormSchemaBuilder` (typed primitive/enum/array construct emitters, flat-object guard, sensitive-field guard), pure single-shot `ElicitationPlanner` (F1/F4/F5/F6 rewrites), `ElicitationDriver` (F2/F3/F8 orchestration with decision-field deny-safety), `FormValidator` (server-side validation), and typed `ElicitationResult` accessors
- `ElicitationCapabilities` parsed at `initialize` (form/url modes; empty object = form-only) with structural mode-guarding so the server never sends a mode the client did not advertise
- `McpServer.VersionOf(assembly)` returns an assembly's informational version without build metadata, so servers can report their own version in `initialize` (`new McpServer(name, McpServer.VersionOf(typeof(Program).Assembly))`)

### Changed

- Targets .NET 10 (`net10.0`) instead of .NET 9.

### Fixed

- The two-prompt persistence follow-up (`PolicyDispatch`) honors the configured elicitation timeout rather than blocking indefinitely.
- The stdio transport decodes NDJSON input as UTF-8. Previously, non-ASCII text in requests (paths, elicitation answers, emoji) was corrupted.
- Malformed JSON-RPC messages no longer stop the server. Unparseable JSON is answered with a `-32700` parse error, and invalid message shapes with a `-32600` invalid-request error. The transport then keeps serving. A leading UTF-8 BOM is ignored. A request whose `params` is not an object gets `-32602`, and a message with duplicate or undecodable property names, or an undecodable `method` or `id`, gets `-32600`; none of these stops the server in serial or concurrent dispatch. A malformed `_meta` is ignored rather than failing the request.
- The stdio transport reads input in buffered chunks instead of one byte at a time. Reading a 1 MB request over a pipe took ~1.4 s and now takes a few milliseconds.
