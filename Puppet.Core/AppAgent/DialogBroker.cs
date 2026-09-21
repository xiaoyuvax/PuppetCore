using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Puppet.Core.AppAgent
{
    /// <summary>Agent 预置的模态框答案（随动作调用下发；title 精确匹配对话框标题）。</summary>
    public sealed class DialogPreset
    {
        /// <summary>对话框标题（caption，精确匹配，区分大小写）</summary>
        public string Title { get; set; }

        /// <summary>预置答案："Yes"/"No"/"OK"/"Cancel" 等（映射由宿主侧垫片完成）</summary>
        public string Answer { get; set; }
    }

    /// <summary>已答对话框记录（动作响应 dialogsAnswered 字段来源）。</summary>
    public sealed class DialogRecord
    {
        /// <summary>对话框标题</summary>
        public string Title { get; set; }

        /// <summary>实际采用的答案</summary>
        public string Answer { get; set; }

        /// <summary>true=Agent 预答命中；false=安全默认兜底（Agent 未预置该对话框）</summary>
        public bool FromPreset { get; set; }
    }

    /// <summary>
    /// 模态框代理（层2兜底）：Agent 调用期间的对话框预答簿。
    /// Ambient 上下文（AsyncLocal）随 ExecutionContext 流动——含 UI 线程 Send/Invoke 编组与 async 续体；
    /// 宿主经 WinForms 侧 PuppetDialog 垫片查询本类（框架侧无 UI 依赖）。
    /// 语义：Agent 上下文中未预置答案的对话框按宿主给定的安全默认（Cancel/No）自动应答并记录——
    /// B 通道动作永不因模态框阻塞；无 Agent 上下文时宿主走原生模态。
    /// </summary>
    public static class DialogBroker
    {
        private static readonly AsyncLocal<Scope> _current = new();

        /// <summary>Agent 调用上下文是否激活（未激活时宿主应走原生模态）。</summary>
        public static bool IsActive => _current.Value != null;

        /// <summary>推入预答表（执行器在动作调用前调用）。using 还原；嵌套时内层作用域优先。</summary>
        public static IDisposable Push(IEnumerable<DialogPreset> presets)
        {
            var prev = _current.Value;
            _current.Value = new Scope(presets);
            return new Restorer(() => _current.Value = prev);
        }

        /// <summary>解析对话框应答：Agent 上下文中按标题查预答；未命中取 safeDefault（安全默认）。
        /// 两种情况都记录进已答日志。无 Agent 上下文返回 (null, false)——宿主走原生模态。</summary>
        public static (string Answer, bool FromPreset) Resolve(string title, string safeDefault)
        {
            var scope = _current.Value;
            if (scope == null) return (null, false);

            var preset = scope.Presets.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.Ordinal));
            if (preset != null)
            {
                scope.Answered.Add(new DialogRecord { Title = title, Answer = preset.Answer, FromPreset = true });
                return (preset.Answer, true);
            }
            scope.Answered.Add(new DialogRecord { Title = title, Answer = safeDefault, FromPreset = false });
            return (safeDefault, false);
        }

        /// <summary>当前作用域已答记录快照（动作响应 dialogsAnswered 报告用）。</summary>
        public static IReadOnlyList<DialogRecord> AnsweredSnapshot()
        {
            var scope = _current.Value;
            if (scope == null || scope.Answered.Count == 0) return [];
            return scope.Answered.ToList();
        }

        private sealed class Scope
        {
            public readonly List<DialogPreset> Presets;
            public readonly List<DialogRecord> Answered = [];

            public Scope(IEnumerable<DialogPreset> presets) =>
                Presets = presets?.Where(p => !string.IsNullOrEmpty(p?.Title)).ToList() ?? [];
        }

        private sealed class Restorer : IDisposable
        {
            private Action _restore;
            public Restorer(Action restore) => _restore = restore;
            public void Dispose()
            {
                _restore?.Invoke();
                _restore = null;
            }
        }
    }
}
