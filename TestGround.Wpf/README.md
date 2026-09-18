# 页间 · WPF 阅读清单

独立 Windows 桌面宿主：记录书名、总页数及已读页数，按阅读状态筛选，查看单本进度与全书架按页数加权的汇总。浅色双栏原生 XAML，支持窗口缩放、键盘访问键与辅助功能名称。

## 运行

需要 Windows 与 .NET 10 SDK（运行已构建程序需 Windows Desktop Runtime）。在本目录执行：

```powershell
dotnet build TestGround.Wpf.csproj -c Release
dotnet bin/Release/net10.0-windows/TestGround.Wpf.dll
```

书名去除首尾空白后为 1–120 字，不允许控制字符或忽略大小写的重名；总页数为 1–100000 整数，已读页数为 0–总页数，可调低以纠错。新增自动切回全部并选中；筛选隐藏所选书时清空选择。数据仅存内存，关闭即丢失，移除不提供撤销。

## Puppet

由启动进程的环境安全注入非空白 `PUPPET_TESTGROUND_KEY`；不要写入配置、脚本或日志。Loaded 在主 Dispatcher 注册 `ReadingList`，仅监听 `127.0.0.1:19103`。未给密钥不启动服务器；端口冲突时本地书架仍可用。读取环境后清除进程内该变量，访问密钥动态读取 PuppetKeyVault；退出注销、停止服务并刷新密钥。

Agent 使用 Bearer 鉴权调用 `/agent/invoke?name=ReadingList&method=AddBook`，JSON 数组参数例如 `["小王子","100"]`。其余用户操作为 `SelectBook(index)`（当前可见列表零基索引）、`UpdateProgress(readPages)`、`SetFilter(filter)`、`RemoveSelectedBook()`；页数参数为字符串，与 UI 输入共用验证。筛选值为“全部 / 未读完 / 已读完”。观察使用 `/agent/get?name=ReadingList&path=SelectedReadPages` 等标量属性，不序列化整个 Window，不提供 `/agent/control`。

运行自检与实际结果见 DEVELOPMENT.md；约束见 AGENTS.md。项目未加入解决方案，不打包，无新增直接 NuGet 依赖，只引用 Puppet.Core。
