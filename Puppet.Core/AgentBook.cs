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
        /// <summary>最近一条对话内容（自由文本）；空串表示未发言。</summary>
        public string LastMessage { get; set; }
        /// <summary>最近一条对话的更新时间；未发言时为 null。</summary>
        public DateTime? LastMessageAt { get; set; }
        /// <summary>最近一条对话的目标 Agent Id；空串表示广播。</summary>
        public string MessageTo { get; set; }
        /// <summary>最近几次工具调用的一行摘要（旧→新）；同工作区的其他会话靠它看进展。</summary>
        public List<string> Steps { get; set; } = new();
    }

    public sealed class ChatMessage
    {
        public long Seq { get; set; }
        public string AgentId { get; set; }
        public string Text { get; set; }
        public string To { get; set; }
        public DateTime At { get; set; }
    }

    public sealed class ChatOverview
    {
        public int Messages { get; set; }
        public int Agents { get; set; }
        public DateTime? FirstAt { get; set; }
        public DateTime? LastAt { get; set; }
        public ChatMessage Last { get; set; }
        public string Summary { get; set; }
    }

    public static class AgentBook
    {
        private static readonly ConcurrentDictionary<string, AgentInfo> _agents = new();
        private static readonly List<ChatMessage> _chat = new();
        private static readonly object _chatLock = new();
        private static long _chatSeq;
        private const int MaxChatMessages = 500;
        private const int MaxChatTextLength = 4000;
        private const int MaxAgentSteps = 12;

        /// <summary>留言与登记的保留时长：超过就清理，保证留言板只反映最近这段时间的会话。</summary>
        public static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

        /// <summary>步骤统一由本进程打时间：留言板上所有会话的步骤共用一个时钟，同伴能看出一条步骤是多久以前的。</summary>
        private static string Stamped(string step) => DateTime.Now.ToString("HH:mm:ss") + " " + step;

        /// <summary>老化：丢掉过期的留言与长期没有心跳的登记（进程生命周期内不无限增长）。</summary>
        public static void Prune(DateTime now)
        {
            var cutoff = now - Retention;
            lock (_chatLock) _chat.RemoveAll(m => m.At < cutoff);
            foreach (var pair in _agents)
                if (pair.Value.LastHeartbeatAt < cutoff) _agents.TryRemove(pair.Key, out _);
        }

        public static void Heartbeat(string agentId, string purpose, string operation, int estWaitSec, string puppetInstanceName, string site = null, LockMode lockMode = LockMode.None, string note = null, string message = null, string messageTo = null, string step = null)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            Prune(DateTime.Now);
            if (message is { Length: > MaxChatTextLength }) message = message.Substring(0, MaxChatTextLength);

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
                    LastMessage = message ?? "",
                    LastMessageAt = string.IsNullOrEmpty(message) ? null : DateTime.Now,
                    MessageTo = string.IsNullOrEmpty(message) ? "" : (messageTo ?? ""),
                    Steps = string.IsNullOrWhiteSpace(step) ? new List<string>() : new List<string> { Stamped(step) }
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
                    if (message != null)
                    {
                        var messageToValue = string.IsNullOrEmpty(message) ? "" : (messageTo ?? existing.MessageTo ?? "");
                        if (!string.Equals(existing.LastMessage, message, StringComparison.Ordinal)
                            || !string.Equals(existing.MessageTo, messageToValue, StringComparison.Ordinal))
                        {
                            existing.LastMessage = message;
                            existing.MessageTo = messageToValue;
                            existing.LastMessageAt = string.IsNullOrEmpty(message) ? null : DateTime.Now;
                        }
                    }
                    else if (messageTo != null && !string.IsNullOrEmpty(existing.LastMessage))
                    {
                        existing.MessageTo = messageTo;
                    }
                    if (!string.IsNullOrWhiteSpace(step))
                    {
                        if (existing.Steps == null) existing.Steps = new List<string>();
                        lock (existing.Steps)
                        {
                            existing.Steps.Add(Stamped(step));
                            if (existing.Steps.Count > MaxAgentSteps)
                                existing.Steps.RemoveRange(0, existing.Steps.Count - MaxAgentSteps);
                        }
                    }
                    return existing;
                });

            if (!string.IsNullOrEmpty(message))
                AppendMessage(agentId, message, messageTo ?? _agents[agentId].MessageTo, DateTime.Now);

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

        /// <summary>
        /// 会话收场：留下最后一句遗言，并清掉该 Agent 之前的全部留言（本进程生命周期内只留遗言）。
        /// 与 <see cref="Release"/> 的区别是保留对话记录，供后来者看到它最后说了什么。
        /// </summary>
        public static void Leave(string agentId, string farewell)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            if (farewell is { Length: > MaxChatTextLength }) farewell = farewell.Substring(0, MaxChatTextLength);

            lock (_chatLock)
            {
                if (!string.IsNullOrWhiteSpace(farewell))
                    _chat.Add(new ChatMessage { Seq = ++_chatSeq, AgentId = agentId, Text = farewell, To = "", At = DateTime.Now });

                // 同一 Agent 只保留最后一条（遗言）；重复调用也幂等。
                var last = _chat.LastOrDefault(m => m.AgentId == agentId);
                _chat.RemoveAll(m => m.AgentId == agentId && !ReferenceEquals(m, last));
                while (_chat.Count > MaxChatMessages) _chat.RemoveAt(0);
            }

            _agents.TryRemove(agentId, out _);
        }

        public static List<AgentInfo> GetAll(bool includeStale = false)
        {
            Prune(DateTime.Now);
            var cutoff = DateTime.Now.AddMinutes(-5);
            return _agents.Values
                .Where(a => includeStale || a.LastHeartbeatAt >= cutoff)
                .ToList();
        }

        /// <summary>工具步骤的线程安全快照：心跳线程正在追加时直接 ToArray 会抛异常。</summary>
        public static string[] SnapshotSteps(AgentInfo agent)
        {
            var steps = agent?.Steps;
            if (steps == null) return Array.Empty<string>();
            lock (steps) return steps.ToArray();
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

        /// <summary>按时间/关键字/收发方筛选本次应用启动以来的对话；结果按时间正序。</summary>
        public static List<ChatMessage> GetChat(DateTime? since = null, DateTime? until = null, string keyword = null, string from = null, string to = null)
        {
            Prune(DateTime.Now);
            lock (_chatLock)
            {
                IEnumerable<ChatMessage> query = _chat;
                if (since.HasValue) query = query.Where(m => m.At >= since.Value);
                if (until.HasValue) query = query.Where(m => m.At <= until.Value);
                if (!string.IsNullOrEmpty(from)) query = query.Where(m => m.AgentId == from);
                if (!string.IsNullOrEmpty(to)) query = query.Where(m => string.Equals(m.To ?? "", to, StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(keyword)) query = query.Where(m => m.Text?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                return query.ToList();
            }
        }

        public static ChatOverview GetChatOverview()
        {
            Prune(DateTime.Now);
            lock (_chatLock)
            {
                if (_chat.Count == 0) return new ChatOverview { Summary = "no conversation yet" };
                var last = _chat[_chat.Count - 1];
                var agents = _chat.Select(m => m.AgentId).Distinct().Count();
                return new ChatOverview
                {
                    Messages = _chat.Count,
                    Agents = agents,
                    FirstAt = _chat[0].At,
                    LastAt = last.At,
                    Last = last,
                    Summary = $"{_chat.Count} messages from {agents} agents since {_chat[0].At:yyyy-MM-dd HH:mm:ss}; " +
                              $"last from {last.AgentId}" + (string.IsNullOrEmpty(last.To) ? "" : $" to {last.To}") +
                              $" at {last.At:yyyy-MM-dd HH:mm:ss}"
                };
            }
        }

        private static void AppendMessage(string agentId, string text, string to, DateTime at)
        {
            lock (_chatLock)
            {
                for (var i = _chat.Count - 1; i >= 0; i--)
                {
                    if (_chat[i].AgentId != agentId) continue;
                    // 同一 Agent 每次调用都会重复带上当前消息，只在内容/目标变化时记一条
                    if (_chat[i].Text == text && string.Equals(_chat[i].To ?? "", to ?? "", StringComparison.Ordinal)) return;
                    break;
                }
                _chat.Add(new ChatMessage { Seq = ++_chatSeq, AgentId = agentId, Text = text, To = to ?? "", At = at });
                while (_chat.Count > MaxChatMessages) _chat.RemoveAt(0);
            }
        }

        private static void CleanupStale()
        {
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var key in _agents.Where(kvp => kvp.Value.LastHeartbeatAt < cutoff).Select(kvp => kvp.Key).ToArray())
                _agents.TryRemove(key, out _);
        }
    }
}
