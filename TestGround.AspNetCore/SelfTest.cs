using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace TestGround.AspNetCore;

internal static class SelfTest
{
    private static Inventory _inventory = null!;
    private static HttpClient _appClient = null!;
    private static HttpClient _agentClient = null!;

    internal static async Task Run(Inventory inventory, string key)
    {
        _inventory = inventory;
        Console.WriteLine("CHECK service validation, immutable snapshots, capacity and atomic release");
        var local = new Inventory();
        foreach (var sku in new[] { null, "", "lower", "A B", new string('A', 25) })
            Reject<ArgumentException>(() => local.CreateStock(sku!, "Item", 1));
        foreach (var name in new[] { null, "", "  ", "line\nbreak", "hidden\u202etext", new string('n', 81) })
            Reject<ArgumentException>(() => local.CreateStock("A", name!, 1));
        foreach (var quantity in new[] { int.MinValue, -1, 0, 1000001, int.MaxValue })
        {
            Reject<ArgumentException>(() => local.CreateStock("A", "Item", quantity));
            Reject<ArgumentException>(() => local.Reserve("A", quantity));
        }
        Check(local.Snapshot().Stock.IsEmpty);
        var maximum = local.CreateStock(new string('A', 24), new string('n', 80), 1000000);
        Check(maximum.Available == 1000000);
        Reject<InvalidOperationException>(() => local.CreateStock(maximum.Sku, "Duplicate", 1));
        Reject<KeyNotFoundException>(() => local.Reserve("MISSING", 1));
        Reject<ArgumentException>(() => local.Reserve(null!, 1));
        Reject<ArgumentException>(() => local.Release(Guid.Empty));
        var before = local.Snapshot();
        var reservation = local.Reserve(maximum.Sku, 1000000);
        Reject<InvalidOperationException>(() => local.Reserve(maximum.Sku, 1));
        var released = 0;
        Parallel.For(0, 24, _ =>
        {
            try { local.Release(reservation.Id); Interlocked.Increment(ref released); }
            catch (KeyNotFoundException) { }
        });
        Check(released == 1 && before.Stock[0].Reserved == 0 && before.Reservations.IsEmpty);
        Check(local.Snapshot().Stock[0].Available == 1000000);
        for (var i = 1; i < 1000; i++) local.CreateStock("S" + i, "Item", 1);
        Reject<InvalidOperationException>(() => local.CreateStock("FULL", "Item", 1));
        for (var i = 0; i < 10000; i++) local.Reserve(maximum.Sku, 1);
        Reject<InvalidOperationException>(() => local.Reserve(maximum.Sku, 1));
        local.Release(local.Snapshot().Reservations[0].Id);
        local.Reserve(maximum.Sku, 1);
        Check(local.Snapshot().Stock.Single(s => s.Sku == maximum.Sku).Reserved == 10000);

        using var app = Client(19204);
        using var agent = Client(19104);
        _appClient = app;
        _agentClient = agent;
        Console.WriteLine("CHECK HTTP readiness, HTML, authentication and validation");
        var ready = false;
        for (var attempt = 0; attempt < 30 && !ready; attempt++)
        {
            try { using var ping = await agent.GetAsync("/ping"); ready = ping.IsSuccessStatusCode; }
            catch (HttpRequestException) { }
            if (!ready) await Task.Delay(100);
        }
        Check(ready);
        using (var page = await app.GetAsync("/"))
        {
            var html = await page.Content.ReadAsStringAsync();
            Check(page.IsSuccessStatusCode && page.Content.Headers.ContentType?.MediaType == "text/html");
            Check(html.Contains("type=\"password\"") && html.Contains("/api/reservations") && !html.Contains(key, StringComparison.Ordinal));
            Check(page.Headers.CacheControl?.NoStore == true && page.Headers.Contains("Content-Security-Policy"));
        }
        Check((await Get(app, "/api/inventory"))["stock"]!.AsArray().Count == 0);
        await Status(app, HttpMethod.Post, "/api/stock", "{}", 401);
        await Status(app, HttpMethod.Post, "/api/reservations", "{}", 401);
        await Status(app, HttpMethod.Delete, "/api/reservations/" + Guid.NewGuid(), null, 401);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        await Status(app, HttpMethod.Post, "/api/stock", "{}", 401);
        await Status(agent, HttpMethod.Get, "/agent/registry", null, 404);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        await Status(agent, HttpMethod.Post, "/agent/invoke?name=Inventory&method=CreateStock", "[\"BAD\",\"Denied\",1]", 404);
        Check(inventory.Snapshot().Stock.IsEmpty);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var body in new[] { "{", "null", "{}", "{\"sku\":null,\"name\":\"A\",\"quantity\":1}", "{\"sku\":\"bad\",\"name\":\"A\",\"quantity\":1}", "{\"sku\":\"A\",\"name\":\"A\",\"quantity\":0}", "{\"sku\":\"A\",\"name\":\"A\",\"quantity\":1.5}" })
            await Status(app, HttpMethod.Post, "/api/stock", body, 400);
        await Status(app, HttpMethod.Post, "/api/stock", new string('x', 4097), 413);
        await Status(app, HttpMethod.Delete, "/api/reservations/not-a-guid", null, 400);
        await Status(app, HttpMethod.Delete, "/api/reservations/" + Guid.Empty, null, 400);
        await Status(app, HttpMethod.Delete, "/api/reservations/" + Guid.NewGuid(), null, 404);
        await Status(app, HttpMethod.Post, "/api/reservations", "{\"sku\":\"MISSING\",\"quantity\":1}", 404);
        Check(inventory.Snapshot().Stock.IsEmpty);
        using (var wrongType = new StringContent("{}", Encoding.UTF8, "text/plain"))
        using (var response = await app.PostAsync("/api/stock", wrongType)) Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType);

        Console.WriteLine("CHECK ordinary HTTP and Puppet share real business operations");
        var registry = await Get(agent, "/agent/registry");
        Check(registry["Instances"]!.AsArray().Any(i => i!["InstanceName"]!.GetValue<string>() == "Inventory"));
        var description = await agent.GetStringAsync("/agent/describe?name=Inventory&format=json");
        Check(description.Contains("CreateStock") && description.Contains("Reserve") && description.Contains("Release") && !description.Contains(key, StringComparison.Ordinal));
        foreach (var path in new[] { "AgentAccessKey", "AgentLog", "_gate", "_stock", "_reservations" })
            Check(!(await Get(agent, "/agent/get?name=Inventory&path=" + path))["ok"]!.GetValue<bool>());
        var state = await agent.GetStringAsync("/agent/state?name=Inventory");
        Check(!state.Contains(key, StringComparison.Ordinal));
        await Status(app, HttpMethod.Post, "/api/stock", "{\"sku\":\"PAPER\",\"name\":\"纸张 <b>not HTML</b>\",\"quantity\":12}", 201);
        await Status(app, HttpMethod.Post, "/api/stock", "{\"sku\":\"PAPER\",\"name\":\"Duplicate\",\"quantity\":1}", 409);
        var reserved = await Invoke(agent, "Reserve", "[\"PAPER\",2]");
        Check(reserved["ok"]!.GetValue<bool>() && inventory.Snapshot().Stock.Single().Available == 10);
        var id = reserved["result"]!["Id"]!.GetValue<string>();
        Check((await Get(app, "/api/inventory"))["reservations"]!.AsArray().Count == 1);
        await Status(app, HttpMethod.Delete, "/api/reservations/" + id, null, 204);
        await Status(app, HttpMethod.Delete, "/api/reservations/" + id, null, 404);
        Check(!(await Invoke(agent, "Reserve", "[\"PAPER\",13]"))["ok"]!.GetValue<bool>());
        Check(!(await Invoke(agent, "CreateStock", "[\"bad\",\"Item\",1]"))["ok"]!.GetValue<bool>());
        await Status(agent, HttpMethod.Post, "/agent/invoke?name=Inventory&method=Reserve", new string('x', 4097), 413);
        Check((await Invoke(agent, "CreateStock", "[\"INK\",\"Ink\",1]"))["ok"]!.GetValue<bool>());
        Check((await Get(app, "/api/inventory"))["stock"]!.AsArray().Count == 2);

        Console.WriteLine("CHECK 48 parallel HTTP reservations for 12 units: exactly 12 successes, no overselling");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 48).Select(async _ =>
        {
            using var response = await app.PostAsJsonAsync("/api/reservations", new { sku = "PAPER", quantity = 1 });
            Check(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);
            return response.StatusCode;
        }));
        Check(attempts.Count(s => s == HttpStatusCode.Created) == 12);
        var snapshot = inventory.Snapshot();
        Check(snapshot.Stock.Single(s => s.Sku == "PAPER").Available == 0 && snapshot.Reservations.Length == 12);
        Check(snapshot.Reservations.Select(r => r.Id).Distinct().Count() == 12);
        var puppetSnapshot = await Invoke(agent, "Snapshot", "[]");
        Check(puppetSnapshot["result"]!["Reservations"]!.AsArray().Count == 12);
        var first = snapshot.Reservations[0].Id;
        var releases = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await app.DeleteAsync("/api/reservations/" + first);
            Check(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound);
            return response.StatusCode;
        }));
        Check(releases.Count(s => s == HttpStatusCode.NoContent) == 1);
        foreach (var remaining in inventory.Snapshot().Reservations)
            Check((await Invoke(agent, "Release", "[\"" + remaining.Id + "\"]"))["ok"]!.GetValue<bool>());
        Check(inventory.Snapshot().Stock.Single(s => s.Sku == "PAPER").Available == 12 && snapshot.Reservations.Length == 12);

        Console.WriteLine("CHECK runtime key rotation invalidates old key on both HTTP surfaces");
        using var refreshBody = new StringContent("{}");
        using var refreshResponse = await agent.PostAsync("/agent/key/refresh", refreshBody);
        Check(refreshResponse.IsSuccessStatusCode);
        var refreshed = JsonNode.Parse(await refreshResponse.Content.ReadAsStringAsync())!["newKey"]!.GetValue<string>();
        await Status(app, HttpMethod.Post, "/api/reservations", "{\"sku\":\"INK\",\"quantity\":1}", 401);
        await Status(agent, HttpMethod.Get, "/agent/registry", null, 404);
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        await Status(app, HttpMethod.Post, "/api/reservations", "{\"sku\":\"INK\",\"quantity\":1}", 201);
        Check((await Invoke(agent, "Snapshot", "[]"))["ok"]!.GetValue<bool>());
        Console.WriteLine("PASS service, browser page and HTTP checks");

        await RunAppAgentSmokeAsync();
    }

    /// <summary>
    /// B 面向烟雾测试（模式 C 双服务器）：独立 PuppetWebServer 上的 /appagent/* 与 Kestrel 业务 API、A 通道并存；
    /// 关键点：B 凭证与 A 密钥分属不同鉴权域，A 密钥轮换后 B 档案凭证不受影响。
    /// </summary>
    private static async Task RunAppAgentSmokeAsync()
    {
        Console.WriteLine("CHECK appagent on mode C: profile discovery, probe, manifest, action, cross-domain key independence");
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Puppet.AppAgents");
        var profileFile = Directory.GetFiles(profileDir, "Inventory.*.json").SingleOrDefault()
            ?? throw new InvalidOperationException("appagent discovery profile missing (mode C host)");
        string profileKey;
        using (var pdoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(profileFile)))
            profileKey = pdoc.RootElement.GetProperty("key").GetString()!;

        using var bclient = new HttpClient(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri("http://127.0.0.1:19104"), Timeout = TimeSpan.FromSeconds(5) };
        var app = _appClient;
        using (var probe = await bclient.GetAsync("/appagent/probe"))
            Check((await probe.Content.ReadAsStringAsync()).Contains("appagent/1.0"));

        // A 通道密钥（已轮换）不能当 B 凭证用；B 凭证在 A 轮换后依然有效 → 鉴权域独立
        using (var cross = new HttpRequestMessage(HttpMethod.Get, "/appagent/manifest"))
        {
            cross.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.DefaultRequestHeaders.Authorization!.Parameter!);
            using var response = await bclient.SendAsync(cross);
            Check(response.StatusCode == HttpStatusCode.Unauthorized);
        }
        _agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.DefaultRequestHeaders.Authorization!.Parameter!);
        using (var probeA = await _agentClient.GetAsync("/agent/registry"))
            Check(probeA.IsSuccessStatusCode);
        bclient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", profileKey);
        using (var manifest = await bclient.GetAsync("/appagent/manifest"))
        {
            manifest.EnsureSuccessStatusCode();
            var json = await manifest.Content.ReadAsStringAsync();
            Check(json.Contains("\"reserve\"") && json.Contains("Reserve stock by SKU"));
            Check(!json.Contains("Snapshot") && !json.Contains("CreateStock"));
        }
        // B 释放 A 段轮换测试留下的存量预留（B 动作与 Kestrel API 操作同一业务状态）
        var existing = _inventory.Snapshot().Reservations.Single();
        using (var call = await bclient.PostAsync("/appagent/actions/release",
            new StringContent("{\"args\":{\"id\":\"" + existing.Id + "\"}}", Encoding.UTF8, "application/json")))
        {
            call.EnsureSuccessStatusCode();
            Check((await call.Content.ReadAsStringAsync()).Contains("\"ok\":true"));
        }
        Check(_inventory.Snapshot().Stock.Single(s => s.Sku == "INK").Reserved == 0);
        // B 预留成功 → 错误契约：空 GUID 触发宿主异常式校验 → 500 action-failed
        using (var call = await bclient.PostAsync("/appagent/actions/reserve",
            new StringContent("{\"args\":{\"sku\":\"INK\",\"quantity\":1}}", Encoding.UTF8, "application/json")))
        {
            call.EnsureSuccessStatusCode();
            Check((await call.Content.ReadAsStringAsync()).Contains("\"ok\":true"));
        }
        Check(_inventory.Snapshot().Stock.Single(s => s.Sku == "INK").Reserved == 1);
        using (var del = await bclient.PostAsync("/appagent/actions/release",
            new StringContent("{\"args\":{\"id\":\"00000000-0000-0000-0000-000000000000\"}}", Encoding.UTF8, "application/json")))
            Check(del.StatusCode == HttpStatusCode.InternalServerError);
        Console.WriteLine("PASS appagent smoke on mode C host");
    }

    private static HttpClient Client(int port) => new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5)
    };

    private static async Task<JsonNode> Get(HttpClient client, string path)
        => JsonNode.Parse(await client.GetStringAsync(path))!;

    private static async Task<JsonNode> Invoke(HttpClient client, string method, string args)
    {
        using var body = new StringContent(args, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/agent/invoke?name=Inventory&method=" + method, body);
        Check(response.IsSuccessStatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task Status(HttpClient client, HttpMethod method, string path, string? body, int expected, [CallerLineNumber] int line = 0)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Check((int)response.StatusCode == expected, line);
        if (path.StartsWith("/api", StringComparison.Ordinal) && expected >= 400)
            Check((await response.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<int>() == expected, line);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected validation failure.");
    }

    private static void Check(bool condition, [CallerLineNumber] int line = 0)
    {
        if (condition) return;
        Console.Error.WriteLine($"Assertion failed at SelfTest.cs:{line}.");
        throw new InvalidOperationException("Assertion failed.");
    }
}
