# Agent 约束

本目录是独立 ASP.NET Core 库存/预留工作台，用真实浏览器、普通 HTTP 与 Puppet 路径验证共享业务，不是核心库或调试接口集合。

- 以本应用真实用户需求为先，可复现证据指向共享核心问题时做最小框架修改并重跑受影响的 TestGround，不做投机性新增；仅在用户要求时提交。保持 Microsoft.NET.Sdk.Web、net10.0、IsPackable=false，仅 ProjectReference Puppet.Core，不新增依赖。
- Inventory 是唯一业务状态源，注册实例与 DI 单例必须相同。创建、预留、释放的验证在业务方法内；锁内原子变更和一致不可变快照不可省略，不能超卖或重复归还数量。
- 模式 C：应用 127.0.0.1:19204，独立既有 PuppetWebServer 127.0.0.1:19104；不注册服务器、应用、容器、请求或日志对象。未来同端口必须先适配请求类型，不复制核心协议。
- IPuppet 显式实现；安全业务方法显式标记 PuppetExpose，基础设施标记 PuppetIgnore。不得暴露锁、可变字典、密钥或测试专用钩子。SelfTest 仅编排正常用户方法和真实 HTTP。
- PUPPET_TESTGROUND_KEY 仅运行时注入，读取后清除进程环境；每次鉴权读取 PuppetKeyVault.GlobalKey，支持轮换。普通写路由同样必须认证。密钥不进入 URL、日志、配置或任何文件；浏览器只用密码输入和内存变量，不用 storage/cookie。诊断只记录阶段、异常类型和源码行号，不打印请求、响应或异常对象。
- 所有代码及文档 UTF-8、CRLF，代码无注释。保持原生 HTML/form/fetch，不引入前端构建链。
- 所有退出路径在 finally 注销、关闭双服务器、刷新密钥。不得通过测试专用远程 Stop 方法绕过正常生命周期。

## 必跑验证

本目录 `dotnet build TestGround.AspNetCore.csproj -c Release`，用运行时随机环境密钥执行 `dotnet bin/Release/net10.0/TestGround.AspNetCore.dll --self-test`，退出必须为 0。无密钥普通启动本地可用（Agent 端点禁用），无密钥与非法短密钥的 `--self-test` 必须退出 1，未知参数退出 2。自检需覆盖普通写路由认证、输入边界、共享状态、并发不超卖/不重复释放、Puppet 隐藏基础设施和密钥轮换。

未发现独立 lint/typecheck 命令；Release 编译承担 C# 类型检查。后续维护者提供额外命令时应补记并运行。实际证据、限制和未来任务记入 DEVELOPMENT.md，不将计划或静态检查写成已运行的浏览器测试。
