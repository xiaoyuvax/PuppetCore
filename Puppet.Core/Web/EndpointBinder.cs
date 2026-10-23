using System.Net;
using System.Net.Sockets;

namespace Puppet.Core.Web
{
    /// <summary>
    /// 监听端点解析与端口占用探测。
    /// 启动前先找一个真正可绑定的端口：Kestrel 的绑定失败发生在引擎内部的后台任务里
    /// （NanoKestrel.Start 用 ContinueWith，异常不会冒到 StartWebEngine），
    /// 端口被占用时 Start 仍返回 true、服务器却静默不可用。所以必须自己探测。
    /// </summary>
    internal static class EndpointBinder
    {
        /// <summary>从指定端口起最多尝试的端口数（含指定端口本身）。</summary>
        internal const int MaxAttempts = 10;

        /// <summary>解析 "host:port"；只接受 IP 字面量、localhost 与 0.0.0.0/*/+ 通配。</summary>
        internal static bool TryParse(string endpoint, out IPAddress address, out int port)
        {
            address = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(endpoint)) return false;

            int idx = endpoint.LastIndexOf(':');
            if (idx <= 0) return false;
            if (!int.TryParse(endpoint[(idx + 1)..].Trim(), out port) || port <= 0 || port > 65535)
            {
                port = 0;
                return false;
            }
            if (!TryParseAddress(endpoint[..idx].Trim(), out address))
            {
                port = 0;
                return false;
            }
            return true;
        }

        private static bool TryParseAddress(string host, out IPAddress address)
        {
            address = null;
            if (host is "0.0.0.0" or "*" or "+") { address = IPAddress.Any; return true; }
            if (host is "localhost") { address = IPAddress.Loopback; return true; }
            if (host is "::" or "[::]") { address = IPAddress.IPv6Any; return true; }
            if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
            return IPAddress.TryParse(host, out address);
        }

        /// <summary>
        /// 返回第一个可用端点（保留调用方的 host 写法，只换端口）。
        /// 全部被占用返回 null；端点无法解析时原样返回，交给引擎按旧逻辑尝试。
        /// </summary>
        internal static string FindFree(string endpoint, int maxAttempts = MaxAttempts)
        {
            if (!TryParse(endpoint, out var address, out int port)) return endpoint;

            string host = endpoint[..endpoint.LastIndexOf(':')];
            for (int i = 0; i < maxAttempts && port + i <= 65535; i++)
            {
                int candidate = port + i;
                if (IsFree(address, candidate)) return $"{host}:{candidate}";
            }
            return null;
        }

        /// <summary>
        /// 以独占方式试绑一个端口：只有确定「已被占用」才返回 false；
        /// 其它错误（如本机未启用 IPv6、权限限制）视为无法判定，返回 true 交给引擎照旧尝试。
        /// </summary>
        internal static bool IsFree(IPAddress address, int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
                listener.Start();
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
            catch
            {
                return true;
            }
            finally
            {
                try { listener?.Stop(); } catch { /* 探测失败不影响结论 */ }
            }
        }
    }
}
