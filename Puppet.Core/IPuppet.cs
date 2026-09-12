using Common.Logging;

namespace Puppet.Core
{
    /// <summary>
    /// 继承此接口的类型（含窗体）可向 AI Agent 暴露运行时状态与调用能力。
    /// 能力描述通过反射自动生成，无需硬编码。
    /// Agent 是 Puppet 化的实施者——实现此接口、调用 Register()、注入端点等均由 Agent 基于 DEVELOPMENT.md 自主完成。
    /// 人类开发者仅需告知 Agent 阅读 DEVELOPMENT.md 并指示在开发中启用框架。
    /// </summary>
    public interface IPuppet
    {
        /// <summary>
        /// Agent 访问密钥：调用 /agent/* 端点时 Authorization 头须匹配此值（或 PuppetKeyVault 全局密钥）。
        /// 由源码端控制，可运行时刷新。
        /// </summary>
        string AgentAccessKey { get; }

        /// <summary>
        /// Agent 调试日志实例。对于已有 WimaLogger 字段的类型，
        /// 直接返回该字段即可（WimaLogger 实现 Common.Logging.ILog），无需替换为 ILog 类型。
        /// 日志通过 WimaLogger.LogBook 自动注册到 Web /agent/logs 端点。
        /// 注：Wima.Log 是独立库，本库仅引用不包含。
        /// </summary>
        ILog AgentLog { get; }

        /// <summary>
        /// 注册到 PuppetRegistry 的唯一实例名（默认可用 GetType().Name + 实例标识）。
        /// </summary>
        string AgentInstanceName { get; }
    }
}
