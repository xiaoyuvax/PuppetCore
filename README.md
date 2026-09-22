# Puppet.Core

[中文](README.md) · [English](README.en.md)

---

### 简介

**Puppet.Core** 是一个由 AI Agent 自己开发出来、并为 AI Agent 使用而设计的 .NET 操控框架：为任意 .NET 类型（含 WinForms 窗体与控件）自动暴露可操控的 WebApi 接口，通过反射生成能力描述，支持运行时状态查询、属性读写与方法调用。

它不是给开发者用的库，而是 Agent 的工具。其目的是在**运行时**支持两件事：

1. **Agent 自动调试**（面向 A）——Agent 借助它模拟真实用户操作、观测运行时状态，对宿主程序实施自动化调试与测试。
2. **用户通过自然语言操作 Agent 来操控应用**（面向 B）——把应用变成一种"Agent 可代理操控"的新型应用形态：用户对个人 Agent 下达自然语言指令，Agent 以语义化动作驱动应用完成操作流程，**无需 UI Automation**（不截屏、不合成输入、不依赖焦点与控件句柄）。

一套内核、两个门面：`/agent/*`（A 自调试）与 `/appagent/*`（B 用户代理），详见下文《两种面向：A 自调试 / B 用户代理》。

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
| Puppet.Core | 通用核心：接口、序列化、能力描述、注册表、Web 端点、内建 Web 服务器、使用日志、Console 进程诊断 |
| Puppet.Core.WinForms | WinForms 专用：控件树遍历、Click / Text / Select 控件操作 |
| TestGround.Winform | WinForms 工作台（任务看板）：框架自我调试与演进的常驻试验场 |
| TestGround.Console | 控制台工作台（支出账本）：命令行宿主的集成与验证试验场 |
| TestGround.Wpf | WPF 工作台（阅读清单）：数据绑定与 Dispatcher 场景试验场 |
| TestGround.AspNetCore | ASP.NET Core 工作台（库存预留）：Web 服务与并发场景试验场 |

### TestGround：自我调试与演进试验场

四个 TestGround 应用是框架的"练兵场"：它们各自实现一个有实际意义的小型真实应用（任务看板、支出账本、阅读清单、库存预留），覆盖 WinForms、Console、WPF、ASP.NET Core 四类典型宿主形态。Agent 在其上持续工作——用 Puppet 端点模拟真实用户操作、观测状态、发现问题——即可逐步覆盖对应类型程序设计的方方面面，并把经验沉淀在各项目自己的 `AGENTS.md` / `README.md` / `DEVELOPMENT.md` 中，反过来驱动框架修复与演进。原则：业务代码保持优雅、清晰、简洁、面向人类审美的美观易用；框架改动必须由可复现的问题证据驱动，不投机性加功能。

### 快速开始（供 Agent 阅读）

> **安装 Puppet.Core 只需 Agent 阅读本 README 即可。**  
> **Agent 在安装时应询问用户采用哪种部署模式：**
> - **源码引用** —— 将 Puppet.Core 源码放入宿主工程（本仓库 `Puppet.Core/` 目录），Agent 可修改框架源码并重新编译
> - **NuGet 包** —— 从 NuGet.org 安装 `Puppet.Core`，使用发布版本的静态架构经验

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

### 两种面向：A 自调试 / B 用户代理

Puppet.Core 是**一套内核、两个门面**：

| 面向 | 端点前缀 | 使用者 | 场景 |
| ---- | ---- | ---- | ---- |
| **A 自调试** | `/agent/*` | 开发期 Agent（拥有宿主源码） | 模拟用户操作、观测状态、驱动自动化调试 |
| **B 用户代理** | `/appagent/*` | 终端用户的个人 Agent（应用已编译发布，不可改码） | 用户用**自然语言**下达指令，Agent 代理完成操作流程 |

#### B 面向：把应用变成「Agent 可代理操控」的新型应用形态

让 Agent 操控 GUI 应用，传统上只能走 **UI Automation**（截屏/像素匹配/控件树/合成鼠标键盘）。这条路**不可靠、低效率、易受干扰**：界面一改就失效，依赖焦点与窗口状态，多显示器/并发下极其脆弱，且只能表达"点哪里"而不能表达"要做什么"。

Puppet.Core 走的是另一条路——**把"动作"提升为一等公民**（Actionize 范式），Agent 用**语义化动作名 + 结构化参数**驱动应用，全程不碰 UI：

* **不依赖 UI Automation**：不截屏、不合成输入、不依赖控件句柄与焦点——天然稳定、可并发、可审计、可回归（动作名稳定，UI 改版不影响 Agent 指令）
* **动作即接口**：事件处理器里的业务逻辑提炼为 `public` + 成败返回（`bool` / `OperationResult` / `Task<…>`）的方法，即**自动**进入能力目录 `GET /appagent/manifest`；Agent 按动作名与参数调用 `POST /appagent/actions/{name}`
* **模态框可代理**：带对话框的入口以 `PuppetDialog.Ask` 替代 `MessageBox.Show`，Agent 经 `dialogs` 预答表代答，**永不阻塞**
* **零配置发现**：枚举 `%LOCALAPPDATA%\Puppet.AppAgents\*.json` 即得 `{app, endpoint, key}`（用户档案 ACL 隔离）；用户只需说"访问本地 9090 端口了解详情"

| 端点 | 凭证 | 说明 |
| ---- | ---- | ---- |
| `GET /appagent/probe` | 无 | 探针（最小披露：puppet / protocol / auth / help） |
| `GET /appagent/help` | 无 | 自描述手册（CLI `--help` 等价物） |
| `GET /appagent/manifest` | Bearer | 能力目录：actions / state / uiTexts / concurrency |
| `POST /appagent/actions/{name}` | Bearer | 执行动作，body `{"args":{…},"callId":"…","dialogs":[…]}` |
| `GET /appagent/state/{key}` | Bearer | 简单类型状态直读（文本聊天视图） |
| `GET /appagent/assets/{id}` | Bearer | 产物下载（内存态模拟磁盘文件） |

> 不启用 B 面向时零代码路径、零行为差异；成品应用可设 `BlockAgentEndpoints=true` 让 `/agent/*` 一律 404。

#### 启用 B 面向：指示 Agent 对项目做 Actionize

B 面向的能力强弱取决于项目里"被 actionize 的动作"有多少，默认并不充分。要充分发挥，用户需要**明确指示 Agent 对项目进行 actionize**，例如："全面 actionize，覆盖所有界的操作细节"。效果视模型能力而不同——能力越强的 Agent 覆盖越全。即便如此仍可能有动作缺失（埋藏在私有事件处理器里，或界面未暴露的内部能力），可要求 Agent 尽力增补。

#### 现状与观察

* **A 面向（自调试闭环）已成熟**：状态可读到单属性粒度（`/agent/get?path=a.b.c`），`invoke` 会阻塞等待 `Task`，跨线程自动切回 UI 线程，实测可稳定驱动完整业务流程。
* **B 面向（代理用户操作）仍在探索**：主要瓶颈是**动作覆盖度**——Actionize 范式只收录"已写成 `public` + 成败返回"的方法，而大量用户操作仍埋在私有事件处理器里，且目前**没有完备性门禁**把"UI 控件 ↔ 动作"绑定起来。如何系统地找出并补齐这些缺口，见 [DEVELOPMENT.md](DEVELOPMENT.md) 的《Actionize 覆盖度扫描》。

### 文档

* [DEVELOPMENT.md](DEVELOPMENT.md) —— 面向 AI Agent 的开发与使用指南：集成模式、调用约定、安全约束、实战踩坑记录。

**Agent 读取入口（二选一）**：

1. **`/agent/docs` 端点**：宿主运行后，Agent 使用 `Authorization: Bearer <全局密钥>` 请求 `GET /agent/docs`，即可取回与运行中程序集版本一致的 DEVELOPMENT.md（`text/markdown; charset=utf-8`），无需实例名。**建议 Agent 在安装/集成时将以下指南写入宿主工程的 `AGENTS.md`**，供后续会话发现入口；NuGet 不会自动修改该文件：

   > 使用 PuppetCore 调试宿主前，先读取宿主 Puppet 服务的 `GET /agent/docs`，通过 Authorization Bearer 头传递运行时全局密钥；宿主未启动或所用旧版本不支持此端点时，读取已还原 NuGet 包中的 DEVELOPMENT.md。记录服务地址及密钥获取方式，不要将实际密钥写入文件或日志。

2. **NuGet 包内文档**：还原依赖后，可读取 `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`。Windows 默认路径为 `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md`，Linux/macOS 为 `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md`。在宿主目录运行 `dotnet nuget locals global-packages --list` 查询缓存位置；如工程自定义了还原目录，以 `obj/project.assets.json` 中的 `packageFolders` 为准，`<version>` 使用该文件中实际解析的 Puppet.Core 版本，而非任取缓存中的最新版本。

### 许可证

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax
