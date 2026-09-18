namespace TestGround.Winform;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new TaskBoardForm();
        var selfTest = args.Contains("--self-test", StringComparer.Ordinal);
        var exitCode = selfTest ? 1 : 0;
        if (selfTest)
        {
            form.Shown += async (_, _) =>
            {
                await Task.Yield();
                try
                {
                    await SelfTest.RunAsync(form);
                    exitCode = 0;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
                }
                finally
                {
                    form.Close();
                }
            };
        }
        Application.Run(form);
        if (selfTest)
        {
            if (Puppet.Core.PuppetRegistry.Resolve("TaskBoard") != null) return 1;
            if (form.AgentStatus.Contains("127.0.0.1:19101", StringComparison.Ordinal)
                && System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == 19101)) return 1;
            if (exitCode == 0) Console.WriteLine("PASS: task board user operations, unregister and server cleanup.");
        }
        return exitCode;
    }
}
