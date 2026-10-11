---
name: puppet-core
description: >
  Use the Puppet.Core .NET agent-control framework: discover, observe, and drive a running
  .NET host application (WinForms/WPF/Console/ASP.NET Core) through its /agent/* HTTP API
  or the /mcp MCP endpoint. Use when asked to debug, test, or automate a .NET app that
  references Puppet.Core, or when the user mentions Puppet, /agent/invoke, /appagent, or MCP
  tools for a local .NET host.
---

# Puppet.Core Skill

Puppet.Core lets you (an AI agent) control a running .NET host app: read state, set
properties (fires real UI events), invoke methods, capture screenshots — without UI
Automation. Full guide: read `GET /agent/docs` (Bearer global key) or the repo's
`DEVELOPMENT.md`.

## 1. Find the host

1. Ask the user for the endpoint (default `127.0.0.1:9090`) and how to obtain the key
   (runtime env `PUPPET_TESTGROUND_KEY`, key vault, or user-provided). Never log the key.
2. `GET /agent/registry` (Bearer) → list instances. `GET /agent/capabilities` → version,
   source-vs-NuGet, feature flags (`SupportsMcp`, `SupportsAppAgent`, …).

## 2. Core workflow

1. `GET /agent/describe?name=X&format=md` → capability schema; plan the operation sequence.
2. Read state: `GET /agent/get?name=X&path=a.b.c` (single property; never dump whole objects).
3. Act: `POST /agent/set` (body `{path,value}`, fires UI events) or
   `POST /agent/invoke?name=X&method=Y` (URL-encoded name; body = JSON args array; blocks on
   async). Modal dialogs auto-answer with safe defaults on agent paths — never blocks.
4. Logs/usage: `GET /agent/logs?name=…`, `GET /agent/usage`.
5. Multi-agent: `/agent/book` heartbeats (auto), `/agent/lock/*` advisory site locks.

## 3. MCP (zero-learning-cost alternative)

`POST /mcp` (Bearer **global** key) speaks MCP Streamable HTTP, stateless JSON-RPC:
`initialize`, `tools/list` (all instances × methods, generated from describe),
`tools/call` (forwards to invoke). Fixed tools: `puppet_registry`, `puppet_describe`,
`puppet_get`, `puppet_usage`, `puppet_logs`.

Install into the user's client (agent writes config; user restarts client once):

```jsonc
// opencode: opencode.json  |  Cursor/Claude Desktop: mcpServers
"mcp": { "puppet": { "type": "remote", "url": "http://127.0.0.1:9090/mcp",
  "headers": { "Authorization": "Bearer <global key>" } } }
```

Stdio-only clients need their own HTTP bridge (not bundled).

## 4. Facet B (user proxy, `/appagent/*`)

End-user agents drive shipped apps via semantic actions: `GET /appagent/probe` (no auth) →
`manifest` → `POST /appagent/actions/{name}`. Discovery: enumerate
`%LOCALAPPDATA%\Puppet.AppAgents\*.json`. B-key only; never mix with A-channel keys.

## 5. Hard rules

- Never write keys to logs/files/URLs. Prefer `/agent/get` over whole-object `/agent/state`.
- Expose new host members only if host designed behavior is unchanged; `[PuppetIgnore]` wins.
- Test hooks must be `[PuppetTest]`-marked and cleaned up after the task.
- Framework iteration (source mode): use `logs/Puppet_*.log` usage reports + self-test
  three-section summary to decide capability keep/fix/remove — see DEVELOPMENT.md
  「自进化流程」. Conservative framework core is never pruned.
