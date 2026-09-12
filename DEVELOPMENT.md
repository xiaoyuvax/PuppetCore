# Puppet.Core 开发指南（供 AI Agent 阅读）

> 本指南的阅读对象是 **AI Agent**，不是人类开发者。所有原则、边界、规范都以"Agent 如何高效、安全地使用并迭代此框架来调试宿主工程"为准绳。开始使用前先通读全篇。

## 库概述

* 名称：Puppet.Core

* 目标框架：net10.0

* 用途：为任意 .NET 类型（含 WinForm 窗体）提供 Agent 可操控的 WebApi 接口。通过反射自动生成能力描述，支持运行时状态查询与方法调用。

* 定位：**这是 agent 的工具，不是给开发者用的库**。代码不追求开发者友好性，只追求 agent 友好性。Agent 在使用中根据需要主动维护与迭代本框架。

## 依赖

* Wima.Core（Utils、Reflection、LogExtention）

* Wima.Log（独立库，仅引用；WimaLogger.LogBook、ILog）——本框架的日志体系即引入 WimaLogger

* Wima.Web.Engine（WebServerBase、WebRequest/Response）

* Newtonsoft.Json

## 子项目

* Puppet.Core：通用核心（接口、序列化、描述、注册表、Web 端点、内建 Web 服务器、使用日志）

* Puppet.Core.WinForms：WinForm 专用（控件树遍历、控件操作 Click/Text/Select）

* Puppet.Core.Console：Console 专用（进程状态、stdin/stdout）

## 核心接口

### IPuppet

* `string AgentAccessKey { get; }` — Agent 访问密钥

* `Common.Logging.ILog AgentLog { get; }` — 宿主实例日志（WimaLogger 实现 ILog，可直接返回）

* `string AgentInstanceName { get; }` — 实例唯一名

### 特性

* `[PuppetIgnore]` — 不暴露给 Agent 的成员（密钥/句柄/内部状态等）

* `[PuppetExpose("理由")]` — 显式暴露**非 public** 成员；public 成员默认已暴露，无需此特性

* `[PuppetDescription("描述")]` — 补充/覆盖描述

> 成员可访问规则：`public` 默认可访问；`non-public` 需 `[PuppetExpose]` 才可被调用/读取。`[PuppetIgnore]` 优先级最高，任何情况下都不暴露。

### 枚举

* `DescribeFormat` — Json / Markdown

* `InfoSource` — Reflection / HumanSummary / PuppetDescription（描述信息可靠性）

## 集成模式

### 模式 A：极简注入（宿主已有 WebServerBase 子类）—— 推荐（如 VisualDataHub）

1. 实现 IPuppet（3 属性）
2. 构造时 `this.Register()`，关闭时 `this.Unregister()`
3. 重写 `ProcessWebRequest` 并在其中注入 `PuppetWebHandler.TryHandle(ref req)`

```csharp
public override WebResponse ProcessWebRequest(WebRequest req)
{
    base.ProcessWebRequest(req);
    if (PuppetWebHandler.TryHandle(ref req)) { req.Response.OutputCompressed(); return req.Response; }
    // ... 原有端点处理
    return req.Response;
}
```

### 模式 B：内建服务器（宿主无 Web 服务器）

```csharp
PuppetKeyVault.SetKey("your-secret");
new PuppetWebServer().Start("0.0.0.0:9090");
```

WinForm 宿主需 `/agent/control` 控件端点时，用 WinForms 包扩展挂接：

```csharp
new PuppetWebServer().UseFormControls().Start("0.0.0.0:9090");
```

其他自定义端点用 `UseHandler(req => myHandler.TryHandle(ref req))` 挂接（先于内建 `/agent/*` 执行）。

### 模式 C：双服务器（端口隔离）

```csharp
PuppetKeyVault.SetKey("your-secret");
var host = new MyServer(); host.Start("0.0.0.0:80");
new PuppetWebServer().Start("0.0.0.0:9090");
```

## 运行时端点

请求头一律：`Authorization: Bearer <key>`

| 端点                   | 方法   | 说明                                                                    |
| -------------------- | ---- | --------------------------------------------------------------------- |
| `/agent/registry`    | GET  | 条件查询实例列表（type/tags/includeInternal/activeWithin/keyword/offset/limit） |
| `/agent/state`       | GET  | JSONPath 查询运行时状态（`path`/`include`/`exclude`）                          |
| `/agent/get`         | GET  | 按点分路径读单属性（`path`，如 `SelectedStateName`）——优先使用                         |
| `/agent/set`         | POST | 按点分路径写属性，触发对应 UI 事件（body: `{path,value}`）                             |
| `/agent/describe`    | GET  | 获取实例 schema（`format=md\|json`）                                        |
| `/agent/invoke`      | POST | 调用方法：`?name=X&method=Y`，body = JSON 数组 args\[]                        |
| `/agent/logs`        | GET  | 读取 logger 缓冲（`name` 可不填列出全部 logger 名）                                 |
| `/agent/usage`       | GET  | 使用率摘要：各功能类别调用次数 + error 数（供优化/清理由）                                    |
| `/agent/key/refresh` | POST | 刷新全局密钥                                                                |

### 调用约定（重要）

* `/agent/invoke` 的 `name`、`method` 是**查询参数**（中文方法名需 URL 编码），`args` 放 body 的 JSON 数组。

* 优先用 `/agent/get?path=<member>` 读单个值，而非序列化整对象（见「踩坑」）。

* `/agent/set` 设控件值即模拟真实用户操作（会触发对应事件），用于测试现场。

* async 方法：invoke 会阻塞等待 Task；无返回值时返回 `null`。

## Agent 使用与迭代原则（必读）

1. **框架为 agent 而生**：它服务于 Agent 自动调试，不考虑人类开发者友好性。代码/文档都面向 Agent 高效理解。
2. **Agent 负责维护与迭代本框架**：在宿主工程开发过程中，Agent 按调试需要随时优化框架，确保调试顺利、高效、安全。**防止框架臃肿**——定期用 `/agent/usage` 分析功能利用率，对未使用或低价值的能力予以清理。
3. **操作与观测的分野（两类接口，目的不同）**

   * **操作类接口**：负责模拟用户操作流程以实施调试/测试（同时具备观测能力）。

   * **日志类接口**：负责客观观测。

   * 日志中**与用户共享**的部分保留在代码里；**不与用户共享**的部分在测试完成后清理。

   * 操作类的去留：最终交付时由用户确认后才决定清理或保留（保留便于后续跟踪探测，但必须强化安全性——如密钥设计）。

   * 两类接口都可能贯穿"开发到成果"全周期，管好各自的**时间节点**（引入、稳定、交付清理）。
4. **操作类接口的硬约束**：**严禁**在宿主工程内部编写/暴露专门的调试代码或方法（如 `XxxForTest()`）。必须只通过调用**用户工程本身已有的、面向用户的代码/方法/接口**来模拟用户操作流程，到达测试现场。
5. **接口暴露先授权**：任何向 Agent 暴露新接口的改动，**先向用户请求授权**，得到确认后再实施。

## 安全

* 密钥由源码端控制，可运行时刷新（`PuppetKeyVault`）。**不要把密钥写进日志**。

* `[PuppetIgnore]` 显式屏蔽敏感成员（密钥、句柄、内部状态）。

* 内部后台实例注册时标记 `internalOnly: true`，默认不进入查询结果（显式 `includeInternal=true` 才可见）。

* 强化密钥设计（含日志接口的鉴权）。交付时若保留操作类/日志类，务必复核安全性。

## 宿主工程约束（Agent 必须遵守）

* **不插入任何探测/临时代码**；一切通过既有宿主代码路径完成。

* 带模态对话框（OpenFileDialog/SaveFileDialog/InputBox）的事件处理器必须拆分为 UI 层（`_Click`）与业务层（`XxxAsync/Xxx`，标 `[PuppetExpose]`）。UI 层只留对话框交互、光标、错误提示；业务层含完整逻辑，Agent 直接调业务层绕过模态框。

* 涉及 UI 的状态变更，异步事件完成后需**轮询就绪标志**（如 `/agent/get?path=IsReady`）再走下一步。

* 暴露任何新成员前先取得用户授权。

## 使用日志（观测与分析）

* 框架引入 `WimaLogger` 作为日志体系（`PuppetUsage`）。

* 自动记录：端点命中（`endpoint`）、方法调用（`invoke::<method>`）、实例注册（`registration`）、实例解析（`resolve`）、意外/异常（`error`）。

* 读取明细：`/agent/logs?name=Puppet`（内存缓冲）；同时落盘 `logs/Puppet_*.log`。

* 读取结构化统计：`/agent/usage` → 各功能调用次数 + error 数，据此判断哪些能力该清理/保留。

* 日志只记元信息（类别/调用方/次数），**绝不记密钥与参数值**。

* 该日志是框架级观测，独立于各宿主实例的 `AgentLog`。

## 外接 Web 服务器注意事项

本框架默认使用**内建 WebServerBase**，端点入口参数类型（`WebRequest`/`WebResponse`）由内部引用的库提供。
若宿主改用**外部 Web 服务器**（如 Kestrel/ASP.NET），外部框架的请求对象与本框架的 `WebRequest` 类型不同，**必须做对应的类型包装**才能交给框架内的处理过程（`PuppetWebHandler.TryHandle`）。否则端点无法正常读取 `Path`/`Queries`/`Headers`/`InputStream`。

## 实战踩坑记录（来自 VisualDataHub 等项目）

* **`/agent/state`** **整对象序列化可致进程崩溃**：对含 WebView2/Form/事件处理器的复杂对象，在 web 线程整对象序列化会触发跨线程异常或访问已释放资源。请用 `/agent/get?path=<single>` 或 `path=a.b.c` 加深层路径；`GetProperty` 对复杂对象只返回 `{type,note}`，不会序列化对象图，可安全探测 null vs 非 null。

* **invoke 的 name/method 是 URL 查询参数**，中文方法名须 `UrlEncode`；args 在 body（JSON 数组）。

* **PowerShell 调用注意**：外部命令传含引号参数会丢引号。用 `Invoke-RestMethod -Body` 或 `curl.exe --data-binary @file`，避免 `curl -d` 双引号丢失。

* **UI 线程切换**：框架已通过 `PuppetUiContext` 自动把跨线程调用切回 UI 线程（覆盖控件句柄未创建时 `InvokeRequired` 恒为 false 的误判）。

* **异步就绪判定**：涉及异步 UI 流程时，调用后轮询就绪标志，不要立即读后续状态。

* **注册时机**：窗体在**构造时**注册到注册表（`Disposed → Unregister`）。查询可用实例先 `/agent/registry`。

## DEBUG 工作流

1. 通读本指南，确认集成模式（优先模式 A）与密钥。
2. `/agent/registry` 确认目标实例已注册。
3. `/agent/describe?name=X&format=md` 查看能力，规划操作序列。
4. 优先用 `/agent/get` 读状态、`/agent/set` 与 `/agent/invoke` 模拟用户操作，逐步推进到测试现场。
5. 遇到异常用 `/agent/logs` 与 `/agent/usage` 观测，必要时据此迭代框架本身。
6. 所有临时观测日志在完成后清理；与用户共享的日志保留。

