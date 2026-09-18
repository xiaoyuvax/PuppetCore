using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using Common.Logging;
using Puppet.Core;

namespace TestGround.Wpf;

public sealed record ReadingBook(string Title, int TotalPages, int ReadPages)
{
    public double Progress => 100.0 * ReadPages / TotalPages;
    public string PageSummary => $"{ReadPages:N0} / {TotalPages:N0} 页";
}

public sealed class ReadingList : INotifyPropertyChanged, IPuppet
{
    [PuppetIgnore] private readonly ObservableCollection<ReadingBook> _books = [];
    [PuppetIgnore] private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    [PuppetIgnore] private readonly ILog _log = LogManager.GetLogger(typeof(ReadingList));
    [PuppetIgnore] private ReadingBook? _selected;
    [PuppetIgnore] private string _filter = "全部";
    [PuppetIgnore] private string _status = "从一本想读的书开始。数据仅保存在本次会话。";
    [PuppetIgnore] private string _agentStatus = "Puppet 尚未启动";

    [PuppetIgnore] [field: PuppetIgnore] public event PropertyChangedEventHandler? PropertyChanged;
    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => _log;
    [PuppetIgnore] string IPuppet.AgentInstanceName => "ReadingList";
    [PuppetIgnore] [field: PuppetIgnore] public ICollectionView Books { get; }
    [PuppetIgnore] [field: PuppetIgnore] public string TitleInput { get; set; } = "";
    [PuppetIgnore] [field: PuppetIgnore] public string TotalInput { get; set; } = "";
    [PuppetIgnore] [field: PuppetIgnore] public string ReadInput { get; set; } = "";
    [PuppetIgnore] public string[] Filters => ["全部", "未读完", "已读完"];
    [PuppetIgnore] public bool HasSelection => _selected != null;
    [PuppetIgnore] public ReadingBook? SelectedBook
    {
        get => _selected;
        set
        {
            _dispatcher.VerifyAccess();
            if (ReferenceEquals(value, _selected) || (value != null && !Books.Contains(value))) return;
            _selected = value;
            ReadInput = value?.ReadPages.ToString(CultureInfo.InvariantCulture) ?? "";
            Notify();
        }
    }
    [PuppetIgnore] public string Filter
    {
        get => _filter;
        set => SetFilter(value);
    }

    public int TotalCount => _books.Count;
    public int CompletedCount => _books.Count(book => book.ReadPages == book.TotalPages);
    public int VisibleCount => Books.Cast<ReadingBook>().Count();
    public long TotalPages => _books.Sum(book => (long)book.TotalPages);
    public long ReadPages => _books.Sum(book => (long)book.ReadPages);
    public double OverallProgress => TotalPages == 0 ? 0 : 100.0 * ReadPages / TotalPages;
    public string AggregateText => $"{TotalCount} 本书 · {CompletedCount} 本读完 · {ReadPages:N0} / {TotalPages:N0} 页";
    public string CurrentFilter => _filter;
    public string SelectedTitle => _selected?.Title ?? "尚未选择书籍";
    public int SelectedTotalPages => _selected?.TotalPages ?? 0;
    public int SelectedReadPages => _selected?.ReadPages ?? 0;
    public double SelectedProgress => _selected?.Progress ?? 0;
    public string SelectionSummary => _selected?.PageSummary ?? "在左侧选择一本书，记录今天读到的页数。";
    public string StatusText => _status;
    public string AgentStatus => _agentStatus;

    public ReadingList()
    {
        Books = new ListCollectionView(_books);
        Books.Filter = item => item is ReadingBook book && (_filter == "全部" ||
            (book.ReadPages == book.TotalPages) == (_filter == "已读完"));
    }

    public bool AddBook(string? title, string? totalPages)
    {
        _dispatcher.VerifyAccess();
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 120 || title.Any(char.IsControl))
            return Report("请输入 1–120 字的单行书名。", false);
        if (!int.TryParse(totalPages, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) || total is < 1 or > 100000)
            return Report("总页数须为 1–100,000 的整数。", false);
        if (_books.Any(book => string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase)))
            return Report("书架上已有同名书籍，请使用不同书名区分版本。", false);
        var book = new ReadingBook(title, total, 0);
        _books.Add(book);
        SetFilter("全部");
        SelectedBook = book;
        TitleInput = "";
        TotalInput = "";
        return Report("已加入书架，开始新的阅读旅程。", true);
    }

    public bool SelectBook(int index)
    {
        _dispatcher.VerifyAccess();
        if (index < 0 || index >= VisibleCount) return Report("请选择当前列表中的书籍。", false);
        SelectedBook = Books.Cast<ReadingBook>().ElementAt(index);
        return Report("已选择书籍，可更新进度或移除。", true);
    }

    public bool UpdateProgress(string? readPages)
    {
        _dispatcher.VerifyAccess();
        if (_selected == null) return Report("请先选择一本书。", false);
        if (!int.TryParse(readPages, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) || pages < 0 || pages > _selected.TotalPages)
            return Report($"已读页数须为 0–{_selected.TotalPages:N0} 的整数。", false);
        var updated = _selected with { ReadPages = pages };
        _books[_books.IndexOf(_selected)] = updated;
        Books.Refresh();
        SelectedBook = Books.Contains(updated) ? updated : null;
        return Report(pages == updated.TotalPages ? "读完了，真棒！" : "进度已保存；也可以调低页数来纠正记录。", true);
    }

    public bool RemoveSelectedBook()
    {
        _dispatcher.VerifyAccess();
        if (_selected == null) return Report("请先选择一本书。", false);
        _books.Remove(_selected);
        SelectedBook = null;
        return Report("已从本次会话的书架移除。", true);
    }

    public bool SetFilter(string? filter)
    {
        _dispatcher.VerifyAccess();
        if (filter is not ("全部" or "未读完" or "已读完")) return Report("筛选请选择：全部、未读完、已读完。", false);
        _filter = filter;
        Books.Refresh();
        if (_selected != null && !Books.Contains(_selected)) SelectedBook = null;
        return Report(VisibleCount == 0 ? "当前筛选下没有书籍，可添加一本或切换筛选。" : $"正在显示{filter}书籍。", true);
    }

    [PuppetIgnore]
    internal void SetAgentStatus(string status)
    {
        _agentStatus = status;
        Notify();
    }

    private bool Report(string status, bool success)
    {
        _status = status;
        Notify();
        return success;
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
