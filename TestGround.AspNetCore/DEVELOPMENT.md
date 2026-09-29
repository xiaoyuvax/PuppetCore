# 开发说明与验证证据

## 目的与结构

最小但有真实业务意义的内存库存/预留服务，验证 ASP.NET Core 原生请求路径与 Puppet 调用的行为一致性。

| 文件 | 职责 |
| --- | --- |
| TestGround.AspNetCore.csproj | Web SDK、net10.0、不可打包、核心引用、嵌入 HTML |
| Inventory.cs | 有界输入、原子库存变更、活动预留和不可变快照 |
| Program.cs | DI 单例、普通 API/鉴权/Problem Details、模式 C、生命周期 |
| index.html | 原生表单、响应式样式、fetch、内存密钥和安全文本渲染 |
| SelfTest.cs | 正常业务与真实 HTTP 的可运行断言，无暴露钩子 |

不导入根 Puppet.Common.targets，不修改解决方案；构建必须直接指定此项目。HTML 嵌入程序集，启动不依赖当前目录的静态资源路径。只使用平台自带能力与核心已有传递依赖。

## 业务原则

创建库存是一次性创建唯一 SKU，不隐式补货。Total 固定，Reserved 为活动预留数量之和，Available = Total - Reserved。Reserve 的查询、余额判断、预留插入和库存更新在同一实例锁内；Release 在同一锁内移除活动预留并归还单位。重复释放返回不存在，不二次增加库存。

Snapshot 在同一锁内复制库存和预留为 ImmutableArray；元素为不可变 record，客户端 with 产生新值而不改变服务。上限为 1000 SKU 和 10000 活动预留；单锁串行化写入，快照排序 O(S log S)、复制 O(S+R)，这是有界本机工作台的明确性能限制。只有实测不足时才考虑分页或细粒度锁，不提前增加抽象。

## 传输与安全边界

模式 C：既有 PuppetWebServer 19104 与应用 Kestrel 19204 均固定 IPv4 回环，共享同一 Inventory 实例。应用不挂载 /agent，浏览器仅访问同源普通 API，不需要跨域或适配器。

普通 API 所有非 GET/HEAD 请求在绑定 body 前验证 Bearer 密钥，使用当前 PuppetKeyVault.GlobalKey；匿名可读库存。未启用 CORS、cookie 或持久会话，前端通过 Authorization 头写入。页面设置 no-store、nosniff、no-referrer、frame-ancestors none；所有业务字符串使用 textContent/Option 渲染，不拼接业务 HTML。单文件页面为内联脚本/样式允许 CSP unsafe-inline，不能宣称严格 nonce CSP。

业务输入错误不回显原始输入；默认空错误响应补充 Problem Details。Kestrel body 上限 4096 字节；Puppet 在既有 UseHandler 检查 Content-Length，拒绝 chunked 和无长度 POST。后者可能发生在底层缓冲之后，不等同于服务器层流量防护。Puppet 保留核心 404 鉴权隐藏和 ok/result 协议。

PuppetUsage.Enabled=false，IPuppet.AgentLog 为 NoOpLogger，服务器 LogMan 关闭输出，应用 ClearProviders；底层构造和 Kestrel 仍产生生命周期输出。宿主不记录密钥/请求体/请求头/完整异常，诊断仅异常类型与自检源码行号。环境清除及托管字符串释放不等于物理擦除内存，浏览器密码管理器是用户自行控制的外部功能。普通只读 API 对其他本机进程可见；此项目不是多用户权限隔离或公网安全方案。

正常运行 Ctrl+C 使用标准 Host 生命周期。无论启动、自检或运行失败，finally 均尝试注销与关闭两个服务器并刷新密钥；单次 HTTP 超时 5 秒，自检等待 60 秒，关闭各服务器等待 10 秒。没有测试专用公开入口。自检操作本次进程的新库存，不附着已有业务进程。

## 可复现检查

在本目录运行：

```powershell
dotnet build TestGround.AspNetCore.csproj -c Release
$env:PUPPET_TESTGROUND_KEY = [Guid]::NewGuid().ToString('N')
try {
    dotnet bin/Release/net10.0/TestGround.AspNetCore.dll --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed' }
} finally { $env:PUPPET_TESTGROUND_KEY = $null }
```

无独立 lint/typecheck 配置，Release 编译作为 C# 类型检查。不要把随机密钥输出或重定向到文件。

## 已验证证据（2026-09-17，Windows / .NET 10）

- Release 构建成功，0 warnings、0 errors；最新 --self-test 全部通过，退出 0，finally 注销通过。
- 首次完整自检发现部分框架错误只有状态码、没有 JSON；加入标准 StatusCodePages 生成 Problem Details 后重跑通过。实现期间未完成文件导致的早期编译错误已消除。
- 服务校验：空/非法/超长 SKU 与名称、控制/格式字符、数量上下界、空 ID；最大合法名称/SKU/数量接受。重复 SKU、未知库存、余额不足和两种容量上限拒绝。
- 服务并发重复释放 24 次仅成功一次；历史快照不变；满 10000 活动预留后拒绝，释放后可再次预留。
- 真实普通 HTTP：HTML 200/content-type、安全头、密码输入存在；空库存读取；所有三种写路由无密钥拒绝、错误密钥拒绝；非法 JSON、null、缺字段、无效 SKU/数量/UUID、未知目标；409 重复、413 超限、415 媒体类型。错误响应包含正确 Problem Details status。
- 真实 Puppet HTTP：ping、未授权/错误密钥 404、registry、describe、Snapshot/CreateStock/Reserve/Release；业务状态在普通 API、服务与 Puppet 间共享。AgentAccessKey、AgentLog、锁和字典不可 get；描述与状态不包含注入密钥。
- 48 个并行 HTTP 请求竞争 12 个单位：恰好 12 次 201，其余 409，无超卖，预留 ID 唯一；12 次并行 HTTP 释放同一笔恰好一次 204，其余 404；Puppet 释放其余预留后库存恢复。
- 核心 HTTP 刷新密钥后，普通 API 与 Puppet 均拒绝旧密钥，新密钥均可使用。
- 无密钥普通启动本地运行（Agent 端点禁用），无密钥/非法短密钥 `--self-test` 退出 1，未知参数退出 2；自检进程结束后可重新绑定 19104 与 19204。
- git status 中原有根 targets、核心源码及其他 TestGround 的修改保持原样，本任务只新增本目录文件，无提交。

底层提示回环非 TLS 端点禁用 HTTP/2、HTTP/3，HTTP/1.1 实测正常。未手动操作真实浏览器（只验证 HTML HTTP 内容，不等于 JS 交互/视觉/键盘测试）；未验证实际 Ctrl+C、启动端口冲突、跨平台、持续压力、恶意流量、Puppet chunked 防护、发布/Trim/AOT。无持久化，未实施失败重试幂等协议。

## 后续任务（按实际需求推进）

1. 手动浏览器核对创建/预留/释放、密码遗忘、中文名称与窄屏键盘可用性；补标准进程 Ctrl+C 和端口占用回归，仍不暴露测试钩子。
2. 只有跨会话业务需求出现才设计持久化、事务和恢复；定义失败不丢数据与迁移验证后再实现。
3. 需要补货、出库、部分释放、过期或请求重试时先明确业务语义与幂等策略，继续复用共享服务方法。
4. 同端口确有需求时实现 HttpContext/WebRequest 适配，验证请求体、头、状态码、压缩及响应生命周期兼容性，再调用 PuppetWebHandler；当前不加无用适配器。
5. 网络部署需独立评估 TLS、身份/授权、请求上限、速率限制、审计与敏感数据保护，不能简单改为 0.0.0.0。只有测量证明容量或单锁不足时才优化并发与查询。
