using Newtonsoft.Json.Linq;
using Puppet.Core.AppAgent;
using Puppet.Core.Describe;
using Puppet.Core.Extentions;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>
    /// A 面合成 UI 基础 action 与合成 state（1.3.1）：GUI 实例 describe/invoke 注入一致性、
    /// 位置参数绑定（int / long / JToken 解包）、OperationResult 返回形状、合成 state 可读、非 UI 实例零影响。
    /// 与 <see cref="CaptureActionTests"/> 同 Collection 串行（共用 PuppetUiActions 静态 resolver 槽位）。
    /// </summary>
    [Collection("PuppetUiActions")]
    public class UiBaseActionSyntheticTests : IDisposable
    {
        private readonly FakePuppet _puppet = new() { AgentInstanceName = "UiSyn#1" };
        private readonly FakeUiElement _element = new();
        private readonly FakeUiWindow _window = new();

        public void Dispose()
        {
            PuppetUiActions.UiElementResolver = null;
            PuppetUiActions.WindowResolver = null;
            PuppetUiActions.ScreensProvider = null;
            PuppetUiActions.CaptureResolver = null;
            PuppetUiActions.Confirm = null;
        }

        private void WireUi()
        {
            PuppetUiActions.UiElementResolver = o => ReferenceEquals(o, _puppet) ? _element : null;
            PuppetUiActions.WindowResolver = o => ReferenceEquals(o, _puppet) ? _window : null;
            PuppetUiActions.ScreensProvider = () => new List<PuppetScreen>
            {
                new() { DeviceName = @"\\.\DISPLAY1", Primary = true, X = 0, Y = 0, Width = 1920, Height = 1080, WorkX = 0, WorkY = 0, WorkWidth = 1920, WorkHeight = 1040 },
                new() { DeviceName = @"\\.\DISPLAY2", Primary = false, X = 1920, Y = 0, Width = 2560, Height = 1440, WorkX = 1920, WorkY = 0, WorkWidth = 2560, WorkHeight = 1400 },
            };
        }

        [Fact]
        public void Describe_includes_ui_base_actions_for_gui_instance()
        {
            WireUi();
            var json = JObject.Parse(_puppet.Describe());
            string[] expected =
            {
                PuppetUiActions.MoveAction, PuppetUiActions.ResizeAction, PuppetUiActions.SetBoundsAction,
                PuppetUiActions.SetVisibleAction, PuppetUiActions.SetEnabledAction, PuppetUiActions.SetWindowStateAction,
                PuppetUiActions.ActivateAction, PuppetUiActions.CloseWindowAction, PuppetUiActions.FocusAction,
                PuppetUiActions.SetTopMostAction, PuppetUiActions.SetTitleAction, PuppetUiActions.SetOpacityAction
            };
            foreach (var name in expected)
                Assert.True(Method(json, name) != null, "missing synthetic method " + name);

            var move = Method(json, PuppetUiActions.MoveAction);
            Assert.Equal(typeof(OperationResult).FullName, move.Value<string>("ReturnType"));
            Assert.Equal(new[] { "x", "y" }, move["Parameters"].Select(p => p.Value<string>("Name")).ToArray());

            // CaptureResolver 未注入 → CaptureAction 不出现（与 UI 基础 action 注入互相独立）
            Assert.Null(Method(json, PuppetUiActions.CaptureAction));

            // 非 UI 实例零影响
            var other = JObject.Parse(new FakePuppet { AgentInstanceName = "UiSyn#2" }.Describe());
            Assert.Null(Method(other, PuppetUiActions.MoveAction));
        }

        [Fact]
        public void Invoke_ui_base_action_binds_positional_args_int_long_jtoken()
        {
            WireUi();

            // int（框架直调）
            var move = JObject.Parse(_puppet.Invoke(PuppetUiActions.MoveAction, new object[] { 5, 7 }));
            Assert.True(move.Value<bool>("ok"), move.ToString());
            Assert.Equal(typeof(OperationResult).FullName, move.Value<string>("returnType"));
            Assert.Equal((5, 7), (_element.X, _element.Y));
            Assert.True(move["result"].Value<bool>("Ok"));
            Assert.Contains("已移动", move["result"].Value<string>("Message"));

            // long（HTTP JSON 数字反序列化后的形态）
            var move2 = JObject.Parse(_puppet.Invoke(PuppetUiActions.MoveAction, new object[] { 50L, 60L }));
            Assert.True(move2.Value<bool>("ok"), move2.ToString());
            Assert.Equal((50, 60), (_element.X, _element.Y));

            // JToken（A 面真实 HTTP 解包路径）
            var top = JObject.Parse(_puppet.Invoke(PuppetUiActions.SetTopMostAction, new object[] { new JValue(true) }));
            Assert.True(top.Value<bool>("ok"), top.ToString());
            Assert.True(_window.TopMost);

            // 业务失败：envelope ok 仍为 true（invoke 本身成功），OperationResult.Ok=false
            var bad = JObject.Parse(_puppet.Invoke(PuppetUiActions.ResizeAction, new object[] { 0, 0 }));
            Assert.True(bad.Value<bool>("ok"), bad.ToString());
            Assert.False(bad["result"].Value<bool>("Ok"));
            Assert.Contains("宽高必须为正", bad["result"].Value<string>("Message"));
            Assert.Equal(100, _element.Width);
        }

        [Fact]
        public void Synthetic_screen_states_readable_via_get_and_state()
        {
            WireUi();

            var count = JObject.Parse(_puppet.GetProperty("ScreenCount"));
            Assert.True(count.Value<bool>("ok"), count.ToString());
            Assert.Equal(2, count.Value<int>("value"));

            var screens = JObject.Parse(_puppet.GetProperty("Screens"));
            Assert.True(screens.Value<bool>("ok"), screens.ToString());
            Assert.Contains("DISPLAY1", screens.Value<string>("value"));
            Assert.Contains("bounds=0,0,1920x1080", screens.Value<string>("value"));
            Assert.Contains("primary", screens.Value<string>("value"));

            var viaState = JObject.Parse(_puppet.GetState("Screens"));
            Assert.True(viaState.Value<bool>("ok"), viaState.ToString());
            Assert.Contains("DISPLAY2", viaState.Value<string>("value"));

            // 非 UI 实例 / ScreensProvider 未注入 → 不提供
            Assert.False(PuppetUiActions.TryGetSyntheticState(new FakePuppet(), "Screens", out _, out _));
            PuppetUiActions.ScreensProvider = null;
            Assert.False(PuppetUiActions.TryGetSyntheticState(_puppet, "Screens", out _, out _));
        }

        [Fact]
        public void Non_ui_instance_ui_action_not_synthetic()
        {
            WireUi(); // 解析器只认 _puppet
            var other = new FakePuppet { AgentInstanceName = "UiSyn#3" };
            Assert.False(PuppetUiActions.IsSynthetic(other, PuppetUiActions.MoveAction));
            var inv = JObject.Parse(other.Invoke(PuppetUiActions.MoveAction, new object[] { 1, 1 }));
            Assert.False(inv.Value<bool>("ok"));
            Assert.Equal("method not found", inv.Value<string>("err"));
        }

        private static JObject Method(JObject describe, string name)
            => describe["Methods"].Children<JObject>().FirstOrDefault(m => m.Value<string>("Name") == name);

        private class FakeUiElement : IPuppetUiElement
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

        private sealed class FakeUiWindow : FakeUiElement, IPuppetWindow
        {
            public PuppetWindowState WindowState { get; set; } = PuppetWindowState.Normal;
            public bool TopMost { get; set; }
            public string Title { get; set; } = "Test";
            public double Opacity { get; set; } = 1.0;
            public void Activate() { }
            public void Close() { }
        }
    }
}
