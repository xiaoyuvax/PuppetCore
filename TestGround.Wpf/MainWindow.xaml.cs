using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Puppet.Core;
using Puppet.Core.AppAgent;
using Puppet.Core.Web;

namespace TestGround.Wpf;

public partial class MainWindow : Window
{
    [PuppetIgnore] private readonly ReadingList _model = new();
    [PuppetIgnore] private PuppetWebServer? _server;
    [PuppetIgnore] private bool _loaded;
    [PuppetIgnore] private bool _closing;
    [PuppetIgnore] private bool _stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Loaded += OnLoaded;
        Closing += OnClosing;
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
            _server = new PuppetWebServer().UseAppAgent(o => o.ProductName = "阅读书架");
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
