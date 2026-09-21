using System.Net;
using System.Text;
using System.Text.Json;

namespace TestGround.Console;

using Console = System.Console;

/// <summary>
/// 场景二（B-only 成品形态）：CLI 账本宿主仅启用面向 B（BlockAgentEndpoints=true），无 A 调试通道。
/// 模拟终端用户的个人 Agent 全会话：L0 档案发现 → probe/help → manifest（范式+覆盖收录）→
/// 按名传参自然语言工作流 → 资产下载与过期恢复 → 超限拒绝 → /agent/* 全 404（Paranoid）→ 档案生命周期。
/// </summary>
internal static partial class SelfTest
{
    internal static string ProfileDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Puppet.AppAgents");

    internal static async Task RunBOnly(ExpenseLedger ledger)
    {
Console.WriteLine("CHECK B-only discovery: L0 profile + L1 probe + L2 help (no manual config)");
        var profileFile = Directory.GetFiles(ProfileDir(), "ExpenseLedger.*.json").SingleOrDefault()
            ?? throw new InvalidOperationException("B-only discovery profile missing (L0)");
        using var profileDoc = JsonDocument.Parse(await File.ReadAllTextAsync(profileFile));
        var profile = profileDoc.RootElement;
        var key = profile.GetProperty("key").GetString()!;
        var endpoint = profile.GetProperty("endpoint").GetString()!;
        var app = profile.GetProperty("app").GetString();
        Check(endpoint == "http://127.0.0.1:19102" && app == "ExpenseLedger");

        using var client = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
        using (var probe = await client.GetAsync("/appagent/probe"))
        {
            probe.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("puppet").GetBoolean()
                && doc.RootElement.GetProperty("protocol").GetString() == "appagent/1.0");
        }
        using (var help = await client.GetAsync("/appagent/help"))
            CheckB((await help.Content.ReadAsStringAsync()).Contains("/appagent/manifest"));

    Console.WriteLine("CHECK B-only credential layering and Paranoid /agent/* blocking");
        using (var denied = await client.GetAsync("/appagent/manifest"))
            CheckB(denied.StatusCode == HttpStatusCode.Unauthorized);
        foreach (var path in new[] { "/agent/registry", "/agent/invoke?name=ExpenseLedger&method=Add", "/agent/get?name=ExpenseLedger&path=IsStopping", "/agent/capabilities" })
        {
            using var blocked = await client.GetAsync(path);
            CheckB(blocked.StatusCode == HttpStatusCode.NotFound, $"paranoid block {path}");
        }

        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
    Console.WriteLine("CHECK B-only manifest: pattern + override members, verb descriptions");
        using (var manifest = await client.GetAsync("/appagent/manifest"))
        {
            manifest.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var names = root.GetProperty("actions").EnumerateArray()
                .Select(a => a.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
            CheckB(names.Contains("Add") && names.Contains("Delete"), "override members (complex return) in manifest");
            CheckB(names.Contains("ExportCsv"), "artifact action (int-like return) in manifest");
            CheckB(!names.Contains("Stop") && !names.Contains("List") && !names.Contains("Summary"),
                "non-anchor returns excluded without override");
            var add = root.GetProperty("actions").EnumerateArray().First(a => a.GetProperty("name").GetString() == "Add");
            CheckB(add.GetProperty("desc").GetString() == "记一笔支出", "explicit Chinese description");
            CheckB(add.GetProperty("parameters").EnumerateArray()
                .Any(p => p.GetProperty("name").GetString() == "amount" && p.GetProperty("required").GetBoolean()));
        }

    Console.WriteLine("CHECK B-only natural-language workflow: add → observe → delete (by-name args)");
        using (var call = await client.PostAsync("/appagent/actions/Add", JsonBody(new { args = new { amount = 12.5, category = "food", note = "agent 午餐" }, callId = "nl-1" })))
        {
            if (!call.IsSuccessStatusCode) throw new HttpRequestException((int)call.StatusCode + " " + await call.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("ok").GetBoolean()
                && doc.RootElement.GetProperty("action").GetString() == "Add"
                && doc.RootElement.GetProperty("callId").GetString() == "nl-1");
        }
        Check(ledger.List().Single().Note == "agent 午餐" && ledger.Summary().Total == 12.5m);
        long id;
        using (var call = await client.PostAsync("/appagent/actions/Add", JsonBody(new { args = new { amount = 3, category = "travel", note = "agent 出行" } })))
        {
            if (!call.IsSuccessStatusCode) throw new HttpRequestException((int)call.StatusCode + " " + await call.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        id = ledger.List()[1].Id;
        // 异常式校验宿主（throw ArgumentException）：执行器映射为 500 action-failed，err 携带宿主校验消息
        using (var rejected = await client.PostAsync("/appagent/actions/Add", JsonBody(new { args = new { amount = 0, category = "food", note = "" } })))
        {
            CheckB(rejected.StatusCode == HttpStatusCode.InternalServerError, "exception-style validation -> 500");
            using var doc = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("code").GetString() == "action-failed"
                && doc.RootElement.GetProperty("err").GetString()!.Contains("Amount must be"), "host validation message surfaced");
        }
        CheckB(ledger.Summary().Count == 2, "rejected action left no state");
        using (var del = await client.PostAsync("/appagent/actions/Delete", JsonBody(new { args = new { id } })))
        {
            if (!del.IsSuccessStatusCode) throw new HttpRequestException((int)del.StatusCode + " " + await del.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await del.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("ok").GetBoolean() && doc.RootElement.GetProperty("message").GetString() == "done");
        }
        Check(ledger.Summary().Total == 12.5m);

    Console.WriteLine("CHECK B-only artifact: download now, expire after TTL, agent recovers by re-running");
        string url;
        using (var call = await client.PostAsync("/appagent/actions/ExportCsv", JsonBody(new { args = new { } })))
        {
            if (!call.IsSuccessStatusCode) throw new HttpRequestException((int)call.StatusCode + " " + await call.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
            url = doc.RootElement.GetProperty("asset").GetString()!;
            CheckB(url.StartsWith("/appagent/assets/"));
        }
        using (var download = await client.GetAsync(url))
        {
            download.EnsureSuccessStatusCode();
            var text = await download.Content.ReadAsStringAsync();
            CheckB(text.StartsWith("id,amount,category,note") && text.Contains("food"), "csv content served from memory");
        }
        await Task.Delay(1700); // AssetTtlMs=1500 → 过期
        using (var expired = await client.GetAsync(url))
        {
            CheckB(expired.StatusCode == HttpStatusCode.NotFound, "asset expired after TTL");
            using var doc = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("code").GetString() == "asset-expired");
        }
        using (var rerun = await client.PostAsync("/appagent/actions/ExportCsv", JsonBody(new { args = new { } })))
        {
            if (!rerun.IsSuccessStatusCode) throw new HttpRequestException((int)rerun.StatusCode + " " + await rerun.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await rerun.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("asset").GetString()!.StartsWith("/appagent/assets/"), "agent re-runs action for new asset");
        }

    Console.WriteLine("CHECK B-only asset capacity: per-entry and total limits reject with explicit errors");
        // 连续导出：单条 ~100B，但总量上限仅 2KB 且 TTL 1.5s 内不淘汰 → 必然命中容量拒绝（确定性，不依赖精确字节数）
        HttpStatusCode? capacityStatus = null;
        string capacityBody = "";
        for (var i = 0; i < 200 && capacityStatus is null; i++)
        {
            using var call = await client.PostAsync("/appagent/actions/ExportCsv", JsonBody(new { args = new { } }));
            if (!call.IsSuccessStatusCode)
            {
                capacityStatus = call.StatusCode;
                capacityBody = await call.Content.ReadAsStringAsync();
            }
        }
        CheckB(capacityStatus == HttpStatusCode.InternalServerError
            && capacityBody.Contains("capacity"), $"total-capacity rejection: {(int?)capacityStatus} {capacityBody}");

    Console.WriteLine("CHECK B-only state read and error contract hints");
        using (var state = await client.GetAsync("/appagent/state/IsStopping"))
        {
            state.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
            CheckB(doc.RootElement.GetProperty("value").GetBoolean() == false);
        }
        using (var noState = await client.GetAsync("/appagent/state/NotAKey"))
            CheckB(noState.StatusCode == HttpStatusCode.NotFound);

    Console.WriteLine("PASS B-only CLI appagent scenario");
    }

    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static void CheckB(bool condition, string scenario = "")
    {
        if (!condition) throw new InvalidOperationException($"B-only assertion failed: {scenario}");
    }
}
