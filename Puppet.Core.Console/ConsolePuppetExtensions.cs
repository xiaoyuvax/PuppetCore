using System.Diagnostics;
using Wima.Core;

namespace Puppet.Core.Console
{
    /// <summary>
    /// Console 应用 Agent 扩展方法。
    /// 提供 Console 进程状态描述（进程信息、环境变量、命令行参数、uptime、内存）。
    /// Agent 若需向 Console 程序发送命令，请通过 /agent/invoke 调用公共方法。
    /// </summary>
    public static class ConsoleAgentExtensions
    {
        /// <summary>Console 状态：进程信息、环境变量、命令行参数、退出码、uptime、内存</summary>
        /// <param name="host">实现 IPuppet 的宿主实例</param>
        public static string DescribeConsoleState(this IPuppet host)
        {
            var proc = Process.GetCurrentProcess();
            return Utils.ToJson(new
            {
                ok = true,
                pid = Environment.ProcessId,
                machineName = Environment.MachineName,
                args = Environment.GetCommandLineArgs(),
                workingDir = Environment.CurrentDirectory,
                exitCode = Environment.ExitCode,
                uptime = (System.DateTime.Now - proc.StartTime).TotalSeconds,
                threadCount = proc.Threads.Count,
                memoryMB = proc.WorkingSet64 / 1024.0 / 1024.0
            });
        }
    }
}
