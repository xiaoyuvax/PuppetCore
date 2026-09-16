using System.Collections.Concurrent;

namespace Puppet.Core
{
    public sealed class SiteLock
    {
        private sealed class LockEntry
        {
            public string Site;
            public string OwnerAgentId;
            public LockMode Mode;
            public int ReadCount;
            public DateTime AcquiredAt;
        }

        private static readonly ConcurrentDictionary<string, LockEntry> _locks = new();
        private static readonly object _gate = new();

        public static bool TryAcquire(string site, string agentId, LockMode mode, int timeoutMs = 5000)
        {
            if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(agentId) || mode == LockMode.None) return true;

            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                lock (_gate)
                {
                    if (!_locks.TryGetValue(site, out var entry))
                    {
                        _locks[site] = new LockEntry
                        {
                            Site = site,
                            OwnerAgentId = agentId,
                            Mode = mode,
                            ReadCount = mode == LockMode.Read ? 1 : 0,
                            AcquiredAt = DateTime.Now
                        };
                        return true;
                    }

                    if (mode == LockMode.Write)
                    {
                        if (entry.OwnerAgentId == agentId && entry.Mode == LockMode.Write) return true;
                        if (entry.Mode == LockMode.Read && entry.ReadCount > 0) { /* wait */ }
                        else if (entry.Mode == LockMode.Write && entry.OwnerAgentId != agentId) { /* wait */ }
                        else
                        {
                            entry.Mode = LockMode.Write;
                            entry.OwnerAgentId = agentId;
                            entry.ReadCount = 0;
                            return true;
                        }
                    }
                    else if (mode == LockMode.Read)
                    {
                        if (entry.Mode == LockMode.Write && entry.OwnerAgentId != agentId) { /* wait */ }
                        else
                        {
                            entry.Mode = LockMode.Read;
                            entry.ReadCount++;
                            if (entry.OwnerAgentId == null) entry.OwnerAgentId = agentId;
                            return true;
                        }
                    }
                }
                System.Threading.Thread.Sleep(20);
            }
            return false;
        }

        public static void Release(string site, string agentId)
        {
            if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(agentId)) return;

            lock (_gate)
            {
                if (!_locks.TryGetValue(site, out var entry)) return;
                if (entry.OwnerAgentId != agentId) return;

                if (entry.Mode == LockMode.Read)
                {
                    if (--entry.ReadCount <= 0) _locks.TryRemove(site, out _);
                }
                else
                {
                    _locks.TryRemove(site, out _);
                }
            }
        }

        public static LockStatus GetStatus(string site)
        {
            if (string.IsNullOrEmpty(site)) return new LockStatus { Site = site, Mode = LockMode.None };
            if (_locks.TryGetValue(site, out var e))
                return new LockStatus { Site = site, Mode = e.Mode, OwnerAgentId = e.OwnerAgentId, ReadCount = e.ReadCount, Since = e.AcquiredAt };
            return new LockStatus { Site = site, Mode = LockMode.None };
        }

        public static List<LockStatus> GetAll()
        {
            return _locks.Values.Select(e => new LockStatus
            {
                Site = e.Site,
                Mode = e.Mode,
                OwnerAgentId = e.OwnerAgentId,
                ReadCount = e.ReadCount,
                Since = e.AcquiredAt
            }).ToList();
        }
    }

    public class LockStatus
    {
        public string Site { get; set; }
        public LockMode Mode { get; set; }
        public string OwnerAgentId { get; set; }
        public int ReadCount { get; set; }
        public DateTime Since { get; set; }
    }
}