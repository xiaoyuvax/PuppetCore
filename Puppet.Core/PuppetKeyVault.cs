namespace Puppet.Core
{
    /// <summary>
    /// 安全密钥管理。
    /// 全局密钥由源码端控制，可运行时刷新。
    /// 请求 Authorization 头须匹配实例密钥或全局密钥。
    /// </summary>
    public static class PuppetKeyVault
    {
        private static string _globalKey = System.Guid.NewGuid().ToString("N");

        /// <summary>全局密钥（源码端设置，可运行时刷新）</summary>
        public static string GlobalKey => _globalKey;

        /// <summary>刷新全局密钥（源码端调用）</summary>
        public static string RefreshKey() => _globalKey = System.Guid.NewGuid().ToString("N");

        /// <summary>设置全局密钥（源码端调用）</summary>
        /// <param name="key">密钥值，不可为 null</param>
        public static void SetKey(string key) => _globalKey = key ?? throw new System.ArgumentNullException(nameof(key));

        /// <summary>校验请求：实例密钥优先，否则全局密钥</summary>
        public static bool Authorize(IPuppet target, string requestKey)
        {
            if (string.IsNullOrEmpty(requestKey)) return false;
            return requestKey == target?.AgentAccessKey || requestKey == _globalKey;
        }

        /// <summary>校验全局密钥（用于元端点）</summary>
        public static bool AuthorizeGlobal(string requestKey)
            => !string.IsNullOrEmpty(requestKey) && requestKey == _globalKey;
    }
}
