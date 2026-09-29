# Agent 边界

- 本目录是独立 WPF 阅读清单宿主；以本应用真实用户需求为先，可复现证据指向共享核心问题时做最小框架修改并重跑受影响的 TestGround，不做投机性新增；仅在用户要求时提交。
- 保持 net10.0-windows、WinExe、UseWPF、IsPackable=false，仅引用 Puppet.Core；不创建 WPF 核心程序集、不加依赖。
- 用户界面和 Agent 共用 ReadingList 的实际操作及验证；ObservableCollection + ICollectionView + INotifyPropertyChanged，无测试专用暴露入口。
- 必须在主窗口 Loaded、主 Dispatcher 的 SynchronizationContext 有效后注册 IPuppet。UI 集合与所有变更保留线程亲和检查。
- 基础设施、绑定对象、事件及编译器后备字段标记 PuppetIgnore；仅用标量观察，不对 Window 做整体状态序列化。
- 密钥仅来自运行时 PUPPET_TESTGROUND_KEY，不打印、不写文件；仅回环 19103；关闭注销、异步停止服务器、刷新密钥，再排队关闭窗口，避免 Closing 重入。
- 代码使用 UTF-8、CRLF，无代码注释。日志不混入业务操作；验证产生的内部日志测试后清理。

## 必跑检查

在本目录执行 `dotnet build TestGround.Wpf.csproj -c Release`，随后 `dotnet bin/Release/net10.0-windows/TestGround.Wpf.dll --self-test`。
分别运行无密钥、环境注入随机密钥两种模式，均应退出 0；自检 60 秒硬超时退出 2，失败退出 1。详细复现命令、已验证结果与分期演进见 DEVELOPMENT.md；仓库未提供独立 lint 命令，编译承担 C#/XAML 类型检查。
