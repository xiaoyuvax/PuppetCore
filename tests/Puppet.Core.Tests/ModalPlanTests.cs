using Puppet.Core.AppAgent;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>ModalPlan 包裹范式：人工/Agent 双上下文、预置与安全默认、不同输入项与 DialogResult 的映射、答案载荷不透明。</summary>
    public class ModalPlanTests
    {
        [Fact]
        public void Outside_scope_shows_real_dialog_and_never_synthesizes()
        {
            bool shown = false, synthesized = false;
            var plan = new ModalPlan<string>
            {
                Title = "命名粘贴图片",
                SafeDefaultAnswer = "pasted",
                Synthesize = _ => { synthesized = true; return "synth"; },
                Show = () => { shown = true; return "real"; }
            };

            Assert.Equal("real", plan.Run());
            Assert.True(shown);
            Assert.False(synthesized);
        }

        [Fact]
        public void Inside_scope_never_shows_and_uses_safe_default()
        {
            using (DialogBroker.Push(null))
            {
                bool shown = false;
                var plan = new ModalPlan<string>
                {
                    Title = "文件名",
                    SafeDefaultAnswer = "默认名",
                    Synthesize = a => a,
                    Show = () => { shown = true; return "x"; }
                };

                Assert.Equal("默认名", plan.Run());
                Assert.False(shown);                       // HTTP 不阻塞的关键：Agent 上下文永不弹窗

                var answered = DialogBroker.AnsweredSnapshot();
                Assert.Single(answered);
                Assert.Equal("文件名", answered[0].Title);
                Assert.Equal("默认名", answered[0].Answer);
                Assert.False(answered[0].FromPreset);      // 安全默认应答
            }
        }

        [Fact]
        public void Inside_scope_preset_answer_wins_and_records_FromPreset()
        {
            using (DialogBroker.Push(new[] { new DialogPreset { Title = "打开图片", Answer = "C:/in/a.png" } }))
            {
                var plan = new ModalPlan<string>
                {
                    Title = "打开图片",
                    SafeDefaultAnswer = "",
                    Synthesize = a => string.IsNullOrEmpty(a) ? null : a,
                    Show = () => "never"
                };

                Assert.Equal("C:/in/a.png", plan.Run());    // 预置直接成为该框的输入/结果
                Assert.Equal("C:/in/a.png", DialogBroker.AnsweredSnapshot()[0].Answer);
                Assert.True(DialogBroker.AnsweredSnapshot()[0].FromPreset);
            }
        }

        [Fact]
        public void Same_model_expresses_different_inputs_and_dialog_results()
        {
            using (DialogBroker.Push(null))
            {
                // 文本输入框 → string
                var input = new ModalPlan<string>
                {
                    Title = "命名粘贴图片",
                    SafeDefaultAnswer = "pasted",
                    Synthesize = a => a,
                    Show = () => "real"
                };
                Assert.Equal("pasted", input.Run());

                // 文件选择框 → 路径（空串 = 取消 = null）
                var file = new ModalPlan<string>
                {
                    Title = "打开",
                    SafeDefaultAnswer = "",
                    Synthesize = a => string.IsNullOrEmpty(a) ? null : a,
                    Show = () => "real"
                };
                Assert.Null(file.Run());

                // 确认框 → bool（安全默认 No → false）
                var confirm = new ModalPlan<bool>
                {
                    Title = "退出确认",
                    SafeDefaultAnswer = "No",
                    Synthesize = a => string.Equals(a, "Yes", StringComparison.OrdinalIgnoreCase),
                    Show = () => true
                };
                Assert.False(confirm.Run());

                // 颜色选择框 → int（安全默认给 hex）
                var color = new ModalPlan<int>
                {
                    Title = "选颜色",
                    SafeDefaultAnswer = "#FF0000",
                    Synthesize = a => int.Parse(a.TrimStart('#'), System.Globalization.NumberStyles.HexNumber),
                    Show = () => 0
                };
                Assert.Equal(0xFF0000, color.Run());
            }
        }

        [Fact]
        public void Answer_payload_is_opaque_and_parsed_by_host_synthesize()
        {
            using (DialogBroker.Push(new[] { new DialogPreset { Title = "多输入框", Answer = "name=pasted;prefix=Source" } }))
            {
                var plan = new ModalPlan<IReadOnlyDictionary<string, string>>
                {
                    Title = "多输入框",
                    SafeDefaultAnswer = "",
                    Synthesize = a => a.Split(';')
                        .Select(kv => kv.Split('='))
                        .Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1]),
                    Show = () => new Dictionary<string, string>()
                };

                var inputs = plan.Run();
                Assert.Equal("pasted", inputs["name"]);
                Assert.Equal("Source", inputs["prefix"]);
            }
        }

        [Fact]
        public void No_safe_default_yields_null_and_still_skips_dialog()
        {
            using (DialogBroker.Push(null))
            {
                bool shown = false;
                var plan = new ModalPlan<string>
                {
                    Title = "任意框",
                    Synthesize = a => a,
                    Show = () => { shown = true; return "x"; }
                };

                Assert.Null(plan.Run());   // 未预置且未给安全默认 → null（等价取消）
                Assert.False(shown);
            }
        }
    }
}
