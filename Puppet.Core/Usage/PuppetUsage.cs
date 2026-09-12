using System.Collections.Concurrent;
using Wima.Log;

namespace Puppet.Core.Usage
{
    /// <summary>
    /// Puppet 框架级使用日志（导入 WimaLogger）。
    /// 记录所有功能的利用情况（端点/方法调用次数与明细）与意外（异常），供 Agent 后续做优化观测与利用率分析。
    /// - 日志同时写入内存缓冲（LogBuf，可通过 /agent/logs?name=Puppet 读取）与磁盘（logs/Puppet_*.log，便于持久化分析）。
    /// - 使用计数器按类别聚合调用次数，供 /agent/usage 返回结构化利用率摘要。
    /// - 严格只记录类别/调用方/次数等元信息，绝不记录密钥、参数值等敏感内容。
    /// 关注点：本组件是"观测"，独立于各宿主实例的 AgentLog；Agent 显式调用 Track/Error 以点亮日志。
    /// </summary>
    public static class PuppetUsage
    {
        private static readonly Lazy<WimaLogger> _log = new(() =>
            new WimaLogger("Puppet", logMode: LogMode.Native | LogMode.Console));

        /// <summary>按类别（endpoint/invoke/registration/resolve 等）聚合的调用计数</summary>
        private static readonly ConcurrentDictionary<string, long> _counts = new();

        /// <summary>是否记录使用日志（默认开启；异常时置无损开关，不影响主流程）</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>框架日志实例（已注册进 WimaLogger.LogBook，供 /agent/logs?name=Puppet 读取）</summary>
        public static WimaLogger Log => _log.Value;

        /// <summary>记录一次能力调用（计数 + 明细）。category 用于聚合统计，detail 仅用于日志明细。</summary>
        public static void Track(string category, string detail)
        {
            if (!Enabled) return;
            _counts.AddOrUpdate(category, 1L, (_, v) => v + 1L);
            try { _log.Value.Info($"[{category}]\t{detail}"); }
            catch { /* 日志故障不得影响主流程 */ }
        }

        /// <summary>记录一次意外/异常（计入 Error 计数）</summary>
        public static void Error(string category, string detail)
        {
            if (!Enabled) return;
            _counts.AddOrUpdate("error", 1L, (_, v) => v + 1L);
            try { _log.Value.Error($"[{category}]\t{detail}"); }
            catch { }
        }

        /// <summary>记录一次意外/异常（计入 Error 计数，附带异常对象）</summary>
        public static void Error(string category, string detail, Exception ex)
        {
            if (!Enabled) return;
            _counts.AddOrUpdate("error", 1L, (_, v) => v + 1L);
            try { _log.Value.Error($"[{category}]\t{detail} {ex?.Message}", ex); }
            catch { }
        }

        /// <summary>各类别调用计数（按次数降序）</summary>
        public static IReadOnlyDictionary<string, long> Counts()
            => _counts.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        /// <summary>所有类别总调用次数</summary>
        public static long Total()
        {
            long sum = 0;
            foreach (var v in _counts.Values) sum += v;
            return sum;
        }

        /// <summary>可重置所有计数（观测窗口开始/结束时调用）</summary>
        public static void Reset()
        {
            _counts.Clear();
            try { _log.Value.Info("[usage]\t计数已重置"); }
            catch { }
        }
    }
}