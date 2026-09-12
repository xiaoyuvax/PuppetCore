# PuppetCore 项目 Agent 上下文

> 本文件为 AI Agent 提供项目全局上下文，按需阅读。框架详细文档见 `DEVELOPMENT.md`。

## 项目概述

Puppet.Core：为任意 .NET 类型（含 WinForms 窗体）提供 AI Agent 可操控 WebApi 接口的框架。通过反射自动生成能力描述，支持运行时状态查询与方法调用。**这是 agent 的工具，不是给开发者用的库。**

## 技术栈

| 层面 | 技术 |
|------|------|
| 框架 | .NET 10.0 / C# 12.0 |
| 序列化 | Newtonsoft.Json |
| 日志 | Wima.Log（WimaLogger） |
| Web | Wima.Web.Engine（WebServerBase） |
| 反射/工具 | Wima.Core |
| 构建 | MSBuild / .NET SDK |

## 构建命令

```bash
# 还原依赖
dotnet restore PuppetCore.slnx

# 构建
dotnet build PuppetCore.slnx -c Release

# 打包
dotnet pack PuppetCore.slnx -c Release -o ./publish
```

## 项目结构

```
PuppetCore/
├── Puppet.Core/              # 通用核心（接口、序列化、描述、注册表、Web 端点、内建 Web 服务器、使用日志）
├── Puppet.Core.WinForms/     # WinForms 专用（控件树遍历、Click/Text/Select 控件操作）
├── Puppet.Core.Console/      # Console 专用（进程状态、stdin/stdout）
├── Puppet.Common.targets     # 全局 MSBuild 设置
├── PuppetCore.slnx           # 解决方案
├── DEVELOPMENT.md            # Agent 开发指南（必读）
├── README.md                 # 中英文双语说明
└── LICENSE                   # MIT
```

## 代码规范

- 项目 SDK：`Microsoft.NET.Sdk`
- 全局隐式导入：`enable`（ImplicitUsings）
- 全局不可变全球化：`true`
- AOT 兼容：`IsAotCompatible=true`，`EnableTrimAnalyzer=true`
- 全局 using：各项目 `GlobalUsing.cs`（`JSN = Newtonsoft.Json`）
- 行尾风格：**Windows 风格（CRLF `\r\n`）**，所有代码文件必须使用 CRLF 行尾

## 版本管理

- 全局版本号：`Puppet.Common.targets` 中 `<Version>1.0.0</Version>`
- 作者：`Xiaoyuvax`

## Agent 使用必读

使用本框架调试宿主工程前，**先通读 `DEVELOPMENT.md`**。要点：

- 集成模式 A/B/C；端点 `/agent/registry` `/agent/get` `/agent/set` `/agent/invoke` 等
- 严禁在宿主工程插入探测/临时代码；只调用宿主已有的用户级代码
- 操作类与日志类接口分开管理，暴露新接口前先取得用户授权

## NuGet 发布流程

发布 NuGet 包前必须先**与用户确认版本号**，再执行 pack 和 push。

### 版本号确认

1. 读取 `Puppet.Common.targets` 中当前 `<Version>`
2. 向用户确认新版本号（升版策略由用户决定）
3. 更新 targets 中的版本号

### API Key 获取

API Key 存放在 `D:\PUB\Nuget Packages\push.cmd` 文件中，通过 `set "API_KEY=..."` 行读取。

**禁止将 API Key 写入日志、AGENTS.md 或任何会被版本控制的文件。**

### 发布步骤

```bash
# 1. 确认版本号（必须与用户确认）
# 2. 更新 targets 版本号
# 3. Pack
dotnet pack -c Release -o "D:\PUB\Nuget Packages"

# 4. 从 push.cmd 中读取 API Key（仅在内存中使用）
# 5. Push
dotnet nuget push "<PackagePath>.nupkg" --api-key "<KEY>" --source "https://api.nuget.org/v3/index.json" --skip-duplicate
```

## 版本控制

- **Git**：本仓库自身即 Git 仓库，日常开发直接提交
- 行尾提交时的 LF→CRLF 警告属正常现象（core.autocrlf）

## PowerShell / cmd 避坑指南（Agent 必读）

### 编码（最高频踩坑）

**PS 5.1 写文件会毁掉中文（血泪教训）**：`Set-Content` / `Out-File` 默认编码非 UTF-8，管道经过控制台编码再写出，非 ASCII 字符被替换为 `?`。

```powershell
# ✅ 唯一可靠写法（无 BOM）
[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
# ✅ 需要 BOM
[System.IO.File]::WriteAllText($path, $text, [System.Text.Encoding]::UTF8)
```

**含中文的代码文件改一行两行，优先用编辑工具，不要用 PowerShell 正则替换写回。**

### cmd.exe 逐行截断

多行命令只执行第一行，退出码 0，无报错。**命令必须写在一行**，链式用 `&`（顺序执行）或 `&&`（成功才继续）。复杂逻辑写 `.ps1` 文件再 `-File` 执行，**不要**往 `-Command` 里塞复杂脚本。

### `-replace` 三个暗坑

1. 第一参数是**正则**：`-replace 'a.b'` 里 `.` 匹配任意字符；按字面匹配用 `[regex]::Escape($s)`
2. 替换文本里反引号（`` `n ``）**不会**被解释成换行；要插换行先构造：`$nl = [char]13 + [char]10`
3. **替换文本含 `$` 会当成分组引用**：`-replace 'x', '$new'` 中 `$new` 不会展开

### git credential fill 管道传参

cmd 的 `echo xxx&` 尾随空格会混入 stdin，host 变成 `https ://github.com `。正确写法：

```cmd
(echo protocol=https& echo host=github.com) | git credential fill
```

`&` 前必须紧跟内容、不留空格。或用 PowerShell 数组逐行进管道：

```powershell
'protocol=https','host=github.com','' | git credential fill
```

### 交互式命令在无 TTY 环境

`gh auth login`、`winget install` 在无 TTY 环境会挂起。先设 `GIT_TERMINAL_PROMPT=0`、`GCM_INTERACTIVE=never` 让它快速失败。

### Select-String / findstr 中文匹配

`findstr` 的中文参数在 GBK 控制台下会失效或乱码。对 UTF-8 文件用 `Select-String -Encoding utf8`（PS5.1）；无 BOM 时必须显式 `-Encoding utf8`。

## WinForms 布局原则（避坑经验）

> 本框架的 WinForms 扩展与宿主调试涉及 UI 操作，以下经验通用。

### 控件叠压顺序（Z-Order）

**原则：Label 等静态文本控件必须最先添加到 Controls 集合（最底层 Z-Index），输入控件（TextBox/ComboBox/PropertyGrid 等）后添加（顶层）。**

```csharp
// ❌ 错误：Label 后加，会遮挡输入控件
Controls.Add(txtPath);
Controls.Add(lblPath);

// ✅ 正确：Label 先加，输入控件后加
Controls.Add(lblPath);      // 底层
Controls.Add(txtPath);      // 顶层
```

> 原因：WinForms `Controls.Add()` 追加到集合末尾 = 最高 Z-Index。DPI 缩放、Anchor 布局重排时，底层 Label 可能渲染在输入控件之上导致无法点击/编辑。

### Dock/Fill 与 AutoSize 冲突

- `Dock = Fill` 的控件**不能**同时设 `AutoSize = true`，会导致布局失效或宽度异常（如 ComboBox 宽度收缩到 0）。
- ComboBox 下拉列表需配合 `DropDownWidth` 或 `MinimumSize` 保证可读性。

### ToolStrip 遮挡 Dock=Fill 控件

**ToolStrip 必须先添加到 Controls（或放在专用 Panel 顶部），Dock=Fill 的内容控件后添加。**

```csharp
// 容器 Panel
pnlRight.Controls.Add(toolStrip);     // 先加 → 顶部占位
pnlRight.Controls.Add(contentCtrl);   // 后加 → Dock=Fill 剩余空间
contentCtrl.Dock = DockStyle.Fill;
```

> 若 `contentCtrl` 先加再加 `toolStrip`，ToolStrip 会覆盖内容控件顶部区域。

### SplitContainer SplitterDistance 设置

设置 `SplitterDistance` 前必须检查 `Panel1MinSize` / `Panel2MinSize`，超出范围会抛异常：

```csharp
var min = sc.Panel1MinSize;
var max = (sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height)
          - sc.SplitterWidth
          - (sc.Orientation == Orientation.Vertical ? sc.Panel1MinSize : sc.Panel2MinSize);
var dist = Math.Clamp(target, min, max);
sc.SplitterDistance = dist;
```
