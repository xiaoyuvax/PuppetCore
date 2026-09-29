# 开发与验证

## 文件职责

| 文件 | 职责 |
| --- | --- |
| TestGround.Console.csproj | 独立 net10.0 Exe，不打包，仅引用核心 |
| ExpenseLedger.cs | 业务验证、并发安全变更、不可变明细与汇总、真实停止命令 |
| Commands.cs | 固定 CLI 语法、InvariantCulture 金额解析、有界行读取、JSON 输出 |
| Program.cs | 启动选项、运行时密钥、回环服务器、注册/注销、EOF 与关闭流程 |
| SelfTest.cs | 内部自检编排，通过真实用户方法、命令分发器和实际 HTTP 请求断言 |
| README.md | 面向使用者的命令、接口与限制 |
| AGENTS.md | 后续 Agent 必须遵守的修改边界与检查要求 |

不导入根 targets，避免继承多目标框架和库打包设置；核心仍按现有项目配置构建。未更改解决方案，必须直接指定项目构建。未添加 NuGet 包；ImmutableArray、HTTP 客户端和 JSON CLI 输出使用 .NET 自带能力，日志与 Web API 使用现有核心传递依赖。

## 实现边界

业务变更由一个实例级锁保护。List 在锁内复制为 ImmutableArray，元素只读；Summary 从一次快照计算总额和分类合计，因此计数、总额、分类来自同一时点。删除是 O(n)，复制快照是 O(n)，最多 10000 条，不额外引入数据库、缓存或索引。不同调用的快照时点可以不同。

Stop 是终端 quit、Ctrl+C、HTTP 共同的真实生命周期方法，不在请求处理线程直接停止 Web 服务器；主程序收到取消信号后在 finally 中注销、停止并轮换密钥。阻塞的控制台读取在线程池执行，取消等待后进程可以结束；不为输入线程创建测试接口。

宿主关闭 PuppetUsage 明细日志，AgentLog 使用 NoOpLogger，服务器运行日志替换为关闭模式；核心构造阶段与传递依赖仍有生命周期输出/日志文件，不能宣称整个依赖树完全无日志。宿主不记录密钥、请求头、业务参数或异常对象。密钥不属于账本状态；状态、描述和属性读取检查不泄露它。不要把任何凭据当作账本备注输入。

HTTP body 上限在现有 UseHandler 入口检查 Content-Length，拒绝 chunked 及无长度 POST；底层服务器可能在回调前已经缓冲请求，因此这不是网络层抗大流量资源保证。仅供可信本机集成，不支持公网部署、多租户、TLS、授权角色或持久化审计。密钥刷新响应只在客户端内存处理；环境清除不等于托管字符串物理擦除。

## 可复现检查

在本目录运行 PowerShell，随机密钥只进入环境和进程内存：

```powershell
dotnet build TestGround.Console.csproj -c Release
$env:PUPPET_TESTGROUND_KEY = [Guid]::NewGuid().ToString('N')
try {
    dotnet bin/Release/net10.0/TestGround.Console.dll --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed' }
} finally {
    $env:PUPPET_TESTGROUND_KEY = $null
}
```

自检总等待上限 60 秒，HTTP 单请求 5 秒，服务器关闭等待 10 秒；失败返回 1。断言失败仅输出源码行号/检查阶段，不打印响应、密钥或参数。没有可暴露的测试专用成员。

本地无密钥 smoke：

```powershell
$env:PUPPET_TESTGROUND_KEY = $null
"add 1.25 food lunch`nsummary`ndelete 1`nlist" | dotnet bin/Release/net10.0/TestGround.Console.dll
dotnet bin/Release/net10.0/TestGround.Console.dll --help
dotnet bin/Release/net10.0/TestGround.Console.dll --self-test
```

最后一条应返回 2，前两条返回 0。端口不能被其他实例占用；--self-test 会操作本次新建的空账本并轮换本进程密钥，不能附着到生产账本。

## 已实际验证（2026-09-17，Windows / .NET 10）

- Release 构建通过：0 warnings、0 errors；修复初次编译时 Console 命名空间/别名冲突后重新构建并自检通过。
- --self-test 完整通过，退出 0：CLI 添加/列表/汇总/删除/帮助/quit，fr-FR 当前区域下不变金额语法，decimal 精确合计，空汇总，ID 不复用。
- 无效金额、类别、备注、ID、参数数量和超长命令被拒绝且不改变记录；类别/备注最大长度、最大金额可接受。
- 行读取覆盖 CRLF/LF/CR、无末尾换行 EOF、512 字符边界、超长行丢弃后下一条恢复。
- 并发 200 次新增/快照/汇总、并发删除、唯一 ID、历史快照稳定；10000 条容量满拒绝、删除后可再次添加，最大总额无溢出。
- 真实 HTTP：ping、未认证/错误密钥 404、registry、describe、Add/List/Summary/Delete 与本地共享状态、无效业务输入、4097 字节 body 返回 413、密钥属性不可读取、状态与描述不包含密钥。
- 真实 HTTP 密钥轮换：旧密钥失效、新密钥 Stop 成功；停止后写入拒绝、finally 注销完成。
- 独立进程 --serve：重定向 stdin 后立即关闭输入，进程仍存活；HTTP Add/Stop 成功，15 秒内退出 0，捕获输出不含注入密钥，关闭后可重新绑定 19102。
- 无密钥管道 CLI 和 EOF 正常退出 0；--help 退出 0；缺密钥 --self-test、未知启动参数退出 2。

底层 Kestrel 提示无 TLS 时禁用 HTTP/2、HTTP/3，实测 HTTP/1.1 正常。未手工验证真实终端 Ctrl+C、中文键盘编辑体验、跨平台运行、端口冲突恢复、持续压力或 chunked 请求。未做 AOT/Trim 发布验证。无独立 lint 命令，Release 编译作为当前类型检查。

## 后续增量 backlog

1. 先补实际终端 Ctrl+C、中文输入和异常断管场景的进程级回归；需要时再扩展自检，不添加暴露钩子。
2. 核心 Console 扩展迁移完成后确认程序集与 Puppet.Core.Console 命名空间，仅在实际需要进程观测时采用，不提前引用旧项目。
3. 确有跨会话需求才加用户明确选择的持久化；先定义数据格式、原子写入、失败不丢数据和迁移测试，再考虑导入导出。
4. 确有账目管理需求再增日期筛选、预算或撤销；继续共用真实方法，每次功能附最小可运行检查。
5. 只有测量证明 10000 条上限/快照复制/线性删除不足时才增加分页或索引；网络部署需求必须另行评估服务器层 body 限制、TLS 与权限边界，不能直接改为通配地址。
