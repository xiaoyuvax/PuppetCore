using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Puppet.Core;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;
using Puppet.Core.Wpf;

namespace TestGround.Wpf;

public partial class MainWindow : Window
{
    [PuppetIgnore] private readonly ReadingList _model = new();
    [PuppetIgnore] private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    [PuppetIgnore] private PuppetWebServer? _server;
    [PuppetIgnore] private bool _loaded;
    [PuppetIgnore] private bool _closing;
    [PuppetIgnore] private bool _stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        _presenceTimer.Tick += (_, _) => RefreshPresence();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    [PuppetIgnore]
    internal void RefreshPresence()
    {
        var puppets = PuppetRegistry.Query(includeInternal: true).Instances
            .OrderBy(instance => instance.InstanceName, StringComparer.Ordinal)
            .Select(instance => $"  {instance.InstanceName} ({instance.TypeName}) · {Loc.T("最近活跃", "last active")} {instance.LastActiveAt:HH:mm:ss}")
            .ToList();
        var now = DateTime.Now;
        var agents = AgentBook.GetAll(includeStale: true)
            .OrderBy(agent => agent.Id, StringComparer.Ordinal)
            .ToList();

        string FormatAgent(AgentInfo agent)
        {
            var idle = (int)Math.Max(0, (now - agent.LastHeartbeatAt).TotalSeconds);
            var helpFlag = string.IsNullOrEmpty(agent.Help)
                ? ""
                : string.IsNullOrEmpty(agent.HelpTo)
                    ? Loc.T("[求助] ", "[help] ")
                    : Loc.T($"[求助→{agent.HelpTo}] ", $"[help→{agent.HelpTo}] ");
            var flags = helpFlag + (idle > 60 ? Loc.T("[超过60秒未心跳] ", "[stale >60s] ") : "");
            var line = $"  {flags}{agent.Id} · {agent.PuppetInstanceName}";
            if (!string.IsNullOrEmpty(agent.CurrentSite)) line += $" · {Loc.T("站点", "site")} {agent.CurrentSite}";
            if (!string.IsNullOrEmpty(agent.Operation)) line += $" · {agent.Operation}";
            var status = agent.Status switch
            {
                Puppet.Core.AgentStatus.ShuttingDown => Loc.T("正在关闭", "shutting down"),
                Puppet.Core.AgentStatus.Active => Loc.T("活跃", "active"),
                _ => Loc.T("空闲", "idle")
            };
            line += $" · {idle}s · {status} · {AgentBook.DescribeReportedLock(agent.CurrentLockMode)}";
            if (!string.IsNullOrEmpty(agent.Note)) line += Environment.NewLine + $"    {Loc.T("注意", "note")}：{agent.Note}";
            if (!string.IsNullOrEmpty(agent.Help)) line += Environment.NewLine + $"    {Loc.T("求助", "help")}：{agent.Help}";
            return line;
        }

        var helpAgents = agents.Where(agent => !string.IsNullOrEmpty(agent.Help)).Select(FormatAgent).ToList();
        var otherAgents = agents.Where(agent => string.IsNullOrEmpty(agent.Help)).Select(FormatAgent).ToList();

        var text = new StringBuilder();
        text.AppendLine(Loc.T($"Puppet 注册 · {puppets.Count}", $"Puppet registrations · {puppets.Count}"));
        if (puppets.Count == 0) text.AppendLine(Loc.T("  暂无注册实例。", "  No registered instances."));
        else foreach (var puppet in puppets) text.AppendLine(puppet);
        text.AppendLine(Loc.T($"求助 · {helpAgents.Count}", $"Help requests · {helpAgents.Count}"));
        if (helpAgents.Count == 0) text.AppendLine(Loc.T("  暂无求助。", "  No help requests."));
        else foreach (var agent in helpAgents) text.AppendLine(agent);
        text.AppendLine(Loc.T($"Agent 状态 · {otherAgents.Count}", $"Agent status · {otherAgents.Count}"));
        if (otherAgents.Count == 0) text.AppendLine(Loc.T("  暂无其他 Agent。", "  No other agents."));
        else foreach (var agent in otherAgents) text.AppendLine(agent);
        _model.SetAgentPresence(text.ToString().TrimEnd());
    }

    private void AddClicked(object sender, RoutedEventArgs e)
    {
        if (_model.AddBook(_model.TitleInput, _model.TotalInput)) TitleBox.Focus();
    }

    private void SaveClicked(object sender, RoutedEventArgs e) => _model.UpdateProgress(_model.ReadInput);
    private void RemoveClicked(object sender, RoutedEventArgs e) => _model.RemoveSelectedBook();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        Dispatcher.VerifyAccess();
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("WPF dispatcher context is required.");
        PuppetRegistry.Register(_model);
        RefreshPresence();
        _presenceTimer.Start();
        var key = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_KEY");
        Environment.SetEnvironmentVariable("PUPPET_TESTGROUND_KEY", null);
        if (string.IsNullOrWhiteSpace(key))
        {
            _model.SetAgentStatus(Loc.T("Puppet 已禁用 · 未提供 PUPPET_TESTGROUND_KEY", "Puppet disabled · no PUPPET_TESTGROUND_KEY"));
            return;
        }
        PuppetKeyVault.SetKey(key);
        try
        {
            // 模式 A 宿主（外部无 WebServerBase 的 WPF）同样可启用面向 B：无 WinForms 包则无 L1 文案桥，desc 走 L3 推断
            _server = new PuppetWebServer().UseWpfCapture().UseAppAgent(o => o.ProductName = "阅读书架");
            _model.SetAgentStatus(_server.Start("127.0.0.1:19103")
                ? "Puppet · 127.0.0.1:19103 · ReadingList"
                : Loc.T("Puppet 启动失败；本地书架仍可使用。", "Puppet failed to start; the local shelf still works."));
        }
        catch (Exception)
        {
            _model.SetAgentStatus(Loc.T("Puppet 启动失败；本地书架仍可使用。", "Puppet failed to start; the local shelf still works."));
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _presenceTimer.Stop();
        if (_stopped) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        PuppetRegistry.Unregister("ReadingList");
        try
        {
            if (_server != null) await Task.Run(_server.Stop);
        }
        catch (Exception)
        {
            _model.SetAgentStatus(Loc.T("Puppet 服务停止失败。", "Puppet service failed to stop."));
        }
        finally
        {
            _server = null;
            PuppetKeyVault.RefreshKey();
            _stopped = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
