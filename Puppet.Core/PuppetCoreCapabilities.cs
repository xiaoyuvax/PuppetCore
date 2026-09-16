using System.Reflection;
using System.Runtime.Versioning;

namespace Puppet.Core
{
    /// <summary>
    /// 当前运行的 PuppetCore 实例能力描述。
    /// 供 Agent 查询以决定可用功能与策略（如是否可修改源码）。
    /// </summary>
    public static class PuppetCoreCapabilities
    {
        private static readonly Lazy<PuppetCoreCapabilityInfo> _info = new(Detect);

        public static PuppetCoreCapabilityInfo Current => _info.Value;

        public static string ToJson() => Newtonsoft.Json.JsonConvert.SerializeObject(Current, Newtonsoft.Json.Formatting.Indented);

        private static PuppetCoreCapabilityInfo Detect()
        {
            var asm = typeof(PuppetCoreCapabilities).Assembly;
            var name = asm.GetName();
            var informationalVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // 判断是否为源码引用
            // 启发式：源码引用通常在项目输出目录下，路径包含 "bin" 但不在 NuGet 包缓存路径
            // 更可靠：检查是否有 SourceLink / EmbeddedSource / 是否为动态编译
            // 这里用简单规则：Assembly.Location 不在用户 NuGet 缓存目录下 = 源码引用
            var location = asm.Location ?? "";
            var isSourceRef = !IsNuGetPackagePath(location);

            return new PuppetCoreCapabilityInfo
            {
                Version = name.Version?.ToString() ?? "unknown",
                InformationalVersion = informationalVersion,
                AssemblyPath = location,
                IsSourceReference = isSourceRef,
                IsNuGetPackage = !isSourceRef,
                SupportsPuppetHint = true, // 当前版本已内置支持
                SupportsAgentBook = true,
                SupportsSiteLock = true,
                SupportsLogForwarder = true,
                SupportsMultiAgent = true,
                TargetFramework = GetTargetFramework(asm),
                BuildConfiguration = GetBuildConfiguration(asm)
            };
        }

        private static bool IsNuGetPackagePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            var lower = path.ToLowerInvariant();
            // NuGet 缓存典型路径
            return lower.Contains(".nuget\\packages") || lower.Contains(".nuget/packages") ||
                   lower.Contains("packages\\puppet.core") || lower.Contains("/packages/puppet.core");
        }

        private static string GetTargetFramework(Assembly asm)
        {
            var attr = asm.GetCustomAttribute<TargetFrameworkAttribute>();
            return attr?.FrameworkName ?? "unknown";
        }

        private static string GetBuildConfiguration(Assembly asm)
        {
            // 尝试从 AssemblyConfigurationAttribute 读取
            var attr = asm.GetCustomAttribute<AssemblyConfigurationAttribute>();
            if (!string.IsNullOrEmpty(attr?.Configuration)) return attr.Configuration;

            // 启发式：路径包含 Debug/Release
            var loc = asm.Location?.ToLowerInvariant() ?? "";
            if (loc.Contains("\\debug\\") || loc.Contains("/debug/")) return "Debug";
            if (loc.Contains("\\release\\") || loc.Contains("/release/")) return "Release";
            return "Unknown";
        }
    }

    /// <summary>
    /// PuppetCore 运行时能力信息
    /// </summary>
    public class PuppetCoreCapabilityInfo
    {
        /// <summary>程序集版本</summary>
        public string Version { get; set; }
        /// <summary>信息版本（含 git hash 等）</summary>
        public string InformationalVersion { get; set; }
        /// <summary>程序集文件路径</summary>
        public string AssemblyPath { get; set; }
        /// <summary>是否为源码引用 - 可修改 PuppetCore 源码</summary>
        public bool IsSourceReference { get; set; }
        /// <summary>是否为 NuGet 包引用 - 不可修改源码</summary>
        public bool IsNuGetPackage { get; set; }
        /// <summary>目标框架</summary>
        public string TargetFramework { get; set; }
        /// <summary>构建配置</summary>
        public string BuildConfiguration { get; set; }

        // 功能支持矩阵
        /// <summary>支持 PuppetHintAttribute</summary>
        public bool SupportsPuppetHint { get; set; }
        /// <summary>支持 AgentBook 多 Agent 协调</summary>
        public bool SupportsAgentBook { get; set; }
        /// <summary>支持 SiteLock 建议性锁</summary>
        public bool SupportsSiteLock { get; set; }
        /// <summary>支持 PLog 日志转发</summary>
        public bool SupportsLogForwarder { get; set; }
        /// <summary>支持多 Agent 共存</summary>
        public bool SupportsMultiAgent { get; set; }
    }
}