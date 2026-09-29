# Agent 约束

## 目的与原则

- 本目录是独立 WinForms 用户宿主；以本应用真实用户需求为先，可复现证据指向共享核心问题时做最小框架修改并重跑受影响的 TestGround，不做投机性新增；仅在用户要求时提交。
- 保持 net10.0-windows / WinExe / UseWindowsForms / IsPackable=false，仅引用两个现有项目，不加依赖。
- UI 和 Puppet 必须共用实际用户操作；验证编排留在内部 SelfTest，不添加可暴露测试钩子。
- 状态通过标量只读属性观察；不用整个 Form 的 `/agent/state`。基础设施与敏感成员标记 `[PuppetIgnore]`。
- 密钥只从非空白 `PUPPET_TESTGROUND_KEY` 读取，绝不记录；仅回环 19101，Shown 注册，关闭注销并停止服务。
- 所有代码 UTF-8、CRLF，无代码注释。文档只记录实际验证结果。

## 验证与演进

- 构建：`dotnet build TestGround.Winform.csproj -c Release`。
- 验证：`dotnet bin/Release/net10.0-windows/TestGround.Winform.dll --self-test`；退出码必须为 0。
- 需分别运行无密钥与环境注入密钥两种模式；后者增加真实 HTTP 检查。
- 已验证场景、框架警告与未验证项见 DEVELOPMENT.md；持久化、撤销、异步刷新仅在有实际需求后增加。
