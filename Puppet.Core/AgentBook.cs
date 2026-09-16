using System.Collections.Concurrent;

namespace Puppet.Core
{
    public enum AgentStatus { Active, Idle, ShuttingDown }

    public enum LockMode { None, Read, Write }

    public sealed class AgentInfo
    {
        public string Id { get; set; }
        public string Purpose { get; set; }
        public string Operation { get; set; }
        public int EstimatedWaitSeconds { get; set; }
        public AgentStatus Status { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime LastHeartbeatAt { get; set; }
        public string PuppetInstanceName { get; set; }
        public string CurrentSite { get; set; }
        public LockMode CurrentLockMode { get; set; }
    }

    public static class AgentBook
    {
        private static readonly ConcurrentDictionary<string, AgentInfo> _agents = new();

        public static void Heartbeat(string agentId, string purpose, string operation, int estWaitSec, string puppetInstanceName, string site = null, LockMode lockMode = LockMode.None)
        {
            if (string.IsNullOrEmpty(agentId)) return;

            _agents.AddOrUpdate(agentId,
                _ => new AgentInfo
                {
                    Id = agentId,
                    Purpose = purpose ?? "",
                    Operation = operation ?? "",
                    EstimatedWaitSeconds = estWaitSec,
                    Status = estWaitSec > 0 ? AgentStatus.Active : AgentStatus.Idle,
                    RegisteredAt = DateTime.Now,
                    LastHeartbeatAt = DateTime.Now,
                    PuppetInstanceName = puppetInstanceName ?? "unknown",
                    CurrentSite = site ?? "",
                    CurrentLockMode = lockMode
                },
                (_, existing) =>
                {
                    existing.Purpose = purpose ?? existing.Purpose;
                    existing.Operation = operation ?? existing.Operation;
                    existing.EstimatedWaitSeconds = estWaitSec;
                    existing.Status = estWaitSec > 0 ? AgentStatus.Active : AgentStatus.Idle;
                    existing.LastHeartbeatAt = DateTime.Now;
                    existing.PuppetInstanceName = puppetInstanceName ?? existing.PuppetInstanceName;
                    existing.CurrentSite = site ?? existing.CurrentSite;
                    existing.CurrentLockMode = lockMode;
                    return existing;
                });

            CleanupStale();
        }

        public static bool CanShutdown(string agentId, int waitTimeoutSec = 30)
        {
            if (!_agents.TryGetValue(agentId, out var info)) return true;
            info.Status = AgentStatus.ShuttingDown;

            var deadline = DateTime.Now.AddSeconds(waitTimeoutSec);
            while (DateTime.Now < deadline)
            {
                var others = _agents.Values.Where(a => a.Id != agentId && a.Status != AgentStatus.ShuttingDown).ToList();
                if (!others.Any()) return true;
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        public static void Release(string agentId)
        {
            _agents.TryRemove(agentId, out _);
        }

        public static List<AgentInfo> GetAll(bool includeStale = false)
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            return _agents.Values
                .Where(a => includeStale || a.LastHeartbeatAt >= cutoff)
                .ToList();
        }

        private static void CleanupStale()
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var key in _agents.Where(kvp => kvp.Value.LastHeartbeatAt < cutoff).Select(kvp => kvp.Key).ToArray())
                _agents.TryRemove(key, out _);
        }
    }
}