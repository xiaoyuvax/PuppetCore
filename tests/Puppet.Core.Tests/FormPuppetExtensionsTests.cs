using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json.Linq;
using Puppet.Core.Describe;
using Puppet.Core.WinForms;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>ToolStrip 寻址（1.4.x 盲区回归）：工具栏按钮/菜单项不是 Control，
    /// 纯控件树遍历看不到 —— describe 必须列出来、invoke/get 必须能按名命中。</summary>
    public class FormPuppetExtensionsTests
    {
        /// <summary>最小宿主：ToolStrip + 普通按钮 + 下拉子菜单 + 一个 [PuppetIgnore] 按钮。</summary>
        private sealed class ToolbarHost : Control, IPuppet
        {
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string AgentAccessKey { get; set; } = "k";
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public Common.Logging.ILog AgentLog { get; set; }
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string AgentInstanceName { get; set; } = "Toolbar#1";

            public bool UploadClicked;
            public bool ChildClicked;

            // Designer 模式：字段名 == 控件名（[PuppetIgnore] 按字段名匹配条目名）
            [PuppetIgnore]
            private readonly ToolStripButton tsbSecret = new() { Name = "tsbSecret", Text = "secret" };
            private readonly ToolStrip tsMain = new() { Name = "tsMain" };

            public ToolbarHost()
            {
                var upload = new ToolStripButton("上传") { Name = "tsbUpload" };
                upload.Click += (_, _) => UploadClicked = true;

                var more = new ToolStripDropDownButton("更多") { Name = "tsbMore" };
                var child = new ToolStripMenuItem("子项") { Name = "tsmChild" };
                child.Click += (_, _) => ChildClicked = true;
                more.DropDownItems.Add(child);

                tsMain.Items.Add(upload);
                tsMain.Items.Add(more);
                tsMain.Items.Add(new ToolStripButton("禁用") { Name = "tsbDisabled", Enabled = false });
                tsMain.Items.Add(tsbSecret);

                Controls.Add(tsMain);
                Controls.Add(new Button { Name = "btnOk", Text = "OK" });
            }
        }

        private static JObject Json(string s) => JObject.Parse(s);

        [Fact]
        public void Describe_own_filters_inherited_Control_properties()
        {
            using var host = new ToolbarHost();

            // 默认（own=false）：含 Control 继承属性（Visible/Text…）
            var all = Json(host.Describe()).Value<JArray>("Properties")
                .Select(p => p.Value<string>("Name")).ToHashSet();
            Assert.Contains("Visible", all);
            Assert.Contains("AgentInstanceName", all);

            // own=true：继承属性被滤掉，本类型声明成员保留
            var own = Json(host.Describe(DescribeFormat.Json, null, true)).Value<JArray>("Properties")
                .Select(p => p.Value<string>("Name")).ToHashSet();
            Assert.DoesNotContain("Visible", own);
            Assert.Contains("AgentInstanceName", own);
        }

        [Fact]
        public void DescribeControls_Lists_ToolStripItems_WithCapabilities()
        {
            using var host = new ToolbarHost();
            var json = Json(host.DescribeControls());
            Assert.True(json.Value<bool>("ok"));

            var names = json["controls"].Select(c => (string)c["name"]).ToArray();
            Assert.Contains("tsbUpload", names);      // 工具栏按钮（旧实现不可见）
            Assert.Contains("tsbMore", names);        // 下拉容器
            Assert.Contains("tsmChild", names);       // 下拉子菜单项
            Assert.Contains("btnOk", names);          // 普通控件不回归

            var upload = json["controls"].First(c => (string)c["name"] == "tsbUpload");
            Assert.Equal("ToolStripButton", upload.Value<string>("type"));
            Assert.Contains("click", upload["capabilities"].Select(c => (string)c));
        }

        [Fact]
        public void DescribeControls_Skips_PuppetIgnore_Item()
        {
            using var host = new ToolbarHost();
            var json = Json(host.DescribeControls());
            var names = json["controls"].Select(c => (string)c["name"]).ToArray();
            Assert.DoesNotContain("tsbSecret", names);
        }

        [Fact]
        public void InvokeControl_Clicks_ToolStripButton()
        {
            using var host = new ToolbarHost();
            var json = Json(host.InvokeControl("tsbUpload", "click"));
            Assert.True(json.Value<bool>("ok"));
            Assert.Equal("tsbUpload", json.Value<string>("clicked"));
            Assert.True(host.UploadClicked, "PerformClick 应真正触发 Click 事件");
        }

        [Fact]
        public void InvokeControl_Clicks_DropDownChild_MenuItem()
        {
            using var host = new ToolbarHost();
            var json = Json(host.InvokeControl("tsmChild", "click"));
            Assert.True(json.Value<bool>("ok"));
            Assert.True(host.ChildClicked);
        }

        [Fact]
        public void InvokeControl_PuppetIgnored_Item_IsNotFound()
        {
            using var host = new ToolbarHost();
            var json = Json(host.InvokeControl("tsbSecret", "click"));
            Assert.False(json.Value<bool>("ok"));
            Assert.Equal("control not found", json.Value<string>("err"));
        }

        [Fact]
        public void InvokeControl_Disabled_Item_IsRejected()
        {
            using var host = new ToolbarHost();
            var json = Json(host.InvokeControl("tsbDisabled", "click"));
            Assert.False(json.Value<bool>("ok"));
            Assert.Equal("item disabled", json.Value<string>("err"));
        }

        [Fact]
        public void InvokeControl_Unknown_Name_Reports_NotFound()
        {
            using var host = new ToolbarHost();
            var json = Json(host.InvokeControl("tsbNope", "click"));
            Assert.Equal("control not found", json.Value<string>("err"));
        }

        [Fact]
        public void GetControlState_Returns_ToolStripItem_Shape()
        {
            using var host = new ToolbarHost();
            var json = Json(host.GetControlState("tsbUpload"));
            Assert.True(json.Value<bool>("ok"));
            Assert.Equal("tsbUpload", json.Value<string>("name"));
            Assert.Equal("ToolStripButton", json.Value<string>("type"));
            Assert.NotNull(json["checkedState"]);
        }

        /// <summary>标准值转换器（模拟 Content.fontWeight 这类"下拉值 + 中文标签"）。</summary>
        private sealed class LevelConverter : StringConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
                => new(new[] { "a", "b" });
            public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture,
                object value, Type destinationType)
                => destinationType == typeof(string) && value is string s
                    ? (s == "a" ? "甲 (a)" : s == "b" ? "乙 (b)" : s)
                    : base.ConvertTo(context, culture, value, destinationType);
            public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
                => value is string s && s.StartsWith("甲") ? "a" : value is string s2 && s2.StartsWith("乙") ? "b" : base.ConvertFrom(context, culture, value);
        }

        private sealed class GridModel
        {
            [DisplayName("档位")]
            [TypeConverter(typeof(LevelConverter))]
            public string level { get; set; } = "a";

            [DisplayName("名称")]
            public string title { get; set; } = "hello";
        }

        private sealed class GridHost : Form, IPuppet
        {
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string AgentAccessKey { get; set; } = "k";
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public Common.Logging.ILog AgentLog { get; set; }
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string AgentInstanceName { get; set; } = "Grid#1";

            public readonly PropertyGrid pgModel = new() { Name = "pgModel", Dock = DockStyle.Fill };

            public GridHost()
            {
                Controls.Add(pgModel);
                pgModel.SelectedObject = new GridModel();
                CreateControl();
                pgModel.CreateControl();
                pgModel.Refresh();
            }
        }

        /// <summary>PropertyGrid rows：导出行 + 下拉候选值（与下拉列表同源，避免点开模态列表）。</summary>
        [Fact]
        public void InvokeControl_PropertyGrid_Rows_Exports_StandardValues()
        {
            using var host = new GridHost();
            var json = Json(host.InvokeControl("pgModel", "rows"));
            Assert.True(json.Value<bool>("ok"), json.ToString());

            var level = json["rows"].FirstOrDefault(r => (string)r["property"] == "level");
            Assert.NotNull(level);
            Assert.Equal("a", level.Value<string>("value"));
            Assert.Equal("甲 (a)", level.Value<string>("display"));
            Assert.Equal(new[] { "甲 (a)", "乙 (b)" }, level["standardValues"].Select(v => (string)v).ToArray());

            var title = json["rows"].FirstOrDefault(r => (string)r["property"] == "title");
            Assert.NotNull(title);
            Assert.Null(title["standardValues"]);
        }

        [Fact]
        public void DescribeControls_PropertyGrid_Advertises_Rows()
        {
            using var host = new GridHost();
            var json = Json(host.DescribeControls());
            var pg = json["controls"].First(c => (string)c["name"] == "pgModel");
            Assert.Contains("rows", pg["capabilities"].Select(c => (string)c));
        }
    }
}
