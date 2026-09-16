# PuppetCore 项目 Agent 上下文

> 本文件为 AI Agent 提供项目全局上下文，**仅保留不可从代码/目录/DEVELOPMENT.md 推断的硬约束与特殊陷阱**。框架详细文档见 `DEVELOPMENT.md`。

## 必读硬约束（违反即破坏框架设计）

1. **严禁在宿主工程插入探测/临时代码**；一切通过既有宿主代码路径完成（调用用户级现有方法/接口）。
2. **操作类与日志类接口分开管理**：操作类模拟用户流程；日志类客观观测。日志中不与用户共享的部分测试后清理。
3. **暴露新成员无需用户授权**，前提：**不改变宿主原定设计行为或表现**；`[PuppetExpose]` 与 `[PuppetHint]` 可长期保留，无需清理。
4. **所有代码文件必须使用 CRLF 行尾（`\r\n`）** —— 项目配置 `core.autocrlf` 但编辑器需显式保证。
5. **密钥绝不写入日志/文件**；`PuppetKeyVault` 运行时刷新，API Key 仅内存传递。

## 构建与打包（含特定路径）

```bash
# 还原依赖
dotnet restore PuppetCore.slnx

# 构建
dotnet build PuppetCore.slnx -c Release

# 打包（输出到固定发布目录）
dotnet pack PuppetCore.slnx -c Release -o "D:\PUB\Nuget Packages"
```

- 版本号单一源头：`Puppet.Common.targets` 中 `<Version>`
- 目标框架：`net8.0;net9.0;net10.0`（见 targets 文件）

## NuGet 发布流程（固化路径与步骤）

发布前**必须与用户确认版本号**。

```bash
# 1. 确认版本号（读取 targets，与用户对齐）
# 2. 更新 Puppet.Common.targets 版本号
# 3. Pack 到固定目录
dotnet pack -c Release -o "D:\PUB\Nuget Packages"

# 4. 从 push.cmd 读取 API Key（仅内存使用，严禁写入任何文件）
# 5. Push
dotnet nuget push "<PackagePath>.nupkg" --api-key "<KEY>" --source "https://api.nuget.org/v3/index.json" --skip-duplicate
```

- `push.cmd` 位于仓库根目录或用户告知路径，**不在版本控制内**。

## 验证过的特殊陷阱

| 场景 | 陷阱 | 正确做法 |
|------|------|----------|
| PS 5.1 写中文文件 | `Set-Content`/`Out-File` 默认编码非 UTF-8，中文变 `?` | `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))` |
| cmd 多行命令 | 只执行第一行，退出码 0 无报错 | 命令写一行，链式用 `&` 或 `&&`；复杂逻辑写 `.ps1` 再 `-File` |
| `-replace` 正则 | 参数1是正则，`$` 被当分组引用，反引号不转义 | 字面匹配用 `[regex]::Escape`；换行用 `$nl=[char]13+[char]10`；`$` 转义 `$$` |
| `git credential fill` 管道 | `echo xxx&` 尾随空格污染 stdin | `(echo protocol=https& echo host=github.com) \| git credential fill` |
| 无 TTY 交互命令 | `gh auth login`/`winget` 挂起 | 先设 `GIT_TERMINAL_PROMPT=0` `GCM_INTERACTIVE=never` 快速失败 |
| `findstr` 中文 | GBK 控制台失效/乱码 | 用 `Select-String -Encoding utf8`（PS5.1），无 BOM 必须显式指定 |
| `/agent/state` 整对象序列化 | 含 WebView2/Form/事件处理器的对象会跨线程崩溃 | 仅用 `/agent/get?path=<single>` 或深层路径 `a.b.c` |
| `invoke` 中文方法名 | URL 查询参数需编码 | `UrlEncode` 后传 `name`/`method` |
| 外部 Web 服务器 (Kestrel) | 请求对象类型不兼容 `WebRequest` | 必须包装类型后传给 `PuppetWebHandler.TryHandle` |

## 关键文件定位（不可推断）

- `Puppet.Common.targets` — 全局版本、TFM、AOT/Trim 配置
- 各项目 `GlobalUsing.cs` — 全局 `using JSN = Newtonsoft.Json;`
- `D:\PUB\Nuget Packages\push.cmd` — NuGet API Key 存放（不入库）
- `D:\PUB\Nuget Packages` — 固定打包输出目录