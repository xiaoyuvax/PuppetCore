using System.Globalization;
using Puppet.Core;
using Puppet.Core.Usage;
using Puppet.Core.Web;
using Wima.Log;

namespace TestGround.Console;

using Console = System.Console;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(Commands.Help);
            return 0;
        }
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--self-test" or "--serve")))
        {
            Console.Error.WriteLine("Usage: TestGround.Console [--help|--self-test|--serve]");
            return 2;
        }
        var selfTest = args is ["--self-test"];
        var serve = args is ["--serve"];
        var key = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_KEY");
        Environment.SetEnvironmentVariable("PUPPET_TESTGROUND_KEY", null);
        if (key is not null && (key.Length is < 16 or > 256 || key.Any(c => c < '!' || c > '~')))
        {
            Console.Error.WriteLine("PUPPET_TESTGROUND_KEY must contain 16..256 printable non-space ASCII characters.");
            return 2;
        }
        if (key is null && (selfTest || serve))
        {
            Console.Error.WriteLine("PUPPET_TESTGROUND_KEY is required for --self-test and --serve.");
            return 2;
        }
        PuppetUsage.Enabled = false;
        var ledger = new ExpenseLedger();
        PuppetWebServer? server = null;
        var exitCode = 0;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; ledger.Stop(); };
        Console.CancelKeyPress += cancel;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (key is not null)
            {
                PuppetKeyVault.SetKey(key);
                PuppetRegistry.Register(ledger);
                server = new PuppetWebServer { LogMan = new WimaLogger("ExpenseLedger.Server", logMode: (LogMode)0) };
                server.UseHandler(req =>
                {
                    var length = req.Headers?.Get("Content-Length");
                    if (!string.IsNullOrEmpty(req.Headers?.Get("Transfer-Encoding")) ||
                        (req.Method == "POST" && length is null) ||
                        (length is not null && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > 4096)))
                    {
                        req.Response.StatusCode = 413;
                        return true;
                    }
                    return false;
                });
                if (!server.Start("127.0.0.1:19102")) throw new InvalidOperationException("Server start failed.");
                Console.Error.WriteLine("Agent: 127.0.0.1:19102 / ExpenseLedger");
            }
            else Console.Error.WriteLine("Agent disabled: no PUPPET_TESTGROUND_KEY; local ledger only.");
            if (selfTest)
                await Task.Run(() => SelfTest.Run(ledger, key!)).WaitAsync(timeout.Token);
            else
                await RunCommands(ledger, serve);
        }
        catch (OperationCanceledException) when (selfTest && timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine("Self-test timed out.");
            exitCode = 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine(selfTest ? "Self-test failed; see last check name." : "Host failed to start or run.");
            exitCode = 1;
        }
        finally
        {
            ledger.Stop();
            PuppetRegistry.Unregister("ExpenseLedger");
            try
            {
                if (server is not null) await Task.Run(server.Stop).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Server shutdown failed.");
                exitCode = 1;
            }
            PuppetKeyVault.RefreshKey();
            Console.CancelKeyPress -= cancel;
        }
        if (selfTest && exitCode == 0)
        {
            if (PuppetRegistry.Resolve("ExpenseLedger") is not null || !ledger.IsStopping) return 1;
            Console.WriteLine("PASS lifecycle cleanup; self-test complete.");
        }
        return exitCode;
    }

    private static async Task RunCommands(ExpenseLedger ledger, bool serve)
    {
        if (!Console.IsInputRedirected) Console.WriteLine(Commands.Help);
        while (!ledger.IsStopping)
        {
            try
            {
                if (!Console.IsInputRedirected) Console.Write("> ");
                var line = await Task.Run(() => Commands.ReadLine(Console.In)).WaitAsync(ledger.Stopping);
                if (line is null)
                {
                    if (serve) await Task.Delay(Timeout.Infinite, ledger.Stopping);
                    else ledger.Stop();
                    break;
                }
                Commands.Execute(ledger, line, Console.Out);
            }
            catch (OperationCanceledException) when (ledger.IsStopping) { break; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }
    }
}
