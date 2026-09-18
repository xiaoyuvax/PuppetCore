# Stockroom：ASP.NET Core 库存与预留宿主

## 用途与范围

一个可独立运行的内存库存工作台：创建库存、查看可用数量、预留单位、释放整笔预留。浏览器和 Puppet 调用同一个并发安全的 Inventory 实例，不是测试钩子演示。Microsoft.NET.Sdk.Web、net10.0、IsPackable=false，仅引用 Puppet.Core，不新增依赖，不加入根解决方案。

## 启动

在本目录执行：

```powershell
dotnet build TestGround.AspNetCore.csproj -c Release
```

直接运行即可本地使用（浏览器读写均正常，写接口要求运行时密钥认证）：

```powershell
dotnet bin/Release/net10.0/TestGround.AspNetCore.dll
```

如需 Agent 经 Puppet 端点操控，通过可信的运行时环境注入 `PUPPET_TESTGROUND_KEY` 后再启动：

```powershell
try { dotnet bin/Release/net10.0/TestGround.AspNetCore.dll }
finally { $env:PUPPET_TESTGROUND_KEY = $null }
```

密钥必须为 16..256 个非空格可打印 ASCII 字符；不要在命令历史、配置、脚本、日志或文件中写密钥字面量。宿主读取后清除自身环境副本。无密钥时应用正常本地运行、Agent 端点禁用；`--self-test` 必须带密钥（缺失或非法退出 1），未知参数退出 2。

浏览器打开 `http://127.0.0.1:19204`。在密码框中输入同一密钥并点击 Use key；输入框随即清空，密钥仅保留在页面内存。不要接受浏览器保存密码。Forget key、刷新页面或离开页面会遗忘密钥。Use key 不会立即验证凭据，实际修改时才验证。Ctrl+C 停止宿主，所有业务数据随进程结束丢失。

## 模式 C

- 应用原生 Kestrel 固定回环 `127.0.0.1:19204`，提供 HTML 和普通 API。
- 既有 PuppetWebServer 独立监听回环 `127.0.0.1:19104`，注册名 `Inventory`。
- 两个服务器同进程共享服务，不需要请求适配器；不是通过 HTTP 同步两份数据。端口冲突应先停止占用进程，不回退到通配地址。
- 未来确需同端口时才实现 Kestrel HttpContext 到 Wima.Web.WebRequest 的适配，之后调用 PuppetWebHandler.TryHandle；不能直接传入不兼容的请求对象。

## 普通 API

写请求使用 `Authorization: Bearer <运行时密钥>`；密钥不放 URL。读取无需认证，仅用于可信本机。

| 请求 | 请求体 | 成功 |
| --- | --- | --- |
| GET /api/inventory | 无 | 200，一次一致快照，stock 与 reservations |
| POST /api/stock | {"sku":"PAPER","name":"纸张","quantity":12} | 201，库存 |
| POST /api/reservations | {"sku":"PAPER","quantity":2} | 201，预留，含 id |
| DELETE /api/reservations/{id} | 无 | 204，释放整笔预留 |

SKU 为 1..24 个大写 ASCII 字母、数字或连字符；名称为 1..80 字符，拒绝空白名称、控制及格式字符；数量为 1..1000000 整数。最多 1000 个 SKU、10000 笔活动预留。无追加库存、出库、部分释放或删除 SKU 功能。

普通 API 错误使用 Problem Details：400 输入无效，401 密钥无效，404 目标不存在或预留已释放，409 重复 SKU、库存不足或容量已满，413 请求体超过 4096 字节，415 不支持的媒体类型。错误不改变库存。并发释放同一预留只成功一次，其余 404；重试创建/预留没有幂等键，网络结果不确定时先刷新状态。

Puppet 暴露真实业务方法 `Snapshot()`、`CreateStock(sku,name,quantity)`、`Reserve(sku,quantity)`、`Release(id)`。例如 POST `/agent/invoke?name=Inventory&method=Reserve`，JSON 参数数组 `["PAPER",2]`。沿用核心响应约定（业务失败为 ok=false，鉴权失败为 404），不强行改成普通 API 状态码。核心 `/agent/key/refresh` 会让两套写接口上的旧密钥失效；调用方只在内存接收新密钥，页面需重新输入。

## 自检与限制

```powershell
$env:PUPPET_TESTGROUND_KEY = [Guid]::NewGuid().ToString('N')
try {
    dotnet bin/Release/net10.0/TestGround.AspNetCore.dll --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Self-test failed' }
} finally { $env:PUPPET_TESTGROUND_KEY = $null }
```

自检启动真实双服务器，调用业务方法和 HTTP，结束时在 finally 停止并注销；退出 0 才表示通过。验证证据与未验证项见 DEVELOPMENT.md。仅可信本机演示：无持久化、多用户权限、TLS 或公网部署支持；不要录入敏感业务数据或将密钥当作名称。底层框架仍可能输出生命周期日志，不承诺依赖树零日志。
