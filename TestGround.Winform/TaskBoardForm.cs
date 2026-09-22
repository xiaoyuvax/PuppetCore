using Common.Logging;
using Puppet.Core;
using Puppet.Core.Web;
using Puppet.Core.WinForms;

namespace TestGround.Winform;

public sealed class TaskBoardForm : Form, IPuppet
{
    private enum Priority { Normal = 0, High = 1 }

    private sealed record Choice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record BoardTask(string Title, Priority Priority = Priority.Normal, bool Completed = false, DateTime CreatedAt = default)
    {
        public BoardTask() : this("") { }
        public override string ToString() => $"{(Completed ? Loc.T("已完成", "Done") : Loc.T("待办", "To do"))}   ·   {PriorityDisplay()}   ·   {Title}";
        internal string PriorityLabel() => Priority switch { Priority.High => "高", _ => "普通" };
        internal string PriorityDisplay() => Priority switch { Priority.High => Loc.T("高", "High"), _ => Loc.T("普通", "Normal") };
    }

    private readonly List<BoardTask> _tasks = [];
    private readonly TextBox _taskInput = new() { Name = "TaskInput", AccessibleName = Loc.T("新任务标题", "New task title"), PlaceholderText = Loc.T("下一件值得完成的小事…", "The next small thing worth finishing…"), Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly ListBox _taskList = new() { Name = "TaskList", AccessibleName = Loc.T("任务列表", "Task list"), Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, ItemHeight = 34, HorizontalScrollbar = true, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly ComboBox _filter = new() { Name = "TaskFilter", AccessibleName = Loc.T("任务筛选", "Task filter"), DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _priority = new() { Name = "TaskPriority", AccessibleName = Loc.T("任务优先级", "Task priority"), DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _addButton = MakeButton("AddButton", Loc.T("添加 (&A)", "Add (&A)"), true);
    private readonly Button _completeButton = MakeButton("CompleteButton", Loc.T("完成所选 (&C)", "Complete selected (&C)"));
    private readonly Button _removeButton = MakeButton("RemoveButton", Loc.T("删除所选 (&D)", "Remove selected (&D)"));
    private readonly Button _renameButton = MakeButton("RenameButton", Loc.T("重命名 (&R)", "Rename (&R)"));
    private readonly Button _priorityButton = MakeButton("PriorityButton", Loc.T("切换优先级 (&P)", "Toggle priority (&P)"));
    private readonly Button _clearDoneButton = MakeButton("ClearDoneButton", Loc.T("清除已完成 (&E)", "Clear completed (&E)"));
    private readonly Label _counts = MakeLabel("TaskCounts", "", 11);
    private readonly Label _status = MakeLabel("BoardStatus", Loc.T("从一件小事开始。任务仅保存在本次会话。", "Start with one small thing. Tasks live only in this session."), 10);
    private readonly Label _agentStatus = MakeLabel("AgentStatus", Loc.T("Puppet 尚未启动", "Puppet not started"), 9);
    [PuppetIgnore] private readonly ILog _log = LogManager.GetLogger(typeof(TaskBoardForm));
    [PuppetIgnore] private PuppetWebServer? _server;
    [PuppetIgnore] private bool _rendering;
    [PuppetIgnore] private bool _closing;
    [PuppetIgnore] private bool _shutdownComplete;

    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => _log;
    string IPuppet.AgentInstanceName => "TaskBoard";

    public int TotalCount => _tasks.Count;
    public int CompletedCount => _tasks.Count(task => task.Completed);
    public int PendingCount => TotalCount - CompletedCount;
    public int HighPriorityPendingCount => _tasks.Count(task => task.Priority == Priority.High && !task.Completed);
    public int VisibleCount => _taskList.Items.Count;
    public string SelectedTitle => (_taskList.SelectedItem as BoardTask)?.Title ?? "";
    public bool SelectedCompleted => (_taskList.SelectedItem as BoardTask)?.Completed ?? false;
    public string SelectedPriority => (_taskList.SelectedItem as BoardTask)?.PriorityLabel() ?? "";
    public string SelectedCreatedAt => (_taskList.SelectedItem as BoardTask)?.CreatedAt.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string CurrentFilter => (_filter.SelectedItem as Choice)?.Value ?? "全部";
    public string StatusText => _status.Text;
    public string AgentStatus => _agentStatus.Text;

    public TaskBoardForm()
    {
        Name = "TaskBoard";
        Text = Loc.T("小步 · 任务看板", "Pace · Task Board");
        AccessibleName = Loc.T("小步任务看板", "Pace Task Board");
        Font = new Font("Microsoft YaHei UI", 11);
        BackColor = Color.FromArgb(243, 246, 250);
        ForeColor = Color.FromArgb(35, 48, 65);
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(720, 540);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Name = "BoardLayout", AccessibleName = Loc.T("任务看板布局", "Task board layout"), Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 56f, 36f, 50f, 44f, 0f, 54f, 40f, 28f })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(MakeLabel("Heading", Loc.T("小步  /  TASK BOARD", "PACE  /  TASK BOARD"), 23, FontStyle.Bold), 0, 0);
        layout.Controls.Add(MakeLabel("Subtitle", Loc.T("专注眼前，把计划变成已完成。", "Focus on what's here and turn plans into done."), 10), 0, 1);

        var entry = new TableLayoutPanel { Name = "EntryLayout", AccessibleName = Loc.T("添加任务", "Add task"), Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        entry.Controls.Add(_taskInput, 0, 0);
        _priority.Items.AddRange([new Choice("普通", Loc.T("普通", "Normal")), new Choice("高", Loc.T("高", "High"))]);
        _priority.SelectedIndex = 0;
        entry.Controls.Add(_priority, 1, 0);
        entry.Controls.Add(_addButton, 2, 0);
        layout.Controls.Add(entry, 0, 2);

        var toolbar = new TableLayoutPanel { Name = "FilterLayout", AccessibleName = Loc.T("筛选与数量", "Filter and counts"), Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        toolbar.Controls.Add(_counts, 0, 0);
        _filter.Items.AddRange([new Choice("全部", Loc.T("全部", "All")), new Choice("待办", Loc.T("待办", "Open")), new Choice("已完成", Loc.T("已完成", "Done"))]);
        _filter.SelectedIndex = 0;
        toolbar.Controls.Add(_filter, 1, 0);
        layout.Controls.Add(toolbar, 0, 3);
        layout.Controls.Add(_taskList, 0, 4);

        var actions = new FlowLayoutPanel { Name = "ActionLayout", AccessibleName = Loc.T("所选任务操作", "Selected-task actions"), Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
        actions.Controls.AddRange([_completeButton, _removeButton, _renameButton, _priorityButton, _clearDoneButton]);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(_status, 0, 6);
        layout.Controls.Add(_agentStatus, 0, 7);
        Controls.Add(layout);
        AcceptButton = _addButton;
        _taskInput.TabIndex = 0;
        _priority.TabIndex = 1;
        _addButton.TabIndex = 2;
        _taskList.TabIndex = 4;
        _completeButton.TabIndex = 0;
        _removeButton.TabIndex = 1;
        _renameButton.TabIndex = 2;
        _priorityButton.TabIndex = 3;
        _clearDoneButton.TabIndex = 4;
        _addButton.Click += (_, _) => AddTask(_taskInput.Text, (_priority.SelectedItem as Choice)?.Value);
        _completeButton.Click += (_, _) => CompleteSelectedTask();
        _removeButton.Click += (_, _) => RemoveSelectedTask();
        _renameButton.Click += (_, _) => RenameSelectedTask(_taskInput.Text);
        _priorityButton.Click += (_, _) => ToggleSelectedPriority();
        _clearDoneButton.Click += (_, _) => ClearCompletedTasks();
        _filter.SelectedIndexChanged += (_, _) => SetFilter(CurrentFilter);
        _taskList.SelectedIndexChanged += (_, _) =>
        {
            if (!_rendering)
            {
                if (_taskList.SelectedIndex >= 0) SelectTask(_taskList.SelectedIndex);
                else UpdateSelection();
            }
        };
        _taskList.DrawItem += DrawTaskItem;
        RenderTasks();
    }

    /// <summary>v1.0 演示：异步范式动作——Task&lt;bool&gt; 命中语义锚点自动收录（提案 §3.2 async 锚点）；兼作实例串行 409 演示载体</summary>
    public async Task<bool> SlowMarkdown(int milliseconds = 500)
    {
        await Task.Delay(Math.Max(0, milliseconds));
        return true;
    }

    /// <summary>v1.0 面向 B 演示：范式外成员（返回 int）以 [PuppetAction] 覆盖收录，产物走 /appagent/assets 下载</summary>
    [Puppet.Core.AppAgent.PuppetAction(Desc = "导出当前看板摘要文本文件")]
    public Puppet.Core.AppAgent.PuppetArtifact ExportSummary()
    {
        var lines = $"任务看板摘要\r\n生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                    $"总任务数：{TotalCount}\r\n待办：{PendingCount}（高优先级 {HighPriorityPendingCount}）\r\n已完成：{CompletedCount}\r\n";
        return new Puppet.Core.AppAgent.PuppetArtifact("summary.txt", "text/plain; charset=utf-8",
            System.Text.Encoding.UTF8.GetBytes(lines));
    }

    /// <summary>面向 B 的 UI 文案静态表演示位（L2）：本示例文案全部由 L1 桥从控件自动提取，无需维护</summary>
    internal static class AppAgentDescriptors { }

    public bool AddTask(string? title, string? priority = "普通")
    {
        if (priority is not ("普通" or "高")) return Report(Loc.T("优先级必须为：普通或高。", "Priority must be Normal or High."), false);
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 120 || title.Any(char.IsControl))
            return Report(Loc.T("请输入 1–120 字的单行任务标题。", "Enter a single-line title of 1–120 characters."), false);
        if (_tasks.Any(task => string.Equals(task.Title, title, StringComparison.OrdinalIgnoreCase)))
            return Report(Loc.T("已有同名任务，请换一个更具体的标题。", "A task with this title already exists; use a more specific one."), false);
        var task = new BoardTask(title, priority == "高" ? Priority.High : Priority.Normal, false, DateTime.Now);
        _tasks.Add(task);
        SetFilter("全部");
        RenderTasks(task);
        _taskInput.Clear();
        _priority.SelectedIndex = 0;
        _taskInput.Focus();
        return Report(priority == "高" ? Loc.T("已添加高优先级任务，先啃硬骨头。", "Added a high-priority task; tackle the hard one first.") : Loc.T("已添加任务。", "Task added."), true);
    }

    public bool SelectTask(int index)
    {
        if (index < 0 || index >= _taskList.Items.Count)
            return Report(Loc.T("请选择当前列表中的任务。", "Select a task from the current list."), false);
        if (_taskList.SelectedIndex != index) _taskList.SelectedIndex = index;
        UpdateSelection();
        return Report(SelectedCompleted ? Loc.T("所选任务已完成。", "The selected task is already done.") : Loc.T("已选中任务，可完成、重命名或删除。", "Task selected; you can complete, rename, or remove it."), true);
    }

    public bool CompleteSelectedTask()
    {
        if (_taskList.SelectedItem is not BoardTask task)
            return Report(Loc.T("请先选择任务。", "Select a task first."), false);
        if (task.Completed) return Report(Loc.T("该任务已经完成。", "This task is already done."), false);
        Replace(task, task with { Completed = true });
        RenderTasks(task with { Completed = true });
        return Report(Loc.T("做得好，又完成了一件事。", "Well done; one more finished."), true);
    }

    public bool RenameSelectedTask(string? title)
    {
        if (_taskList.SelectedItem is not BoardTask task)
            return Report(Loc.T("请先选择要重命名的任务。", "Select a task to rename first."), false);
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 120 || title.Any(char.IsControl))
            return Report(Loc.T("请输入 1–120 字的单行新标题。", "Enter a single-line new title of 1–120 characters."), false);
        if (string.Equals(task.Title, title, StringComparison.OrdinalIgnoreCase))
            return Report(Loc.T("新标题与原标题相同。", "The new title is the same as the old one."), false);
        if (_tasks.Any(other => !ReferenceEquals(other, task) && string.Equals(other.Title, title, StringComparison.OrdinalIgnoreCase)))
            return Report(Loc.T("已有同名任务，请换一个更具体的标题。", "A task with this title already exists; use a more specific one."), false);
        var renamed = task with { Title = title };
        Replace(task, renamed);
        RenderTasks(renamed);
        _taskInput.Clear();
        return Report(Loc.T("已重命名任务。", "Task renamed."), true);
    }

    public bool ToggleSelectedPriority()
    {
        if (_taskList.SelectedItem is not BoardTask task)
            return Report(Loc.T("请先选择任务。", "Select a task first."), false);
        if (task.Completed) return Report(Loc.T("已完成的任务不再调整优先级。", "Completed tasks keep their priority."), false);
        var next = task.Priority == Priority.High ? Priority.Normal : Priority.High;
        var toggled = task with { Priority = next };
        Replace(task, toggled);
        RenderTasks(toggled);
        return Report(next == Priority.High ? Loc.T("已标记为高优先级。", "Marked as high priority.") : Loc.T("已恢复为普通优先级。", "Restored to normal priority."), true);
    }

    public int ClearCompletedTasks()
    {
        var removed = CompletedCount;
        if (removed == 0)
        {
            Report(Loc.T("当前没有已完成的任务。", "There are no completed tasks."), false);
            return 0;
        }
        _tasks.RemoveAll(task => task.Completed);
        RenderTasks();
        Report(Loc.T($"已清除 {removed} 项已完成任务。", $"Cleared {removed} completed task(s)."), true);
        return removed;
    }

    public bool RemoveSelectedTask()
    {
        if (_taskList.SelectedItem is not BoardTask task)
            return Report(Loc.T("请先选择任务。", "Select a task first."), false);
        _tasks.Remove(task);
        RenderTasks();
        return Report(Loc.T("已删除任务。", "Task removed."), true);
    }

    public bool SetFilter(string? filter)
    {
        if (filter is not ("全部" or "待办" or "已完成"))
            return Report(Loc.T("筛选必须为：全部、待办或已完成。", "Filter must be All, Open, or Done."), false);
        if (CurrentFilter != filter)
        {
            SelectChoice(_filter, filter);
            return true;
        }
        RenderTasks(_taskList.SelectedItem as BoardTask);
        return Report(VisibleCount == 0 ? Loc.T("当前筛选下没有任务。", "No tasks under the current filter.") : Loc.T($"正在显示{FilterDisplay(filter)}任务。", $"Showing {FilterDisplay(filter)} tasks."), true);
    }

    private static string FilterDisplay(string value) => value switch
    {
        "全部" => Loc.T("全部", "All"),
        "待办" => Loc.T("待办", "Open"),
        _ => Loc.T("已完成", "Done")
    };

    private static void SelectChoice(ComboBox box, string value)
    {
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is Choice choice && choice.Value == value) { box.SelectedIndex = i; return; }
    }

    private void Replace(BoardTask oldTask, BoardTask newTask) => _tasks[_tasks.IndexOf(oldTask)] = newTask;

    private void DrawTaskItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _taskList.Items[e.Index] is not BoardTask task) return;
        var backColor = e.State.HasFlag(DrawItemState.Selected) ? Color.FromArgb(41, 91, 211) : (e.Index % 2 == 0 ? Color.White : Color.FromArgb(248, 250, 253));
        using var background = new SolidBrush(backColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var foreColor = task.Completed ? Color.FromArgb(120, 132, 148)
            : task.Priority == Priority.High ? Color.FromArgb(178, 58, 48)
            : ForeColor;
        using var foreBrush = new SolidBrush(foreColor);
        var font = new Font("Microsoft YaHei UI", task.Priority == Priority.High && !task.Completed ? 11.5f : 11f, task.Priority == Priority.High && !task.Completed ? FontStyle.Bold : FontStyle.Regular);
        e.Graphics.DrawString(task.ToString(), font, foreBrush, e.Bounds);
        e.DrawFocusRectangle();
    }

    private void RenderTasks(BoardTask? selected = null)
    {
        _rendering = true;
        _taskList.BeginUpdate();
        try
        {
            _taskList.Items.Clear();
            foreach (var task in _tasks.Where(task => CurrentFilter == "全部" || task.Completed == (CurrentFilter == "已完成")))
                _taskList.Items.Add(task);
            if (selected != null) _taskList.SelectedItem = selected;
        }
        finally
        {
            _taskList.EndUpdate();
            _rendering = false;
        }
        _counts.Text = Loc.T(
            $"{TotalCount} 项任务   ·   {PendingCount} 待办（{HighPriorityPendingCount} 高优）   ·   {CompletedCount} 完成   /   显示 {VisibleCount}",
            $"{TotalCount} tasks   ·   {PendingCount} open ({HighPriorityPendingCount} high)   ·   {CompletedCount} done   /   showing {VisibleCount}");
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        _completeButton.Enabled = _taskList.SelectedItem is BoardTask { Completed: false };
        _removeButton.Enabled = _taskList.SelectedItem is BoardTask;
        _renameButton.Enabled = _taskList.SelectedItem is BoardTask;
        _priorityButton.Enabled = _taskList.SelectedItem is BoardTask { Completed: false };
        _clearDoneButton.Enabled = CompletedCount > 0;
    }

    private bool Report(string message, bool success)
    {
        _status.Text = message;
        _status.ForeColor = success ? Color.FromArgb(36, 99, 79) : Color.FromArgb(166, 53, 43);
        return success;
    }

    protected override void OnShown(EventArgs e)
    {
        PuppetRegistry.Register(this);
        var key = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_KEY");
        Environment.SetEnvironmentVariable("PUPPET_TESTGROUND_KEY", null);        if (!string.IsNullOrWhiteSpace(key))
            {
            PuppetKeyVault.SetKey(key);
            try
            {
                _server = new PuppetWebServer()
                    .UseFormControls()
                    .UseAppAgentWithUiText(o =>
                    {
                        o.ProductName = "小步任务看板";
                        o.BlockAgentEndpoints = false; // 双面向并存演示；B-only 成品建议 true（Paranoid）
                        o.BusyWaitMs = 200; // 实例串行等待（SelfTest 409 场景需要短超时）
                    });
                _agentStatus.Text = _server.Start("127.0.0.1:19101")
                    ? "Puppet · 127.0.0.1:19101 · TaskBoard"
                    : Loc.T("Puppet 启动失败；本地看板仍可使用。", "Puppet failed to start; the local board still works.");
            }
            catch (Exception)
            {
                _agentStatus.Text = Loc.T("Puppet 启动失败；本地看板仍可使用。", "Puppet failed to start; the local board still works.");
            }
        }
        else _agentStatus.Text = Loc.T("Puppet 已禁用 · 未提供 PUPPET_TESTGROUND_KEY", "Puppet disabled · no PUPPET_TESTGROUND_KEY");
        base.OnShown(e);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || _shutdownComplete) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        Enabled = false;
        PuppetRegistry.Unregister(((IPuppet)this).AgentInstanceName);
        try
        {
            if (_server != null) await Task.Run(_server.Stop);
        }
        catch (Exception)
        {
            _log.Warn("Puppet 服务停止失败。");
        }
        finally
        {
            _server = null;
            PuppetKeyVault.RefreshKey();
            _shutdownComplete = true;
            Close();
        }
    }

    private static Label MakeLabel(string name, string text, float size, FontStyle style = FontStyle.Regular) => new()
    {
        Name = name, AccessibleName = name, Text = text, Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", size, style), TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = Padding.Empty
    };

    private static Button MakeButton(string name, string text, bool primary = false) => new()
    {
        Name = name, AccessibleName = text.Replace("(&A)", "").Replace("(&C)", "").Replace("(&D)", "").Replace("(&R)", "").Replace("(&P)", "").Replace("(&E)", "").Trim(),
        Text = text, AutoSize = true, Height = 36, MinimumSize = new Size(112, 36),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(41, 91, 211) : Color.White,
        ForeColor = primary ? Color.White : Color.FromArgb(35, 48, 65), Cursor = Cursors.Hand,
        Margin = new Padding(4, 0, 8, 0)
    };
}
