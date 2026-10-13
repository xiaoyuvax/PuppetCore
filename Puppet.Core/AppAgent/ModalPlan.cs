namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// 任意模态框的包裹范式（通用代码模型）：宿主把一次模态调用整体收进一个 ModalPlan。
    /// <list type="bullet">
    /// <item><b>人工上下文</b>（DialogBroker 未激活）：走 <see cref="Show"/> 真弹窗，与未接入前行为完全一致；</item>
    /// <item><b>Agent 上下文</b>（DialogBroker.IsActive）：不弹窗——按 <see cref="Title"/> 取预置答案
    /// （B 通道 dialogPresets），未预置取 <see cref="SafeDefaultAnswer"/>（接口参数，由调用方按该框语义给），
    /// 再交 <see cref="Synthesize"/> 造出这个框特有的结果。HTTP 调用永不因模态框阻塞。</item>
    /// </list>
    /// 不同模态框有不同的输入项与 DialogResult：映射逻辑全部收在宿主的 <see cref="Synthesize"/> 里，
    /// 答案串本身是<b>不透明载荷</b>——按钮名（OK/Yes/取消…）、文件路径、颜色 hex、甚至 k=v/JSON 多值，
    /// 只要宿主认得即可。已应答（预置或安全默认）记入 <see cref="DialogBroker.AnsweredSnapshot"/>（dialogsAnswered）。
    /// </summary>
    /// <typeparam name="TResult">该模态框的结果类型（DialogResult、string 路径/文本、int 颜色、自定义元组均可）。</typeparam>
    public sealed class ModalPlan<TResult>
    {
        /// <summary>对话框标题 = 预置匹配键（与 DialogPreset.Title 精确匹配，区分大小写）。</summary>
        public string Title { get; init; }

        /// <summary>安全默认答案（接口参数）：Agent 未预置时用它，等价于「点了该框的安全默认项」；不给则为 null。</summary>
        public string SafeDefaultAnswer { get; init; }

        /// <summary>答案串 → 本框特有结果（仅 Agent 上下文调用，永不弹窗）。</summary>
        public Func<string, TResult> Synthesize { get; init; }

        /// <summary>真弹窗（仅人工上下文调用）：宿主原样搬进来的对话框代码。</summary>
        public Func<TResult> Show { get; init; }

        /// <summary>按当前上下文执行一次模态调用。</summary>
        public TResult Run()
        {
            if (!DialogBroker.IsActive) return Show();

            var (answer, _) = DialogBroker.Resolve(Title, SafeDefaultAnswer);
            return Synthesize(answer ?? SafeDefaultAnswer);
        }
    }
}
