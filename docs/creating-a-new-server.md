# Creating a New MCP Server

Guide for adding a new MCP server to this project using the McpSharp library.

## Step 1: Create the Project

```bash
dotnet new console -o src/MyMcp
dotnet sln mcp-tools.sln add src/MyMcp
dotnet add src/MyMcp reference src/McpSharp
```

## Step 2: Minimal Server

```csharp
using McpSharp;
using System.Text.Json.Nodes;

var server = new McpServer("my-mcp");

server.RegisterTool(new ToolInfo
{
    Name = "hello",
    Description = "Say hello",
    InputSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Name to greet" },
        },
    },
    Handler = args =>
    {
        var name = args["name"]?.GetValue<string>() ?? "world";
        return new JsonObject { ["greeting"] = $"Hello, {name}!" };
    },
});

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var transport = new McpTransport(input, output);
transport.Run((method, parameters) => server.Dispatch(method, parameters));
```

## Step 3: Add Policy Guardrails (Optional)

If your server has state-modifying operations that should require user approval:

### 1. Implement the three interfaces

```csharp
using McpSharp.Policy;

public class MyToolClassifier : IToolClassifier
{
    public PolicyDecision Classify(string toolName, JsonObject args) =>
        toolName switch
        {
            "dangerous_tool" => PolicyDecision.Confirm,
            _ => PolicyDecision.Allow,
        };
}

public class MyRuleMatcher : IRuleMatcher
{
    public bool Matches(ApprovalRule rule, string toolName, JsonObject args)
    {
        // Match server-specific constraints from rule.Constraints dictionary.
        // Return true if all constraints match.
        return rule.Constraints == null || rule.Constraints.Count == 0;
    }
}

public class MyOptionGenerator : IOptionGenerator
{
    public List<ElicitationOption> Generate(
        string toolName, JsonObject args, PolicyEvaluation evaluation)
    {
        return new List<ElicitationOption>
        {
            new() { Label = "Allow once", Persistence = ApprovalPersistence.Once,
                    Polarity = ApprovalPolarity.Allow },
            new() { Label = "Allow all (this session)", Persistence = ApprovalPersistence.Session,
                    Polarity = ApprovalPolarity.Allow,
                    Rule = new ApprovalRule { Tools = [toolName] } },
            new() { Label = "Deny", Persistence = ApprovalPersistence.Once,
                    Polarity = ApprovalPolarity.Deny },
        };
    }
}
```

### 2. Wire into transport

```csharp
var classifier = new MyToolClassifier();
var matcher = new MyRuleMatcher();
var optionGenerator = new MyOptionGenerator();
var policy = PolicyEngine.Load<PolicyConfig>(
    classifier, matcher,
    defaultFileName: "my-mcp-policy.json");

server.Transport = transport;
transport.Run((method, parameters) =>
    PolicyDispatch.Dispatch(method, parameters, server, policy, optionGenerator));
```

### 3. Extended policy config (optional)

For server-specific policy settings, extend `PolicyConfig`:

```csharp
public class MyPolicyConfig : PolicyConfig
{
    [JsonPropertyName("my_setting")]
    public string? MySetting { get; set; }
}
```

Then load with `PolicyEngine.Load<MyPolicyConfig>(...)`.

## Step 4: Add Tests

Create a test project:

```bash
dotnet new xunit -o tests/MyMcp.Tests
dotnet sln mcp-tools.sln add tests/MyMcp.Tests
dotnet add tests/MyMcp.Tests reference src/MyMcp
```

Test tool registration and dispatch:

```csharp
[Fact]
public void ToolRegistration_Works()
{
    var server = new McpServer("test");
    server.RegisterTool(new ToolInfo { ... });
    var result = server.Dispatch("tools/list", null);
    Assert.NotNull(result);
}
```

## Step 5: Publish

```bash
dotnet publish src/MyMcp -c Release -o publish/my-mcp
```

Configure in MCP client:

```json
{
  "mcpServers": {
    "my-mcp": {
      "command": "path/to/publish/my-mcp/my-mcp.exe"
    }
  }
}
```

## Reference Implementations

- **Simple**: CiDebugMcp — 6 tools, no policy, straightforward tool handlers
- **With policy**: MsBuildMcp — 13 tools, build confirmation with target/config constraints
- **Full guardrails**: HyperVMcp — 26 tools, VM-scoped policy, command analysis, risk tiers
