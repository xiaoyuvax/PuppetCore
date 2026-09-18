# 开发与验证

## 结构与原则

- `TaskBoardForm.cs`：原生布局、内存任务状态、共用用户方法、显式 IPuppet、服务生命周期。
- `Program.cs`：STA 消息循环与 `--self-test` 编排入口；测试完成后检查注销和监听端口释放。
- `SelfTest.cs`：内部验证编排，通过真实用户方法、原生控件和既有 HTTP API 操作；不是注册实例，也没有暴露的测试成员。
- 日志与操作分开：用户反馈在 StatusText，ILog 由 Common.Logging.LogManager 创建。未承诺该 logger 自动出现在 Wima LogBook；框架自身仍会产生使用日志。
- 关闭时先注销，再后台停止服务器，避免 UI 线程等待正在回调 UI 的 HTTP 请求。无持久化、无额外刷新线程。

## 已验证（2026-09-17，Windows / SDK 10.0.401）

命令在本目录执行：

```powershell
dotnet build TestGround.Winform.csproj -c Release
dotnet bin/Release/net10.0-windows/TestGround.Winform.dll --self-test
```

- Release 构建：0 警告、0 错误。
- 无密钥自检通过：UI 句柄与注册时机、空列表、空白/null/超长/控制字符标题、非法优先级、120 字符边界、去空白、大小写重复校验。
- 用户操作通过：非法索引/筛选、添加（含高优先级）/选中/完成/重复完成/删除、重命名（trim、同名、与原相同）、优先级切换往返、清除已完成（含空表 0 返回）、计数（含高优计数）、待办与已完成筛选、隐藏选择清除、筛选中添加自动展示、创建时间戳观测。
- 真实原生控件通过：文本输入、优先级下拉（添加后复位）、添加/完成/删除/重命名/优先级/清除已完成按钮、ComboBox 筛选事件、ListBox 选择事件、按钮启用状态。
- 临时随机密钥仅内存注入后自检通过：未鉴权返回 404、HTTP registry、UseFormControls 控件树、invoke AddTask(title, "高") 跨线程回 UI、get TotalCount 与 SelectedCreatedAt、control 完成按钮。
- 关闭后注册表不再包含 TaskBoard，19101 无活动监听；自检退出码 0。
- 2026-09-17 增补：优先级（记录+切换+OwnerDraw 高亮）、重命名、清除已完成、SelectedCreatedAt/SelectedPriority/HighPriorityPendingCount 观测属性；自检同步覆盖（无密钥模式与含密钥模式均退出 0）。修复过程：SelfTest 断言按新交互语义修正（添加后自动选中新任务），业务代码未因此改动。

复现含 HTTP 的验证（仅生成一次性测试密钥，不打印、不写文件）：

```powershell
$env:PUPPET_TESTGROUND_KEY = [Guid]::NewGuid().ToString('N')
try {
    dotnet bin/Release/net10.0-windows/TestGround.Winform.dll --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed' }
} finally {
    $env:PUPPET_TESTGROUND_KEY = $null
}
```

## 限制与演进清单

- 任务只存内存，删除无撤销；有需求再加本地持久化与撤销，不预建仓储层。
- 当前无异步样例刷新；有真实数据源再加取消、失败提示及就绪状态。
- 尚未人工验收多 DPI、屏幕阅读器、高对比度和极端缩放；当前为原生控件、命名、AccessibleName、快捷键、最小尺寸及伸缩布局。
- 尚未验证端口占用、Windows 注销、异常强制退出、并发外部调用关闭等边界。
- 现有服务器输出 wwwroot 不存在与 HTTP/2、HTTP/3 无 TLS 的警告；已验证仍以 HTTP/1.1 正常完成回环请求，未创建无用静态目录或更改核心代码。
- 框架日志生成在运行工作目录的 logs；请从本目录运行。不要读取/分享包含潜在敏感业务信息的完整日志。
- 未找到独立 lint/typecheck 命令；当前使用 dotnet build 的编译器检查，无额外测试依赖。
