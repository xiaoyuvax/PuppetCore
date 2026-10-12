# Puppet.Core

[中文](README.md) · [English](README.en.md)

---

### Overview

**Puppet.Core** is a .NET control framework built by AI agents, for AI agents: it automatically exposes controllable WebAPI endpoints for any .NET type (WinForms, WPF, ASP.NET Core, Console, etc.), generates capability descriptions via reflection, and supports runtime state queries, property read/write, and method invocation.

**Core positioning: a tool for agents, not a library for human developers.** At runtime it serves two core purposes:

| Core Capability | Facet | Endpoints | Description |
|----------------|-------|-----------|-------------|
| **① Agent Auto-debugging** | **A** | `/agent/*` | **Auto = Self + Automatic**: dev-time agent owns source & can recompile; simulates real user actions, observes runtime state, **automates** the debugging & testing loop. |
| **② Agent-ready** (make apps agent-operable) | **B** | `/appagent/*` + `/mcp` | Turn **compiled/shipped apps** into Agent-ready services: users give natural-language instructions, agents drive apps via **semantic actions** (Actionize), **zero UI Automation** (no screenshots/synthesized input, no focus/handle dependence). |

One kernel, two facades: `/agent/*` (A, auto-debugging) and `/appagent/*`, `/mcp` (B, user proxy) — see "Two Facets: A Auto-Debugging / B User Proxy" below.

### Integration Modes

| Mode | How | Best for |
|------|-----|----------|
| **Source import** | Add Puppet.Core source into the host project | Agent can freely improve the framework itself — self-evolving capability |
| **NuGet package** | Install the NuGet package; Agent reads `DEVELOPMENT.md` | Static architectural experience from released versions, works out of the box |

### Philosophy

Puppet.Core enables agents to **automatically debug applications** — specifically applications that require compilation before debugging. It is not limited to C#; porting to other languages works the same way. The most critical piece is **DEVELOPMENT.md**: this file serves as a Skill specification, giving agents automatic debugging capability throughout the entire application development lifecycle, with the ability to self-improve and evolve (especially in source-import mode).

### Sub-projects

| Project | Description |
| ------- | ----------- |
| Puppet.Core | Generic core: interfaces, serialization, capability description, registry, web endpoints, built-in web server, usage logging, console process diagnostics |
| Puppet.Core.WinForms | WinForms support: control-tree traversal, Click / Text / Select operations |
| Puppet.Core.Wpf | WPF support: internal screen capture adapter (`RenderTargetBitmap`), wired up by the host via `UseWpfCapture()` |
| TestGround.Winform | WinForms demo app (task board) |
| TestGround.Console | Console demo app (expense ledger) |
| TestGround.Wpf | WPF demo app (reading list) |
| TestGround.AspNetCore | ASP.NET Core demo app (inventory reservations) |

### TestGround: demos for four host types

TestGround is only **demonstration projects applying Puppet.Core to four .NET application types** (WinForms, Console, WPF, ASP.NET Core):
- Provide **developers and agents** with runnable, readable reference implementations to quickly understand how to integrate and use Puppet.Core in real hosts
- **Not the primary driver for framework improvements** — they are simple apps without complex/deep logic architectures or rich business content
- Framework evolution happens in **users' real projects**; TestGround serves only as a verification platform (see [DEVELOPMENT.md#两种部署模式的闭环](DEVELOPMENT.md#两种部署模式的闭环))

### Deployment scenarios (per DEVELOPMENT.md)

How Puppet.Core is deployed determines how agents acquire framework knowledge and capabilities:

| Scenario | Scope | How the agent gets capabilities | Typical use case |
|----------|-------|--------------------------------|------------------|
| **Single-project integration** | One host project | Feed the Puppet.Core GitHub URL or `DEVELOPMENT.md` directly to the agent | Rapid prototyping, personal projects, one-off debugging tasks |
| **Multi-project standardization** | Team/org with multiple projects | Install `SKILL.md` (repo root); agent auto-loads the skill | Team standardization, multi-repo onboarding, CI/CD integration |
| **Host as a service (MCP endpoint)** | Expose the host app as a service | Enable the `/mcp` endpoint (JSON-RPC 2.0); any MCP client connects with zero learning curve | **B-facing app control** — turn a compiled/shipped app into an Agent-ready service; **not suited for A (agent self-debugging)** since host source is unavailable and runtime is read-only |

> **Key distinction**: A-facet (`/agent/*`) targets **development-time agents** (have source, can recompile) for self-debugging; B-facet (`/appagent/*` and `/mcp`) targets **end-users' personal agents** (app is shipped, no code changes) for natural-language proxy operations. **A core purpose of Puppet is to make host apps Agent-ready** — by exposing semantic capabilities via `/mcp`, not UI Automation.

### Quick Start (for Agent)

> **Installing Puppet.Core only requires the Agent to read this README.**  
> **The Agent should ask the user which deployment mode to use during installation:**
> - **Source reference** — add Puppet.Core source into the host project (this repo's `Puppet.Core/` directory); Agent can modify framework source and recompile
> - **NuGet package** — install `Puppet.Core` from NuGet.org; use the released version's static architectural experience

The `Puppet.Core` package is available on **NuGet.org**:

```bash
dotnet add package Puppet.Core --source https://api.nuget.org/v3/index.json
```

After installation, read the guide under "Documentation" below and add the read entry to the host project's `AGENTS.md`.

Target framework: net8.0 / net9.0 / net10.0. A host implements `IPuppet` (3 properties) to become discoverable and controllable by agents.

**Built-in server** (host has no web server):

```csharp
PuppetKeyVault.SetKey("your-secret");
new PuppetWebServer().Start("0.0.0.0:9090");
```

**WinForms host** (enables the control endpoint):

```csharp
new PuppetWebServer().UseFormControls().Start("0.0.0.0:9090");
```

**Inject into an existing server** (host already subclasses `WebServerBase`): implement `IPuppet`, call `this.Register()` in the constructor, and invoke `PuppetWebHandler.TryHandle(ref req)` inside `ProcessWebRequest`.

### Endpoints

All requests require the `Authorization: Bearer <key>` header:

| Endpoint | Method | Description |
| -------- | ------ | ----------- |
| `/agent/registry` | GET | Query registered instances |
| `/agent/state` | GET | Query runtime state via JSONPath |
| `/agent/get` | GET | Read a single property by dotted path |
| `/agent/set` | POST | Write a property (fires the corresponding UI event) |
| `/agent/describe` | GET | Capability schema (Json / Markdown) |
| `/agent/invoke` | POST | Invoke an instance method |
| `/agent/logs` | GET | Read log buffers |
| `/agent/docs` | GET | Fetch the embedded DEVELOPMENT.md (global key) |
| `/agent/usage` | GET | Usage statistics (for capability pruning) |
| `/agent/key/refresh` | POST | Rotate the global key |
| `/mcp` | POST | MCP Streamable HTTP endpoint (global key) — zero-learning-cost integration for any MCP client, see below |

### MCP Integration (mainstream agent spec compatibility)

`/mcp` is a standard MCP (Model Context Protocol) Streamable HTTP stateless endpoint: `tools/list` is generated dynamically from describe (every registered instance × invocable method, including synthetic actions), and `tools/call` forwards to the existing invoke path (auth / usage tracking / safe modal defaults all inherited); five fixed tools are also built in: `puppet_registry` / `puppet_describe` / `puppet_get` / `puppet_usage` / `puppet_logs`. Same port, same key — MCP is available as soon as the host starts, no extra process.

**Install (the agent can do this autonomously; the user restarts their client once)**: with the host running and `/mcp` reachable, copy the snippet for your client into its config file (never commit the key; fill at runtime or read from an environment variable):

```jsonc
// opencode: opencode.json
"mcp": { "puppet": { "type": "remote", "url": "http://127.0.0.1:9090/mcp",
  "headers": { "Authorization": "Bearer <global key>" } } }
```

```jsonc
// Cursor (.cursor/mcp.json) / Claude Desktop (mcpServers), etc.
"mcpServers": { "puppet": { "url": "http://127.0.0.1:9090/mcp",
  "headers": { "Authorization": "Bearer <global key>" } } }
```

Restart the client; Puppet tools appear in the next session. `GET /agent/capabilities` returning `SupportsMcp: true` means the host supports it. Stdio-only clients need their own HTTP bridge (not bundled).

### Two Facets: A Self-Debugging / B User Proxy

Puppet.Core is **one kernel with two facades**:

| Facet | Endpoint prefix | Consumer | Scenario |
| ---- | ---- | ---- | ---- |
| **A Self-debugging** | `/agent/*` | Development-time agent (owns the host source) | Simulate user actions, observe state, drive automated debugging |
| **B User proxy** | `/appagent/*` | The end user's personal agent (the app is compiled and shipped; no code changes) | The user issues **natural-language** instructions; the agent performs the workflow on their behalf |

#### Facet B: turning an app into an agent-operable application form

Historically, letting an agent drive a GUI app meant **UI Automation** (screenshots, pixel matching, control trees, synthesized mouse/keyboard). That path is **unreliable, inefficient, and easily disturbed**: it breaks whenever the UI changes, depends on focus and window state, is extremely fragile under multiple monitors or concurrency, and can express "where to click" but never "what to do".

Puppet.Core takes a different route — it promotes **actions to first-class citizens** (the Actionize paradigm), so an agent drives the app through **semantic action names plus structured arguments**, never going through the UI side:

* **It never goes through the UI side (the root reason)**: not driven by screenshots or synthesized input, no dependence on control handles or focus. The reason is that Puppet does not share an operating path with the user — the agent's commands never enter the UI pipeline, so it neither competes for focus nor gets disturbed by what the user does. The result is inherent stability, concurrency safety, auditability, and regressibility (action names are stable, so UI redesigns do not break agent instructions)
* **Screen images come from inside the application**: when it needs to "take a look", it uses facet A via `GET /agent/describe` + `POST /agent/invoke?method=CaptureAction` (WinForms and WPF supported), reading pixels the application rendered itself — an image of the UI held on the Puppet side. It intercepts nothing, covers nothing, and never produces a full-screen-sized image on multi-monitor setups, in keeping with the principle of not sharing a path with the user and not interfering
* **Actions are the interface**: business logic inside event handlers, once lifted into `public` methods returning a success/failure type (`bool` / `OperationResult` / `Task<…>`), is **automatically** published in the capability catalog `GET /appagent/manifest`; agents invoke it via `POST /appagent/actions/{name}`
* **Modal dialogs are proxyable**: entry points use `PuppetDialog.Ask` instead of `MessageBox.Show`, so an agent answers through the `dialogs` preset table and **never blocks**
* **Zero-configuration discovery**: enumerate `%LOCALAPPDATA%\Puppet.AppAgents\*.json` to get `{app, endpoint, key}` (isolated by user-profile ACLs); the user just says "look at local port 9090"
* **Built-in window actions**: a GUI host (WinForms via `UseFormControls()`) gets framework-synthesized generic actions for free — move / resize / bounds / show-hide / enable / focus / window state / activate / top-most / title / opacity / close (destructive ones take `confirm`), plus the multi-monitor states `ScreenCount` / `Screens`; non-window types are unaffected

| Endpoint | Credential | Description |
| ---- | ---- | ---- |
| `GET /appagent/probe` | none | Probe (minimal disclosure: puppet / protocol / auth / help) |
| `GET /appagent/help` | none | Self-describing manual (the CLI `--help` equivalent) |
| `GET /appagent/manifest` | Bearer | Capability catalog: actions / state / uiTexts / concurrency |
| `POST /appagent/actions/{name}` | Bearer | Execute an action; body `{"args":{…},"callId":"…","dialogs":[…]}` |
| `GET /appagent/state/{key}` | Bearer | Direct read of simple-typed state (text-chat view) |
| `GET /appagent/assets/{id}` | Bearer | Artifact download (in-memory stand-in for disk files) |

> With facet B disabled there is zero code path and zero behavioral difference; a shipped app can set `BlockAgentEndpoints=true` to answer 404 for all `/agent/*`.

#### Enabling facet B: instruct the agent to actionize the project

How capable facet B is depends on how many "actions" have been actionized in the project, and it is not sufficient by default. To get the most out of it, the user must **explicitly instruct the agent to actionize the project** — for example, "fully actionize, covering every operation detail". Results vary with model capability: stronger agents cover more. Even then, actions may still be missing (buried in private event handlers, or internal capabilities not exposed in the UI); the user can ask the agent to supplement as best it can.

#### Status and observations

* **Facet A (self-debugging loop) is mature**: state is readable down to a single property (`/agent/get?path=a.b.c`), `invoke` blocks until the `Task` completes, cross-thread calls are marshalled back to the UI thread, and complete business flows can be driven reliably in practice.
* **Facet B (proxying user operations) is still exploratory**: the main bottleneck is **action coverage** — the Actionize paradigm only collects methods already written as `public` with a success/failure return, while many user operations still live inside private event handlers, and there is currently **no completeness gate** binding "UI control ↔ action". For a systematic way to find and close those gaps, see the "Actionize 覆盖度扫描" section in [DEVELOPMENT.md](DEVELOPMENT.md).

### In-App Screenshot

Puppet.Core provides **in-app screenshot** capability — **no OS/UI Automation screen capture**. Instead, the application itself renders the pixels and returns a PNG. The process never intercepts input, never covers the UI, and on multi-monitor setups never produces a full-screen image, adhering to the "no shared operation path" principle.

| Host Type | Implementation | Integration | Key Points |
|-----------|----------------|-------------|------------|
| **WinForms** | `Control.DrawToBitmap` reads the control's own rendered pixels | `new PuppetWebServer().UseFormControls().Start(...)` auto-injects | Any control/form region; mixed-DPI multi-monitor offset fixed |
| **WPF** | `RenderTargetBitmap` renders the visual tree | Host calls `UseWpfCapture()` (requires `Puppet.Core.Wpf` reference) | Any `Visual` element; element-level or window-level |
| **ASP.NET Core** | Page side: DOM clone → inline computed styles per element → SVG foreignObject → `<img>` data:URL decode → canvas → PNG | Code ships with page, **off by default**; user checks "Allow Puppet to capture this page image" → page polls `GET /api/screenshot/pending` every second, posts back base64 PNG | **Opt-in, not standing capability**; user can uncheck/navigate away to stop; Puppet cannot self-enable; PNG magic bytes validated, 8B–1.5MB, id must match; 5s timeout |

**Unified invocation protocol (A-facet synthetic method)**:
```http
POST /agent/invoke?name=<instance>&method=CaptureAction
Content-Type: application/json
Authorization: Bearer <key>

[10, 20, 300, 200]  // x, y, width, height (all optional; omit/≤0 = whole element)
```
Returns: `PuppetArtifact` (base64-inlined PNG bytes). Same `describe`/`invoke` contract across all three host types.

> **Key difference**: traditional UI Automation relies on OS full-screen capture / synthesized input — fragile to focus, occlusion, multi-monitor. Puppet's in-app screenshot is **the pixels the app itself painted**; the agent merely "takes a look" — no focus contention, no user interference, concurrent-safe, auditable.

### Documentation

* [DEVELOPMENT.md](DEVELOPMENT.md) — the AI-agent-oriented development and usage guide: integration modes, calling conventions, security rules, and pitfalls from practice.

**Agent read entries (choose one):**

1. **`/agent/docs` endpoint**: while the host is running, request `GET /agent/docs` with `Authorization: Bearer <global key>` to retrieve DEVELOPMENT.md matching the running assembly (`text/markdown; charset=utf-8`). No instance name is required. **Agents should add the following guidance to the host project's `AGENTS.md` during installation/integration** so future sessions can discover it; NuGet does not modify that file automatically:

   > Before debugging the host with PuppetCore, read `GET /agent/docs` from the host's Puppet service, passing the runtime global key in the Authorization Bearer header. If the host is not running or an older version lacks this endpoint, read DEVELOPMENT.md from the restored NuGet package. Record the service address and how to obtain the key, never the actual key in files or logs.

2. **Documentation inside the NuGet package**: after restore, read `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`. Defaults are `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md` on Windows and `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md` on Linux/macOS. Run `dotnet nuget locals global-packages --list` from the host directory to locate the cache. For project-specific restore directories, use `packageFolders` in `obj/project.assets.json`; choose the Puppet.Core version actually resolved in that file, not simply the newest cached version.

### License

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax
