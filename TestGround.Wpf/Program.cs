using System.Windows;
using Puppet.Core;

namespace TestGround.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var selfTest = args.Contains("--self-test", StringComparer.Ordinal);
        using var timeout = new Timer(_ => Environment.Exit(2), null,
            selfTest ? TimeSpan.FromSeconds(60) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        var exitCode = selfTest ? 1 : 0;
        if (selfTest)
        {
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await SelfTest.RunAsync(window);
                    exitCode = 0;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
                }
                finally
                {
                    window.Close();
                }
            };
        }
        application.Run(window);
        if (selfTest)
        {
            if (PuppetRegistry.Resolve("ReadingList") != null) return 1;
            var model = (ReadingList)window.DataContext;
            if (model.AgentStatus.Contains("127.0.0.1:19103", StringComparison.Ordinal)
                && System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == 19103)) return 1;
            if (exitCode == 0) Console.WriteLine("PASS: reading list, WPF binding, unregister and server shutdown.");
        }
        return exitCode;
    }
}
