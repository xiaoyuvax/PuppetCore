using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
using Puppet.Core.Describe;
using Puppet.Core.Extentions;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>A 面合成方法 CaptureAction（1.3.0）：describe/invoke 注入一致性、位置参数契约、产物 base64 往返。</summary>
    public class CaptureActionTests : IDisposable
    {
        private static readonly byte[] Png =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        };

        private readonly FakePuppet _puppet = new() { AgentInstanceName = "Capture#1" };

        public void Dispose() => PuppetUiActions.CaptureResolver = null;

        [Fact]
        public void Describe_without_capture_resolver_omits_CaptureAction()
        {
            PuppetUiActions.CaptureResolver = null;
            var json = JObject.Parse(_puppet.Describe());
            Assert.Null(Method(json, PuppetUiActions.CaptureAction));

            var inv = JObject.Parse(_puppet.Invoke(PuppetUiActions.CaptureAction, Array.Empty<object>()));
            Assert.False(inv.Value<bool>("ok"));
            Assert.Equal("method not found", inv.Value<string>("err"));
        }

        [Fact]
        public void Describe_with_capture_resolver_declares_positional_params()
        {
            var stub = new StubCapture();
            PuppetUiActions.CaptureResolver = o => ReferenceEquals(o, _puppet) ? stub : null;

            var json = JObject.Parse(_puppet.Describe());
            var m = Method(json, PuppetUiActions.CaptureAction);
            Assert.NotNull(m);
            Assert.Equal(typeof(PuppetArtifact).FullName, m.Value<string>("ReturnType"));

            var p = m["Parameters"].ToList();
            Assert.Equal(new[] { "x", "y", "width", "height" }, p.Select(t => t.Value<string>("Name")).ToArray());
            Assert.All(p, t => Assert.True(t.Value<bool>("IsOptional")));
            Assert.All(p, t => Assert.Equal(typeof(int).FullName, t.Value<string>("Type")));

            var other = JObject.Parse(new FakePuppet { AgentInstanceName = "Capture#2" }.Describe());
            Assert.Null(Method(other, PuppetUiActions.CaptureAction));
        }

        [Fact]
        public void Invoke_CaptureAction_returns_png_artifact_and_binds_positional_args()
        {
            var stub = new StubCapture();
            PuppetUiActions.CaptureResolver = o => ReferenceEquals(o, _puppet) ? stub : null;

            var inv = JObject.Parse(_puppet.Invoke(PuppetUiActions.CaptureAction, new object[] { 10, 20, 300, 200 }));
            Assert.True(inv.Value<bool>("ok"), inv.ToString());
            Assert.Equal(typeof(PuppetArtifact).FullName, inv.Value<string>("returnType"));
            Assert.Equal(1, stub.Calls);
            Assert.Equal((10, 20, 300, 200), (stub.X, stub.Y, stub.W, stub.H));

            var result = inv["result"];
            Assert.Equal("image/png", result.Value<string>("ContentType"));
            Assert.Equal("capture.png", result.Value<string>("FileName"));
            var bytes = Convert.FromBase64String(result.Value<string>("Content"));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            Assert.Equal(Png, bytes);

            var full = JObject.Parse(_puppet.Invoke(PuppetUiActions.CaptureAction, Array.Empty<object>()));
            Assert.True(full.Value<bool>("ok"));
            Assert.Equal(2, stub.Calls);
            Assert.Equal((0, 0, 0, 0), (stub.X, stub.Y, stub.W, stub.H));
        }

        private static JObject Method(JObject describe, string name)
            => describe["Methods"].Children<JObject>().FirstOrDefault(m => m.Value<string>("Name") == name);

        private sealed class StubCapture : IPuppetCapture
        {
            public int Calls, X, Y, W, H;

            public PuppetArtifact Capture(int x, int y, int width, int height)
            {
                Calls++;
                X = x;
                Y = y;
                W = width;
                H = height;
                return new PuppetArtifact("capture.png", "image/png", Png);
            }
        }
    }
}
