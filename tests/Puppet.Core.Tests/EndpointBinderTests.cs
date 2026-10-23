using System.Net;
using System.Net.Sockets;
using Puppet.Core.Web;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>端口探测与顺延：Start 不能再静默地绑失败（Kestrel 后台任务会吞掉绑定异常）。</summary>
    public class EndpointBinderTests
    {
        [Theory]
        [InlineData("127.0.0.1:9090", "127.0.0.1", 9090)]
        [InlineData("0.0.0.0:9090", "0.0.0.0", 9090)]
        [InlineData("*:8080", "0.0.0.0", 8080)]
        [InlineData("+:8080", "0.0.0.0", 8080)]
        [InlineData("localhost:9090", "127.0.0.1", 9090)]
        [InlineData("[::]:9090", "::", 9090)]
        public void TryParse_AcceptsCommonForms(string endpoint, string host, int port)
        {
            Assert.True(EndpointBinder.TryParse(endpoint, out var address, out var parsedPort));
            Assert.Equal(IPAddress.Parse(host), address);
            Assert.Equal(port, parsedPort);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("9090")]
        [InlineData("127.0.0.1:abc")]
        [InlineData("127.0.0.1:0")]
        [InlineData("127.0.0.1:70000")]
        [InlineData("my-host:9090")]
        public void TryParse_RejectsInvalid(string endpoint)
        {
            Assert.False(EndpointBinder.TryParse(endpoint, out _, out _));
        }

        [Fact]
        public void FindFree_SkipsOccupiedPort()
        {
            using var blocker = PortBlock.One(out int port);

            string actual = EndpointBinder.FindFree($"127.0.0.1:{port}", maxAttempts: 3);

            Assert.Equal($"127.0.0.1:{port + 1}", actual);
        }

        [Fact]
        public void FindFree_AllBusy_ReturnsNull()
        {
            using var blocker = PortBlock.Consecutive(2, out int port);

            Assert.Null(EndpointBinder.FindFree($"127.0.0.1:{port}", maxAttempts: 2));
        }

        [Fact]
        public void FindFree_Unparseable_ReturnsInputUnchanged()
        {
            Assert.Equal("my-host:9090", EndpointBinder.FindFree("my-host:9090"));
        }

        [Fact]
        public void Start_FallsBackToNextPort_WhenConfiguredPortIsTaken()
        {
            using var blocker = PortBlock.One(out int port);

            var server = new PuppetWebServer();
            try
            {
                Assert.True(server.Start($"127.0.0.1:{port}"));
                Assert.Equal($"127.0.0.1:{port + 1}", server.Endpoint);
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public void Stop_ClearsEndpoint()
        {
            using var blocker = PortBlock.One(out int port);
            int freePort = port;
            blocker.Dispose();

            var server = new PuppetWebServer();
            try
            {
                Assert.True(server.Start($"127.0.0.1:{freePort}"));
                Assert.NotNull(server.Endpoint);

                server.Stop();
                Assert.Null(server.Endpoint);
            }
            finally
            {
                server.Stop();
            }
        }

        /// <summary>占住若干端口直到 Dispose（测试用）。</summary>
        private sealed class PortBlock : IDisposable
        {
            private readonly List<TcpListener> _listeners;

            private PortBlock(List<TcpListener> listeners) => _listeners = listeners;

            public static PortBlock One(out int port) => Consecutive(1, out port);

            public static PortBlock Consecutive(int count, out int firstPort)
            {
                for (int candidate = 20000; candidate < 60000; candidate++)
                {
                    var listeners = new List<TcpListener>(count);
                    bool ok = true;
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            var l = new TcpListener(IPAddress.Loopback, candidate + i) { ExclusiveAddressUse = true };
                            l.Start();
                            listeners.Add(l);
                        }
                        catch
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                    {
                        firstPort = candidate;
                        return new PortBlock(listeners);
                    }
                    foreach (var l in listeners) { try { l.Stop(); } catch { } }
                }
                throw new InvalidOperationException("找不到连续空闲端口");
            }

            public void Dispose()
            {
                foreach (var l in _listeners) { try { l.Stop(); } catch { } }
            }
        }
    }
}
