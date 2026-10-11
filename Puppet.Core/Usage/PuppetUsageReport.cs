using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Puppet.Core.Usage
{
    /// <summary>
    /// usage 长周期聚合报告：从磁盘日志 logs/Puppet_*.log 聚合累计调用计数，
    /// 与当前注册实例的 describe 可见方法做差集，输出固定三段报告（self-test 结尾调用）。
    /// - 持久层即日志文件本身：长周期不清零，要开新观测窗口就删日志文件。
    /// - 只统计方法调用（invoke）与控件操作（control）；属性读写（get/set）粒度过细不计。
    /// - 保守性接口（框架本体，namespace 以 Puppet.Core 开头的类型）不进 unused 评估。
    /// </summary>
    public static class PuppetUsageReport
    {
        private static readonly Regex CatPattern = new(@"\[(?<cat>[^\]]+)\]\t", RegexOptions.Compiled);

        /// <summary>扫描日志目录，按 category 聚合累计计数</summary>
        /// <param name="logDir">日志目录（默认相对路径 logs，即本工程工作目录下的日志）</param>
        public static Dictionary<string, long> Aggregate(string logDir = "logs")
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (!Directory.Exists(logDir)) return counts;
            foreach (var file in Directory.GetFiles(logDir, "Puppet_*.log"))
            {
                IEnumerable<string> lines;
                try
                {
                    // 日志可能正被 WimaLogger 持有写句柄；以共享读方式打开，锁冲突则跳过该文件
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8, true);
                    lines = sr.ReadToEnd().Split('\n');
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var line in lines)
                {
                    var m = CatPattern.Match(line);
                    if (!m.Success) continue;
                    var cat = m.Groups["cat"].Value;
                    counts.TryGetValue(cat, out var v);
                    counts[cat] = v + 1;
                }
            }
            return counts;
        }

        /// <summary>生成三段式报告文本（framework usage / host surface / unused 候选）</summary>
        /// <param name="logDir">日志目录（默认 logs）</param>
        public static string Build(string logDir = "logs")
        {
            var counts = Aggregate(logDir);
            var sb = new StringBuilder();
            sb.AppendLine($"== usage report (logDir={logDir}, files={SafeFileCount(logDir)}) ==");

            // 段一：framework usage —— 非 invoke/control 类别原样 + invoke/control 聚合总数
            sb.AppendLine("[framework usage]");
            long invokeTotal = 0, controlTotal = 0;
            foreach (var kv in counts.OrderByDescending(k => k.Value))
            {
                if (kv.Key.StartsWith("invoke::", StringComparison.Ordinal)) { invokeTotal += kv.Value; continue; }
                if (kv.Key.StartsWith("control::", StringComparison.Ordinal)) { controlTotal += kv.Value; continue; }
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }
            if (invokeTotal > 0) sb.AppendLine($"  invoke(total): {invokeTotal}");
            if (controlTotal > 0) sb.AppendLine($"  control(total): {controlTotal}");
            if (counts.Count == 0) sb.AppendLine("  (no usage logs)");

            // 段二：host surface —— 按宿主类型聚合 invoke/control
            sb.AppendLine("[host surface]");
            var byType = new Dictionary<string, (long Invoke, long Control)>(StringComparer.Ordinal);
            foreach (var kv in counts)
            {
                if (kv.Key.StartsWith("invoke::", StringComparison.Ordinal))
                {
                    var type = ExtractType(kv.Key, "invoke::");
                    byType.TryGetValue(type, out var cur);
                    byType[type] = (cur.Invoke + kv.Value, cur.Control);
                }
                else if (kv.Key.StartsWith("control::", StringComparison.Ordinal))
                {
                    var type = ExtractType(kv.Key, "control::");
                    byType.TryGetValue(type, out var cur);
                    byType[type] = (cur.Invoke, cur.Control + kv.Value);
                }
            }
            foreach (var kv in byType.OrderByDescending(k => k.Value.Invoke + k.Value.Control))
                sb.AppendLine($"  {kv.Key}: invoke={kv.Value.Invoke} control={kv.Value.Control}");
            if (byType.Count == 0) sb.AppendLine("  (no method/control calls recorded)");

            // 段三：unused 候选 —— 存活实例的 describe 可见 public/exposed 方法中从未被 invoke 的
            sb.AppendLine("[unused candidates]");
            var unusedFound = false;
            foreach (var instance in PuppetRegistry.AllAlive())
            {
                var type = instance.GetType();
                // 保守性过滤：框架本体类型不进评估
                if (type.Namespace != null && type.Namespace.StartsWith("Puppet.Core", StringComparison.Ordinal)) continue;
                var typeName = type.Name;
                foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.IsSpecialName) continue;
                    if (m.GetCustomAttribute<PuppetIgnoreAttribute>() != null) continue;
                    var isPublic = m.IsPublic;
                    var isExposed = m.GetCustomAttribute<PuppetExposeAttribute>() != null;
                    if (!isPublic && !isExposed) continue;
                    var key = $"invoke::{typeName}.{m.Name}";
                    if (!counts.ContainsKey(key))
                    {
                        sb.AppendLine($"  {typeName}.{m.Name} (never invoked)");
                        unusedFound = true;
                    }
                }
            }
            if (!unusedFound) sb.AppendLine("  (none)");
            return sb.ToString();
        }

        private static string ExtractType(string category, string prefix)
        {
            // control::action 无类型名 → 归入 "(control)"；invoke::Type.Method → Type
            var rest = category.Substring(prefix.Length);
            if (prefix == "control::") return "(control)";
            var dot = rest.IndexOf('.');
            return dot > 0 ? rest[..dot] : rest;
        }

        private static int SafeFileCount(string logDir)
        {
            try { return Directory.Exists(logDir) ? Directory.GetFiles(logDir, "Puppet_*.log").Length : 0; }
            catch { return 0; }
        }
    }
}
