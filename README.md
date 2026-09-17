# Puppet.Core

[中文](#中文) · [English](#english)

---

## 中文

### 简介

**Puppet.Core** 是一个由 AI Agent 自己开发出来的且为 AI Agent 使用而设计的 .NET 操控框架：为任意 .NET 类型（含 WinForms 窗体与控件）自动暴露可操控的 WebApi 接口，通过反射生成能力描述，支持运行时状态查询、属性读写与方法调用。

它不是给开发者用的库，而是 Agent 的工具——Agent 借助它模拟真实用户操作、观测运行时状态，对宿主程序实施自动化调试与测试。

### 两种集成方式

| 方式 | 做法 | 适合场景 |
|------|------|----------|
| **源码导入** | 将 Puppet.Core 源码放入宿主工程 | Agent 可自由改进框架本身，具备自我演化能力 |
| **NuGet 包** | 安装 NuGet 包，Agent 阅读本项目的 `DEVELOPMENT.md` | 使用发布版本积累的静态架构经验，开箱即用 |

### 设计理念

Puppet.Core 让 Agent **自动调试应用程序**——面向需要编译后进行调试的应用程序类型。它不限于 C#，移植到其他语言同样适用。最关键的核心在于 **DEVELOPMENT.md**：这个文件相当于一份 Skill 说明，让 Agent 在整个应用开发周期内都具备自动调试能力，并且具备自我改进/演化的能力（尤其是源码导入模式下）。

### 子项目

| 项目 | 说明 |
| ---- | ---- |
| Puppet.Core | 通用核心：接口、序列化、能力描述、注册表、Web 端点、内建 Web 服务器、使用日志 |
| Puppet.Core.WinForms | WinForms 专用：控件树遍历、Click / Text / Select 控件操作 |
| Puppet.Core.Console | Console 专用：进程状态、stdin/stdout 交互 |

### 快速开始

`Puppet.Core` 包可从 **NuGet.org** 获取：

```bash
dotnet add package Puppet.Core --source https://api.nuget.org/v3/index.json
```

安装后请先阅读下方“文档”中的指南，并将读取入口写入宿主工程的 `AGENTS.md`。

目标框架：net8.0 / net9.0 / net10.0。宿主程序实现 `IPuppet`（3 个属性），即可被 Agent 发现与操控。

**内建服务器**（宿主无 Web 服务器）：

```csharp
PuppetKeyVault.SetKey("your-secret");
new PuppetWebServer().Start("0.0.0.0:9090");
```

**WinForms 宿主**（启用控件端点）：

```csharp
new PuppetWebServer().UseFormControls().Start("0.0.0.0:9090");
```

**注入已有服务器**（宿主已有 `WebServerBase` 子类）：实现 `IPuppet`，构造时 `this.Register()`，并在 `ProcessWebRequest` 中调用 `PuppetWebHandler.TryHandle(ref req)`。

### 端点一览

所有请求携带 `Authorization: Bearer <key>`：

| 端点 | 方法 | 说明 |
| ---- | ---- | ---- |
| `/agent/registry` | GET | 条件查询已注册实例 |
| `/agent/state` | GET | JSONPath 查询运行时状态 |
| `/agent/get` | GET | 按点分路径读取单个属性 |
| `/agent/set` | POST | 写属性，触发对应 UI 事件 |
| `/agent/describe` | GET | 实例能力描述（Json / Markdown） |
| `/agent/invoke` | POST | 调用实例方法 |
| `/agent/logs` | GET | 读取日志缓冲 |
| `/agent/docs` | GET | 读取内嵌 DEVELOPMENT.md（全局密钥） |
| `/agent/usage` | GET | 使用率统计（供能力清理） |
| `/agent/key/refresh` | POST | 刷新全局密钥 |

### 文档

* [DEVELOPMENT.md](DEVELOPMENT.md) —— 面向 AI Agent 的开发与使用指南：集成模式、调用约定、安全约束、实战踩坑记录。

**Agent 读取入口（二选一）**：

1. **`/agent/docs` 端点**：宿主运行后，Agent 使用 `Authorization: Bearer <全局密钥>` 请求 `GET /agent/docs`，即可取回与运行中程序集版本一致的 DEVELOPMENT.md（`text/markdown; charset=utf-8`），无需实例名。**建议 Agent 在安装/集成时将以下指南写入宿主工程的 `AGENTS.md`**，供后续会话发现入口；NuGet 不会自动修改该文件：

   > 使用 PuppetCore 调试宿主前，先读取宿主 Puppet 服务的 `GET /agent/docs`，通过 Authorization Bearer 头传递运行时全局密钥；宿主未启动或所用旧版本不支持此端点时，读取已还原 NuGet 包中的 DEVELOPMENT.md。记录服务地址及密钥获取方式，不要将实际密钥写入文件或日志。

2. **NuGet 包内文档**：还原依赖后，可读取 `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`。Windows 默认路径为 `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md`，Linux/macOS 为 `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md`。在宿主目录运行 `dotnet nuget locals global-packages --list` 查询缓存位置；如工程自定义了还原目录，以 `obj/project.assets.json` 中的 `packageFolders` 为准，`<version>` 使用该文件中实际解析的 Puppet.Core 版本，而非任取缓存中的最新版本。

### 许可证

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax

---

## English

### Overview

**Puppet.Core** is a .NET framework built for AI agents: it automatically exposes controllable WebApi endpoints for any .NET type (including WinForms windows and controls), generates capability descriptions via reflection, and supports runtime state queries, property read/write, and method invocation.

It is not a library for human developers — it is a tool for agents. Agents use it to simulate real user interactions and observe runtime state, driving automated debugging and testing of host applications.

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
| Puppet.Core | Generic core: interfaces, serialization, capability description, registry, web endpoints, built-in web server, usage logging |
| Puppet.Core.WinForms | WinForms support: control-tree traversal, Click / Text / Select operations |
| Puppet.Core.Console | Console support: process state, stdin/stdout |

### Quick Start

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

### Documentation

* [DEVELOPMENT.md](DEVELOPMENT.md) — the AI-agent-oriented development and usage guide: integration modes, calling conventions, security rules, and pitfalls from practice.

**Agent read entries (choose one):**

1. **`/agent/docs` endpoint**: while the host is running, request `GET /agent/docs` with `Authorization: Bearer <global key>` to retrieve DEVELOPMENT.md matching the running assembly (`text/markdown; charset=utf-8`). No instance name is required. **Agents should add the following guidance to the host project's `AGENTS.md` during installation/integration** so future sessions can discover it; NuGet does not modify that file automatically:

   > Before debugging the host with PuppetCore, read `GET /agent/docs` from the host's Puppet service, passing the runtime global key in the Authorization Bearer header. If the host is not running or an older version lacks this endpoint, read DEVELOPMENT.md from the restored NuGet package. Record the service address and how to obtain the key, never the actual key in files or logs.

2. **Documentation inside the NuGet package**: after restore, read `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`. Defaults are `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md` on Windows and `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md` on Linux/macOS. Run `dotnet nuget locals global-packages --list` from the host directory to locate the cache. For project-specific restore directories, use `packageFolders` in `obj/project.assets.json`; choose the Puppet.Core version actually resolved in that file, not simply the newest cached version.

### License

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax
