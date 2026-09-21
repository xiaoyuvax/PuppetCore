using Puppet.Core.AppAgent;

namespace Puppet.Core.WinForms
{
    /// <summary>
    /// 模态框代理（层2兜底）：宿主代码用 PuppetDialog.Ask 替代 MessageBox.Show——
    /// Agent 调用上下文（DialogBroker.IsActive）中按预答表按标题自动应答；
    /// 未预置的对话框按安全默认（Cancel/No）自动应答，B 通道动作永不因模态框阻塞；
    /// 无 Agent 上下文走原生模态，用户行为完全不变。
    /// 刻意排除 Win32 消息点按钮 / UI Automation 方案（不可靠且用户禁令）。
    /// </summary>
    public static class PuppetDialog
    {
        /// <summary>确认框（等价 MessageBox.Show；owner 可为 null）。
        /// Agent 上下文：预答命中用预答答案；未预置/答案非法用安全默认——两种情况均不弹窗。</summary>
        public static DialogResult Ask(IWin32Window owner, string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            if (!DialogBroker.IsActive)
                return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);

            var (answer, _) = DialogBroker.Resolve(caption, SafeDefault(buttons));
            return MapAnswer(answer, buttons);
        }

        /// <summary>确认框（无 owner 重载）。</summary>
        public static DialogResult Ask(string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
            => Ask(null, text, caption, buttons, icon, defaultButton);

        /// <summary>按钮组合的安全默认答案（Agent 未预置时的兜底应答：取保守选项，不执行破坏性操作）。</summary>
        private static string SafeDefault(MessageBoxButtons buttons) => buttons switch
        {
            MessageBoxButtons.YesNo => "No",             // 不执行询问的操作
            MessageBoxButtons.OKCancel => "Cancel",   // 不执行询问的操作
            MessageBoxButtons.YesNoCancel => "Cancel", // 不执行询问的操作
            MessageBoxButtons.RetryCancel => "Cancel", // 不重试
            MessageBoxButtons.AbortRetryIgnore => "Ignore", // 不中断流程（Abort 语义过重）
            _ => "OK"                                 // 纯信息框（只有 OK）
        };

        /// <summary>答案字符串 → DialogResult（不区分大小写；按钮组合外的非法答案回退安全默认）。</summary>
        private static DialogResult MapAnswer(string answer, MessageBoxButtons buttons)
        {
            var a = answer?.Trim();
            if (string.IsNullOrEmpty(a) || !ValidResults(buttons).Contains(a, StringComparer.OrdinalIgnoreCase))
                a = SafeDefault(buttons);
            return a.ToLowerInvariant() switch
            {
                "yes" => DialogResult.Yes,
                "no" => DialogResult.No,
                "ok" => DialogResult.OK,
                "cancel" => DialogResult.Cancel,
                "abort" => DialogResult.Abort,
                "retry" => DialogResult.Retry,
                "ignore" => DialogResult.Ignore,
                _ => DialogResult.Cancel
            };
        }

        /// <summary>按钮组合的合法答案集合。</summary>
        private static string[] ValidResults(MessageBoxButtons buttons) => buttons switch
        {
            MessageBoxButtons.OK => ["OK"],
            MessageBoxButtons.OKCancel => ["OK", "Cancel"],
            MessageBoxButtons.YesNo => ["Yes", "No"],
            MessageBoxButtons.YesNoCancel => ["Yes", "No", "Cancel"],
            MessageBoxButtons.RetryCancel => ["Retry", "Cancel"],
            MessageBoxButtons.AbortRetryIgnore => ["Abort", "Retry", "Ignore"],
            _ => ["OK"]
        };
    }
}
