using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TestGround.Console;

internal static class Commands
{
    internal const int MaxLineLength = 512;

    internal static string Help => Loc.T(
        "Commands (case-sensitive, ASCII spaces):\nadd <amount> <category> [note text]\nlist\nsummary\ndelete <id>\nhelp\nquit\n" +
        "金额：数字，可含小数点后 1–2 位，范围 0.01..1000000，小数点固定为“.”；不允许正负号/千分位/指数。\n" +
        "分类：1..32 个小写 ASCII 字母、数字、- 或 _。备注：0..200 字符，不含控制/格式字符。\n" +
        "备注为行内其余内容，不支持引号/转义；首尾空白会被去除。\n" +
        "单条命令上限 512 字符；账本上限 10000 条。\n" +
        "--serve 在 stdin EOF 后继续运行；quit、Ctrl+C 或 Agent Stop 结束进程。\n" +
        "--self-test 需要 PUPPET_TESTGROUND_KEY，执行本地与 HTTP 检查。",
        "Commands (case-sensitive, ASCII spaces):\nadd <amount> <category> [note text]\nlist\nsummary\ndelete <id>\nhelp\nquit\n" +
        "Amount: digits[.1-2 digits], 0.01..1000000, invariant decimal point; no sign/grouping/exponent.\n" +
        "Category: 1..32 lowercase ASCII letters/digits/-/_. Note: 0..200 characters, no control/format characters.\n" +
        "Note is the rest of the line, no quoting/escaping; surrounding spaces are trimmed.\n" +
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
