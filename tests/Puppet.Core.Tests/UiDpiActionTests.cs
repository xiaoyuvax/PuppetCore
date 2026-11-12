using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
using Puppet.Core.Extentions;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>
    /// DIP 几何基础 action（MoveDipAction/ResizeDipAction/SetBoundsDipAction）：
    /// 只在适配器实现 IPuppetUiDpi 时发布、按位置绑定能把 screen 透传/缺省 -1、Screens state 带逐屏 scale。
    /// 与其它共用 PuppetUiActions 静态 resolver 的测试同 Collection 串行。
    /// </summary>
    [Collection("PuppetUiActions")]
    public class UiDpiActionTests : IDisposable
    {
        private readonly FakePuppet _puppet = new() { AgentInstanceName = "UiDpi#1" };
        private readonly FakeDpiElement _element = new();

        public void Dispose()
        {
            PuppetUiActions.UiElementResolver = null;
            PuppetUiActions.WindowResolver = null;
            PuppetUiActions.ScreensProvider = null;
        }

        private void Wire(object element) =>
            PuppetUiActions.UiElementResolver = o => ReferenceEquals(o, _puppet) ? (IPuppetUiElement)element : null;

        [Fact]
        public void Dip_actions_published_only_for_capable_elements()
        {
            Wire(_element);
            var (actions, _) = ActionizePolicy.Scan(_puppet.GetType(), _puppet);
            string[] expected = { PuppetUiActions.MoveDipAction, PuppetUiActions.ResizeDipAction, PuppetUiActions.SetBoundsDipAction };
            foreach (var name in expected)
                Assert.Contains(actions, a => a.Name == name);

            // 参数按名可见（B 面按名绑定依赖它）
            var move = actions.First(a => a.Name == PuppetUiActions.MoveDipAction);
            Assert.Equal(new[] { "x", "y", "screen" }, move.Params.Select(p => p.Name).ToArray());
            Assert.False(move.Params[2].Required);

            // 不支持 DIP 的适配器：不发布，避免发布兑现不了的动作
            Wire(new PlainElement());
            var (plain, _) = ActionizePolicy.Scan(_puppet.GetType(), _puppet);
            Assert.DoesNotContain(plain, a => a.Name == PuppetUiActions.MoveDipAction);
        }

        [Fact]
        public void Invoke_dip_action_binds_positional_args_and_defaults_screen_to_minus_one()
        {
            Wire(_element);

            var move = JObject.Parse(_puppet.Invoke(PuppetUiActions.MoveDipAction, new object[] { 10, 20 }));
            Assert.True(move.Value<bool>("ok"), move.ToString());
            Assert.Equal((10, 20, -1), (_element.X, _element.Y, _element.Screen));

            var bounds = JObject.Parse(_puppet.Invoke(
                PuppetUiActions.SetBoundsDipAction, new object[] { 1L, 2L, 300L, 200L, 1L }));
            Assert.True(bounds.Value<bool>("ok"), bounds.ToString());
            Assert.Equal((1, 2, 300, 200, 1), (_element.X, _element.Y, _element.Width, _element.Height, _element.Screen));

            // 适配器业务失败：envelope ok=true，OperationResult.Ok=false
            _element.FailNext = true;
            var bad = JObject.Parse(_puppet.Invoke(PuppetUiActions.ResizeDipAction, new object[] { 0, 0 }));
            Assert.True(bad.Value<bool>("ok"), bad.ToString());
            Assert.False(bad["result"].Value<bool>("Ok"));
        }

        [Fact]
        public void Screens_state_carries_per_screen_scale()
        {
            Wire(_element);
            PuppetUiActions.ScreensProvider = () => new List<PuppetScreen>
            {
                new() { DeviceName = @"\\.\DISPLAY1", Primary = true, Width = 3840, Height = 2160,
                        WorkX = 0, WorkY = 0, WorkWidth = 3840, WorkHeight = 2040, Scale = 2.5 },
                new() { DeviceName = @"\\.\DISPLAY2", X = 3840, Width = 1920, Height = 1080,
                        WorkX = 3840, WorkY = 0, WorkWidth = 1920, WorkHeight = 1040, Scale = 1.0 },
            };

            var screens = JObject.Parse(_puppet.GetProperty("Screens"));
            Assert.True(screens.Value<bool>("ok"), screens.ToString());
            string text = screens.Value<string>("value");
            Assert.Contains("scale=2.5", text);
            Assert.Contains("scale=1", text);
        }

        private class PlainElement : IPuppetUiElement
        {
            public int X { get; private set; }
            public int Y { get; private set; }
            public int Width { get; private set; } = 100;
            public int Height { get; private set; } = 50;
            public bool Visible { get; set; } = true;
            public bool Enabled { get; set; } = true;
            public void Move(int x, int y) { X = x; Y = y; }
            public void Resize(int width, int height) { Width = width; Height = height; }
            public void SetBounds(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
            public void Focus() { }
        }

        private sealed class FakeDpiElement : PlainElement, IPuppetUiDpi
        {
            public int Screen { get; private set; } = -1;
            public bool FailNext { get; set; }

            public OperationResult MoveDip(int x, int y, int screen)
            {
                Screen = screen;
                Move(x, y);
                return OperationResult.Success("moved dip");
            }

            public OperationResult ResizeDip(int width, int height)
                => FailNext ? OperationResult.Failure("宽高必须为正") : OperationResult.Success("resized dip");

            public OperationResult SetBoundsDip(int x, int y, int width, int height, int screen)
            {
                Screen = screen;
                SetBounds(x, y, width, height);
                return OperationResult.Success("bounds dip");
            }
        }
    }
}
