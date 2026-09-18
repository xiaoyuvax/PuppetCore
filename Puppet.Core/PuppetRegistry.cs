using System.Collections.Concurrent;
using Puppet.Core.Usage;

namespace Puppet.Core
{
    /// <summary>
    /// Puppet 实例注册表。
    /// 使用 WeakReference 避免阻止窗体 GC；Key = AgentInstanceName。
    /// 跨 Web 服务器实例共享（静态字段），支持模式 C 双服务器并行。
    /// 支持标签分类、可见性配置、条件过滤。
    /// </summary>
    public static class PuppetRegistry
    {
        /// <summary>注册条目（实例 + 元数据）</summary>
        private sealed class RegistryEntry
        {
            public WeakReference<IPuppet> Ref;
            public string[] Tags;
            public bool InternalOnly;
            public DateTime RegisteredAt;
            public DateTime LastActiveAt;
        }

        private static readonly ConcurrentDictionary<string, RegistryEntry> _instances = new();
        private static readonly object _compactLock = new();
        private static int _accessCount;

        /// <summary>注册实例（默认公开）</summary>
        public static void Register(IPuppet instance)
            => Register(instance, tags: null, internalOnly: false);

        /// <summary>注册实例（带标签，可配置可见性）</summary>
        /// <param name="instance">要注册的实例</param>
        /// <param name="tags">标签数组，用于分类搜索（如 "preview", "窗体"）</param>
        /// <param name="internalOnly">true=后台内部实例，默认不返回（除非查询时 includeInternal=true）</param>
        public static void Register(IPuppet instance, string[] tags, bool internalOnly = false)
        {
            if (instance == null) return;
            PuppetUiContext.Capture(); //注册通常发生在 UI 线程（窗体构造时），捕获 SynchronizationContext 供跨线程调用兜底
            var name = instance.AgentInstanceName ?? instance.GetType().Name;
            _instances[name] = new RegistryEntry
            {
                Ref = new WeakReference<IPuppet>(instance),
                Tags = tags ?? System.Array.Empty<string>(),
                InternalOnly = internalOnly,
                RegisteredAt = DateTime.Now,
                LastActiveAt = DateTime.Now
            };
            CompactIfNeeded();
            PuppetUsage.Track("registration", $"{name} type={instance.GetType().Name} internalOnly={internalOnly}");
        }

        /// <summary>注销实例</summary>
        public static void Unregister(string name)
        {
            if (name != null) _instances.TryRemove(name, out _);
        }

        /// <summary>更新实例活跃时间（Agent 调用后或实例状态变化时调用）</summary>
        public static void TouchActivity(string name)
        {
            if (name != null && _instances.TryGetValue(name, out var entry))
                entry.LastActiveAt = DateTime.Now;
        }

        /// <summary>按名称解析实例</summary>
        public static IPuppet Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_instances.TryGetValue(name, out var entry) && entry.Ref.TryGetTarget(out var target))
            {
                entry.LastActiveAt = DateTime.Now;
                PuppetUsage.Track("resolve", name);
                return target;
            }
            _instances.TryRemove(name, out _);
            return null;
        }

        /// <summary>条件查询实例列表</summary>
        /// <param name="typeFilter">类型名过滤（模糊匹配，null=不限）</param>
        /// <param name="tagFilter">标签过滤（任一匹配即返回，null=不限）</param>
        /// <param name="includeInternal">是否包含 internalOnly 实例（默认 false，确保无盲区时设 true）</param>
        /// <param name="activeWithinMinutes">仅返回 N 分钟内活跃的实例（0=不限）</param>
        /// <param name="keyword">关键词搜索（匹配实例名/类型名/标签）</param>
        /// <param name="offset">分页偏移</param>
        /// <param name="limit">每页数量（0=不限）</param>
        public static InstanceListResult Query(
            string typeFilter = null,
            string[] tagFilter = null,
            bool includeInternal = false,
            int activeWithinMinutes = 0,
            string keyword = null,
            int offset = 0,
            int limit = 0)
        {
            var now = DateTime.Now;
            var alive = new List<InstanceInfo>();

            foreach (var kvp in _instances)
            {
                if (!kvp.Value.Ref.TryGetTarget(out var target))
                {
                    _instances.TryRemove(kvp.Key, out _);
                    continue;
                }

                if (kvp.Value.InternalOnly && !includeInternal) continue;

                var typeName = target.GetType().Name;
                if (typeFilter != null && !typeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (tagFilter != null && tagFilter.Length > 0
                    && !kvp.Value.Tags.Any(t => tagFilter.Any(f => t.Contains(f, StringComparison.OrdinalIgnoreCase))))
                    continue;

                if (activeWithinMinutes > 0
                    && (now - kvp.Value.LastActiveAt).TotalMinutes > activeWithinMinutes)
                    continue;

                if (keyword != null)
                {
                    var matches = kvp.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                               || typeName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                               || kvp.Value.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                    if (!matches) continue;
                }

                alive.Add(new InstanceInfo
                {
                    InstanceName = kvp.Key,
                    TypeName = typeName,
                    Tags = kvp.Value.Tags,
                    InternalOnly = kvp.Value.InternalOnly,
                    LastActiveAt = kvp.Value.LastActiveAt
                });
            }

            var total = alive.Count;
            var paged = limit > 0 ? alive.Skip(offset).Take(limit).ToList() : alive.Skip(offset).ToList();

            return new InstanceListResult
            {
                Total = total,
                Offset = offset,
                Limit = limit,
                Instances = paged
            };
        }

        /// <summary>所有存活实例（含 internal）</summary>
        public static IEnumerable<IPuppet> AllAlive()
        {
            foreach (var kvp in _instances)
                if (kvp.Value.Ref.TryGetTarget(out var t))
                    yield return t;
        }

        /// <summary>清理已 GC 的弱引用</summary>
        public static void Compact()
        {
            lock (_compactLock)
            {
                foreach (var key in _instances.Keys.ToArray())
                    if (!_instances[key].Ref.TryGetTarget(out _))
                        _instances.TryRemove(key, out _);
            }
        }

        private static void CompactIfNeeded()
        {
            if (System.Threading.Interlocked.Increment(ref _accessCount) % 50 == 0) Compact();
        }
    }

    /// <summary>
    /// UI 线程上下文：PuppetRegistry.Register 在 UI 线程调用时捕获 SynchronizationContext 与线程引用。
    /// WinForm 控件在句柄未创建时 InvokeRequired 恒为 false（无法据此识别跨线程调用），
    /// 此时 Agent 从 Web 线程（MTA）调用窗体方法会导致 WebView2 等要求 STA 的组件初始化失败（RPC_E_CHANGED_MODE）。
    /// 本上下文提供兜底切换：非 UI 线程访问 IPuppet 成员时仍可切回 UI 线程执行。
    /// </summary>
    internal static class PuppetUiContext
    {
        private static SynchronizationContext _context;
        private static System.Threading.Thread _uiThread;

        /// <summary>
        /// 在注册线程（通常是 UI 线程）捕获上下文。
        /// 仅当当前线程存在 SynchronizationContext 时捕获（Web/线程池线程的 Current 为 null，不会覆盖 UI 上下文）。
        /// 只认首个捕获：全局 fallback 上下文必须是主 UI 线程。若在其它拥有消息泵的线程（如
        /// 独立线程托管的编辑器窗体）上注册时覆盖捕获，此后所有经 fallback 的 marshal 都会
        /// 投递到该线程：主线程窗体成员的读取被迫跨线程 SendMessage，与目标线程自身的 marshal
        /// 等待相互交叉时形成死锁（转储证据：主线程卡在 marshal 回调的 GetWindowTextLength）。
        /// 各窗体自身的线程亲和已由 RunOnUi 的 ISynchronizeInvoke 分支正确处理，无需在此覆盖。
        /// </summary>
        public static void Capture()
        {
            if (_context != null) return; // 首个（主 UI 线程）获胜，禁止后续异线程覆盖
            var ctx = SynchronizationContext.Current;
            if (ctx == null) return;
            _context = ctx;
            _uiThread = System.Threading.Thread.CurrentThread;
        }

        /// <summary>当前线程非 UI 线程且存在可切换的 UI 上下文</summary>
        public static bool ShouldSwitch()
            => _context != null && !ReferenceEquals(_uiThread, System.Threading.Thread.CurrentThread);

        /// <summary>同步切换到 UI 线程执行（阻塞调用线程直至完成，与 Control.Invoke 语义一致）</summary>
        public static object Send(Func<object> action)
        {
            object result = null;
            _context.Send(_ => result = action(), null);
            return result;
        }
    }

    /// <summary>实例列表查询结果（含分页信息）</summary>
    public class InstanceListResult
    {
        /// <summary>总数</summary>
        public int Total { get; set; }
        /// <summary>偏移</summary>
        public int Offset { get; set; }
        /// <summary>每页数量</summary>
        public int Limit { get; set; }
        /// <summary>实例列表</summary>
        public List<InstanceInfo> Instances { get; set; }
    }

    /// <summary>实例摘要信息（轻量，不含运行时值）</summary>
    public class InstanceInfo
    {
        /// <summary>实例名</summary>
        public string InstanceName { get; set; }
        /// <summary>类型名</summary>
        public string TypeName { get; set; }
        /// <summary>标签</summary>
        public string[] Tags { get; set; }
        /// <summary>是否内部实例</summary>
        public bool InternalOnly { get; set; }
        /// <summary>最后活跃时间</summary>
        public DateTime LastActiveAt { get; set; }
    }
}
