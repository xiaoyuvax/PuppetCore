using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppet.Core.AppAgent
{
    /// <summary>UseAppAgent 配置选项（提案 §7）</summary>
    public sealed class AppAgentOptions
    {
        /// <summary>产品名（档案文件名与 manifest.productName）</summary>
        public string ProductName { get; set; } = "app";

        /// <summary>实例绑定清单：null = 绑定 PuppetRegistry 全部存活实例（B-only 成品常态）</summary>
        public string[] InstanceNames { get; set; }

        /// <summary>action 串行队列等待超时（409 busy 前的毫秒数）</summary>
        public int BusyWaitMs { get; set; } = 10_000;

        /// <summary>产物 TTL（毫秒，默认 10 分钟）</summary>
        public int AssetTtlMs { get; set; } = 600_000;

        /// <summary>产物仓库总量上限（字节，默认 256MB）</summary>
        public long AssetTotalBytes { get; set; } = 256L * 1024 * 1024;

        /// <summary>产物单条目上限（字节，默认 100MB）</summary>
        public long AssetMaxEntryBytes { get; set; } = 100L * 1024 * 1024;

        /// <summary>
        /// Paranoid 开关（v0.5.4 定案）：true 时 /agent/* 一律 404。
        /// 防作者习惯残留事故链：成品二进制中遗留 SetKey("字面量") 或历史 Register() 时，
        /// 字符串常量可被反编译提取（钥匙写在二进制里 = 锁焊在门外）。B-only 产品建议开启。
        /// </summary>
        public bool BlockAgentEndpoints { get; set; }

        /// <summary>
        /// 动态绑定（opt-in，默认 false）：开启后新注册的 IPuppet 实例自动进入 B 通道绑定表
        /// （按名去重替换），注销时自动移除。适用于运行期创建窗体的宿主（如编辑器类 WinForms 应用）。
        /// manifest 仍是编译期范式形状（暴露面判定不变，§3.5），只扩实例集合；
        /// 成品安全默认不变——不开启则维持启动快照语义。
        /// </summary>
        public bool DynamicBinding { get; set; }
    }

    /// <summary>面向 B 的实例绑定</summary>
    public sealed class AppAgentInstance
    {
        public IPuppet Instance { get; init; }
        public string Name { get; init; }
        public string ProductName { get; init; }
        public AppAgentRuntime Runtime { get; init; }
    }

    /// <summary>
    /// 面向 B 运行时：UseAppAgent 时创建，持有选项/实例/资产仓库/执行器。
    /// 纯本地模式（MVP）：凭证来自档案文件；AllowRemote 远程授权列二期。
    /// </summary>
    public sealed class AppAgentRuntime
    {
        public AppAgentOptions Options { get; }
        internal AppAgentAssetStore Assets { get; }
        internal AppAgentExecutor Executor { get; }

        private readonly List<AppAgentInstance> _instances = new();
        private readonly object _instancesLock = new();

        /// <summary>已绑定实例（快照副本）。Web 线程枚举与 UI 线程动态注册并发，取快照避免枚举中变更。</summary>
        internal List<AppAgentInstance> Instances
        {
            get { lock (_instancesLock) return new List<AppAgentInstance>(_instances); }
        }

        public AppAgentRuntime(AppAgentOptions options)
        {
            Options = options ?? new AppAgentOptions();
            Assets = new AppAgentAssetStore(Options);
            Executor = new AppAgentExecutor(this);
        }

        /// <summary>发现档案文件路径（宿主正常退出清理后，测试需自行验证；异常退出由 SweepStale 兑底）。</summary>
        public string ProfileFile => _profileFile;

        /// <summary>凭证校验：与档案 key 精确匹配（固定时间比较，防时序侧信道）</summary>
        internal bool Authorize(string requestKey)
        {
            if (string.IsNullOrEmpty(requestKey) || _profileKey == null) return false;
            return FixedTimeEquals(requestKey, _profileKey);
        }

        private string _profileKey;

        /// <summary>绑定实例并写档案（发现 L0）。port 用于档案 endpoint 记录。
        /// 模式 A/C（外部 Web 服务器）在 Start 成功后由宿主显式调用；模式 B 由 PuppetWebServer.Start 自动调用。
        /// DynamicBinding 开启时先订阅注册事件再取快照（消除订阅与快照间的窗口；重叠实例按名去重）。</summary>
        public void Bind(int port)
        {
            _profileKey = Guid.NewGuid().ToString("N");

            if (Options.DynamicBinding)
            {
                // 先退订再订阅：Stop/Start 重启会再次 Bind，防同一 handler 重复挂接
                PuppetRegistry.InstanceRegistered -= OnInstanceRegistered;
                PuppetRegistry.InstanceUnregistered -= OnInstanceUnregistered;
                PuppetRegistry.InstanceRegistered += OnInstanceRegistered;
                PuppetRegistry.InstanceUnregistered += OnInstanceUnregistered;
            }

            var names = Options.InstanceNames;
            var candidates = PuppetRegistry.Query(includeInternal: true).Instances;
            IEnumerable<IPuppet> targets = names == null
                ? PuppetRegistry.AllAlive()
                : candidates.Where(i => names.Contains(i.InstanceName))
                            .Select(i => PuppetRegistry.Resolve(i.InstanceName))
                            .Where(p => p != null);

            foreach (var p in targets.ToList()) AddInstance(p);

            AppAgentProfileStore.SweepStale();
            _profileFile = AppAgentProfileStore.Write(Options.ProductName, port, _profileKey);
        }

        /// <summary>加入（或按名替换）一个绑定实例。Bind 快照与动态注册事件共用，按名去重。</summary>
        public void AddInstance(IPuppet p)
        {
            if (p == null) return;
            var name = p.AgentInstanceName ?? p.GetType().Name;
            lock (_instancesLock)
            {
                _instances.RemoveAll(i => string.Equals(i.Name, name, StringComparison.Ordinal));
                _instances.Add(new AppAgentInstance
                {
                    Instance = p,
                    Name = name,
                    ProductName = Options.ProductName,
                    Runtime = this
                });
            }
        }

        /// <summary>按名移除绑定实例（动态绑定时由注销事件自动调用；AppAgentInstance 持强引用，不移除会阻止窗体 GC）。</summary>
        public void RemoveInstance(string name)
        {
            lock (_instancesLock)
                _instances.RemoveAll(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        }

        private void OnInstanceRegistered(IPuppet p) => AddInstance(p);
        private void OnInstanceUnregistered(string name) => RemoveInstance(name);

        private string _profileFile;

        /// <summary>宿主关闭时清理档案（正常退出路径）</summary>
        public void Unbind()
        {
            if (Options.DynamicBinding)
            {
                PuppetRegistry.InstanceRegistered -= OnInstanceRegistered;
                PuppetRegistry.InstanceUnregistered -= OnInstanceUnregistered;
            }
            try { if (_profileFile != null) AppAgentProfileStore.Remove(Options.ProductName); } catch { }
            _profileFile = null;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

}
