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
- `Elicit(string message, JsonObject schema)` — Send elicitation request to client
- `ClientSupportsElicitation` — Check if client declared elicitation capability
- `Transport` — Set for bidirectional communication (required for elicitation)

### McpTransport

- `McpTransport(Stream input, Stream output, string? logPrefix = null)` — Create a stdio transport
- `Run(Func<string, JsonNode?, JsonNode?> handler)` — Enter the event loop
- `ReadMessage()` / `WriteMessage(JsonNode msg)` — Low-level message I/O

### Types

- `ToolInfo` — Name, Description, InputSchema, Handler
- `ResourceInfo` — Uri, Name, Description, MimeType, Reader
- `PromptInfo` — Name, Description, Arguments, Handler
- `PromptArgument` — Name, Description, Required

## Policy Framework

McpSharp includes a shared policy framework (`McpSharp.Policy` namespace) for guardrail enforcement across MCP servers. It provides:

### Core Components

- **`PolicyEngine`** — Evaluates tool calls against deny rules, user rules, and session approvals. Loads/saves policy files with atomic writes.
- **`PolicyDispatch`** — Dispatch-level interception that wraps `tools/call` with policy checks and MCP elicitation for user confirmation.
- **`ElicitationOption`** — Represents a user-facing approval choice with scope, persistence, and polarity.
- **`ApprovalRule`** — Describes what to auto-approve/deny. Server-specific constraints stored via `[JsonExtensionData]`.

### Interfaces (servers implement)

- **`IToolClassifier`** — Classify tool calls (Allow / Deny / Confirm) and build evaluations with metadata.
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

// Wire into transport with optional pre-validation and args enrichment
transport.Run((method, parameters) =>
    PolicyDispatch.Dispatch(method, parameters, server, policy, optionGenerator,
        preValidator: MyPreValidator,
        argsEnricher: MyArgsEnricher));
```

### MCP Elicitation

The framework uses [MCP elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) (protocol 2025-06-18) to prompt users directly through the MCP client UI. The agent never sees approval options — tool calls either succeed or fail.

Supports single-prompt flow (persistence encoded in labels) and two-prompt flow (scope selection then persistence selection via nullable `ElicitationOption.Persistence`).
