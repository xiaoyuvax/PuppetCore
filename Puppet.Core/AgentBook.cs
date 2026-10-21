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
        /// <summary>给其他 Agent 的注意事项（如「不要关闭宿主」「不要操作某 site」）；空串表示无。</summary>
        public string Note { get; set; }
        /// <summary>向其他 Agent 的求助/协作邀请（自由文本，如「需要人核对保存对话框」）；空串表示无。</summary>
        public string Help { get; set; }
        /// <summary>当前求助的更新时间；无求助时为 null。</summary>
        public DateTime? HelpUpdatedAt { get; set; }
        /// <summary>求助的目标 Agent Id；空串表示广播。</summary>
        public string HelpTo { get; set; }
    }

    public sealed class HelpRecord
    {
        public string AgentId { get; set; }
        public string Text { get; set; }
        public string To { get; set; }
        public DateTime At { get; set; }
    }

    public static class AgentBook
    {
        private static readonly ConcurrentDictionary<string, AgentInfo> _agents = new();
        private static readonly ConcurrentQueue<HelpRecord> _helpLog = new();
        private const int MaxHelpLog = 20;

        public static void Heartbeat(string agentId, string purpose, string operation, int estWaitSec, string puppetInstanceName, string site = null, LockMode lockMode = LockMode.None, string note = null, string help = null, string helpTo = null)
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
                    CurrentLockMode = lockMode,
                    Note = note ?? "",
                    Help = help ?? "",
                    HelpUpdatedAt = string.IsNullOrEmpty(help) ? null : DateTime.Now,
                    HelpTo = string.IsNullOrEmpty(help) ? "" : (helpTo ?? "")
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
                    existing.Note = note ?? existing.Note;
                    if (help != null)
                    {
                        var helpToValue = string.IsNullOrEmpty(help) ? "" : (helpTo ?? existing.HelpTo ?? "");
                        if (!string.Equals(existing.Help, help, StringComparison.Ordinal)
                            || !string.Equals(existing.HelpTo, helpToValue, StringComparison.Ordinal))
                        {
                            existing.Help = help;
                            existing.HelpTo = helpToValue;
                            existing.HelpUpdatedAt = string.IsNullOrEmpty(help) ? null : DateTime.Now;
                        }
                    }
                    else if (helpTo != null && !string.IsNullOrEmpty(existing.Help))
                    {
                        existing.HelpTo = helpTo;
                    }
                    return existing;
                });

            if (!string.IsNullOrEmpty(help) && !_helpLog.Any(h => h.AgentId == agentId && h.Text == help))
                RecordHelp(agentId, help, helpTo ?? _agents[agentId].HelpTo, _agents[agentId].HelpUpdatedAt ?? DateTime.Now);

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

        /// <summary>锁状态的自然语言描述（由 SiteLock 等可信来源派生时使用）。</summary>
        public static string DescribeLock(LockMode mode) => mode switch
        {
            LockMode.Read => "holds a read lock",
            LockMode.Write => "holds a write lock",
            _ => "holds no lock"
        };

        /// <summary>Agent 自报锁状态的自然语言描述；与 SiteLock 实际持有状态可能不同。</summary>
        public static string DescribeReportedLock(LockMode mode) => mode switch
        {
            LockMode.Read => "reports holding a read lock",
            LockMode.Write => "reports holding a write lock",
            _ => "reports holding no lock"
        };

        public static List<HelpRecord> GetHelpLog() => _helpLog.ToList();

        private static void RecordHelp(string agentId, string text, string to, DateTime at)
        {
            _helpLog.Enqueue(new HelpRecord { AgentId = agentId, Text = text, To = to, At = at });
            while (_helpLog.Count > MaxHelpLog && _helpLog.TryDequeue(out _)) { }
        }

        private static void CleanupStale()
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var key in _agents.Where(kvp => kvp.Value.LastHeartbeatAt < cutoff).Select(kvp => kvp.Key).ToArray())
                _agents.TryRemove(key, out _);
        }
    }
}