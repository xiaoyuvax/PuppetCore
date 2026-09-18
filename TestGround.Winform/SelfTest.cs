using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Puppet.Core;

namespace TestGround.Winform;

internal static class SelfTest
{
    internal static async Task RunAsync(TaskBoardForm form)
    {
        Check(form.IsHandleCreated && ReferenceEquals(PuppetRegistry.Resolve("TaskBoard"), form), "UI-ready registration");
        Check(form.TotalCount == 0 && form.VisibleCount == 0, "empty board");
        Check(!form.AddTask(null) && !form.AddTask(" \t ") && !form.AddTask(new string('x', 121)) && !form.AddTask("a\nb"), "invalid titles");
        Check(!form.AddTask("bad priority", "紧急") && form.TotalCount == 0, "invalid priority rejected");
        Check(!form.CompleteSelectedTask() && !form.RemoveSelectedTask() && !form.SelectTask(0) && !form.ToggleSelectedPriority() && !form.RenameSelectedTask("x"), "no selection");
        Check(form.AddTask("  Draft proposal  ") && form.SelectedTitle == "Draft proposal", "trim and select added task");
        Check(!form.AddTask("draft proposal") && form.TotalCount == 1, "case-insensitive duplicate");
        Check(form.AddTask(new string('x', 120)) && form.TotalCount == 2, "title boundary");
        Check(!form.SetFilter("invalid") && form.CurrentFilter == "全部", "invalid filter preserves state");
        Check(!form.SelectTask(-1) && !form.SelectTask(2), "invalid selection");
        Check(form.SelectTask(0) && form.CompleteSelectedTask(), "complete selection");
        Check(!form.CompleteSelectedTask() && form.CompletedCount == 1 && form.PendingCount == 1, "repeat completion and counts");
        Check(form.SetFilter("待办") && form.VisibleCount == 1 && form.SelectedTitle == "", "filter clears hidden selection");
        Check(!form.RemoveSelectedTask() && form.SelectTask(0) && form.CompleteSelectedTask() && form.VisibleCount == 0, "completion leaves pending filter");
        Check(form.SetFilter("已完成") && form.VisibleCount == 2, "completed filter");
        Check(form.SelectTask(0) && form.RemoveSelectedTask() && form.TotalCount == 1, "remove completed task");
        Check(form.SelectTask(0) && form.RemoveSelectedTask() && form.TotalCount == 0, "remove final task");
        Check(form.AddTask("筛选中添加") && form.CurrentFilter == "全部" && form.VisibleCount == 1, "add reveals new task");
        Check(form.RemoveSelectedTask(), "remove added task");

        Check(form.AddTask("优先级任务", "高") && form.SelectedPriority == "高" && form.HighPriorityPendingCount == 1, "high priority add");
        Check(form.SelectedCreatedAt.Length == 16 && form.SelectedCreatedAt.Contains('-', StringComparison.Ordinal), "created timestamp observed");
        Check(form.AddTask("普通任务") && form.SelectedTitle == "普通任务" && form.SelectedPriority == "普通", "add selects new pending task");
        Check(form.ToggleSelectedPriority() && form.SelectedPriority == "高" && form.HighPriorityPendingCount == 2, "toggle to high");
        Check(form.ToggleSelectedPriority() && form.SelectedPriority == "普通" && form.HighPriorityPendingCount == 1, "toggle back to normal");
        Check(!form.RenameSelectedTask(" ") && !form.RenameSelectedTask("优先级任务") && form.SelectedTitle == "普通任务", "rename validation");
        Check(form.RenameSelectedTask("  新名字  ") && form.SelectedTitle == "新名字" && form.TotalCount == 2, "rename trims and applies");
        Check(form.AddTask("批量完成甲") && form.AddTask("批量完成乙") && form.SelectTask(0) && form.CompleteSelectedTask(), "seed clear-completed");
        Check(form.SelectTask(1) && form.CompleteSelectedTask() && form.CompletedCount == 2, "complete more for clear");
        Check(form.ClearCompletedTasks() == 2 && form.TotalCount == 2 && form.CompletedCount == 0, "clear completed removes all");
        Check(form.ClearCompletedTasks() == 0 && form.StatusText == "当前没有已完成的任务。", "clear completed empty no-op");
        Check(form.SelectTask(0) && form.RemoveSelectedTask() && form.SelectTask(0) && form.RemoveSelectedTask() && form.TotalCount == 0, "final cleanup");

        var input = (TextBox)form.Controls.Find("TaskInput", true).Single();
        var add = (Button)form.Controls.Find("AddButton", true).Single();
        var complete = (Button)form.Controls.Find("CompleteButton", true).Single();
        var remove = (Button)form.Controls.Find("RemoveButton", true).Single();
        var rename = (Button)form.Controls.Find("RenameButton", true).Single();
        var togglePriority = (Button)form.Controls.Find("PriorityButton", true).Single();
        var clearDone = (Button)form.Controls.Find("ClearDoneButton", true).Single();
        var filter = (ComboBox)form.Controls.Find("TaskFilter", true).Single();
        var priority = (ComboBox)form.Controls.Find("TaskPriority", true).Single();
        var list = (ListBox)form.Controls.Find("TaskList", true).Single();
        Check(!complete.Enabled && !remove.Enabled && !rename.Enabled && !togglePriority.Enabled && !clearDone.Enabled, "empty buttons disabled");
        input.Text = "通过真实按钮添加";
        priority.SelectedIndex = 1;
        add.PerformClick();
        Check(form.TotalCount == 1 && form.SelectedPriority == "高" && priority.SelectedIndex == 0, "add button honors priority combo");
        togglePriority.PerformClick();
        Check(form.SelectedPriority == "普通" && form.HighPriorityPendingCount == 0, "priority button wiring");
        input.Text = "按钮重命名";
        rename.PerformClick();
        Check(form.SelectedTitle == "按钮重命名" && input.Text == "", "rename button wiring");
        complete.PerformClick();
        Check(form.CompletedCount == 1 && !complete.Enabled && remove.Enabled, "complete button wiring");
        clearDone.PerformClick();
        Check(form.TotalCount == 0 && !remove.Enabled, "clear completed button wiring");
        input.Text = "列表选择任务";
        add.PerformClick();
        complete.PerformClick();
        filter.SelectedIndex = 1;
        Check(form.VisibleCount == 0 && !remove.Enabled, "filter event wiring");
        filter.SelectedIndex = 2;
        list.SelectedIndex = 0;
        Check(remove.Enabled && form.SelectedCompleted, "list selection wiring");
        remove.PerformClick();
        Check(form.TotalCount == 0 && !remove.Enabled, "remove button wiring");
        form.SetFilter("全部");

        if (form.AgentStatus.StartsWith("Puppet 已禁用", StringComparison.Ordinal)) return;
        Check(form.AgentStatus.Contains("127.0.0.1:19101", StringComparison.Ordinal), "server startup");
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:19101"), Timeout = TimeSpan.FromSeconds(10) };
        using (var unauthorized = await client.GetAsync("/agent/get?name=TaskBoard&path=TotalCount"))
            Check(unauthorized.StatusCode == HttpStatusCode.NotFound, "unauthenticated request denied");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ((IPuppet)form).AgentAccessKey);
        using (var registry = await client.GetAsync("/agent/registry"))
        {
            registry.EnsureSuccessStatusCode();
            Check((await registry.Content.ReadAsStringAsync()).Contains("TaskBoard", StringComparison.Ordinal), "HTTP registration");
        }
        using (var controls = await client.GetAsync("/agent/control?name=TaskBoard"))
        {
            controls.EnsureSuccessStatusCode();
            Check((await controls.Content.ReadAsStringAsync()).Contains("TaskInput", StringComparison.Ordinal), "UseFormControls route");
        }
        using (var invoke = await client.PostAsJsonAsync("/agent/invoke?name=TaskBoard&method=AddTask", new object[] { "HTTP task", "高" }))
        {
            invoke.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await invoke.Content.ReadAsStringAsync());
            Check(result.RootElement.GetProperty("ok").GetBoolean() && result.RootElement.GetProperty("result").GetBoolean(), "HTTP shared operation");
        }
        Check(form.TotalCount == 1 && form.SelectedTitle == "HTTP task" && form.SelectedPriority == "高", "HTTP marshals to UI");
        using (var get = await client.GetAsync("/agent/get?name=TaskBoard&path=TotalCount"))
        {
            get.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Check(result.RootElement.GetProperty("value").GetInt32() == 1, "HTTP scalar observation");
        }
        using (var get = await client.GetAsync("/agent/get?name=TaskBoard&path=SelectedCreatedAt"))
        {
            get.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Check(result.RootElement.GetProperty("value").GetString()!.Length == 16, "HTTP timestamp observation");
        }
        using (var click = await client.PostAsync("/agent/control?name=TaskBoard&ctrl=CompleteButton&action=click", null))
        {
            click.EnsureSuccessStatusCode();
            Check(form.CompletedCount == 1, "HTTP native button operation");
        }
        Check(form.RemoveSelectedTask() && form.TotalCount == 0, "HTTP scenario cleanup");
    }

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
