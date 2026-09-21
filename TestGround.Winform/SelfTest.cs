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

        await RunAppAgentAsync(form);
    }

    /// <summary>
    /// 面向 B（/appagent/*）锁定测试：发现（L0 档案 + L1 探针 + L2 手册）→ 凭证分层 → manifest 范式与三层文案 →
    /// 按名传参严格校验 → 实例串行 409 → Task&lt;bool&gt; 解包 → 产物下载 → 错误契约。
    /// 全程走真实用户 Agent 发现路径（枚举 %LOCALAPPDATA% 档案取凭证），不依赖任何测试钩子。
    /// </summary>
    private static async Task RunAppAgentAsync(TaskBoardForm form)
    {
        // ---- L0 发现：枚举档案目录（用户 Agent 的真实路径） ----
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Puppet.AppAgents");
        var profileFile = Directory.GetFiles(profileDir, "小步任务看板.*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("discovery profile file missing (L0)");
        using var profileDoc = JsonDocument.Parse(await File.ReadAllTextAsync(profileFile));
        var profile = profileDoc.RootElement;
        var key = profile.GetProperty("key").GetString();
        var endpoint = profile.GetProperty("endpoint").GetString();
        Check(endpoint == "http://127.0.0.1:19101", "L0 profile endpoint");

        var client = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            // ---- L1 探针 + L2 手册：无凭证可达，最小披露（不含能力细节） ----
            using (var probe = await client.GetAsync("/appagent/probe"))
            {
                probe.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("puppet").GetBoolean()
                    && doc.RootElement.GetProperty("protocol").GetString() == "appagent/1.0"
                    && doc.RootElement.GetProperty("help").GetString() == "/appagent/help"
                    && !doc.RootElement.TryGetProperty("actions", out _), "L1 probe minimal disclosure");
            }
            using (var help = await client.GetAsync("/appagent/help"))
            {
                help.EnsureSuccessStatusCode();
                Check((await help.Content.ReadAsStringAsync()).Contains("/appagent/manifest", StringComparison.Ordinal), "L2 self-describing help");
            }

            // ---- 凭证分层：能力目录必须持档案凭证（与 A 的 404 静默刻意不同：B 是公开协议，凭证明示） ----
            using (var denied = await client.GetAsync("/appagent/manifest"))
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "manifest requires credential");
            using (var bad = new HttpRequestMessage(HttpMethod.Get, "/appagent/manifest"))
            {
                bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-key");
                using var rejected = await client.SendAsync(bad);
                Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "wrong credential rejected");
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            // ---- manifest：范式自动收录 + 覆盖收录 + 三层文案 + L1 原始文案图 + 并发契约 ----
            using (var manifest = await client.GetAsync("/appagent/manifest"))
            {
                manifest.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                Check(root.GetProperty("productName").GetString() == "小步任务看板", "manifest product name");
                Check(root.GetProperty("concurrency").GetProperty("actionExecution").GetString() == "serialized-per-instance", "manifest concurrency contract");
                var actions = root.GetProperty("actions").EnumerateArray().ToArray();
                var actionNames = actions.Select(a => a.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
                Check(actionNames.Contains("AddTask") && actionNames.Contains("CompleteSelectedTask")
                    && actionNames.Contains("RenameSelectedTask") && actionNames.Contains("SetFilter"), "actionize pattern members");
                Check(actionNames.Contains("ExportSummary"), "attribute override member (int return)");
                Check(actionNames.Contains("SlowMarkdown"), "async anchor Task<bool>");
                Check(!actionNames.Contains("Report") && !actionNames.Contains("ToString"), "internals excluded");
                Check(!actionNames.Contains("ClearCompletedTasks"), "non-anchor int return excluded without override");
                var addTask = actions.First(a => a.GetProperty("name").GetString() == "AddTask");
                Check(addTask.GetProperty("desc").GetString() == "Add Task", "L3 inferred description (AddTask)");
                Check(addTask.GetProperty("parameters").EnumerateArray()
                    .Any(p => p.GetProperty("name").GetString() == "title" && p.GetProperty("required").GetBoolean()), "title parameter required");
                var export = actions.First(a => a.GetProperty("name").GetString() == "ExportSummary");
                Check(export.GetProperty("desc").GetString() == "导出当前看板摘要文本文件", "L2 explicit description (attribute)");
                Check(root.GetProperty("uiTexts").GetProperty("AddButton").GetString() == "添加", "L1 raw control text map");
            }

            // ---- 按名严格绑定：未知参数 400 / 缺必填 400 / 未知 action 404 ----
            // 注意：不用 PostAsJsonAsync 传相对 URI——其 UriKind.RelativeOrAbsolute 解析会把 action 名小写化，路径须逐字保留
            using (var unknown = await client.PostAsync("/appagent/actions/AddTask", JsonBody(new { args = new { titel = "x" } })))
                Check(unknown.StatusCode == HttpStatusCode.BadRequest, $"unknown parameter name 400 (got {(int)unknown.StatusCode}: {await unknown.Content.ReadAsStringAsync()})");
            using (var missing = await client.PostAsync("/appagent/actions/AddTask", JsonBody(new { args = new { } })))
                Check(missing.StatusCode == HttpStatusCode.BadRequest, $"missing required parameter 400 (got {(int)missing.StatusCode}: {await missing.Content.ReadAsStringAsync()})");
            using (var notFound = await client.PostAsync("/appagent/actions/Nope", JsonBody(new { args = new { } })))
                Check(notFound.StatusCode == HttpStatusCode.NotFound, $"action not found 404 (got {(int)notFound.StatusCode}: {await notFound.Content.ReadAsStringAsync()})");

            // ---- 按名传参执行：ok 信封 + callId 回显 ----
            using (var call = await client.PostAsJsonAsync("/appagent/actions/AddTask",
                new { args = new { title = "Agent 代办", priority = "高" }, callId = "selftest-b-1" }))
            {
                call.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("ok").GetBoolean()
                    && doc.RootElement.GetProperty("action").GetString() == "AddTask"
                    && doc.RootElement.GetProperty("callId").GetString() == "selftest-b-1", "action envelope");
            }
            Check(form.TotalCount == 1 && form.SelectedTitle == "Agent 代办" && form.SelectedPriority == "高", "action marshals to UI (by-name binding)");

            // ---- 可选参数省略 → 默认值"普通" ----
            using (var call = await client.PostAsJsonAsync("/appagent/actions/AddTask", new { args = new { title = "默认优先级" } }))
            {
                call.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("ok").GetBoolean(), "optional parameter omitted");
            }
            Check(form.SelectedPriority == "普通", "default parameter value applied");

            // ---- 业务拒绝：false 结果与 hint（不落库） ----
            using (var rejected = await client.PostAsJsonAsync("/appagent/actions/AddTask", new { args = new { title = " " } }))
            {
                rejected.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Check(!doc.RootElement.GetProperty("ok").GetBoolean()
                    && doc.RootElement.GetProperty("hint").GetString()!.Length > 0, "business rejection with hint");
            }
            Check(form.TotalCount == 2, "rejected action left no state");

            // ---- 实例级强制串行：占住执行锁后再调用 → 409 busy + retryAfterMs（BusyWaitMs=200） ----
            var blocker = client.PostAsJsonAsync("/appagent/actions/SlowMarkdown", new { args = new { milliseconds = 900 } });
            await Task.Delay(150); // blocker 已持有实例锁（SlowMarkdown 于 UI 线程执行中）
            using (var busy = await client.PostAsync("/appagent/actions/ExportSummary",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            {
                Check(busy.StatusCode == HttpStatusCode.Conflict, "instance serialized");
                using var doc = JsonDocument.Parse(await busy.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("code").GetString() == "instance-busy"
                    && doc.RootElement.GetProperty("retryAfterMs").GetInt32() == 200, "409 busy contract with retryAfterMs");
            }
            using (var blocked = await blocker)
            {
                blocked.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("ok").GetBoolean(), "Task<bool> unwrapped after serialization");
            }
            Check(form.TotalCount == 2, "serial queue preserved state");

            // ---- 产物通道：ExportSummary → asset URL → 凭证下载（内存态模拟磁盘文件） ----
            using (var export = await client.PostAsync("/appagent/actions/ExportSummary",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            {
                export.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await export.Content.ReadAsStringAsync());
                var assetUrl = doc.RootElement.GetProperty("asset").GetString();
                Check(assetUrl != null && assetUrl.StartsWith("/appagent/assets/", StringComparison.Ordinal), "artifact asset url");
                using var download = await client.GetAsync(assetUrl);
                download.EnsureSuccessStatusCode();
                var bytes = await download.Content.ReadAsByteArrayAsync();
                var cd = download.Content.Headers.ContentDisposition;
                var fileName = (cd.FileNameStar ?? cd.FileName)?.Trim('"');
                Check(download.Content.Headers.ContentType!.MediaType == "text/plain"
                    && fileName == "summary.txt"
                    && System.Text.Encoding.UTF8.GetString(bytes).Contains("任务看板摘要", StringComparison.Ordinal), "asset download with content type");
                using var noAsset = await client.GetAsync(assetUrl + "-zz");
                Check(noAsset.StatusCode == HttpStatusCode.NotFound, "asset not found 404");
            }

            // ---- state 直读 + 错误契约（state-not-found 带 hint） ----
            using (var state = await client.GetAsync("/appagent/state/TotalCount"))
            {
                state.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
                Check(doc.RootElement.GetProperty("key").GetString() == "TotalCount"
                    && doc.RootElement.GetProperty("value").GetInt32() == 2, "state scalar read");
            }
            using (var noState = await client.GetAsync("/appagent/state/SecretInternalThing"))
                Check(noState.StatusCode == HttpStatusCode.NotFound, "state not found 404");

            // ---- 清场（B 场景数据不残留） ----
            Check(form.SelectTask(0) && form.RemoveSelectedTask()
                && form.SelectTask(0) && form.RemoveSelectedTask() && form.TotalCount == 0, "appagent scenario cleanup");
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>action 调用体（POST 相对路径用 StringContent，防 System.Net.Http.Json 相对 URI 小写化坑）</summary>
    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
