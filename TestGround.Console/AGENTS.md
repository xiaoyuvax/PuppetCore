# Agent 约束

本目录是独立 Console 支出账本宿主，目的是验证真实用户操作与 Puppet 调用的一致性，不是新的核心库或测试钩子集合。

- 以本应用真实用户需求为先，可复现证据指向共享核心问题时做最小框架修改并重跑受影响的 TestGround，不做投机性新增；仅在用户要求时提交。保持 net10.0、Exe、IsPackable=false，仅 ProjectReference Puppet.Core。
- 旧 Puppet.Core.Console 的诊断扩展已移入核心，使用 Puppet.Core.Extentions 命名空间，不保留旧项目、包或命名空间；按实际需要使用，不提前复制实现。
- CLI 与 Agent 共用 ExpenseLedger 的 Add/List/Summary/Delete/Stop；验证放在业务方法，不写测试专用暴露入口。SelfTest 仅编排真实调用。
- decimal 金额、输入上限、锁内变更、不可变快照不可省略。日志只观察，不作为操作入口。不得暴露可变集合、锁、取消源、日志对象或密钥。
- 密钥仅通过运行时 PUPPET_TESTGROUND_KEY 注入，读取后清除当前进程环境副本；不记录、不落盘、不放 URL。IPuppet 每次读取 PuppetKeyVault.GlobalKey，支持核心轮换。无密钥仅本地模式，--serve/--self-test 缺密钥必须失败。
- 固定回环 19102；启用 Agent 才注册。所有退出路径注销、停止服务器、刷新密钥。不要注册服务器本身。
- 代码及文档 UTF-8、CRLF，代码无注释。测试后清理本目录测试产生的底层日志，不删除其他宿主数据。

## 必跑检查

在本目录执行 `dotnet build TestGround.Console.csproj -c Release`，运行时生成环境密钥后执行 `dotnet bin/Release/net10.0/TestGround.Console.dll --self-test`，必须退出 0。无密钥自检预期退出 2，不是跳过 HTTP 后假通过。

目前无仓库提供的独立 lint/typecheck 命令；构建承担 C# 类型检查。若维护者提供额外命令，将其记录于此并执行。实际结果、复现方式、未验证项及分期 backlog 见 DEVELOPMENT.md，不把计划写成已完成。
