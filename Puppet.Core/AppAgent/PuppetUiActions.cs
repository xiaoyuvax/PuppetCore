using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppet.Core.AppAgent
{
    /// <summary>UI 窗口态（跨平台语义：WinForms FormWindowState / WPF WindowState / 浏览器窗口）。</summary>
    public enum PuppetWindowState
    {
        /// <summary>正常（非最大化/最小化）。</summary>
        Normal,
        /// <summary>最大化。</summary>
        Maximized,
        /// <summary>最小化。</summary>
        Minimized
    }

    /// <summary>一个显示器的信息（跨平台：WinForms Screen / WPF SystemParameters / 浏览器 screen）。</summary>
    public sealed class PuppetScreen
    {
        /// <summary>设备名（如 \\.\DISPLAY1）。</summary>
        public string DeviceName { get; init; }
        /// <summary>是否主显示器。</summary>
        public bool Primary { get; init; }
        /// <summary>屏幕边界（物理像素）。</summary>
        public int X { get; init; }
        /// <summary>屏幕边界（物理像素）。</summary>
        public int Y { get; init; }
        /// <summary>屏幕宽（物理像素）。</summary>
        public int Width { get; init; }
        /// <summary>屏幕高（物理像素）。</summary>
        public int Height { get; init; }
        /// <summary>工作区（去掉任务栏等）。</summary>
        public int WorkX { get; init; }
        /// <summary>工作区（去掉任务栏等）。</summary>
        public int WorkY { get; init; }
        /// <summary>工作区宽。</summary>
        public int WorkWidth { get; init; }
        /// <summary>工作区高。</summary>
        public int WorkHeight { get; init; }
    }

    /// <summary>
    /// UI 元素能力（几何 + 可见性/可用性）。坐标语义由适配器定义：
    /// WinForms 的 Form.Bounds 是屏幕坐标，Control.Bounds 是父容器坐标。
    /// 由平台包（WinForms/WPF/Web）实现，或由宿主自行实现。
    /// </summary>
    public interface IPuppetUiElement
    {
        /// <summary>元素左上角 X（坐标语义由适配器定义）。</summary>
        int X { get; }
        /// <summary>元素左上角 Y。</summary>
        int Y { get; }
        /// <summary>元素宽。</summary>
        int Width { get; }
        /// <summary>元素高。</summary>
        int Height { get; }
        /// <summary>是否可见。</summary>
        bool Visible { get; set; }
        /// <summary>是否可用（Enabled）。</summary>
        bool Enabled { get; set; }
        /// <summary>移动元素到 (x, y)。</summary>
        void Move(int x, int y);
        /// <summary>缩放元素到 (width, height)。</summary>
        void Resize(int width, int height);
        /// <summary>同时设置位置与大小。</summary>
        void SetBounds(int x, int y, int width, int height);
        /// <summary>把键盘焦点移到该元素。</summary>
        void Focus();
    }

    /// <summary>
    /// 窗口能力（Form / Window / 浏览器窗口）：在 UI 元素之上加窗口态 / 激活 / 置顶 / 标题 / 透明度 / 关闭。
    /// </summary>
    public interface IPuppetWindow : IPuppetUiElement
    {
        /// <summary>窗口态（Normal/Maximized/Minimized）。</summary>
        PuppetWindowState WindowState { get; set; }
        /// <summary>是否置顶（常驻最前）。</summary>
        bool TopMost { get; set; }
        /// <summary>窗口标题。</summary>
        string Title { get; set; }
        /// <summary>窗口不透明度（0.0 全透明 ~ 1.0 不透明）。</summary>
        double Opacity { get; set; }
        /// <summary>激活（前置）窗口。</summary>
        void Activate();
        /// <summary>关闭窗口。</summary>
        void Close();
    }

    /// <summary>
    /// B 面「UI 基础 action」：由 Puppet.Core 定义**能力抽象**（UI 无关），平台包注入解析器把实例
    /// 适配为 <see cref="IPuppetUiElement"/> / <see cref="IPuppetWindow"/>；框架据此**自动 Actionize**
    /// （宿主无需逐窗体声明）。无 UI 能力的类型不受影响（Actionize 依旧完整，只是没有几何类基础 action）。
    /// 原则：**仅 GUI 才有的基础操作默认 Actionize**。
    /// 与 UI 平台无关——WinForms（Form/Control）、WPF（Window/FrameworkElement）、Web（DOM rect / CSS display / fullscreen）
    /// 都可提供适配。
    /// </summary>
    public static class PuppetUiActions
    {
        /// <summary>平台包注入：实例 → UI 元素能力（返回 null 表示该实例无 UI 能力）。</summary>
        public static Func<object, IPuppetUiElement> UiElementResolver { get; set; }

        /// <summary>平台包注入：实例 → 窗口能力（返回 null 表示非窗口）。</summary>
        public static Func<object, IPuppetWindow> WindowResolver { get; set; }

        /// <summary>宿主注入：关闭前确认（owner, text, caption）→ 是否继续；未注入时按安全默认（false）拒绝。</summary>
        public static Func<object, string, string, bool> Confirm { get; set; }

        /// <summary>平台包注入：枚举显示器（供 Agent 了解单屏/多屏环境）。未注入则不提供屏幕 state。</summary>
        public static Func<IReadOnlyList<PuppetScreen>> ScreensProvider { get; set; }

        // ---- action 名（稳定契约）----
        /// <summary>移动 UI 元素到 (x, y)。</summary>
        public const string MoveAction = "MoveAction";
        /// <summary>缩放 UI 元素到 (width, height)。</summary>
        public const string ResizeAction = "ResizeAction";
        /// <summary>同时设置位置与大小。</summary>
        public const string SetBoundsAction = "SetBoundsAction";
        /// <summary>设置 UI 元素可见性。</summary>
        public const string SetVisibleAction = "SetVisibleAction";
        /// <summary>设置 UI 元素可用性。</summary>
        public const string SetEnabledAction = "SetEnabledAction";
        /// <summary>设置窗口状态（max/min/normal）。</summary>
        public const string SetWindowStateAction = "SetWindowStateAction";
        /// <summary>激活（前置）窗口。</summary>
        public const string ActivateAction = "ActivateAction";
        /// <summary>关闭窗口。</summary>
        public const string CloseWindowAction = "CloseWindowAction";
        /// <summary>把键盘焦点移到 UI 元素。</summary>
        public const string FocusAction = "FocusAction";
        /// <summary>设置窗口置顶（常驻最前）。</summary>
        public const string SetTopMostAction = "SetTopMostAction";
        /// <summary>设置窗口标题。</summary>
        public const string SetTitleAction = "SetTitleAction";
        /// <summary>设置窗口不透明度（0.0~1.0）。</summary>
        public const string SetOpacityAction = "SetOpacityAction";

        internal const string GroupName = "UI";

        /// <summary>该实例适用的 UI 基础 action 清单（无 UI 能力返回空）。供 Actionize 追加到 manifest。</summary>
        public static List<ActionizePolicy.ActionMember> ForInstance(object instance)
        {
            var list = new List<ActionizePolicy.ActionMember>();
            if (instance == null) return list;

            bool hasElement = UiElementResolver?.Invoke(instance) != null;
            bool hasWindow = WindowResolver?.Invoke(instance) != null;
            if (!hasElement && !hasWindow) return list;

            if (hasElement)
            {
                list.Add(Base(MoveAction, "移动 UI 元素到 (x, y)（坐标语义见适配器：窗口为屏幕坐标，控件为父容器坐标）",
                    P("x", "integer", "目标 X"), P("y", "integer", "目标 Y")));
                list.Add(Base(ResizeAction, "缩放 UI 元素到 (width, height)",
                    P("width", "integer", "目标宽"), P("height", "integer", "目标高")));
                list.Add(Base(SetBoundsAction, "同时设置位置与大小",
                    P("x", "integer", "目标 X"), P("y", "integer", "目标 Y"),
                    P("width", "integer", "目标宽"), P("height", "integer", "目标高")));
                list.Add(Base(SetVisibleAction, "设置 UI 元素可见性", P("visible", "boolean", "是否可见")));
                list.Add(Base(SetEnabledAction, "设置 UI 元素可用性", P("enabled", "boolean", "是否可用")));
                list.Add(Base(FocusAction, "把键盘焦点移到该 UI 元素"));
            }
            if (hasWindow)
            {
                list.Add(Base(SetWindowStateAction, "设置窗口状态", P("state", "string", "max | min | normal")));
                list.Add(Base(ActivateAction, "激活（前置）窗口"));
                list.Add(Base(SetTopMostAction, "设置窗口置顶（常驻最前）", P("on", "boolean", "true=置顶；false=取消置顶")));
                list.Add(Base(SetTitleAction, "设置窗口标题", P("title", "string", "新标题")));
                list.Add(Base(SetOpacityAction, "设置窗口不透明度", P("opacity", "number", "0.0 全透明 ~ 1.0 不透明")));
                list.Add(Base(CloseWindowAction, "关闭窗口",
                    P("confirm", "boolean", "true=直接关闭；false/null=需用户确认（Agent 上下文按安全默认取消）", required: false)));
            }
            return list;
        }

        /// <summary>
        /// 合成 state：屏幕环境（仅 UI 能力实例；ScreensProvider 未注入则不提供）。
        /// Agent 据此知道单屏/多屏及各屏几何，从而把窗口摆到指定屏幕。
        /// </summary>
        internal static List<ActionizePolicy.StateMember> StatesForInstance(object instance)
        {
            var list = new List<ActionizePolicy.StateMember>();
            if (instance == null || ScreensProvider == null) return list;
            if (UiElementResolver?.Invoke(instance) == null && WindowResolver?.Invoke(instance) == null) return list;

            list.Add(new ActionizePolicy.StateMember
            {
                Name = "ScreenCount",
                Desc = "显示器数量（1=单屏，>1=多屏）",
                TypeName = "integer",
                Source = "base",
                ValueProvider = _ => ScreensProvider()?.Count ?? 0
            });
            list.Add(new ActionizePolicy.StateMember
            {
                Name = "Screens",
                Desc = "显示器列表：索引、设备名、边界、工作区、主屏标记（Agent 据此定位目标屏幕）",
                TypeName = "string",
                Source = "base",
                ValueProvider = _ => FormatScreens(ScreensProvider())
            });
            return list;
        }

        private static string FormatScreens(IReadOnlyList<PuppetScreen> screens)
        {
            if (screens == null || screens.Count == 0) return "";
            return string.Join("; ", screens.Select((s, i) =>
                $"[{i}] {s.DeviceName} bounds={s.X},{s.Y},{s.Width}x{s.Height} " +
                $"work={s.WorkX},{s.WorkY},{s.WorkWidth}x{s.WorkHeight}{(s.Primary ? " primary" : "")}"));
        }

        /// <summary>执行 UI 基础 action；返回 null 表示 actionName 不是 UI 基础 action。</summary>
        public static OperationResult Execute(object instance, string actionName, IDictionary<string, object> args)
        {
            args ??= new Dictionary<string, object>();
            var element = UiElementResolver?.Invoke(instance);
            var window = WindowResolver?.Invoke(instance);

            int Int(string k) => Convert.ToInt32(args.TryGetValue(k, out var v) && v != null ? v : 0);
            bool Bool(string k) => args.TryGetValue(k, out var v) && v is bool b && b;

            switch (actionName)
            {
                case MoveAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    element.Move(Int("x"), Int("y"));
                    return OperationResult.Success($"已移动到 ({element.X}, {element.Y})", Geometry(element));

                case ResizeAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    if (Int("width") <= 0 || Int("height") <= 0) return OperationResult.Failure("宽高必须为正整数");
                    element.Resize(Int("width"), Int("height"));
                    return OperationResult.Success($"已缩放到 {element.Width}×{element.Height}", Geometry(element));

                case SetBoundsAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    if (Int("width") <= 0 || Int("height") <= 0) return OperationResult.Failure("宽高必须为正整数");
                    element.SetBounds(Int("x"), Int("y"), Int("width"), Int("height"));
                    return OperationResult.Success($"已设为 ({element.X}, {element.Y}, {element.Width}×{element.Height})", Geometry(element));

                case SetVisibleAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    element.Visible = Bool("visible");
                    return OperationResult.Success($"可见性已设为 {element.Visible}");

                case SetEnabledAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    element.Enabled = Bool("enabled");
                    return OperationResult.Success($"可用性已设为 {element.Enabled}");

                case SetWindowStateAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    string state = (args.TryGetValue("state", out var sv) ? sv?.ToString() : "")?.Trim().ToLowerInvariant();
                    switch (state)
                    {
                        case "max" or "maximize" or "maximized" or "最大化":
                            window.WindowState = PuppetWindowState.Maximized; break;
                        case "min" or "minimize" or "minimized" or "最小化":
                            window.WindowState = PuppetWindowState.Minimized; break;
                        case "normal" or "restore" or "restored" or "还原" or "正常":
                            window.WindowState = PuppetWindowState.Normal; break;
                        default:
                            return OperationResult.Failure($"未知窗口状态「{state}」（可用 max/min/normal）");
                    }
                    return OperationResult.Success($"窗口状态已设为 {window.WindowState}", Geometry(element));

                case ActivateAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    window.Activate();
                    return OperationResult.Success("窗口已激活");

                case CloseWindowAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    bool? confirm = args.TryGetValue("confirm", out var cv) && cv is bool cb ? cb : (bool?)null;
                    if (confirm == false) return OperationResult.Failure("已取消关闭");
                    if (confirm != true)
                    {
                        bool proceed = Confirm != null && Confirm(instance, "确定关闭该窗口吗？", "关闭窗口");
                        if (!proceed) return OperationResult.Failure("需用户确认：请先征得用户同意再传 confirm=true");
                    }
                    window.Close();
                    return OperationResult.Success("窗口已关闭");

                case FocusAction:
                    if (element == null) return OperationResult.Failure("该实例不支持 UI 元素操作");
                    element.Focus();
                    return OperationResult.Success("已聚焦该 UI 元素");

                case SetTopMostAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    window.TopMost = Bool("on");
                    return OperationResult.Success($"置顶已设为 {window.TopMost}");

                case SetTitleAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    window.Title = args.TryGetValue("title", out var tv) ? tv?.ToString() : null;
                    return OperationResult.Success($"窗口标题已设为「{window.Title}」");

                case SetOpacityAction:
                    if (window == null) return OperationResult.Failure("该实例不是窗口");
                    double op = args.TryGetValue("opacity", out var ov) && ov != null ? Convert.ToDouble(ov) : 1.0;
                    if (op < 0 || op > 1) return OperationResult.Failure("opacity 必须在 0.0~1.0 之间");
                    window.Opacity = op;
                    return OperationResult.Success($"窗口不透明度已设为 {window.Opacity:0.##}");

                default:
                    return null;
            }
        }

        private static string Geometry(IPuppetUiElement e)
            => e == null ? null : $"{e.X},{e.Y},{e.Width},{e.Height}";

        private static ActionizePolicy.ActionMember Base(string name, string desc, params ActionizePolicy.ParamInfo[] ps)
            => new()
            {
                Name = name,
                Method = null,
                Desc = desc,
                Group = GroupName,
                Params = ps.ToList(),
                Source = "base",
                BaseAction = name
            };

        private static ActionizePolicy.ParamInfo P(string name, string type, string desc, bool required = true)
            => new() { Name = name, Type = type, Desc = desc, Required = required, Sensitive = false };
    }
}
