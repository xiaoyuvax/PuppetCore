# PuppetCore 项目 Agent 上下文

> 本文件为 AI Agent 提供项目全局上下文，**仅保留不可从代码/目录/DEVELOPMENT.md 推断的硬约束与特殊陷阱**。框架详细文档见 `DEVELOPMENT.md`。Windows/PS 通用陷阱、NuGet/publish 通用流程见全局 AGENTS.md。

## 必读硬约束（违反即破坏框架设计）

1. **优先通过既有宿主代码路径完成测试**（调用用户级现有方法/接口），首先不要编写/暴露专门的调试代码或方法。仅在有确切证据表明既有路径无法完成测试时，才可考虑编写测试专用“钩子”以达成任务；相关成员必须标注 `[PuppetTest]`（纯标记，无功能代码），并在测试完成后适时清理。
2. **操作类与日志类接口分开管理**：操作类模拟用户流程；日志类客观观测。日志中不与用户共享的部分测试后清理。
3. **暴露新成员无需用户授权**，前提：**不改变宿主原定设计行为或表现**；`[PuppetExpose]` 与 `[PuppetHint]` 可长期保留，无需清理。
4. **PuppetCore 是公开框架，文档/注释/代码一律不得提及任何具体宿主项目**（如外部产品名、内部项目代号）——先例、实测与示例只能引用本仓库自身的 `TestGround.*`。仅作者署名（`Xiaoyuvax`）与仓库自带示例应用除外。
5. **所有代码文件必须使用 CRLF 行尾（见全局 AGENTS.md）** —— 项目配置 `core.autocrlf` 但编辑器需显式保证。
6. **密钥绝不写入日志/文件**；`PuppetKeyVault` 运行时刷新，API Key 仅内存传递。

## 构建与打包（含特定路径）

```bash
dotnet restore PuppetCore.slnx
dotnet build PuppetCore.slnx -c Release
dotnet pack PuppetCore.slnx -c Release -o "D:\PUB\Nuget Packages"
```

- 版本号单一源头：`Puppet.Common.targets` 中 `<Version>`
- 目标框架：`net8.0;net9.0;net10.0`（见 targets 文件）
- NuGet API Key 走环境变量 `NUGET_API_KEY`（用户级）；push 见全局 AGENTS.md

## 验证过的特殊陷阱

| 场景 | 陷阱 | 正确做法 |
|------|------|----------|
| `/agent/state` 整对象序列化 | 含 WebView2/Form/事件处理器的对象会跨线程崩溃 | 仅用 `/agent/get?path=<single>` 或深层路径 `a.b.c` |
| `invoke` 中文方法名 | URL 查询参数需编码 | `UrlEncode` 后传 `name`/`method` |
| 外部 Web 服务器 (Kestrel) | 请求对象类型不兼容 `WebRequest` | 必须包装类型后传给 `PuppetWebHandler.TryHandle` |

## 关键文件定位（不可推断）

- `Puppet.Common.targets` — 全局版本、TFM、AOT/Trim 配置
- 各项目 `GlobalUsing.cs` — 全局 `using JSN = Newtonsoft.Json;`
- `Puppet.Core/AppAgent/PuppetUiActions.cs` — UI 基础 action 能力抽象（`IPuppetUiElement`/`IPuppetWindow` + 平台解析器注入点；跨 WinForms/WPF/Web）
- `Puppet.Core.WinForms/WinFormsPuppetUiActions.cs` — WinForms 适配（`UseFormControls()` 自动注入 Form/Control 能力）
- `D:\PUB\Nuget Packages` — 固定打包输出目录
