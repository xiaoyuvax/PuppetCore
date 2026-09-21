# Puppet.Core 双面向架构提案（v1.0 —— 定稿）

> 状态：**讨论定稿，待实施**。全部架构决策已闭合（§9.3 无遗留）。
> v0.2（2026-09-20）：鉴权模型改为 OS 代理授权（本地免交互，§5）。
> v0.3（2026-09-21）：Action 提炼作者模型反转为开发期 Agent（§3.0）。
> v0.4（2026-09-21）：暴露体系改为范式内化 + UI 文案同源（§3 整章重写），全面静态标注降级为可选覆盖。
> v0.5（2026-09-21）：覆盖特性族更名 Puppet* 前缀（§3.2）；新增内存产物下载通道 PuppetArtifact（§3.6/§4）。
> v0.5.1（2026-09-21）：动作范式返回类型修订——Task<bool>/Task<OperationResult> 等成败可观察异步方法收录（§3.2）。
> v0.5.2（2026-09-21）：/appagent/actions 参数确定按名传 + 严格校验（§4）。
> v0.5.3（2026-09-21）：Paranoid 开关/审计保留/产物定标采用推荐默认（用户未确认，标注可否决，§4/§7）。
> v0.5.4（2026-09-21）：BlockAgentEndpoints 显式开关定案；审计改为仅远程启用（与安全分级一致，MVP 无审计组件）；产物定标定案。
> v0.5.5（2026-09-21）：新增面向 B 两级并发模型——强制实例级 action 串行 + 建议性协调复用 AgentBook/SiteLock（§4 要点 8）。
> v0.5.6（2026-09-21）：新增极简发现模式（档案 L0 / 扫描探针 L1 / 自描述文档 L2）与自描述错误契约（§4.5）。
> v1.0（2026-09-21）：[PuppetAppInfo] 定案（§3.7），§9.3 全部闭合，定稿。
> 目标：让 Puppet.Core 同时服务两个面向——**A. Agent 自调试**（现有能力）与 **B. 用户 Agent 操作接口**（新能力）——一套架构，二位一体，按需分别启用。

---

## 1. 需求重述与问题定义

### 1.1 两个面向的本质差异

| 维度 | 面向 A：Agent 自调试 | 面向 B：用户 Agent 操作接口 |
|------|---------------------|---------------------------|
| **服务对象** | 开发期 Agent（宿主工程的开发/调试者） | 终端用户的个人 Agent（代理用户操作应用） |
| **应用阶段** | 源码开发期，编译前后的调试 | 编译完成的成品应用（发布后） |
| **Agent 身份** | 拥有宿主源码，可改代码、改框架、重新编译 | 只有成品二进制，不可能改代码 |
| **信任模型** | Agent 与宿主同一信任域（Agent 即开发方） | Agent 来自外部，**不可信程度高**，需要应用主动授权 |
| **暴露面** | 尽量宽（含非 public 成员，便于深挖调试） | 必须窄（只暴露应用**有意公开**的操作面） |
| **暴露机制** | 反射自动生成 + Agent 运行时打标（PuppetExpose/PuppetHint） | 设计范式自动 Actionize（动作方法/状态属性）+ 可选覆盖标注；发布期锁定，成品后不可再追加（§3） |
| **生命周期** | 随开发周期：引入 → 稳定 → 交付清理 | 随产品生命周期：随应用安装 → 常驻 → 随应用卸载 |
| **配置来源** | 源码中的 Attribute + Agent 运行时打标 | 源码设计范式 + Agent 提炼静态表（均进版本管理）+ 发布清单（manifest） |
| **失败后果** | 调试中断，可重试 | 用户数据可能被误操作，**不可逆** |
| **审计需求** | 框架级使用日志（PuppetUsage）即可 | 分级：本地=框架级元统计；远程=用户可读操作审计（v0.5.4） |

### 1.2 核心矛盾（为什么不能简单加端点）

1. **信任域不同**：面向 A 的密钥是"开发者知道的所有人都能用"；面向 B 的授权必须由**应用/用户**显式授予，且随时可撤销。
2. **暴露面哲学相反**：A 面向"宽暴露 + 运行时打标"（Agent 有权自我扩展）；B 面向"窄暴露 + 编译期声明"（运行时扩展=安全漏洞）。
3. **成员解析规则不同**：A 的规则是"public 默认可访问、非 public 需 PuppetExpose"；B 若沿用此规则，等于把应用内部状态全部泄露给用户侧 Agent——不可接受。
4. **提示/标注来源不同**：A 的 PuppetHint 是 Agent 写的（避坑记录）；B 的操作说明必须由应用作者写（面向用户的文案，如"点击此按钮提交订单"）。

---

## 2. 总体架构：一套内核，双通道出栈

### 2.1 设计原则

- **一个内核，两个门面（Facade）**：反射引擎、序列化、注册表、UI 线程调度、日志复用现有内核；两个面向各是一个"暴露策略 + 端点族 + 鉴权域"的组合门面。
- **暴露策略（Exposure Policy）作为一等公民**：内核的成员解析不再硬编码"A 规则"，而是按请求所属面向选择解析策略。这是"合二为一"的关键——同一实例、同一反射缓存，不同面向看到不同成员集合。
- **分别启用**：面向 B 不启用时，框架运行时表现与现状**完全一致**（零新增端点、零行为差异）。启用 B 是显式的一次调用，不启用则连代码路径都不进入。
- **面向 A 保持不动**：本提案不改变任何 `/agent/*` 现有端点语义，保证存量宿主与 Agent 工作流零迁移成本。

### 2.2 分层架构图

```
┌─────────────────────────────────────────────────────────────┐
│                        宿主应用（成品或开发中）                  │
│                                                             │
│  ┌───────── 应用源码范式（Agent 按 §3 指南实施）─────────┐        │
│  │  public bool PlaceOrder(...) {...} ← 符合动作范式即收录  │   │
│  │  desc 与按钮/菜单 Text 同源，无需重复文案标注             │   │
│  └────────────────────────────────────────────────────┘        │
│                                                             │
│  ┌────────────────────── 内核（现有，扩展点新增）───────────┐    │
│  │  PuppetRegistry（+面向标记）                            │   │
│  │  MemberResolver 引擎 → ExposurePolicy（策略对象，新增）   │   │
│  │    ├─ DebugExposurePolicy   （= 现行 A 规则，原样）        │  │
│  │    └─ ActionizePolicy  （范式内动作/状态+覆盖标注）      │  │
│  │  PuppetUiContext / RunOnUi（复用）                       │  │
│  │  PuppetUsage（+ 面向 B 审计钩子）                         │  │
│  └────────────────────────────────────────────────────┘        │
│                                                             │
│  ┌──── Web 层 ─────────────────────────────────────────┐      │
│  │  /agent/*        ← 面向 A（现状，不动）                 │     │
│  │  /appagent/*     ← 面向 B（新端点族，见 §4）             │     │
│  │  PuppetWebHandler.TryHandle（先 A 后 B，互不干扰）       │     │
│  └────────────────────────────────────────────────────┘        │
│                                                             │
│  ┌──── 鉴权与授权 ────────────────────────────────────┐        │
│  │  A：PuppetKeyVault（全局/实例密钥，现状不动）           │      │
│  │  B：AppAgentAccessBoundary（v0.2：档案凭证，OS 代理授权） │     │
│  └────────────────────────────────────────────────────┘        │
└─────────────────────────────────────────────────────────────┘
            ↑ 面向 A：开发期 Agent（同信任域）                
            ↑ 面向 B：用户个人 Agent（外部，经用户授权）
```

### 2.3 为什么是"双门面"而不是"两个框架"

- 反射缓存、UI 线程切换、参数绑定、异步 Task 等待——这些机制 A/B 完全同构，复制两份是纯浪费。
- 差异全部收敛到三处：**谁能看见哪些成员**（ExposurePolicy）、**谁能调用**（AuthDomain）、**结果如何呈现**（描述器与审计）。门面就是这三个差异点的组合。
- 这样 A 和 B 可以在同一进程共存（比如开发期的宿主同时开着 B 通道给测试 Agent 用），也可以只开其一。

---

## 3. 面向 B 的 Action 暴露体系：范式内化（v0.4 重写 —— 2026-09-21 讨论确认方向）

### 3.0 作者模型（v0.3 定稿，v0.4 保留）

**提炼者与实施者 = 开发期 Agent，人类开发者只做审定。**（v0.1"应用作者逐一标注"为错误假设，废弃）
理由：只有 Agent 能以"源码通读 + 借 A 端点运行时走查"双向验证保证流程树无缺环；`IPuppet` 的既有哲学
（"Agent 是 Puppet 化的实施者"）与 DEVELOPMENT.md 模态框拆分规则（UI 层 `_Click` / 业务层 `XxxAsync`）
本就是本机制的特例，v0.4 将其推广为通用范式。提炼 spec 与重构指导写入 DEVELOPMENT.md 新章节
「面向 B：Action 体系提炼与重构指南」，随包分发，两种部署模式均可用。声明仍落在源码与版本管理中
（Agent 编辑宿主源码实施，非运行时注册），安全属性不变。

### 3.1 核心设计：三份真相一份源（Action 文案与 UI 同源）

用户 Agent 的操作文案不应在代码中出现第二份拷贝。UI 文案、源码结构、Action 描述同源于一处：

```
GUI 控件 Text / AccessibleName ──┐
事件归属（Click → 业务方法）    ──┼─→ ActionDescriptor（name/desc/params）→ manifest
动作方法范式（签名与可见性）     ──┘
```

- 用户对 Agent 说"帮我加个任务" → Agent 在 manifest 中按文案匹配"添加"（与按钮 Text 同源）→ 命中 AddTask。
- 提炼 Agent 无需在标记内重写与 UI 重复的文案，消除双份维护开销。

### 3.2 Actionize 设计范式（编译期可判定，替代全面静态标注）

**动作方法范式**（→ manifest.actions）——判定标准 = 正面形状 + 语义锚点 + 负面排除清单，三者合取：

正面（全部满足）：
1. `public`；2. 实例方法（非 static）；3. `DeclaredOnly`（排除继承）；4. 非 special name；
5. 非基类 override（排除返回 bool 的 `Equals` 及 ToString/GetHashCode）；
6. 返回**成败可观察**类型：`bool` / `OperationResult`，及其 awaitable 包装 `Task<bool>` / `Task<OperationResult>`
   （v0.5.1：异步不改变语义锚点——HTTP 请求-响应本就等待完成，解包语义与现有 invoke 的 Task 处理一致：
   GetAwaiter().GetResult() 提取 Result，UI 线程启动 + continuation 回空闲 UI 线程无死锁，内核已验证机制直接复用；
   宿主既有约定（模态框拆分规则）本就产出 `XxxAsync` 业务方法，排除 Task<bool> 会造成系统性缺环。
   ValueTask 系语义相同，B 执行器用 AsTask() 一并支持，实施时定）。

**语义锚点（为什么是 bool）**：不是猜测，是把宿主代码既有约定形式化——DEVELOPMENT.md 模态框拆分规则
要求业务层方法独立，TaskBoardForm 的 Report 模式（`public bool AddTask(...)` 成败返回 + 消息呈现给用户）
已确立 `public bool` = "成败式用户操作"，且拆分规则本就命名业务层方法为 `XxxAsync`——异步业务方法是
宿主既有产出的常态。对比：void / 非泛型 Task=fire-and-forget（**无可观察结果**，排除的正是这一类，
而非异步机制本身）；int/string/Task<int> 等数据返回=查询（归 state）。

负面（任一命中即出局，防误收主力）：
1. `[PuppetIgnore]`（永远赢）；2. 带 `out` 参数（TryXxx 技术范式标志，如 `TryParse(s, out x)`）；
3. `Is/Can/Has/Should` 前缀（谓词询问 `IsValidInput`，归 state 侧非动作）。

灰色带（Validate 等既可问可做）两头走覆盖：范式外真动作 → `[PuppetAction]` 收录；范式内非动作 →
`[PuppetIgnore]` 划界；典型宿主灰色成员屈指可数，提炼 Agent 审定兜底（§3.0 作者模型）。

可选宿主配置 `ActionPrefixes`（如 `"Do"`）收紧判定。Do 不作默认：.NET 无此惯例，强制全量重构
丢失"现状即合规"；仅供高安全宿主选用。
- **现状即合规**：TaskBoardForm 的 AddTask / CompleteSelectedTask / RenameSelectedTask / SetFilter 等
  public bool 业务方法零标注即全部收录；protected 事件处理器（`_Click`）天然出局。
  范式外成员（如返回 int 的 ClearCompletedTasks）用 `[AgentAction]` 覆盖收录——覆盖标注存在的意义之一。

**状态属性范式**（→ manifest.state）：
- `public` 属性 + 简单类型（**复用内核 IsSimpleType**：基本类型/字符串/枚举/日期/Guid…）+ 无 `[PuppetIgnore]`。
- 复杂类型属性自动出局（Font/Form/集合对象），无需标注。TaskBoardForm 的 public int/string 属性
  （TotalCount、StatusText…）零标注即全部收录。

**安全默认不变**：`[PuppetIgnore]` 永远赢；将来权限收紧靠"前缀判定 / 注册表收窄"，范式永不自动扩权。

**`[PuppetAction]` / `[PuppetState]`（v0.5 更名，原 AgentAction/AgentState）降级为可选覆盖标注**，仅三种用途：
1. 改 action name（默认 = 方法名；发布后改名 = 破坏性变更，见 §6）；
2. 收录范式外成员（返回值非 bool/OperationResult 的方法等）；
3. `[PuppetParam(sensitive: true)]` 敏感参数标注（审计脱敏）。

**命名规约（v0.5）**：覆盖特性族并入既有 `Puppet*` 家族（PuppetIgnore/PuppetExpose/PuppetHint/PuppetDescription）——
`PuppetAction` / `PuppetActionGroup` / `PuppetState` / `PuppetParam`，与面向 A 概念体系同前缀，明确归属 Puppet.Core；
弃用 Agent* 前缀（Agent 一词在本框架中指调用方，不指宿主侧声明）。补漏与特例用途保留不变。

覆盖标注 desc 缺省时仍走 §3.3 文案解析，不强制写长文案。

### 3.3 文案解析：三层同源（优先级从高到低）

| 层 | 来源 | 机制 | 覆盖 |
|----|------|------|------|
| L1 | UI 同源（内建控件自动） | 启动时扫描控件树（复用 WinForms 包遍历），取 `AccessibleName` > `Text`（剥离助记符 &A），按事件归属关联到动作方法 | Button/MenuItem/ToolStrip 等 .NET 内建控件 |
| L2 | Agent 静态表（编译期提炼，进版本管理） | 提炼 Agent 生成 `AppAgentDescriptors.g.cs`（action → desc/参数说明），素材：XML doc + 邻近 UI 文案 + 交互语境 | 第三方控件、CLI 宿主、无 UI 的内部动作 |
| L3 | 自动兜底 | 方法名符号化拆词 + XML `<summary>`（若有），标 `InfoSource=Inferred` 供用户 Agent 降权展示 | 其余 |

- 文案优先级：`[AgentAction]` 显式 desc > L1 > L2 > L3；维护义务随层递减。
- **事件归属表**：运行时只做"控件 → 事件处理器"扫描；"处理器 → 动作方法"的归并在编译期由提炼 Agent
  完成（同一事件多处挂钩/链式委托在运行时反射不可靠，静态归并可 review）；两表启动时合并。
- 非 .NET 默认控件的通用性挑战由 L2 承接（Agent 理解源码后写静态表），运行时不做猜测性反射探测——
  把不确定性从运行时移到编译期，是与"运行时自动化判断"路线的关键取舍。

### 3.4 方案利弊权衡（静态全面标注 vs 范式内化）

| 维度 | 全面静态标注（v0.1，废弃） | 范式内化（v0.4，采纳） |
|------|--------------------------|----------------------|
| 文案维护 | Attribute 长文案，与 UI 重复 | UI 同源，单一真相 |
| 缺环风险 | 逐一标注，易漏环节 | 范式全量判定，漏出即框架外成员，天然全量 |
| 代码噪音 | 大量长标记 | 几乎零标记（覆盖标注是例外） |
| 误收风险 | 低（显式声明） | 存在：技术性 bool 方法误入 → `[PuppetIgnore]` 划界 / `ActionPrefixes` 收窄 |
| AOT/Trimming | 好 | 同样好（仍是反射枚举，非运行时注册） |
| 实施复杂度 | 低 | 中（范式判定器 + 三层文案解析） |

### 3.5 仍然不是运行时注册（v0.3 结论保留）

- 运行时注册把"暴露面决定权"移给任何拿到进程内引用的代码，成品应用中等于向插件/依赖开放恶意接口能力。
- 范式判定 + 反射枚举在 AOT/Trimming 下可靠（项目已开 `IsAotCompatible`）；静态表与范式均进版本管理，可 review、可审计。
- 例外保留：宿主启动时允许**收窄**（`DisableAction("deleteAll")`），不允许**扩宽**——收窄是产品配置，扩宽是漏洞。

**动态实例绑定（opt-in 修订，2026-09-21）**：`AppAgentOptions.DynamicBinding`（默认 false）开启后，
新注册的 IPuppet 实例自动进入 B 通道绑定表（按名去重替换），注销时自动移除（防强引用泄漏）。
这不是运行时暴露面注册——manifest 内容仍由编译期范式形状唯一决定（§3.2），实例集合的扩张只影响
"哪个实例可查"，不影响"每个类型暴露什么"。适用场景：编辑器类 WinForms 宿主的运行期窗体
（启动快照必然缺失）。成品安全默认不变：不开开关即维持快照语义。

### 3.6 二进制产物通道：内存 Asset 下载（v0.5 新增）

**问题**：状态范式（IsSimpleType）天然适配文本聊天——复杂结构对象没必要输出给用户看；但用户常需要
从 Agent 获取**内存二进制产物**：导出的 Docx/Pptx 报表、渲染的图片/视频、序列化的领域文件。

**方案**：产物不走 state，走**具名 action 的产物下载通道**——WebServer 本就完整支持二进制响应，
将内存字节模拟为 Web asset 提供下载（等同磁盘文件，只是来源是内存）。实施先例：宿主侧
Loramonitor/visualdatahub 已用同一模式把内存态 l3d 文件经 WebServer 模拟磁盘文件提供下载；
本框架将该模式内化为通用能力。

**形态**：
- action 返回值携带 `PuppetArtifact`（FileName / ContentType / 字节内容；或 OperationResult.data 内嵌），
  框架自动存入 `AppAgentAssetStore` 并在 OperationResult 中回传下载 URL：`/appagent/assets/{id}`。
- manifest 不变；asset URL 是**按次调用**的产物，不进能力目录。
- 语义边界：**state 保持简单类型（文本聊天视图），二进制一律走 action 产物**——两者不混淆。

### 3.7 应用信息声明：[PuppetAppInfo]（v1.0 定案）

可选具名声明（程序集级或实例类级），填充 manifest 的 `app` 段（展示名/版本/供应商）：

```csharp
[assembly: PuppetAppInfo("小步任务看板", "2.3.1", "Xiaoyuvax")]
// 或实例级：[PuppetAppInfo("小步任务看板", "2.3.1")] public sealed class TaskBoardForm ...
```

- **不自动收集**：程序集版本、类型信息等一律不自动进 manifest——与"manifest 只含显式声明"原则一致（§9.2 泄露面约束）。
- 缺省时 manifest.app 为 null，用户 Agent 以发现档案（§5.2）中的 productName 兜底。
- 归入覆盖特性族实现清单（§8 #1）。

---

## 4. 面向 B 端点族设计（/appagent/*）

独立前缀，与 `/agent/*` 完全隔离：

| 端点 | 方法 | 鉴权 | 说明 |
|------|------|------|------|
| `/appagent/probe` | GET | 无 | 发现探针：`{puppet, protocol, auth, authHint, help}`，静态常量最小披露（§4.5） |
| `/appagent/help` | GET | 无 | 面向用户 Agent 的极简使用手册（内嵌 markdown，等同 CLI `--help`） |
| `/appagent/manifest` | GET | 授权凭证 | 应用操作清单（name/desc/参数 schema/版本，desc 按 §3.3 三层同源解析），用户 Agent 的"能力目录" |
| `/appagent/actions/{name}` | POST | 授权凭证 | 执行操作：body = `{ args: {…按名传}, callId: "uuid" }`（v0.5.2：参数严格按名绑定，见下） |
| `/appagent/state/{key}` | GET | 授权凭证 | 读单个状态 |
| `/appagent/assets/{id}` | GET | 授权凭证 | 下载内存产物（§3.6：action 产出的二进制模拟 asset 下载） |
| `/appagent/events` | GET(SSE) | 授权凭证 | 订阅应用事件流（可选，见 §5.5） |
> v0.2：consent 端点族从 MVP 取消——本地凭证由 OS 档案 ACL 自动分发（§5.2），无需状态查询/撤销端点；
> 远程口子（§5.3）启用时再引入对应授权端点。

**设计要点**：

1. **审计分级（v0.5.4 定案）**：审计与安全分级一致——本地调用（档案凭证，同信任域，与 CLI 同哲学）**不记审计**，仅框架级元统计（PuppetUsage：类别/次数，无参数值）；远程调用（AllowRemote，跨信任域）才启用用户可读审计：独立 logger "AppAgent.Audit"，含 callId、action name、参数摘要（`[PuppetParam(sensitive:true)]` 脱敏）、成败、耗时、来源；`/appagent/audit/recent` 仅 AllowRemote 模式可用。MVP 仅本地场景 → 审计组件随 AllowRemote 列二期。
2. **操作结果面向用户**：`OperationResult { ok, message, data }`。message 是给用户看的自然语言（应用作者写好的），data 是结构化结果。与面向 A 的 `{ok, result, err}` 形状刻意不同。
3. **无 describe/invoke/set/get 之类的通用端点**：面向 B 拒绝一切"通用反射入口"，只允许"具名操作"。这是安全面的根本差异，也是两个面向不可共用端点的核心原因。
4. **产物生命周期（AppAgentAssetStore）**：内存仓库，条目 id 随机不可枚举，TTL 默认 10 分钟，
   总量上限 + LRU 淘汰（默认值实施时定，如 256 MB / 条目 100 MB），下载响应带 Content-Disposition 文件名；
   超限先淘汰到期条目，仍不足则拒绝而非无界缓存。assets 与全部 /appagent/* 一样强制凭证。
   大文件流式/range 支持列二期（MVP 全内存字节，与 visualdatahub 先例一致）。
5. **参数绑定（v0.5.2 定案：按名传 + 严格校验）**：`args` 为 JSON 对象，键 = manifest 发布的参数名；
   未知参数名 → 400（拼写错误入口即暴露）；缺必填参数 → 400；可选参数省略即取默认值。
   理由：manifest 本就发布具名参数 schema，按名是对契约的完整利用；按位传在版本演进插入参数时
   会**静默错绑**（`["标题","高"]` 中 "高" 绑到新插入的 category 上，不报错效果错误），严格按名则
   演进非破坏。重载消解：每个 action name 仅暴露单一签名（manifest 可验证），同名重载需
   `[PuppetAction]` 改名区分。
6. **审计保留策略（v0.5.4 定案：仅远程启用）**：远程审计采用——内存环形 500 条（即取即用）
   + 默认落盘用户档案 `%LOCALAPPDATA%\<产品名>\logs`（复用 WimaLogger 机制，路径不得用相对
   `logs/`——Program Files 下不可写；落盘失败静默降级为仅内存）+ 敏感值脱敏、无密钥。
   本地调用不记审计（同信任域，与 CLI 一致）。
7. **产物仓库定标（v0.5.4 定案；未来实践后调整）**：TTL 10 分钟 / 总量 256 MB / 单条目 100 MB，
   全部宿主可配置（监控/渲染类宿主自行调大）；超限先淘汰到期条目，仍超则拒绝并报错。
   Agent 契约：action 返回携带 asset URL 时**立即下载**，不假定长期有效；宿主退出仓库即消失。
8. **并发模型（v0.5.5）——两级设计，与信任分级对齐**：
   - **机制层（强制）**：`/appagent/actions/*` **按实例强制串行**——每实例一把执行锁，同一实例的
     action 依次执行；等待超时（默认 10s，可配 busyWaitMs）返回 `409 {ok:false, err:"instance busy", retryAfterMs}`。
     理由：B 是成品通道，调用方是任意用户 Agent（不能假设其自愿遵守锁礼仪），安全边界必须是机制而非约定；
     GUI 操作本是人类速度，串行零吞吐代价；UI 线程 marshal 之下本就大体串行，显式队列补齐 async 插空。
     读类端点（manifest/state/assets）不加锁。**单 action 无需先加锁**——保持"读 help 即可用"体验。
   - **协调层（建议性）**：复用 A 既有机制零新建——B 请求接受与 A 相同的透传参数（agentId/agentOp/
     site/lockMode）进 AgentBook 心跳（本地无审计时这是"现在谁在操作"的唯一可见面）；多步原子序列
     （先选中再重命名，中间不可插队）用 SiteLock 建议性锁，协议与 A 一致，manifest/docs 教会用户 Agent。
   - **场景矩阵**：B-only 成品（A 无注册实例）→ 无跨面向问题；B 内部多 Agent（多产品/编排器并行）→
     强制串行兑底 + 建议性锁协调；双面向开发期（A 调试 + B 试运行同实例）→ 归入 A 自身多 Agent 纪律
     （SiteLock + 开发流程约束：测 B 面向就走 B 通道黑盒，即 §3.0 验证闭环的要求），另设二期可选硬加固
     `CrossGuardAgentWrites=true`（A 的 set/invoke 也过同一实例队列，默认关，不改 A 默认行为）。
   - **manifest 增并发段**（协议 minor 版本）：
     `"concurrency": { "actionExecution": "serialized-per-instance", "busyWaitMs": 10000 }`
     用户 Agent 据此实现 409 重试——它唯一需要知道的并发知识。

### 4.5 极简发现模式与自描述错误契约（v0.5.6 新增）

**目标**：用户对 Agent 说一句"访问本地 9090 了解详情，我们再继续"，或把 Puppet.Core README 扔给
Agent，Agent 即可自动完成发现、学习、连接、等待指令——零手动配置。

**三级发现（本地同用户）**：

| 级 | 手段 | 场景 |
|----|------|------|
| L0 档案发现（首选） | 枚举 `%LOCALAPPDATA%\Puppet.AppAgents\*.json` → {endpoint, productName, protocol, key} | 常态：零扫描、精确、可发现非默认端口实例（§5.2 契约） |
| L1 扫描+探针（兜底） | 扫默认端口 9090（+用户口述端口），`GET /appagent/probe` | 档案缺失/陈旧；用户只给端口 |
| L2 自描述文档 | `GET /appagent/help`（无凭证，内嵌 markdown） | 学习操作方法——CLI `--help` 的等价物 |

**probe 响应**（无凭证、静态常量、最小披露）：

```json
{ "puppet": true, "protocol": "appagent/1.0",
  "auth": "profile-file", "authHint": "%LOCALAPPDATA%\\Puppet.AppAgents\\<app>.json",
  "help": "/appagent/help" }
```

凭证分层与 CLI 类比严格对应：**probe/help = 看见工具 + 读手册**（无凭证，不含应用操作细节）；
**manifest = 能力目录**（需凭证）。远程变体：AllowRemote 下 probe/help 同样可达，authHint 改为
"向应用/用户获取凭证"；不做 mDNS/广播——远程场景由用户显式提供 host:port。

**自描述错误契约**：所有 `/appagent/*` 错误统一 `{ok:false, code, err, hint, retryAfterMs?}`
（Problem Details 风格），每条错误自带行动指引：

| code | hint 示例 |
|------|----------|
| `credential-invalid` | "read key from %LOCALAPPDATA%\\Puppet.AppAgents\\<app>.json" |
| `instance-busy` | "retry after retryAfterMs, or report wait to user" |
| `action-not-found` | "GET /appagent/manifest for available action names" |
| `asset-expired` | "re-run the producing action to get a new asset" |
| `site-locked` | "site held by another agent; acquire via lock/acquire, wait, or proceed non-atomically" |

**标准用户流（README「用户 Agent 发现技能」段固化的四步）**：

```
probe → help → 读档案取 key → manifest → 向用户回报可用操作数并等待指令
```

面向 A 的发现已有成熟入口（/agent/docs、/agent/capabilities，全局密钥），本节不改动 A。

---

## 5. 鉴权与访问边界：OS 代理授权（v0.2 修订 —— 2026-09-20 讨论确认方向）

### 5.1 信任模型（v0.2：本地免交互授权，边界由 OS 执行）

> **v0.2 结论**：默认场景——本机用户自己的 Agent 操作本机应用——与"Agent 在本机执行 CLI 工具"
> 处于同一信任域：能在本机执行命令的进程本就拥有用户档案内全部数据与执行权，HTTP 端点不引入
> 新增能力。因此本地调用**不做任何用户交互式授权**；v0.1 的 Interactive/Persistent/Manual
> 授权模式整体降级为远程专用（§5.3）。授权依然存在，只是从"用户交互确认"改为
> **"操作系统用户隔离代为执行"**。

| 请求来源 | 判定 | 处置 |
|----------|------|------|
| 127.0.0.1 + 同一 Windows 用户 | 本地 | 允许（凭证自动可得，§5.2） |
| 127.0.0.1 + 其他用户会话（RDP 另一账户、快速用户切换另一账户） | **视为远程** | 拒绝 |
| 局域网/外部网络地址 | 远程 | 默认拒绝；`AllowRemote=true` 后走 §5.3 |
| 浏览器网页（DNS rebinding / CSRF drive-by） | 远程 | 拒绝（拿不到凭证） |
| SYSTEM/服务身份宿主 | 跨边界 | 档案凭证不适用，走 §5.3 显式授权 |

### 5.2 执行机制：用户档案凭证文件（零交互）

关键技术事实：**TCP loopback 分不清用户与会话**——另一用户的 RDP 会话连 127.0.0.1:9090，
与本机进程连接在网络层完全相同（loopback 全机共享）。因此"另一登录用户视为远程"不能只靠
绑定 loopback 实现，必须引入**绑定用户的凭证**：

- 应用启动时生成随机 AppAgentKey，连同 endpoint/应用名/协议版本写入
  `%LOCALAPPDATA%\Puppet.AppAgents\<app>.json`（用户档案默认 ACL：仅本用户与管理员可读；
  多实例时文件内 instances 数组或带 PID 后缀，实施时定）。
- 本地 Agent 以同一用户身份运行 → 直接读取该文件 → 携带 `Authorization: Bearer <key>` 调用。
- 其他用户 / 远程来源读不到该文件 → 无法通过鉴权 → 拒绝。
- **所有** /appagent/* 请求一律强制凭证（本地也不例外）——顺带天然免疫 rebinding/CSRF。

与面向 A 的密钥分发对称：**A 的密钥由开发者源码端分发，B 的凭证由 OS 档案 ACL 分发**。

**发现契约（同文件解决）**：用户 Agent 枚举 `%LOCALAPPDATA%\Puppet.AppAgents\*.json`
即可发现本机所有开放 B 面向的应用（地址、名称、协议版本）——与"agent 读 CLI help 后直接
执行"的体验完全对齐，无需注册表/广播机制；CLI 类宿主同样适用。
（v0.5.6：此为发现 L0；端口扫描 + `/appagent/probe` 探针为 L1 兜底，统一见 §4.5。）

诚实边界（明确不设防，与 CLI 同一哲学）：

- 同一用户的本地恶意进程可读凭证文件——但同信任域内它本可键盘记录/读档案/直接执行 exe，
  端点不构成能力升级。
- 管理员账户互读档案与 CLI 同理（管理员本可见全机数据）。

注：AGENTS.md"密钥绝不写入日志/文件"约束针对凭证**泄露**进仓库/日志；本文件是**受控披露**
设计（披露对象=同用户进程），不违背其本意。若不接受文件载体，替代为命名管道 + 客户端
PID/会话校验（进程级边界更硬，依赖 Kestrel 管道传输，成本高）。

### 5.3 远程口子（留而不做）

`UseAppAgent(o => { o.AllowRemote = true; })` 显式开启后，才允许非 loopback 绑定并启用
v0.1 的显式授权机制（应用内展示授权码 / 配置注入密钥，即原 Interactive/Manual 模式）。
默认关闭；MVP 不实现远程授权 UI 与凭证持久化。

### 5.4 权限子集（可选进阶，0.2 再议）

Manifest 中每个 action 可标 `risk: Low|Medium|High`；远程授权凭证可含权限过滤。首版**不实现**，manifest 先输出 risk 字段供用户 Agent 自行判断。

### 5.5 事件订阅（SSE，可选，0.2 再议）

`/appagent/events` 让用户 Agent 获知"应用里发生了什么"。首版不做——轮询 state 端点已够用，SSE 长连接管理成本不小。列为演进项。

---

## 6. 版本化与契约稳定

面向 B 的 manifest 是**对外契约**，必须版本化：

```json
{
  "appagent": "1.0",
  "app": { "name": "小步任务看板", "version": "2.3.1", "vendor": "Xiaoyuvax" },
  "actions": [ { "name": "createTask", "since": "1.0", "risk": "Low", ... } ],
  "state":   [ { "key": "pendingCount", "type": "int", "since": "1.0" } ]
}
```

- `appagent` 协议版本：框架升级新增字段=向后兼容（minor+1）；删改 action 语义=major+1。
- action name 默认 = 方法名（范式判定，§3.2），`[PuppetAction]` 可改名；`since` 字段：action 首次出现的协议版本，用户 Agent 可对老应用降级适配。
- **编译期检查**（后续可加 Analyzer）：`[AgentAction]` name 重复、name 与已删 action 重名等编译报错。

---

## 7. 启用模型：宿主视角的一行开关

### 7.1 启用面向 B（模式 B 内建服务器场景）

```csharp
// 成品应用（如 WinForms 宿主）—— 在 OnShown 或 Main 中：
var appagent = new PuppetWebServer()          // 现有内建服务器
    .UseFormControls()                        // 面向 A 的控件端点（开发调试用）
    .UseAppAgent(o => {                       // ← 新增：启用面向 B
        o.ProductName = "小步任务看板";        // v0.2：本地免交互，凭证写入用户档案（§5.2）
    })
    .Start("127.0.0.1:9090");
```

### 7.2 注入已有服务器（模式 A 场景）

```csharp
// ProcessWebRequest 中（现有注入点之后）：
if (PuppetWebHandler.TryHandle(ref req)) { ... return req.Response; }
if (AppAgentWebHandler.TryHandle(ref req)) { ... return req.Response; }  // ← 新增一行
```

### 7.3 只开 B 不开 A（成品应用最常见）

```csharp
// 不调用 UseFormControls()、不 Register() 开发期实例，只 UseAppAgent()
new PuppetWebServer().UseAppAgent(...).Start("127.0.0.1:9090");
```

> 注意：只开 B 时 `/agent/*` 端点仍然存在（框架内核使然），但**没有任何实例注册**，registry 返回空——天然无暴露面。
> **BlockAgentEndpoints（v0.5.4 定案：显式开关）**：首版带显式开关 `o.BlockAgentEndpoints`（默认 false），
> 置 true 时内建服务器对 `/agent/*` 一律 404。存在理由：防作者习惯残留事故链——成品二进制中遗留
> `SetKey("字面量")` 或历史 `Register()` 时，字符串常量可被反编译提取，任何人（含本机其他用户）
> 持密钥即获全反射控制（钥匙写在二进制里 = 锁焊在门外）。B-only 产品建议开启；文档明示。

### 7.4 未启用 B 时

- 无 `/appagent/*` 路由注册，请求打到 PuppetWebServer 的 404。
- 无任何 consent/manifest/audit 内存分配。
- 框架行为与 1.1.1 版本完全一致。

---

## 8. 内核改造点清单（合二为一的实现路径）

| # | 改造点 | 性质 | 规模 |
|---|--------|------|------|
| 1 | 可选覆盖特性：`PuppetActionAttribute` / `PuppetStateAttribute` / `PuppetParamAttribute` / `PuppetActionGroupAttribute` / `PuppetAppInfoAttribute`（覆盖用途，v0.5 更名并入 Puppet* 家族，§3.2/§3.7） | 新增 | 小 |
| 2 | `ExposurePolicy` 抽象 + `DebugExposurePolicy`（现行 A 规则原样抽出来）+ `ActionizePolicy`（范式判定器，§3.2） | 重构（行为不变）+ 新增 | 中 |
| 3 | `PuppetExtensions.ResolveMethod/ResolveMember/Describe` 按 policy 分派（A 路径零改动；Actionize 提炼仅在宿主源码中实施，不触碰 A 引擎） | 重构 | 中 |
| 4 | `AppAgentManifestBuilder`（范式判定 + 覆盖标注合并 + 三层文案解析 → manifest JSON） | 新增 | 中 |
| 5 | `AppAgentWebHandler`（/appagent/* 端点族） | 新增 | 中 |
| 6 | `AppAgentAccessBoundary`（档案凭证生成/写入/校验；AllowRemote 显式授权留口子） | 新增 | 中 |
| 7 | `AppAgentAudit`（独立审计 logger，用户可读；**二期随 AllowRemote**，MVP 本地无审计，§4 要点 1） | 新增（二期） | 小 |
| 7b | `AppAgentAssetStore` + `PuppetArtifact`（内存产物仓库 + /appagent/assets 下载端点，§3.6） | 新增 | 中 |
| 8 | `PuppetWebServer.UseAppAgent(...)` 扩展 + 模式 A 注入文档 | 新增 | 小 |
| 9 | `/agent/capabilities` 增加 `supportsAppAgent` 能力位 | 新增 | 小 |
| 10 | TestGround.Winform `UseAppAgent` 示例：任务看板 public bool 方法**零标注**即收录，验证范式判定与 UI 文案同源 | 示例 | 小 |
| 11 | DEVELOPMENT.md 新章节「面向 B：Action 体系提炼与重构指南」（流程树归纳、谓词方法重构样式、范式与文案同源规则、验证清单）+ README 双面向章节 + README「用户 Agent 发现技能」段（probe→help→凭证→manifest 四步引导，§4.5） | 文档 | 中 |

**刻意不做**（防臃肿，符合框架"由证据驱动"原则）：
- 不做权限子集过滤（§5.3，0.2）
- 不做 SSE 事件流（§5.4，0.2）
- 不做跨机远程授权（面向 B 先限定本机场景）
- 不做 B 面向的写状态端点（`[AgentState]` 只读；写操作一律走具名 action，保持操作语义在应用代码里）

---

## 9. 合理性与可行性自评（供讨论挑战）

### 9.1 合理性

1. **双门面复用内核**：反射/UI 切换/参数绑定机制同构，差异收敛到"暴露策略 + 鉴权域 + 呈现层"，符合框架已有分层，不引入平行体系。
2. **范式内化面向 B**：与"成品应用"阶段匹配——发布后暴露面冻结（范式与静态表均在源码中），安全可审计；Agent 提炼贴合"Agent 是 Puppet 化实施者"的既有哲学，全量判定消除人工标注缺环风险；与 A 的运行时打标并行不悖。
3. **授权模型本地化**：面向 B 首版限定本机 + 用户显式授权，回避了远程授权/账号体系的巨大复杂度，先服务"用户个人 Agent 代理操作本机应用"这一核心场景。
4. **兼容性**：A 全链路不动；未启用 B 时零行为差异。

### 9.2 可行性风险与对策

| 风险 | 对策 |
|------|------|
| ExposurePolicy 重构触碰 A 面向现有行为 | 先写 SelfTest 锁定 A 现有解析行为（TestGround 已有 SelfTest 机制），重构后跑通再合并 |
| 双面向并存时职责混淆（一个成员既 A 又 B 可见） | 特性语义矩阵（§3.2）+ 编译警告（冲突时 PuppetIgnore 赢） |
| 本机同用户恶意进程读凭证 | 同信任域不设防（与 CLI 同哲学，§5.2 诚实边界）；跨用户/远程由档案 ACL + 强制凭证拦截 |
| 面向 B 被滥用为"通用 RPC" | 端点族只有具名 action，无通用 invoke/set；manifest 是唯一发现入口 |
| Manifest 泄露应用内部信息 | manifest 只含范式内成员与覆盖标注的 name/desc/参数 schema，不含类型全名/程序集信息（与 A 的 describe 刻意相反） |
| 范式判定误收技术性 bool 方法 / 非 .NET 控件文案缺失 | `[PuppetIgnore]` 划界 + `ActionPrefixes` 收紧（§3.2）；文案缺口由 Agent 静态表（L2）承接（§3.3） |
| AOT/Trimming 兼容 | 特性声明反射枚举与现有 Describe 同机制；TrimmerRootAssembly 已配置 |
| 内存产物仓库膨胀 | TTL + 总量上限 + LRU 淘汰（§4 要点 4）；下载强制凭证；超限拒绝而非无界缓存 |

### 9.3 需要用户/作者权衡的点（v1.0：全部闭合）

1. ~~授权流程的产品化程度~~ → v0.2 已定：本地免交互（OS 档案凭证，§5.2），远程留口子不做 UI（§5.3）。
2. ~~动作方法范式的默认判定~~ → 已定（2026-09-21）：无前缀，标准 = 正面形状 + bool 语义锚点 + 负面清单（out 参数/谓词前缀排除），见 §3.2；Do 仅作可选收紧。
3. ~~`/appagent/actions` 的参数传递形态~~ → 已定（2026-09-21）：按名传 + 严格校验（未知参数名/缺必填均 400），见 §4 要点 5。
4. ~~`BlockAgentEndpoints` Paranoid 开关~~ → v0.5.4 已定：显式开关（默认 false），B-only 产品建议开启（§7）。
5. ~~审计日志保留策略~~ → v0.5.4 已定：仅远程启用；本地仅 PuppetUsage 元统计（§4 要点 1/6）。
6. ~~面向 B 是否允许读 `[PuppetState]` 之外的实例信息~~ → v1.0 已定：`[PuppetAppInfo]` 可选具名声明
   （展示名/版本/供应商）进 manifest，不自动收集（§3.7）。由此引出的多 Agent 并发问题已由
   §4 要点 8 两级并发模型承接（v0.5.5）。
7. ~~产物仓库默认值~~ → v0.5.4 已定：TTL 10 分钟 / 256 MB / 100 MB，宿主可配置，未来实践后调整（§4 要点 7）。

---

## 10. 实施路线（确认后分两期）

**第一期（MVP，面向 B 骨架可用）**
- 范式判定器（ActionizePolicy）+ 覆盖特性 + ExposurePolicy 重构（A 行为锁定测试先行）
- Manifest（三层文案解析）+ actions/state/assets 端点 + 档案凭证鉴权（§5.2）+ 内存产物仓库（§3.6）
  + BlockAgentEndpoints 显式开关（v0.5.4；审计已移二期）+ action 实例级强制串行队列与 409 busy（§4 要点 8）
- TestGround.Winform 双面向示例（同进程 A+B 并存演示；任务看板零标注收录验证）
- capabilities 能力位 + 文档

**第二期（按第一期实际使用证据决定）**
- AllowRemote 远程授权 + 远程审计（AppAgentAudit，v0.5.4 移入）、CrossGuardAgentWrites 跨面向硬加固（§4 要点 8）、权限子集、SSE 事件、编译期 Analyzer

### 10.1 第一期实施与验证结果（已完成）

MVP 全部落地：ActionizePolicy 范式判定器、PuppetAction/PuppetState/PuppetParam/PuppetAppInfo 特性族、
OperationResult/PuppetArtifact 契约、probe/help/manifest/actions/state/assets 端点族、
档案凭证（L0 发现 + SweepStale 陈旧清理）、Actionize 内置文案三层解析、实例级强制串行、
内存产物仓库、BlockAgentEndpoints、UseAppAgent/UseAppAgentWithUiText、capabilities.supportsAppAgent。

**TestGround 全场景回归（SelfTest 固化，全部 PASS）**：

| 场景 | 宿主形态 | 验证要点 |
|------|---------|---------|
| Winform | 模式 B，A+B 并存 | L0 档案发现 → probe 最小披露 → 401 凭证分层 → manifest（范式收录/排除、三层文案、L1 原始文案图）→ 按名绑定 400/404 → 409 busy+retryAfterMs → Task<bool> 解包 → 产物下载 → state 直读 → A 通道不受影响 |
| Console（A 模式） | 模式 B，纯 A | 原有 A 面向 SelfTest 全绿（B 未启用零行为差异） |
| Console（B-only） | 模式 B，仅 B + Paranoid | /agent/* 全 404；档案发现；manifest 覆盖收录（Add/Delete/ExportCsv）与排除（List/Summary/Stop）；NL 工作流；CSV 产物下载 → TTL 过期 → 重跑恢复；总量上限确定性拒绝；档案正常退出清理 |
| Wpf | 模式 A，A+B 并存 | 外部无 WebServerBase 宿主经 AppAgentRuntime.Bind 接入；无 WinForms 包时 desc 走 L3 推断；零标注范式收录；动作经 dispatcher marshal 生效于真实 WPF 绑定 |
| AspNetCore | 模式 C，A+B 双服务器 | 独立 PuppetWebServer 上的 /appagent/*；B 凭证与 A 密钥**鉴权域独立**（A 轮换后 B 仍工作、A 密钥打 B 端点 401）；[PuppetAction("reserve")] 自定义契约名；B 动作与 Kestrel API 共享业务状态 |

**验证驱动的三处实现修正**（SelfTest 锁定前发现并修复）：
1. **产物仓库容量守卫**：原实现会把未到期（Agent 尚未下载）的活跃产物 LRU 淘汰，违背 §3.6「先淘汰到期条目、仍超则拒绝」——修正为只淘汰到期条目，存活产物永不静默丢弃，容量不足明确拒绝。
2. **Wima WebRequest.Path 大小写**：框架 Path 为小写化路由视图，action/state 契约名提取改用 PathCaseSensitive（A 面向 method 走查询参数故从未暴露此坑）。
3. **WinForms L1 文案桥键错位**：控件名映射 bug，UI 同源文案取值错误。

**实施与提案的偏差记录**：① 异常式校验宿主（如 ExpenseLedger throw ArgumentException）的 B 错误映射为 `500 action-failed`（err 携带宿主校验消息），Report 式宿主才返回 `ok:false`——两种宿主风格均可接受，文档已注明；② IL 警告（IL2026/IL3050 等）与内核既有 Newtonsoft/反射用法同级共存，AOT 兼容取舍已在 §9.2 记录；③ 模式 A/C 的档案清理由宿主 Stop() 触发（Unbind 内部含档案删除），异常退出靠 SweepStale 兑底。
