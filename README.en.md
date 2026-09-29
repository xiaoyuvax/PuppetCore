# Puppet.Core

[中文](README.md) · [English](README.en.md)

---

### Overview

**Puppet.Core** is a .NET control framework built by AI agents, for AI agents: it automatically exposes controllable WebApi endpoints for any .NET type (including WinForms windows and controls), generates capability descriptions via reflection, and supports runtime state queries, property read/write, and method invocation.

It is not a library for human developers — it is a tool for agents. Its purpose is to support two things **at runtime**:

1. **Agent self-debugging** (facet A) — agents use it to simulate real user interactions and observe runtime state, driving automated debugging and testing of host applications.
2. **Users operating an application through an agent via natural language** (facet B) — turning an app into an "agent-operable" application form: the user gives natural-language instructions to their personal agent, and the agent drives the app through semantic actions to complete the workflow, **with no UI Automation** (no screenshots, no synthesized input, no dependence on focus or control handles).

One kernel, two facades: `/agent/*` (A, self-debugging) and `/appagent/*` (B, user proxy) — see "Two Facets: A Self-Debugging / B User Proxy" below.

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
| TestGround.Winform | WinForms workbench (task board): standing lab for framework self-debugging and evolution |
| TestGround.Console | Console workbench (expense ledger): integration and verification lab for command-line hosts |
| TestGround.Wpf | WPF workbench (reading list): data-binding and Dispatcher scenario lab |
| TestGround.AspNetCore | ASP.NET Core workbench (inventory reservations): web service and concurrency lab |

### TestGround: self-debugging and evolution labs

The four TestGround apps are the framework's training grounds. Each implements a small but genuinely useful application (task board, expense ledger, reading list, inventory reservations) covering four typical host shapes: WinForms, Console, WPF, and ASP.NET Core. By continuously working on them — simulating real user actions through Puppet endpoints, observing state, and surfacing issues — agents progressively cover the design landscape of each app type and distill experience into each project's own `AGENTS.md` / `README.md` / `DEVELOPMENT.md`, which in turn drives framework fixes and evolution. Principles: app code stays elegant, clear, concise, and pleasant for humans; framework changes must be driven by reproducible evidence, never speculative features.

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

### Two Facets: A Self-Debugging / B User Proxy

Puppet.Core is **one kernel with two facades**:

| Facet | Endpoint prefix | Consumer | Scenario |
| ---- | ---- | ---- | ---- |
| **A Self-debugging** | `/agent/*` | Development-time agent (owns the host source) | Simulate user actions, observe state, drive automated debugging |
| **B User proxy** | `/appagent/*` | The end user's personal agent (the app is compiled and shipped; no code changes) | The user issues **natural-language** instructions; the agent performs the workflow on their behalf |

#### Facet B: turning an app into an agent-operable application form

Historically, letting an agent drive a GUI app meant **UI Automation** (screenshots, pixel matching, control trees, synthesized mouse/keyboard). That path is **unreliable, inefficient, and easily disturbed**: it breaks whenever the UI changes, depends on focus and window state, is extremely fragile under multiple monitors or concurrency, and can express "where to click" but never "what to do".

Puppet.Core takes a different route — it promotes **actions to first-class citizens** (the Actionize paradigm), so an agent drives the app through **semantic action names plus structured arguments**, never touching the UI:

* **No UI Automation**: no screenshots, no synthesized input, no dependence on control handles or focus — inherently stable, concurrency-safe, auditable, and regression-friendly (action names are stable, so UI redesigns do not break agent scripts)
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

### Documentation

* [DEVELOPMENT.md](DEVELOPMENT.md) — the AI-agent-oriented development and usage guide: integration modes, calling conventions, security rules, and pitfalls from practice.

**Agent read entries (choose one):**

1. **`/agent/docs` endpoint**: while the host is running, request `GET /agent/docs` with `Authorization: Bearer <global key>` to retrieve DEVELOPMENT.md matching the running assembly (`text/markdown; charset=utf-8`). No instance name is required. **Agents should add the following guidance to the host project's `AGENTS.md` during installation/integration** so future sessions can discover it; NuGet does not modify that file automatically:

   > Before debugging the host with PuppetCore, read `GET /agent/docs` from the host's Puppet service, passing the runtime global key in the Authorization Bearer header. If the host is not running or an older version lacks this endpoint, read DEVELOPMENT.md from the restored NuGet package. Record the service address and how to obtain the key, never the actual key in files or logs.

2. **Documentation inside the NuGet package**: after restore, read `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`. Defaults are `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md` on Windows and `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md` on Linux/macOS. Run `dotnet nuget locals global-packages --list` from the host directory to locate the cache. For project-specific restore directories, use `packageFolders` in `obj/project.assets.json`; choose the Puppet.Core version actually resolved in that file, not simply the newest cached version.

### License

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax
