using System.Collections.Immutable;
using Common.Logging;
using Puppet.Core;

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
    public ImmutableArray<CategoryTotal> Categories { get; }

    internal LedgerSummary(ImmutableArray<Expense> entries)
    {
        Count = entries.Length;
        Total = entries.Sum(entry => entry.Amount);
        Categories = entries.GroupBy(entry => entry.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CategoryTotal(group.Key, group.Count(), group.Sum(entry => entry.Amount)))
            .ToImmutableArray();
    }
}

public sealed class ExpenseLedger : IPuppet
{
    [PuppetIgnore] private readonly object _gate = new();
    [PuppetIgnore] private readonly List<Expense> _entries = [];
    [PuppetIgnore] private readonly CancellationTokenSource _stop = new();
    [PuppetIgnore] private long _nextId = 1;

    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => new Common.Logging.Simple.NoOpLogger();
    string IPuppet.AgentInstanceName => "ExpenseLedger";
    [PuppetIgnore] internal CancellationToken Stopping => _stop.Token;

    public bool IsStopping => _stop.IsCancellationRequested;

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
            return entry;
        }
    }

    public ImmutableArray<Expense> List()
    {
        lock (_gate) return _entries.ToImmutableArray();
    }

    public LedgerSummary Summary() => new(List());

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
