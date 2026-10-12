# Puppet.Core

[中文](README.md) · [English](README.en.md)

---

### 简介

**Puppet.Core** 是一个由 AI Agent 自己开发、为 AI Agent 设计的 .NET 操控框架：为任意 .NET 类型（WinForms、WPF、ASP.NET Core、Console 等）自动暴露可操控的 WebAPI，通过反射生成能力描述，支持运行时状态查询、属性读写与方法调用。

**核心定位：Agent 的工具，非开发者库**。运行时服务于两大核心目的：

| 核心能力 | 面向 | 端点 | 说明 |
|----------|------|------|------|
| **① Agent Auto-debugging**（Agent 自动调试） | **A 面** | `/agent/*` | **Auto = Self + Automatic**：开发期 Agent 拥有源码、可重编译；模拟真实用户操作、观测运行时状态，**自动化**实施调试与测试闭环。典型提示词：「请使用 Puppet 自己调试直至所有问题解决」 |
| **② Agent-ready**（应用即 Agent 就绪） | **B 面** | `/appagent/*` + `/mcp` | 让**已编译发布的应用**变成 Agent 可代理操控的服务：用户用自然语言下指令，Agent 以**语义化动作**（Actionize）驱动应用，**零 UI Automation**（无截屏/合成输入、无焦点/句柄依赖）。典型提示词：「全面 Actionize 所有窗体界面操作，每个菜单、按钮及可操作控件」 |

一套内核、两个门面：`/agent/*`（A 自动调试）与 `/appagent/*`、`/mcp`（B 用户代理），详见下文《两种面向：A 自动调试 / B 用户代理》。

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
| Puppet.Core.Wpf | WPF 专用：内部截屏适配（`RenderTargetBitmap`），宿主 `UseWpfCapture()` 接入 |
| TestGround.Winform | WinForms 演示应用（任务看板） |
| TestGround.Console | 控制台演示应用（支出账本） |
| TestGround.Wpf | WPF 演示应用（阅读清单） |
| TestGround.AspNetCore | ASP.NET Core 演示应用（库存预留） |

### TestGround：Puppet.Core 的四类宿主演示

TestGround 仅是 **Puppet.Core 应用于四种 .NET 应用类型的演示项目**（WinForms、Console、WPF、ASP.NET Core）：
- 为**开发者与 Agent** 提供可运行、可阅读的参考实现，便于快速理解如何在真实宿主中集成与使用 Puppet.Core
- **并非驱动框架改进与演进的主要场所**——它们都是简单应用，不具备复杂/高深的逻辑架构与丰富业务内容
- 框架演进的主战场是**用户的真实项目**；TestGround 仅作为验证平台（见 [DEVELOPMENT.md#两种部署模式的闭环](DEVELOPMENT.md#两种部署模式的闭环)）

### 部署场景（对应 DEVELOPMENT.md 的三种模式）

Puppet.Core 的部署方式决定了 Agent 如何获取框架知识与能力：

| 场景 | 适用范围 | Agent 如何获得能力 | 典型用例 |
|------|----------|-------------------|----------|
| **单项目集成** | 单个宿主项目 | 直接把 Puppet.Core GitHub 地址或 `DEVELOPMENT.md` 投喂给 Agent | 快速原型、个人项目、一次性调试任务 |
| **多项目统一** | 团队/组织多个项目 | 安装 `SKILL.md`（见仓库根目录），Agent 自动加载技能 | 团队标准化、多仓库统一接入、CI/CD 集成 |
| **宿主即服务（MCP 端点）** | 将宿主应用作为服务暴露 | 启用 `/mcp` 端点（JSON-RPC 2.0），任意 MCP 客户端零学习成本接入 | **面向 B 的应用操控**——把已编译发布的应用变成 Agent-ready 服务；不适合 A 面（Agent 自调试），因宿主源码不可改、运行时只读 |

> **关键区别**：A 面（`/agent/*`）面向**开发期 Agent**（拥有源码、可重编译），用于自动调试；B 面（`/appagent/*` 与 `/mcp`）面向**终端用户的个人 Agent**（应用已发布、不可改码），用于自然语言代理操作。**Puppet 的一大目的就是让宿主应用变成 Agent-ready**——通过 `/mcp` 暴露语义化能力，而非 UI Automation。

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
| `/mcp` | POST | MCP Streamable HTTP 端点（仅全局密钥）——任何 MCP 客户端零学习成本接入，见下文《MCP 接入》 |

### MCP 接入（兼容主流 Agent 外接规范）

`/mcp` 是标准 MCP（Model Context Protocol）Streamable HTTP 无状态端点：`tools/list` 由 describe 动态生成（所有已注册实例 × 可调用方法，含框架合成方法），`tools/call` 转发既有 invoke 路径（鉴权 / usage 统计 / 模态安全默认全部继承）；另内置 `puppet_registry` / `puppet_describe` / `puppet_get` / `puppet_usage` / `puppet_logs` 五个固定工具。同端口同密钥，宿主启动即 MCP 可用，无需新进程。

**安装（由 Agent 自主完成，需用户重启客户端一次）**：宿主已启动且 `/mcp` 可达后，按你的客户端复制对应片段写入其配置文件（密钥不进仓库，运行时填入或从环境变量读）：

```jsonc
// opencode：opencode.json
"mcp": { "puppet": { "type": "remote", "url": "http://127.0.0.1:9090/mcp",
  "headers": { "Authorization": "Bearer <全局密钥>" } } }
```

```jsonc
// Cursor（.cursor/mcp.json）/ Claude Desktop（mcpServers 段）等
"mcpServers": { "puppet": { "url": "http://127.0.0.1:9090/mcp",
  "headers": { "Authorization": "Bearer <全局密钥>" } } }
```

写入后重启该客户端，下个会话即可直接用 Puppet 工具。`/agent/capabilities` 返回 `SupportsMcp: true` 表示当前宿主支持。仅支持 stdio 的客户端需自备 HTTP 转发壳（框架不内置）。

### 两种面向：A 自调试 / B 用户代理

Puppet.Core 是**一套内核、两个门面**：

| 面向 | 端点前缀 | 使用者 | 场景 |
| ---- | ---- | ---- | ---- |
| **A 自调试** | `/agent/*` | 开发期 Agent（拥有宿主源码） | 模拟用户操作、观测状态、驱动自动化调试 |
| **B 用户代理** | `/appagent/*` | 终端用户的个人 Agent（应用已编译发布，不可改码） | 用户用**自然语言**下达指令，Agent 代理完成操作流程 |

#### B 面向：把应用变成「Agent 可代理操控」的新型应用形态

让 Agent 操控 GUI 应用，传统上只能走 **UI Automation**（截屏/像素匹配/控件树/合成鼠标键盘）。这条路**不可靠、低效率、易受干扰**：界面一改就失效，依赖焦点与窗口状态，多显示器/并发下极其脆弱，且只能表达"点哪里"而不能表达"要做什么"。

Puppet.Core 走的是另一条路——**把"动作"提升为一等公民**（Actionize 范式），Agent 用**语义化动作名 + 结构化参数**驱动应用，全程不走 UI 侧接口：

* **不走 UI 侧接口（根本原因）**：不靠截屏 / 合成输入驱动、不依赖控件句柄与焦点。这样做是因为 Puppet 不与用户共享同一条操作流程——Agent 的指令从不进入 UI 通路，因而既不与用户抢焦点，也不会被用户操作打断；由此带来天然稳定、可并发、可审计、可回归（动作名稳定，UI 改版不影响 Agent 指令）
* **界面截图取自应用内部**：需要"看一眼界面"时走 A 面 `GET /agent/describe` + `POST /agent/invoke?method=CaptureAction`（WinForms / WPF 均支持），读取的是应用自己绘制出来的像素——这是 Puppet 侧对 UI 的一份映像，不拦截输入、不遮挡界面、多屏下也不产生整屏大图，与"互不共享流程、互不干扰"的原则一致
* **动作即接口**：事件处理器里的业务逻辑提炼为 `public` + 成败返回（`bool` / `OperationResult` / `Task<…>`）的方法，即**自动**进入能力目录 `GET /appagent/manifest`；Agent 按动作名与参数调用 `POST /appagent/actions/{name}`
* **模态框可代理**：带对话框的入口以 `PuppetDialog.Ask` 替代 `MessageBox.Show`，Agent 经 `dialogs` 预答表代答，**永不阻塞**
* **零配置发现**：枚举 `%LOCALAPPDATA%\Puppet.AppAgents\*.json` 即得 `{app, endpoint, key}`（用户档案 ACL 隔离）；用户只需说"访问本地 9090 端口了解详情"
* **基础窗体操作内置**：GUI 宿主（WinForms 用 `UseFormControls()` 自动接入）**无需手写**即可获得框架合成的通用动作——移动 / 缩放 / 边界 / 显隐 / 启用 / 焦点 / 窗口状态 / 激活 / 置顶 / 标题 / 不透明度 / 关闭（破坏性带 `confirm`），以及多屏状态 `ScreenCount` / `Screens`；非窗体类型不受影响

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

### 应用内截图

Puppet.Core 提供**应用内截图**能力——**不走 OS/UI Automation 截屏**，而是由应用自身渲染像素并返回 PNG，全程不拦截输入、不遮挡界面、多显示器下也不产生整屏大图，符合"互不共享操作流程"原则。

| 宿主类型 | 实现方式 | 接入方式 | 特点 |
|----------|----------|----------|------|
| **WinForms** | `Control.DrawToBitmap` 读取控件自身已绘制像素 | `new PuppetWebServer().UseFormControls().Start(...)` 自动注入 | 支持任意控件/窗体区域截取；混合 DPI 多显示器已修复裁切偏移 |
| **WPF** | `RenderTargetBitmap` 渲染可视化树 | 宿主调用 `UseWpfCapture()`（需引用 `Puppet.Core.Wpf`） | 支持任意 `Visual` 元素截取；元素级/窗口级均可 |
| **ASP.NET Core** | 页面端：DOM 克隆 → 逐元素内联计算样式 → SVG foreignObject → `<img>` data:URL 解码 → canvas → PNG | 页面随代码下发，**默认关闭**；用户勾选"允许 Puppet 获取本页渲染截图"后才每秒轮询 `GET /api/screenshot/pending`，回传 base64 PNG | **按需授权、非常设能力**；用户可随时取消勾选/离页停止；Puppet 无法自行开启；PNG 魔数校验、8B~1.5MB、id 匹配；5s 超时 |

**统一调用协议（A 面合成方法）**：
```http
POST /agent/invoke?name=<实例名>&method=CaptureAction
Content-Type: application/json
Authorization: Bearer <key>

[10, 20, 300, 200]  // x, y, width, height（全可选，省略/≤0 = 整个元素）
```
返回：`PuppetArtifact`（base64 内联 PNG 字节）。同一 `describe/invoke` 契约覆盖所有三类宿主。

> **关键区别**：传统 UI Automation 靠 OS 截全屏/合成输入，易受焦点/遮挡/多屏干扰；Puppet 的应用内截图是**应用自己画好的像素**，Agent 只是"看一眼"——不抢焦点、不干扰用户、可并发、可审计。

### 文档

* [DEVELOPMENT.md](DEVELOPMENT.md) —— 面向 AI Agent 的开发与使用指南：集成模式、调用约定、安全约束、实战踩坑记录。

**Agent 读取入口（二选一）**：

1. **`/agent/docs` 端点**：宿主运行后，Agent 使用 `Authorization: Bearer <全局密钥>` 请求 `GET /agent/docs`，即可取回与运行中程序集版本一致的 DEVELOPMENT.md（`text/markdown; charset=utf-8`），无需实例名。**建议 Agent 在安装/集成时将以下指南写入宿主工程的 `AGENTS.md`**，供后续会话发现入口；NuGet 不会自动修改该文件：

   > 使用 PuppetCore 调试宿主前，先读取宿主 Puppet 服务的 `GET /agent/docs`，通过 Authorization Bearer 头传递运行时全局密钥；宿主未启动或所用旧版本不支持此端点时，读取已还原 NuGet 包中的 DEVELOPMENT.md。记录服务地址及密钥获取方式，不要将实际密钥写入文件或日志。

2. **NuGet 包内文档**：还原依赖后，可读取 `<global-packages>/puppet.core/<version>/DEVELOPMENT.md`。Windows 默认路径为 `%USERPROFILE%\.nuget\packages\puppet.core\<version>\DEVELOPMENT.md`，Linux/macOS 为 `~/.nuget/packages/puppet.core/<version>/DEVELOPMENT.md`。在宿主目录运行 `dotnet nuget locals global-packages --list` 查询缓存位置；如工程自定义了还原目录，以 `obj/project.assets.json` 中的 `packageFolders` 为准，`<version>` 使用该文件中实际解析的 Puppet.Core 版本，而非任取缓存中的最新版本。

### 配套工具推荐

* [zcurl](https://github.com/xiaoyuvax/zcurl) —— 中文 Windows 环境下，建议 Agent 用它替代 curl 调用 Puppet 端点：内建 Puppet 命令行拼接支持，免去手工拼 URL、Bearer 头与中文编码的麻烦。

### 许可证

[MIT](LICENSE) · Copyright (c) 2026 Xiaoyuvax
