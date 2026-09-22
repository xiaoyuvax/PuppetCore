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
    public string PageSummary => $"{ReadPages:N0} / {TotalPages:N0} {Loc.T("页", "pages")}";
}

public sealed record FilterChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class ReadingList : INotifyPropertyChanged, IPuppet
{
    [PuppetIgnore] private readonly ObservableCollection<ReadingBook> _books = [];
    [PuppetIgnore] private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    [PuppetIgnore] private readonly ILog _log = LogManager.GetLogger(typeof(ReadingList));
    [PuppetIgnore] private ReadingBook? _selected;
    [PuppetIgnore] private string _filter = "全部";
    [PuppetIgnore] private string _status = Loc.T("从一本想读的书开始。数据仅保存在本次会话。", "Start with a book you want to read. Data lives only in this session.");
    [PuppetIgnore] private string _agentStatus = Loc.T("Puppet 尚未启动", "Puppet not started");

    [PuppetIgnore] [field: PuppetIgnore] public event PropertyChangedEventHandler? PropertyChanged;
    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => _log;
    [PuppetIgnore] string IPuppet.AgentInstanceName => "ReadingList";
    [PuppetIgnore] [field: PuppetIgnore] public ICollectionView Books { get; }
    [PuppetIgnore] [field: PuppetIgnore] public string TitleInput { get; set; } = "";
    [PuppetIgnore] [field: PuppetIgnore] public string TotalInput { get; set; } = "";
    [PuppetIgnore] [field: PuppetIgnore] public string ReadInput { get; set; } = "";
    [PuppetIgnore] public FilterChoice[] Filters =>
    [
        new FilterChoice("全部", Loc.T("全部", "All")),
        new FilterChoice("未读完", Loc.T("未读完", "Unread")),
        new FilterChoice("已读完", Loc.T("已读完", "Read"))
    ];
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
    public string AggregateText => Loc.T(
        $"{TotalCount} 本书 · {CompletedCount} 本读完 · {ReadPages:N0} / {TotalPages:N0} 页",
        $"{TotalCount} books · {CompletedCount} read · {ReadPages:N0} / {TotalPages:N0} pages");
    public string ShelfHeader => Loc.T($"书架 · {VisibleCount} 本", $"Shelf · {VisibleCount} book(s)");
    public string EmptyShelfText => Loc.T("书架还很安静。\n添加一本书，或试试其他筛选。", "The shelf is quiet.\nAdd a book, or try another filter.");
    public string CurrentFilter => _filter;
    public string SelectedTitle => _selected?.Title ?? Loc.T("尚未选择书籍", "No book selected");
    public int SelectedTotalPages => _selected?.TotalPages ?? 0;
    public int SelectedReadPages => _selected?.ReadPages ?? 0;
    public double SelectedProgress => _selected?.Progress ?? 0;
    public string SelectionSummary => _selected?.PageSummary ?? Loc.T("在左侧选择一本书，记录今天读到的页数。", "Select a book on the left and log today's pages.");
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
            return Report(Loc.T("请输入 1–120 字的单行书名。", "Enter a single-line title of 1–120 characters."), false);
        if (!int.TryParse(totalPages, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) || total is < 1 or > 100000)
            return Report(Loc.T("总页数须为 1–100,000 的整数。", "Total pages must be an integer from 1 to 100,000."), false);
        if (_books.Any(book => string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase)))
            return Report(Loc.T("书架上已有同名书籍，请使用不同书名区分版本。", "A book with this title is already on the shelf; use a different title."), false);
        var book = new ReadingBook(title, total, 0);
        _books.Add(book);
        SetFilter("全部");
        SelectedBook = book;
        TitleInput = "";
        TotalInput = "";
        return Report(Loc.T("已加入书架，开始新的阅读旅程。", "Added to the shelf; enjoy the new journey."), true);
    }

    public bool SelectBook(int index)
    {
        _dispatcher.VerifyAccess();
        if (index < 0 || index >= VisibleCount) return Report(Loc.T("请选择当前列表中的书籍。", "Select a book from the current list."), false);
        SelectedBook = Books.Cast<ReadingBook>().ElementAt(index);
        return Report(Loc.T("已选择书籍，可更新进度或移除。", "Book selected; you can update progress or remove it."), true);
    }

    public bool UpdateProgress(string? readPages)
    {
        _dispatcher.VerifyAccess();
        if (_selected == null) return Report(Loc.T("请先选择一本书。", "Select a book first."), false);
        if (!int.TryParse(readPages, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) || pages < 0 || pages > _selected.TotalPages)
            return Report(Loc.T($"已读页数须为 0–{_selected.TotalPages:N0} 的整数。", $"Read pages must be an integer from 0 to {_selected.TotalPages:N0}."), false);
        var updated = _selected with { ReadPages = pages };
        _books[_books.IndexOf(_selected)] = updated;
        Books.Refresh();
        SelectedBook = Books.Contains(updated) ? updated : null;
        return Report(pages == updated.TotalPages ? Loc.T("读完了，真棒！", "Finished — nice!") : Loc.T("进度已保存；也可以调低页数来纠正记录。", "Progress saved; you can lower the page count to correct it."), true);
    }

    public bool RemoveSelectedBook()
    {
        _dispatcher.VerifyAccess();
        if (_selected == null) return Report(Loc.T("请先选择一本书。", "Select a book first."), false);
        _books.Remove(_selected);
        SelectedBook = null;
        return Report(Loc.T("已从本次会话的书架移除。", "Removed from this session's shelf."), true);
    }

    public bool SetFilter(string? filter)
    {
        _dispatcher.VerifyAccess();
        if (filter is not ("全部" or "未读完" or "已读完")) return Report(Loc.T("筛选请选择：全部、未读完、已读完。", "Filter must be All, Unread, or Read."), false);
        _filter = filter;
        Books.Refresh();
        if (_selected != null && !Books.Contains(_selected)) SelectedBook = null;
        return Report(VisibleCount == 0 ? Loc.T("当前筛选下没有书籍，可添加一本或切换筛选。", "No books under the current filter; add one or switch the filter.") : Loc.T($"正在显示{FilterDisplay(filter)}书籍。", $"Showing {FilterDisplay(filter)} books."), true);
    }

    private static string FilterDisplay(string value) => value switch
    {
        "全部" => Loc.T("全部", "All"),
        "未读完" => Loc.T("未读完", "Unread"),
        _ => Loc.T("已读完", "Read")
    };

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
