# Puppet 自进化机制方案

> 状态：方案定稿（2026-10-11），待实施。实施顺序见 E 节。
> 背景：框架已有 `PuppetUsage` + `/agent/usage` 使用率机制，但实践中从未被使用——死于粒度错位（只有 `endpoint` 与 `invoke::方法名`，无宿主类型维度）、无差集信号（只记「调用过的」，缺「describe 暴露但从未调用」的未用集）、无触发事件（文档写「定期分析」但无人定义何时）。本方案修复三点并补齐两种部署模式的反馈闭环。

## A. 接口二分法（评估边界）

| 性质 | 范围 | 判据 | 处置 |
|---|---|---|---|
| **保守性** | `Puppet.Core.dll` 本体：协议层 `Web/*`、生命周期 `PuppetRegistry/AgentBook/SiteLock/PuppetKeyVault`、标注体系（4特性）、发现 `ActionizePolicy/Describe/*/PuppetCoreCapabilities`、基础件 `PLog/PuppetUsage/AgentContractResolver/IPuppet/OperationResult`、B面 `AppAgent/*`、`PuppetUiActions` 基类 | 程序集+命名空间，机器可判 | 永不进评估；新增基础件时同步登记进本清单 |
| **功能性** | ① 适配层具体能力（`Puppet.Core.WinForms` 的 action、`Puppet.Core.Wpf` 捕获等）② `Puppet.Core\Extentions\` 便利方法（保守清单唯一例外）③ 宿主暴露面（`[PuppetExpose]`/public 业务成员） | 同上 | 进 usage 评估，按判据取舍 |

## B. usage 数据链路

1. **采集**：现有埋点保留；粒度修正——`PuppetExtensions.cs:150` 的 category 改为 `invoke::{类型名}.{方法名}`；UI action 路径补一行同格式 Track（否则 UIA 能力恒为 0）。
2. **持久化**：**不新增存储**。`logs/Puppet_*.log` 已是持久层，每工程独立目录（正确语义：各宿主视为独立工程）；`Reset()` 退役——不清零，累计到足够总数一次性分析；要开新窗口就删日志文件。
3. **聚合**（self-test 结尾自动执行）：glob 本工程 `logs/Puppet_*.log` → 正则取 `[category]` 段聚合 → 长周期累计 counts + error 率；对 describe 全集（活宿主现算）做差集 → unused。
4. **输出**（self-test 报告末尾固定三段）：
   - `framework usage`：累计调用次数 + error 占比（按次数降序）
   - `unused 候选`：describe 有、counts 无（已过滤保守性清单）
   - `host surface`：宿主暴露成员各自调用情况
5. **判据**：零用且代码无引用 → 打 `[PuppetIgnore]` 试运行一个周期 → 仍无人用则删；低用高 error → 修；高用 → 保。

## C. 两种部署模式的闭环

### 源码持有模式（agent 全自主）

- 触发：self-test 报告出现 unused/error 异常，或用户指令「分析使用率」。
- 流程：读报告 → grep 宿主+框架源码交叉验证引用 → 决策 → 改 Puppet 源码 → `dotnet build` → TestGround `--self-test` 验证 → 真实宿主复测。若裁掉/新增基础件，同步更新 A 表保守性清单。
- 落点：DEVELOPMENT.md 新章节「自进化流程」，agent 按文档自主执行，无需人工介入。
- 定位澄清：TestGround 仅为 demo 与验证平台，不作为决策依据；进化主战场是用户真实项目；决策实施后在 TestGround 测试与调整。

### NuGet 包模式（报告交接）

- 宿主 agent 只有权清理宿主暴露面 + 识别重复造轮子（该用框架能力却自写的）。
- 框架反馈 = 宿主 self-test 自动产出**标准化报告**（固定 markdown 骨架，即 B4 三段 + 能力缺口段：宿主 agent 调试中绕道自实现的地方），交源码持有方 agent 吸纳。
- fork 模式同理：原则是源码持有方决定进化，包适用方只能文档反馈。
- 宿主 agent 不改框架行为、不继承框架类——框架缺陷必须回源码改，宿主继承只会分叉出私有变体。

## D. 可发现性（兼容主流 agent 规范）

### SKILL.md（~1 小时）

仓库加一份标准 skill 格式文档——Development.md 精简版 + `/agent/*` 端点表 + `/mcp` 用法 + 安装路径说明（Claude skills / opencode skills 各一行）。用户复制即装，解决 Development.md 陌生问题。

### `/mcp` 端点（~半天）

`PuppetWebHandler` 加 `/mcp` 路由，POST JSON-RPC 2.0 无状态模式（tools-only 场景合法，不需要 SSE/会话）：

- `initialize`：声明支持的协议版本
- `tools/list`：由 describe 生成，实例×方法扁平化，参数 schema 从描述产出
- `tools/call`：转发现有 invoke 路径——鉴权、usage Track、日志全部继承
- Bearer 照旧，客户端配置带 header；同端口 9090，宿主启动即 MCP 可用；核心行为零改动；协议弃用时删一个文件
- 服务器基础：`PuppetWebServer` 基于 Kestrel（`WebServerBase`），现有 request/response 抽象足以支撑无状态 JSON-RPC
- 留白：server→client 通知（长任务进度推送）需 SSE，起步不做，真需要时再评估 Kestrel 抽象是否支持流式响应

## E. 实施顺序（估时）

1. DEVELOPMENT.md 新章节（A 表 + B/C 流程 + 判据）— 20 分钟
2. 埋点粒度修正 — 15 分钟
3. self-test 三段 usage 摘要 — 30 分钟（产出格式即 NuGet 模式标准化报告，一次实现两用）
4. SKILL.md — 1 小时
5. `/mcp` 端点 — 半天

## 附：self-test 执行语义（澄清）

`--self-test` 是**启动参数**，不是运行中事件：`宿主.exe --self-test` → 进程启动 → 跑完自检 → 关闭退出。正常启动（无参数）的宿主完全不受影响——self-test 永远不会在正常会话里触发。它是「验证门」而非「监控器」；usage 长周期统计靠日志被动积累，验证靠 self-test 主动跑，两者互补不冲突。
