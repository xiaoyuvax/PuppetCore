using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// 内存产物仓库（提案 §3.6 / §4 要点 7）：随机不可枚举 id、TTL 默认 10 分钟、
    /// 总量上限 256MB / 单条目 100MB（宿主可配）、超限先淘汰到期条目仍超则拒绝、下载强制凭证（由 Handler 保证）。
    /// Agent 契约：拿到 asset URL 立即下载；宿主退出仓库即消失。
    /// </summary>
    internal sealed class AppAgentAssetStore
    {
        private sealed class Entry
        {
            public string Id;
            public PuppetArtifact Artifact;
            public DateTime ExpiresAt;
            public long Bytes;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new();
        private readonly AppAgentOptions _options;

        public AppAgentAssetStore(AppAgentOptions options) => _options = options;

        public string Put(PuppetArtifact artifact)
        {
            var bytes = artifact.Content?.LongLength ?? 0;
            if (bytes > _options.AssetMaxEntryBytes)
                throw new InvalidOperationException(
                    $"artifact exceeds per-entry limit ({bytes} > {_options.AssetMaxEntryBytes} bytes)");

            EvictExpired();
            EvictForCapacity(bytes);

            var id = Guid.NewGuid().ToString("N")[..12];
            _entries[id] = new Entry
            {
                Id = id, Artifact = artifact, Bytes = bytes,
                ExpiresAt = DateTime.UtcNow.AddMilliseconds(_options.AssetTtlMs)
            };
            return $"/appagent/assets/{id}";
        }

        public bool TryGet(string id, out PuppetArtifact artifact, out string fileName)
        {
            artifact = null; fileName = null;
            if (!_entries.TryGetValue(id, out var e)) return false;
            if (DateTime.UtcNow > e.ExpiresAt) { _entries.TryRemove(id, out _); return false; }
            artifact = e.Artifact;
            fileName = string.IsNullOrEmpty(e.Artifact.FileName) ? "artifact" : e.Artifact.FileName;
            return true;
        }

        private void EvictExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _entries)
                if (now > kv.Value.ExpiresAt) _entries.TryRemove(kv.Key, out _);
        }

        /// <summary>容量守卫（提案 §3.6：超限先淘汰到期条目、仍超则拒绝）。
        /// 到期条目已在 Put 入口淘汰；此处只做拒绝判定——存活产物可能尚未被 Agent 下载，不得 LRU 静默丢弃。</summary>
        private void EvictForCapacity(long incoming)
        {
            long total = _entries.Values.Sum(e => e.Bytes);
            if (total + incoming > _options.AssetTotalBytes)
                throw new InvalidOperationException(
                    $"asset store capacity exceeded ({_options.AssetTotalBytes} bytes); retry later");
        }
    }

    /// <summary>
    /// 发现档案仓库（提案 §5.2 / §4.5）：凭证 + endpoint 写入
    /// %LOCALAPPDATA%\Puppet.AppAgents\&lt;app&gt;.json（用户档案 ACL：仅本用户与管理员可读）。
    /// L0 发现入口；跨用户/RDP 读不到 → 拒绝（OS 用户隔离代为执行授权）。
    /// 多实例：文件带 pid 后缀；宿主正常退出清理，异常退出后由陈旧条目清理逻辑兜底。
    /// </summary>
    internal static class AppAgentProfileStore
    {
        private static readonly string DirName = "Puppet.AppAgents";

        public static string Write(string appName, int port, string key)
        {
            try
            {
                var dir = GetDir();
                Directory.CreateDirectory(dir);
                var safe = string.Concat(appName.Split(Path.GetInvalidFileNameChars()));
                var file = Path.Combine(dir, $"{safe}.{Environment.ProcessId}.json");
                var doc = new
                {
                    app = appName,
                    endpoint = $"http://127.0.0.1:{port}",
                    key,
                    protocol = "appagent/1.0",
                    pid = Environment.ProcessId,
                    registeredAt = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(file, JsonConvert.SerializeObject(doc, Formatting.Indented));
                return file;
            }
            catch { return null; } // 档案失败不阻断服务启动（Agent 可走 L1 扫描兜底）
        }

        public static void Remove(string appName)
        {
            try
            {
                var safe = string.Concat(appName.Split(Path.GetInvalidFileNameChars()));
                File.Delete(Path.Combine(GetDir(), $"{safe}.{Environment.ProcessId}.json"));
            }
            catch { }
        }

        /// <summary>清理陈旧条目：进程已死的档案删除（发现时不误报）</summary>
        public static void SweepStale()
        {
            try
            {
                var dir = GetDir();
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var doc = JsonConvert.DeserializeAnonymousType(File.ReadAllText(f),
                            new { pid = 0 });
                        if (doc?.pid > 0)
                        {
                            try { using var p = System.Diagnostics.Process.GetProcessById(doc.pid);
                                  if (!p.HasExited) continue; }
                            catch (ArgumentException) { } // 进程不存在 → 陈旧
                        }
                        File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string GetDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirName);
    }
}
