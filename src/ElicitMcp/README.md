# ElicitMcp

A dual-purpose MCP server for [MCP elicitation](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation) (form mode):

1. **Production purpose** — agent-facing tools that let an agent ask the user for a
   decision, structured input, or free-text feedback, and use the result.
2. **Validation purpose** — a per-construct **conformance / showcase harness** that
   exercises every form-mode construct and the deterministic fallback ladder, and
   reports what the client advertised vs. how it actually behaved.

It is built on the dependency-free `McpSharp.Elicitation` engine
(`FormSchemaBuilder`, `ElicitationPlanner`, `ElicitationDriver`, `FormValidator`).
URL-mode elicitation is **not** implemented.

## Tool surface

The server keeps a deliberately small surface. In **production** it registers only
three tools; the conformance harness is added only when demo mode is enabled, so an
agent connecting the server for real use is not flooded with demo tools.

| Mode | Tools |
|------|-------|
| Production (always) | `request_decision`, `request_input`, `report_capabilities` |
| Demo (`ELICIT_MCP_DEMO_MODE=1`, adds) | `elicit_demo`, `run_conformance` |

## Building & Testing

```bash
dotnet build src/ElicitMcp
dotnet test  tests/ElicitMcp.Tests
```

## Publishing

```bash
dotnet publish src/ElicitMcp -c Release -o publish/elicit-mcp
```

Client configuration:

```json
{
  "mcpServers": {
    "elicit": {
      "command": "C:\\path\\to\\publish\\elicit-mcp\\elicit-mcp.exe"
    }
  }
}
```

To enable the conformance runner (`run_conformance`), set `ELICIT_MCP_DEMO_MODE=1`
in the server's environment. It is gated off by default to avoid accidental prompt
storms:

```json
{
  "mcpServers": {
    "elicit": {
      "command": "C:\\path\\to\\publish\\elicit-mcp\\elicit-mcp.exe",
      "env": { "ELICIT_MCP_DEMO_MODE": "1" }
    }
  }
}
```

## Agent-facing tools (production)

| Tool | Purpose | Returns |
|------|---------|---------|
| `request_decision` | Ask the user to choose among options (single or multi-select, with/without titles) | `{ status, decision_made, decision\|selections, fallback_used }` |
| `request_input` | Collect typed structured input (string/number/integer/boolean/enum, constraints, formats, defaults). For free-text feedback, pass a single optional string field | `{ status, values, fallback_used }` |
| `report_capabilities` | Report the client's advertised form/url support and whether demo mode is active. Sends no prompt | `{ advertised: { supported, form, url }, empty_object_is_form_only, demo_mode_active }` |

They are **safe**: sensitive field names (`password`/`token`/`api_key`/…) are refused
in form mode, and an omitted/declined/cancelled decision is **never** recorded
(`decision_made = false`). `request_decision` is kept separate from `request_input`
because it is the only path that marks a field as the decision field — guaranteeing the
deny-safe semantics rather than relying on an agent-supplied flag.

> **`decision_made` only means a choice was made — not _which_ choice.** A negative
> option (e.g. "Deny") still returns `decision_made = true` with `decision = "Deny"`.
> Callers MUST read the returned `decision`/`selections` value to act on the choice;
> the deny-safety guarantee is that an *omitted or defaulted* decision is never recorded.

`request_decision`/`request_input` use the client's **real** advertised capabilities;
capability simulation (`simulate_caps`) is a conformance-only concern (see below).

## Conformance / demo tools (demo mode only)

Enabled with `ELICIT_MCP_DEMO_MODE=1`.

### `elicit_demo(case, simulate_caps?, message?, note?)`

A single parameterized tool that triggers one named construct/variant. `case` is a
closed enum covering every primitive, format, enum, multi-select, default, response
action, fallback rung, and safety check:

| Group | Cases |
|-------|-------|
| Primitives | `string`, `string_constrained`, `integer`, `number`, `boolean` |
| Formats & defaults | `email`, `uri`, `date`, `datetime`, `string_default` |
| Enums | `enum`, `enum_titled`, `multiselect`, `multiselect_titled`, `enum_default` |
| Response actions | `action_accept`, `action_decline`, `action_cancel`, `action_timeout` |
| Fallbacks | `fallback_titles` (F1), `fallback_multiselect` (F2), `fallback_boolean` (F5), `fallback_number` (F6), `fallback_unsupported` (F8), `fallback_feedback` (F9) |
| Safety | `decision_field_safety`, `sensitive_field_guard` |

Most cases return a machine-checkable conformance record
`{ feature, case, status, fallback_used, decision_made, verdict, values? }`.
The `sensitive_field_guard` case sends **no prompt** and returns
`{ case, guard_fired, verdict }`. The `fallback_*` cases force their own degradation
by default (so any client exercises the rung); pass `simulate_caps`
(`{ form, url, titles, arrays, numbers, booleans }`) to drive a fallback on any case.

### `run_conformance(simulate_caps?)`

Runs a scripted subset of cases and emits a single report
`{ client, results: [...], summary: { pass, fail, needs_human } }`. It is the real
prompt-storm surface, so it is registered only in demo mode.

## Design notes

- **Minimal production surface** — a connected agent sees only the 3 production tools;
  the demo/conformance tools are gated behind `ELICIT_MCP_DEMO_MODE`. `report_capabilities`
  advertises `demo_mode_active` so a conformance client can discover the harness.
- **No policy/guardrail layer** — the server is interactive and has no state-modifying
  operations.
- **Concurrent dispatch** — the entry point runs the transport with `concurrent: true`
  so blocking, user-interactive elicitation tools do not starve the request loop.
- **Decision-field deny-safety** — a fallback or default never resolves a
  decision/approval field to an allow; an omitted decision stays a deny.

See the [McpSharp elicitation API](../McpSharp/README.md#form-mode-elicitation-mcpsharpelicitation)
for the underlying engine.
