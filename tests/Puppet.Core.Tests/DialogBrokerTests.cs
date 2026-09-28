using Puppet.Core.AppAgent;
using Xunit;

namespace Puppet.Core.Tests
{
    /// <summary>DialogBroker 核心路径：作用域生命周期、预答、安全默认、快照、async 流动。</summary>
    public class DialogBrokerTests
    {
        [Fact]
        public void Inactive_by_default_outside_scope()
        {
            Assert.False(DialogBroker.IsActive);
            var (answer, fromPreset) = DialogBroker.Resolve("任意标题", "Cancel");
            Assert.Null(answer);            // 无上下文 → 宿主走原生模态
            Assert.False(fromPreset);
        }

        [Fact]
        public void Push_activates_scope_and_restores_on_dispose()
        {
            Assert.False(DialogBroker.IsActive);
            using (DialogBroker.Push(null))
            {
                Assert.True(DialogBroker.IsActive);
                var (answer, fromPreset) = DialogBroker.Resolve("未预置框", "Cancel");
                Assert.Equal("Cancel", answer);   // 安全默认兜底
                Assert.False(fromPreset);
            }
            Assert.False(DialogBroker.IsActive);  // using 还原
        }

        [Fact]
        public void Preset_hit_answers_from_preset_and_records()
        {
            using (DialogBroker.Push(new[] { new DialogPreset { Title = "保存确认", Answer = "OK" } }))
            {
                var (hit, fromPreset) = DialogBroker.Resolve("保存确认", "Cancel");
                Assert.Equal("OK", hit);
                Assert.True(fromPreset);

                var (miss, missFromPreset) = DialogBroker.Resolve("另一个框", "No");
                Assert.Equal("No", miss);         // 未预置 → 安全默认
                Assert.False(missFromPreset);

                var answered = DialogBroker.AnsweredSnapshot();
                Assert.Equal(2, answered.Count);
                Assert.Equal("保存确认", answered[0].Title);
                Assert.True(answered[0].FromPreset);
                Assert.False(answered[1].FromPreset);
            }
            Assert.Empty(DialogBroker.AnsweredSnapshot());  // 作用域外无记录
        }

        [Fact]
        public void Preset_with_empty_or_null_title_is_ignored()
        {
            using (DialogBroker.Push(new[] { new DialogPreset { Title = "", Answer = "OK" }, null }))
            {
                var (answer, fromPreset) = DialogBroker.Resolve("", "Cancel");
                Assert.Equal("Cancel", answer);   // 空 title 预置无效 → 安全默认
                Assert.False(fromPreset);
            }
        }

        [Fact]
        public void Nested_scope_inner_wins_and_restore_unwinds()
        {
            using (DialogBroker.Push(null))
            {
                Assert.True(DialogBroker.IsActive);
                using (DialogBroker.Push(new[] { new DialogPreset { Title = "T", Answer = "Yes" } }))
                {
                    var (a, p) = DialogBroker.Resolve("T", "No");
                    Assert.Equal("Yes", a);
                    Assert.True(p);
                }
                // 内层还原后回到外层（null presets）：预答不再命中
                var (a2, p2) = DialogBroker.Resolve("T", "No");
                Assert.Equal("No", a2);
                Assert.False(p2);
                Assert.True(DialogBroker.IsActive);   // 外层仍激活
            }
            Assert.False(DialogBroker.IsActive);
        }

        [Fact]
        public async Task Scope_flows_across_await_and_ui_thread_style_continuation()
        {
            using (DialogBroker.Push(null))
            {
                var t1 = Task.Run(async () =>
                {
                    await Task.Delay(10);
                    return DialogBroker.IsActive;   // AsyncLocal 随 ExecutionContext 流动
                });
                Assert.True(await t1);

                // 模拟 RunOnUi 式同步编组（Send/Invoke 走 ExecutionContext.Copy()）
                bool viaScheduler = await Task.Factory.StartNew(
                    () => DialogBroker.IsActive,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    TaskScheduler.Default);
                Assert.True(viaScheduler);
            }
            Assert.False(DialogBroker.IsActive);
        }

        [Fact]
        public void Push_null_presets_is_valid_and_semantics_hold()
        {
            // A 通道根修所依赖的调用形态：Push(null) = 纯安全默认作用域
            using (DialogBroker.Push(null))
            {
                Assert.True(DialogBroker.IsActive);
                var (answer, fromPreset) = DialogBroker.Resolve("任何", "No");
                Assert.Equal("No", answer);
                Assert.False(fromPreset);
            }
        }
    }
}
