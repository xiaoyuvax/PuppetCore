# 开发与验证

## 实现边界

- ReadingList.cs：IPuppet 视图模型，ObservableCollection 保存不可变书籍记录，ListCollectionView 筛选；替换记录更新进度，INotifyPropertyChanged 通知派生汇总及选择属性。汇总用 long 累计页数，进度按总页数加权，不取单本百分比平均。
- MainWindow.xaml / .xaml.cs：原生控件与数据绑定，按钮薄转发到用户方法；Loaded 注册捕获 DispatcherSynchronizationContext，Closing 注销后在线程池停止服务，让主 Dispatcher 继续处理排队请求。
- Program.cs / SelfTest.cs：STA 消息循环与内部验证编排，真实窗口加载、实际用户方法和按钮事件、真实 HTTP；没有新增公开测试钩子。60 秒看门狗仅用于自检，异常路径也尝试关闭，超时强制退出仅作防挂起兜底。

## 复现命令（本目录，PowerShell）

```powershell
dotnet build TestGround.Wpf.csproj -c Release --no-incremental
$env:PUPPET_TESTGROUND_KEY = $null
dotnet bin/Release/net10.0-windows/TestGround.Wpf.dll --self-test
$LASTEXITCODE
try {
    $env:PUPPET_TESTGROUND_KEY = [Guid]::NewGuid().ToString('N')
    dotnet bin/Release/net10.0-windows/TestGround.Wpf.dll --self-test
    $LASTEXITCODE
} finally {
    $env:PUPPET_TESTGROUND_KEY = $null
}
```

随机密钥仅存在进程环境和内存，不输出。不要并行执行使用 19103 的检查。依赖默认可能在工作目录创建 logs；测试后删除本目录内的测试日志，不删除其他项目日志。

## 实际验证（2026-09-17，Windows，SDK 10.0.401）

- 完整 Release 构建退出 0、0 错误；Puppet.Core 产生 103 条既有 XML 文档、Trim/AOT 分析警告，WPF 项目无编译警告；未修改核心库来消除警告。
- 无密钥自检退出 0：空状态、书名空白/长度/控制字符/重复，总页数边界及溢出，已读页数边界，纠错回退，选择边界，筛选隐藏选择，完成与反向切换，加权汇总及清空。
- 真实 TextBox、Button、ComboBox、ListBox 绑定与事件检查通过：添加清空输入、自动选择、非法进度反馈、更新、筛选、删除、按钮可用状态。
- 随机运行时密钥自检退出 0：未认证 404，注册表、共享五个用户方法、非法进度返回 false、标量查询、PuppetIgnore 基础设施不可读、HTTP 操作触发主 Dispatcher 属性通知并更新实际 WPF 控件。
- 两种模式均正常退出并验证注销；服务模式验证 19103 不再监听。修复并回归了无服务器时 Closing 同步重入 Close 的问题。
- 服务依赖提示 wwwroot 不存在、无 TLS 无法启用 HTTP/2/3；实测 HTTP/1.1 Agent 路径通过，不为这些提示创建静态目录或更改核心配置。

未验证：人工视觉审阅、屏幕阅读器与高对比度、多 DPI/显示器、端口冲突恢复、持续并发关闭压力。原生控件保留焦点行为，Label.Target、访问键、自动化名称及状态 LiveSetting 已声明；不把自动化事件测试等同人工无障碍认证。依赖当前工作区已有 PuppetRegistry / PuppetExtensions 的 UI fallback 实现，不主张在旧版核心上已验证。

## 分阶段演进（按实际需求）

1. 当前：会话内单窗口清单，先补人工键盘、缩放、读屏回归；无数据库、命令框架、封面网络请求或后台同步。
2. 需要跨会话保留时：原子 JSON 保存/加载、格式版本和损坏恢复；先补真实用户路径回归，再考虑撤销删除。
3. 书架明显变大时：稳定书籍 ID、文本搜索、针对性属性通知；有测量证据后优化当前线性汇总/筛选。
4. 仅在出现多个真实 WPF 宿主复用需求时讨论共享集成；本目录不提前创建 WPF 核心程序集。
