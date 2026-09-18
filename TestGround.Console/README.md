# 小账本 Console 宿主

一个可实际使用的内存支出账本，也是 Puppet.Core 的 Console 集成验证宿主。支持新增、列表、按类别汇总与删除；终端与 Agent 调用同一组业务方法。退出后数据消失，不用于真实财务留存。

## 运行

需要 .NET 10 SDK，在本目录执行：

```powershell
dotnet build TestGround.Console.csproj -c Release
dotnet bin/Release/net10.0/TestGround.Console.dll
```

未设置 `PUPPET_TESTGROUND_KEY` 时仅提供本地 CLI，不启动网络服务。启用 Agent 时，由启动器在进程环境中注入 16..256 个非空格可打印 ASCII 字符的随机密钥；不要把实际密钥写进源码、命令历史、文档或配置文件。

```text
add 12.50 food 午餐
add 2.00 travel 公交
list
summary
delete 1
help
quit
```

- 命令区分大小写，仅 ASCII 空格分隔；空行忽略。
- 金额语法为整数数字，可带小数点及 1..2 位小数；范围 0.01..1000000，不接受正负号、逗号、科学计数法或自动舍入。
- 类别为 1..32 个小写 ASCII 字母、数字、`-`、`_`。
- 备注可省略，取类别后的整段文本，不处理引号或转义；最多 200 个 UTF-16 字符，禁止控制字符及 Unicode 格式字符，首尾空白去除。
- 每行最多 512 字符；超长行被完整丢弃，下一行仍可执行。最多保存 10000 条记录。
- 列表按递增 ID 排序，删除不复用 ID；汇总类别按 Ordinal 排序，金额使用 decimal。成功结果输出为单行 JSON，错误写 stderr，不回显原始输入。删除不存在的正 ID 返回 `Deleted:false`。

## 启动选项与退出

| 选项 | 行为 |
| --- | --- |
| 无参数 | 交互或管道输入；EOF 自动停止 |
| `--help` | 输出语法，不启动服务 |
| `--serve` | 必须有密钥；stdin 重定向且 EOF 后仍持续服务 |
| `--self-test` | 必须有密钥；运行真实业务与 HTTP 断言，不跳过网络检查 |

`quit`、Ctrl+C、Agent `Stop()` 使用同一停止方法。停止后禁止新增/删除，主程序注销实例、停止服务器并刷新全局密钥。退出码：0 正常，1 启动/运行/自检/关闭失败，2 参数或密钥配置错误。普通 CLI 的单条输入错误会显示并继续，不改变正常退出码。

## Agent 接口

仅监听 `127.0.0.1:19102`，实例名 `ExpenseLedger`。每个 Agent 请求在 `Authorization: Bearer <运行时密钥>` 头中携带密钥，不放在 URL 中。

- `GET /agent/registry`、`GET /agent/describe?name=ExpenseLedger&format=json`
- `POST /agent/invoke?name=ExpenseLedger&method=Add`，JSON body：`[12.50,"food","午餐"]`
- 方法 `List`、`Summary`、`Stop` 的 body 为 `[]`；`Delete` 为 `[1]`。
- 状态优先使用 `GET /agent/get?name=ExpenseLedger&path=IsStopping`。
- HTTP 成功状态不代表业务成功，必须检查响应 `ok`，成功值位于 `result`。
- POST 必须发送 Content-Length，body 上限 4096 字节，不支持 chunked；超限返回 413。未认证按核心协议返回 404。
- 核心 `/agent/key/refresh` 会返回新密钥，仅内存保存；旧密钥立即失效。重启仍读取启动器注入的环境密钥。

业务操作不写日志。当前底层服务器仍会输出生命周期日志和 HTTP/2、HTTP/3 无 TLS 的降级提示，因此启用 Agent 后 stdout 不是纯 JSON 数据流；实际使用 HTTP/1.1。检查范围、边界与后续计划见 DEVELOPMENT.md。
