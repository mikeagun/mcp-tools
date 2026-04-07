# McpSharp

Lightweight MCP (Model Context Protocol) server library for .NET.

Provides a JSON-RPC 2.0 stdio transport with auto-detection of Content-Length and NDJSON framing, plus a registry for tools, resources, and prompts.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test
```

## Usage

```csharp
using McpSharp;

var server = new McpServer("my-server");

server.RegisterTool(new ToolInfo
{
    Name = "hello",
    Description = "Say hello",
    InputSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
        },
    },
    Handler = args =>
    {
        var name = args["name"]?.GetValue<string>() ?? "world";
        return new JsonObject { ["message"] = $"Hello, {name}!" };
    },
});

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var transport = new McpTransport(input, output, logPrefix: "my-server");

transport.Run((method, parameters) => server.Dispatch(method, parameters));
```

## API

### McpServer

- `McpServer(string name, string? version = null)` — Create a server with the given name
- `RegisterTool(ToolInfo tool)` — Register a tool
- `RegisterResource(ResourceInfo resource)` — Register a resource
- `RegisterPrompt(PromptInfo prompt)` — Register a prompt
- `Dispatch(string method, JsonNode? parameters)` — Handle a JSON-RPC method call
- `Elicit(string message, JsonObject requestedSchema, int timeoutSeconds = 0)` — Send elicitation request to client. Returns `ElicitationResult?` (null if transport not set or client doesn't support elicitation). Timeout of 0 blocks indefinitely; on timeout, sends `notifications/cancelled` to the client.
- `ClientSupportsElicitation` — Check if client declared elicitation capability
- `Transport` — Set for bidirectional communication (required for elicitation)

### McpTransport

- `McpTransport(Stream input, Stream output, string? logPrefix = null)` — Create a stdio transport
- `Run(Func<string, JsonNode?, JsonNode?> handler, bool concurrent = false)` — Enter the event loop. When `concurrent` is true, handlers run on ThreadPool threads, allowing blocking tools (like elicitation) without starving other requests.
- `StartReader()` — Start the background reader thread explicitly (called automatically by `Run`)
- `TakeMessage()` / `TakeMessage(int timeoutMs, CancellationToken ct)` — Take next request from the queue (for custom dispatch loops)
- `ReadMessage()` / `WriteMessage(JsonNode msg)` — Low-level message I/O

### Types

- `ToolInfo` — Name, Description, InputSchema, Handler
- `ResourceInfo` — Uri, Name, Description, MimeType, Reader
- `PromptInfo` — Name, Description, Arguments, Handler
- `PromptArgument` — Name, Description, Required
- `ElicitationResult` — Action (ElicitationAction), Content (JsonObject?)
- `ElicitationAction` — enum: Accept, Decline, Cancel, Timeout

## Policy Framework

McpSharp includes a shared policy framework (`McpSharp.Policy` namespace) for guardrail enforcement across MCP servers. It provides:

### Core Components

- **`PolicyEngine`** — Evaluates tool calls against deny rules, user rules, and session approvals. Loads/saves policy files with atomic writes.
  - `Load<TConfig>(classifier, matcher, ...)` — Load policy from file (explicit path, env var, or default filename)
  - `Evaluate(toolName, args)` → `PolicyEvaluation` — Core evaluation method returning decision + reason + metadata
  - `RegisterSessionApproval(rule)` / `RegisterSessionDenial(rule)` — Add session-scoped rules
  - `SaveRuleToPolicy(rule, reason)` / `SaveDenyRuleToPolicy(rule, reason)` — Persist rules to policy file
- **`PolicyDispatch`** — Dispatch-level interception that wraps `tools/call` with policy checks and MCP elicitation for user confirmation.
  - `Dispatch(method, params, server, policy, optionGenerator, preValidator?, argsEnricher?, argsSummaryBuilder?, elicitationTimeoutSeconds?)` — Main entry point
  - `BuildArgsSummary(args, interestingParams?)` — Format tool args for display in elicitation prompts
  - `BuildElicitationSchema(options)` — Build JSON schema for elicitation from option list
- **`ElicitationOption`** — Represents a user-facing approval choice with scope, persistence, and polarity.
- **`ApprovalRule`** — Describes what to auto-approve/deny. Server-specific constraints stored via `[JsonExtensionData]`.

### Types

- **`PolicyEvaluation`** — Decision (PolicyDecision), Reason (string), Metadata (Dictionary for server-specific data)
- **`PolicyDecision`** — enum: Allow, Deny, Confirm
- **`ApprovalPolarity`** — enum: Allow, Deny
- **`ApprovalPersistence`** — enum: Session, Permanent
- **`PolicyConfig`** — Base config with Mode, Rules, DenyRules, UserRules

### Interfaces (servers implement)

- **`IToolClassifier`** — Classify tool calls (Allow / Deny / Confirm) and build `PolicyEvaluation` with metadata.
- **`IRuleMatcher`** — Match approval rule constraints against tool call arguments.
- **`IOptionGenerator`** — Generate elicitation options for tools requiring confirmation.

### Usage

```csharp
// Server implements the three interfaces
var classifier = new MyToolClassifier();
var matcher = new MyRuleMatcher();
var optionGenerator = new MyOptionGenerator();

// Load policy from file
var policy = PolicyEngine.Load<MyPolicyConfig>(
    classifier, matcher,
    explicitPath: policyPath,
    envVarName: "MY_MCP_POLICY",
    defaultFileName: "my-mcp-policy.json");

// Wire into transport with optional callbacks
transport.Run((method, parameters) =>
    PolicyDispatch.Dispatch(method, parameters, server, policy, optionGenerator,
        preValidator: MyPreValidator,
        argsEnricher: MyArgsEnricher,
        argsSummaryBuilder: MyArgsSummaryBuilder,
        elicitationTimeoutSeconds: 120));
```

### MCP Elicitation

The framework uses [MCP elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) to prompt users directly through the MCP client UI. The agent never sees approval options — tool calls either succeed or fail.

Supports single-prompt flow (persistence encoded in labels) and two-prompt flow (scope selection then persistence selection via nullable `ElicitationOption.Persistence`).
