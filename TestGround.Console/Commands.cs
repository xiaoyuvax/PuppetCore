using System.Globalization;
using System.Text;
using System.Text.Json;
using Puppet.Core;

namespace TestGround.Console;

internal static class Commands
{
    internal const int MaxLineLength = 512;

    internal static string Help => Loc.T(
        "Commands (case-sensitive, ASCII spaces):\nadd <amount> <category> [note text]\nlist\nsummary\nbudget <amount|off>\ndelete <id>\nclear\npresence\nhelp\nquit\n" +
        "金额：数字，可含小数点后 1–2 位，范围 0.01..1000000，小数点固定为“.”；不允许正负号/千分位/指数。\n" +
        "分类：1..32 个小写 ASCII 字母、数字、- 或 _。备注：0..200 字符，不含控制/格式字符。\n" +
        "备注为行内其余内容，不支持引号/转义；首尾空白会被去除。\n" +
        "budget 设置预算上限（off 取消）；clear 清空全部记录。\n" +
        "presence 输出已注册 Puppet 与各 Agent 的状态/注意/求助；交互终端在有变化时自动提示。\n" +
        "单条命令上限 512 字符；账本上限 10000 条。\n" +
        "--serve 在 stdin EOF 后继续运行；quit、Ctrl+C 或 Agent Stop 结束进程。\n" +
        "--self-test 需要 PUPPET_TESTGROUND_KEY，执行本地与 HTTP 检查。",
        "Commands (case-sensitive, ASCII spaces):\nadd <amount> <category> [note text]\nlist\nsummary\nbudget <amount|off>\ndelete <id>\nclear\npresence\nhelp\nquit\n" +
        "Amount: digits[.1-2 digits], 0.01..1000000, invariant decimal point; no sign/grouping/exponent.\n" +
        "Category: 1..32 lowercase ASCII letters/digits/-/_. Note: 0..200 characters, no control/format characters.\n" +
        "Note is the rest of the line, no quoting/escaping; surrounding spaces are trimmed.\n" +
        "budget sets an upper bound (off clears it); clear removes every entry.\n" +
        "presence prints registered puppets plus every agent's status/note/help; an interactive terminal also reports changes automatically.\n" +
        "Maximum command: 512 characters; maximum ledger: 10000 entries.\n" +
        "--serve keeps running after stdin EOF; quit, Ctrl+C or Agent Stop ends the process.\n" +
        "--self-test requires PUPPET_TESTGROUND_KEY and runs local plus HTTP checks.");

    internal static void Execute(ExpenseLedger ledger, string line, TextWriter output)
    {
        if (line.Length > MaxLineLength || line.Any(char.IsControl))
            throw new ArgumentException(Loc.T("命令不得超过 512 字符且不含控制字符。", "Command must be at most 512 characters without controls."));
        var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        object? result;
        switch (parts[0])
        {
            case "add" when parts.Length is 3 or 4:
                var amount = parts[1];
                var dot = amount.IndexOf('.');
                if (amount.Any(c => !(c is >= '0' and <= '9' or '.')) ||
                    (dot >= 0 && (dot == 0 || amount.Length - dot - 1 is < 1 or > 2)) ||
                    !decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException(Loc.T("金额格式无效；请用数字，可带小数点后 1–2 位。", "Invalid amount syntax; use digits with an optional decimal point and 1..2 fractional digits."));
                result = ledger.Add(value, parts[2], parts.Length == 4 ? parts[3] : "");
                break;
            case "list" when parts.Length == 1:
                result = ledger.List();
                break;
            case "summary" when parts.Length == 1:
                result = ledger.Summary();
                break;
            case "delete" when parts.Length == 2:
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    throw new ArgumentException(Loc.T("id 格式无效；请用正整数。", "Invalid id syntax; use positive digits."));
                result = new { Deleted = ledger.Delete(id) };
                break;
            case "budget" when parts.Length == 2:
                if (parts[1] == "off")
                {
                    if (!ledger.SetBudget(null)) throw new ArgumentException(Loc.T("预算取值无效。", "Invalid budget."));
                    result = new { Budget = ledger.Budget };
                    break;
                }
                if (parts[1].Any(c => !(c is >= '0' and <= '9' or '.')) ||
                    !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var budget) ||
                    !ledger.SetBudget(budget))
                    throw new ArgumentException(Loc.T("预算格式无效；请用非负数字。", "Invalid budget syntax; use non-negative digits."));
                result = new { Budget = ledger.Budget };
                break;
            case "clear" when parts.Length == 1:
                result = ledger.Clear();
                break;
            case "presence" when parts.Length == 1:
                result = Presence();
                break;
            case "help" when parts.Length == 1:
                output.WriteLine(Help);
                return;
            case "quit" when parts.Length == 1:
                ledger.Stop();
                return;
            default:
                throw new ArgumentException(Loc.T("未知命令或参数数量错误；请用 help。", "Unknown command or wrong argument count; use help."));
        }
        output.WriteLine(JsonSerializer.Serialize(result));
    }

    internal static object Presence()
    {
        var now = DateTime.Now;
        return new
        {
            Puppets = PuppetRegistry.Query(includeInternal: true).Instances
                .OrderBy(instance => instance.InstanceName, StringComparer.Ordinal)
                .Select(instance => new { instance.InstanceName, instance.TypeName, instance.LastActiveAt }),
            Agents = AgentBook.GetAll(includeStale: true)
                .OrderBy(agent => agent.Id, StringComparer.Ordinal)
                .Select(agent => new
                {
                    agent.Id,
                    agent.PuppetInstanceName,
                    agent.CurrentSite,
                    agent.Operation,
                    agent.Purpose,
                    IdleSeconds = (int)Math.Max(0, (now - agent.LastHeartbeatAt).TotalSeconds),
                    Status = agent.Status switch
                    {
                        AgentStatus.ShuttingDown => "shutting down",
                        AgentStatus.Active => "active",
                        _ => "idle"
                    },
                    Lock = AgentBook.DescribeReportedLock(agent.CurrentLockMode),
                    agent.Note,
                    SeeksHelp = string.IsNullOrEmpty(agent.Help) ? null : agent.Help,
                    agent.HelpUpdatedAt,
                    HelpTo = string.IsNullOrEmpty(agent.HelpTo) ? null : agent.HelpTo
                })
        };
    }

    internal static string PresenceSummary()
    {
        var now = DateTime.Now;
        var puppets = PuppetRegistry.Query(includeInternal: true).Instances
            .OrderBy(instance => instance.InstanceName, StringComparer.Ordinal)
            .Select(instance => instance.InstanceName);
        var agents = AgentBook.GetAll(includeStale: true)
            .OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .Select(agent =>
            {
                var line = $"{agent.Id}@{agent.PuppetInstanceName}";
                if (!string.IsNullOrEmpty(agent.CurrentSite)) line += "/" + agent.CurrentSite;
                var status = agent.Status switch
                {
                    AgentStatus.ShuttingDown => "shutting down",
                    AgentStatus.Active => "active",
                    _ => "idle"
                };
                var idle = (int)Math.Max(0, (now - agent.LastHeartbeatAt).TotalSeconds);
                line += $" idle={idle}s status={status}";
                if (idle > 60) line += " stale";
                if (!string.IsNullOrEmpty(agent.Note)) line += $" note=\"{agent.Note}\"";
                if (!string.IsNullOrEmpty(agent.Help)) line += $" help=\"{agent.Help}\"";
                if (!string.IsNullOrEmpty(agent.HelpTo)) line += $" helpTo={agent.HelpTo}";
                return line;
            });
        return $"puppets=[{string.Join(", ", puppets)}] agents=[{string.Join("; ", agents)}]";
    }

    internal static string? ReadLine(TextReader input)
    {
        var line = new StringBuilder();
        var oversized = false;
        while (true)
        {
            var c = input.Read();
            if (c == -1 || c == '\n' || c == '\r')
            {
                if (c == '\r' && input.Peek() == '\n') input.Read();
                if (oversized) throw new ArgumentException(Loc.T("命令超过 512 字符。", "Command exceeds 512 characters."));
                return c == -1 && line.Length == 0 ? null : line.ToString();
            }
            if (line.Length < MaxLineLength) line.Append((char)c);
            else oversized = true;
        }
    }
}
