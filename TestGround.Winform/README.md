# TestGround.Winform

“小步”是 PuppetCore 的原生桌面任务看板试验场，不是框架测试后门。

## 使用

需要 Windows 与 .NET 10 SDK；在本目录执行：

```powershell
dotnet build TestGround.Winform.csproj -c Release
dotnet run --project TestGround.Winform.csproj -c Release --no-build
```

添加 1–120 个 UTF-16 字符的单行标题，自动去首尾空白、拒绝同名（忽略大小写）；可为新任务选择优先级（普通/高），高优先级待办在列表中加粗红字显示。选择后可完成、删除、重命名（输入框内容作为新标题）、切换优先级（仅待办），或一键清除全部已完成任务，支持全部/待办/已完成筛选。添加会切回全部并选中新任务；计数栏显示待办中的高优数量。Enter 添加，Alt+A/C/D/R/P/E 操作按钮，Tab 导航。任务仅存内存，关闭即丢失，删除与清除无撤销。

## Puppet

仅在启动进程的 `PUPPET_TESTGROUND_KEY` 非空白时开启 `127.0.0.1:19101`；未提供时看板照常工作，不监听。请通过可信启动器注入密钥，不写源码、文件、命令历史或日志；窗体读取后移除进程环境变量。实例名 `TaskBoard`，请求使用 Authorization Bearer 头。端口冲突时界面提示启动失败，本地操作仍可使用。

使用 `/agent/invoke?name=TaskBoard&method=AddTask`，JSON 数组正文传参；共享操作为 `AddTask(title, priority)`（priority 可选：普通/高）、`SelectTask(index)`（当前可见列表的零基下标）、`CompleteSelectedTask()`、`RemoveSelectedTask()`、`RenameSelectedTask(title)`、`ToggleSelectedPriority()`、`ClearCompletedTasks()`（返回清除数量）、`SetFilter(filter)`（全部/待办/已完成）。返回 bool（ClearCompletedTasks 返回 int），错误说明见 `StatusText`。

观察用 `/agent/get?name=TaskBoard&path=TotalCount` 等单属性；支持 `PendingCount`、`CompletedCount`、`HighPriorityPendingCount`、`VisibleCount`、`SelectedTitle`、`SelectedCompleted`、`SelectedPriority`、`SelectedCreatedAt`、`CurrentFilter`、`StatusText`、`AgentStatus`。不要序列化整个 Form。`/agent/control` 支持命名控件（含 TaskPriority、RenameButton、PriorityButton、ClearDoneButton）；ListBox 选择使用共享 `SelectTask`，现有框架未实现 ListBox select 控件动作。

## 验证

```powershell
dotnet bin/Release/net10.0-windows/TestGround.Winform.dll --self-test
```

退出码 0 为通过；通过 dotnet 启动可看到输出。无密钥运行验证本地操作；有密钥时还验证真实 HTTP。测试会显示窗体、执行用户操作并自动关闭，请勿同时操作。详细已验证范围及待办见 DEVELOPMENT.md。
