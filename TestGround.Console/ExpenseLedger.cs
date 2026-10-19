using System.Collections.Immutable;
using Common.Logging;
using Puppet.Core;
using Puppet.Core.AppAgent;
using Puppet.Core.Extentions;

namespace TestGround.Console;

public sealed record Expense
{
    public long Id { get; }
    public decimal Amount { get; }
    public string Category { get; }
    public string Note { get; }

    internal Expense(long id, decimal amount, string category, string note)
        => (Id, Amount, Category, Note) = (id, amount, category, note);
}

public sealed record CategoryTotal
{
    public string Category { get; }
    public int Count { get; }
    public decimal Total { get; }

    internal CategoryTotal(string category, int count, decimal total)
        => (Category, Count, Total) = (category, count, total);
}

public sealed record LedgerSummary
{
    public int Count { get; }
    public decimal Total { get; }
    public decimal? Budget { get; }
    public decimal? Remaining { get; }
    public ImmutableArray<CategoryTotal> Categories { get; }

    internal LedgerSummary(ImmutableArray<Expense> entries, decimal? budget)
    {
        Count = entries.Length;
        Total = entries.Sum(entry => entry.Amount);
        Budget = budget;
        Remaining = budget - Total;
        Categories = entries.GroupBy(entry => entry.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CategoryTotal(group.Key, group.Count(), group.Sum(entry => entry.Amount)))
            .ToImmutableArray();
    }
}

[PuppetAppInfo("ExpenseLedger", Version = "1.0", Vendor = "TestGround")]
[PuppetDescription("内存支出账本；CLI 与 Agent 共用 Add/List/Summary/Delete。")]
public sealed class ExpenseLedger : IPuppet
{
    [PuppetIgnore] private readonly object _gate = new();
    [PuppetIgnore] private readonly List<Expense> _entries = [];
    [PuppetIgnore] private readonly CancellationTokenSource _stop = new();
    [PuppetIgnore] private long _nextId = 1;
    [PuppetIgnore] private decimal? _budget;

    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => new Common.Logging.Simple.NoOpLogger();
    string IPuppet.AgentInstanceName => "ExpenseLedger";
    [PuppetIgnore] internal CancellationToken Stopping => _stop.Token;

    public bool IsStopping => _stop.IsCancellationRequested;

    /// <summary>演示 Console 诊断扩展（Puppet.Core.Extentions）：进程 pid/内存/uptime 只读快照</summary>
    [PuppetHint("Tip", "ConsoleState 由 DescribeConsoleState() 生成，可观察进程级状态")]
    public string ConsoleState => this.DescribeConsoleState();

    /// <summary>当前预算上限；null = 未设置。Agent 可用 /agent/set 写入</summary>
    [PuppetHint("Tip", "可直接 /agent/set path=Budget 设预算；null 表示不限额")]
    public decimal? Budget
    {
        get { lock (_gate) return _budget; }
        set
        {
            if (value is < 0 or > 1000000000m) throw new ArgumentException("Budget must be 0..1000000000.");
            lock (_gate) _budget = value;
        }
    }

    /// <summary>是否超预算（谓词前缀，B 面向自动排除；A 面向仍可读）</summary>
    public bool IsOverBudget => Summary().Remaining is < 0;

    /// <summary>v1.0 B-only 演示：CLI 宿主的导出动作（范式外返回类型，[PuppetAction] 覆盖收录；产物走 /appagent/assets）</summary>
    [Puppet.Core.AppAgent.PuppetAction(Desc = "导出账本 CSV 文本")]
    public Puppet.Core.AppAgent.PuppetArtifact ExportCsv()
    {
        lock (_gate)
        {
            var sb = new System.Text.StringBuilder("id,amount,category,note\r\n");
            foreach (var e in _entries)
                sb.Append(e.Id).Append(',').Append(e.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(',').Append(e.Category).Append(",\"").Append(e.Note.Replace("\"", "\"\""))
                  .Append("\"\r\n");
            return new Puppet.Core.AppAgent.PuppetArtifact("ledger.csv", "text/csv",
                System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }

    /// <summary>用户核心动词（返回 Expense 复杂类型，范式外）——提炼 Agent 以 [PuppetAction] 覆盖收录进 B 面向</summary>
    [Puppet.Core.AppAgent.PuppetAction(Desc = "记一笔支出")]
    [PuppetParam(Name = "note", Desc = "可选备注，最多 200 字符")]
    public Expense Add(decimal amount, string category, string note)
    {
        if (amount < 0.01m || amount > 1000000m || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Amount must be 0.01..1000000 with at most two decimal places.");
        if (category is null || category.Length is < 1 or > 32 ||
            category.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("Category must be 1..32 lowercase ASCII letters, digits, '-' or '_'.");
        if (note is null || note.Length > 200 || note.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new ArgumentException("Note must be at most 200 characters without control or format characters.");
        lock (_gate)
        {
            EnsureRunning();
            if (_entries.Count >= 10000) throw new InvalidOperationException("Ledger capacity is 10000 entries.");
            var entry = new Expense(_nextId, amount, category, note.Trim());
            _nextId = checked(_nextId + 1);
            _entries.Add(entry);
            PLog.Fwd(Puppet.Core.LogLevel.Info, $"ledger add category={category}");
            return entry;
        }
    }

    public ImmutableArray<Expense> List()
    {
        lock (_gate) return _entries.ToImmutableArray();
    }

    public LedgerSummary Summary()
    {
        lock (_gate) return new LedgerSummary(_entries.ToImmutableArray(), _budget);
    }

    /// <summary>范式动作（bool 返回，无需标注即被 B 面向自动收录）</summary>
    public bool SetBudget(decimal? budget)
    {
        if (budget is < 0 or > 1000000000m) return false;
        lock (_gate) _budget = budget;
        return true;
    }

    /// <summary>范式动作（OperationResult，携带面向用户的说明）</summary>
    public OperationResult Clear()
    {
        lock (_gate)
        {
            EnsureRunning();
            var removed = _entries.Count;
            _entries.Clear();
            return OperationResult.Success($"已清空 {removed} 笔记录", removed);
        }
    }

    /// <summary>模式 B 产物：汇总 Markdown 报表（范式外返回类型，[PuppetAction] 覆盖收录）</summary>
    [Puppet.Core.AppAgent.PuppetAction(Desc = "导出汇总 Markdown 报表")]
    public PuppetArtifact ExportMarkdown()
    {
        var summary = Summary();
        var sb = new System.Text.StringBuilder("# 账本汇总\r\n\r\n");
        sb.Append("| 类别 | 笔数 | 金额 |\r\n|---|---|---|\r\n");
        foreach (var c in summary.Categories)
            sb.Append($"| {c.Category} | {c.Count} | {c.Total.ToString(System.Globalization.CultureInfo.InvariantCulture)} |\r\n");
        sb.Append($"\r\n合计 {summary.Total.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (summary.Budget is { } budget) sb.Append($" / 预算 {budget.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return new PuppetArtifact("ledger.md", "text/markdown; charset=utf-8",
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>演示 PLog：业务日志照常写宿主，同时镜像到 /agent/logs?name=Puppet.Forwarded</summary>
    [PuppetHint("Gotcha", "Add 会经 PLog.Fwd 镜像一条日志；日志只含类别，不含金额或备注")]
    public void LogCheckpoint(string label)
        => PLog.Fwd(Puppet.Core.LogLevel.Info, $"ledger checkpoint {label}");

    public bool Delete(long id)
    {
        if (id <= 0) throw new ArgumentException("Id must be a positive integer.");
        lock (_gate)
        {
            EnsureRunning();
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return false;
            _entries.RemoveAt(index);
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate) _stop.Cancel();
    }

    private void EnsureRunning()
    {
        if (IsStopping) throw new InvalidOperationException("Ledger is stopping.");
    }
}
