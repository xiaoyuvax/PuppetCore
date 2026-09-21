using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;

namespace TestGround.Console;

using Console = System.Console;

internal static partial class SelfTest
{
    internal static async Task Run(ExpenseLedger ledger, string key)
    {
        Console.WriteLine("CHECK CLI validation, exact decimals, snapshots and deletion");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Execute(ledger, "add 12.50 food lunch with friends");
            Execute(ledger, "add 0.10 food");
            Execute(ledger, "add 2.40 travel bus");
            var snapshot = ledger.List();
            var summary = ledger.Summary();
            Check(summary.Count == 3 && summary.Total == 15m && summary.Categories[0].Total == 12.60m);
            Check(snapshot[0].Note == "lunch with friends" && snapshot[1].Note == "");
            Check(JsonNode.Parse(Execute(ledger, "list"))!.AsArray().Count == 3);
            Check(JsonNode.Parse(Execute(ledger, "summary"))!["Total"]!.GetValue<decimal>() == 15m);
            Check(Execute(ledger, "help").Contains("delete <id>"));
            foreach (var command in new[] { "add 1,20 food", "add 1e2 food", "add +1 food", "add .10 food", "add 1. food", "add 1.000 food", "add 0 food", "add -1 food", "add 1000000.01 food", "add 1 Food", "add 1", "list extra", "summary extra", "quit extra", "delete 0", "delete -1", "delete +1", "delete 9223372036854775808", "unknown", "add\t1 food", new string('x', 513) })
                Reject(() => Execute(ledger, command));
            Reject(() => ledger.Add(1.001m, "food", ""));
            Reject(() => ledger.Add(decimal.MaxValue, "food", ""));
            Reject(() => ledger.Add(1, null!, ""));
            Reject(() => ledger.Add(1, "", ""));
            Reject(() => ledger.Add(1, new string('a', 33), ""));
            Reject(() => ledger.Add(1, "food", null!));
            Reject(() => ledger.Add(1, "food", new string('x', 201)));
            Reject(() => ledger.Add(1, "food", "line\nbreak"));
            Reject(() => ledger.Add(1, "food", "hidden\u202etext"));
            Check(ledger.List().Length == 3);
            Check(JsonNode.Parse(Execute(ledger, $"delete {snapshot[0].Id}"))!["Deleted"]!.GetValue<bool>());
            Check(!ledger.Delete(snapshot[0].Id));
            Check(snapshot.Length == 3 && summary.Count == 3 && summary.Total == 15m);
            var next = ledger.Add(1000000m, new string('a', 32), new string('x', 200));
            Check(next.Id > snapshot[^1].Id && next.Note.Length == 200);
            foreach (var entry in ledger.List()) Check(ledger.Delete(entry.Id));
            Check(ledger.Summary().Total == 0m);
        }
        finally { CultureInfo.CurrentCulture = culture; }

        Console.WriteLine("CHECK bounded line reader and recovery");
        using (var reader = new StringReader(new string('x', 513) + "\r\nhelp\n" + new string('y', 512)))
        {
            Reject(() => Commands.ReadLine(reader));
            Check(Commands.ReadLine(reader) == "help");
            Check(Commands.ReadLine(reader)!.Length == 512);
            Check(Commands.ReadLine(reader) is null);
        }
        using (var reader = new StringReader("summary\rlist\nquit"))
            Check(Commands.ReadLine(reader) == "summary" && Commands.ReadLine(reader) == "list" && Commands.ReadLine(reader) == "quit");

        Console.WriteLine("CHECK parallel operations, capacity and immutable summaries");
        Parallel.For(0, 200, _ =>
        {
            ledger.Add(0.01m, "parallel", "");
            var current = ledger.Summary();
            Check(current.Total == current.Count * 0.01m);
        });
        var parallel = ledger.List();
        Check(parallel.Length == 200 && parallel.Select(entry => entry.Id).Distinct().Count() == 200);
        Parallel.ForEach(parallel, entry => Check(ledger.Delete(entry.Id)));
        Check(ledger.List().IsEmpty && parallel.Length == 200);
        var capacity = new ExpenseLedger();
        Parallel.For(0, 10000, _ => capacity.Add(1000000m, "limit", ""));
        Reject(() => capacity.Add(1, "limit", ""));
        Check(capacity.Summary().Total == 10000000000m);
        Check(capacity.Delete(capacity.List()[0].Id));
        Check(capacity.Add(1, "limit", "").Id == 10001);
        Execute(capacity, "quit");
        Check(capacity.IsStopping);
        Reject(() => capacity.Add(1, "limit", ""));
        Reject(() => capacity.Delete(2));

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:19102"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        Console.WriteLine("CHECK HTTP readiness and authentication");
        var ready = false;
        for (var attempt = 0; attempt < 30 && !ready; attempt++)
        {
            try
            {
                using var ping = await client.GetAsync("/ping");
                ready = ping.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { }
            if (!ready) await Task.Delay(100);
        }
        Check(ready);
        using (var anonymous = await client.GetAsync("/agent/registry")) Check(anonymous.StatusCode == HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-test-key");
        using (var denied = await client.PostAsync("/agent/invoke?name=ExpenseLedger&method=Add", new StringContent("[1,\"food\",\"\"]", Encoding.UTF8, "application/json")))
            Check(denied.StatusCode == HttpStatusCode.NotFound);
        Check(ledger.List().IsEmpty);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var registry = await Get(client, "/agent/registry");
        Check(registry["Instances"]!.AsArray().Any(item => item!["InstanceName"]!.GetValue<string>() == "ExpenseLedger"));
        var describe = await client.GetStringAsync("/agent/describe?name=ExpenseLedger&format=json");
        Check(describe.Contains("Add") && describe.Contains("Summary") && !describe.Contains(key, StringComparison.Ordinal));

        Console.WriteLine("CHECK HTTP shared user methods and validation");
        var added = await Invoke(client, "Add", "[3.25,\"food\",\"HTTP lunch\"]");
        Check(added["ok"]!.GetValue<bool>());
        var id = added["result"]!["Id"]!.GetValue<long>();
        Check(ledger.List().Single().Id == id && ledger.Summary().Total == 3.25m);
        Execute(ledger, "add 0.75 food CLI snack");
        var remoteSummary = await Invoke(client, "Summary", "[]");
        Check(remoteSummary["result"]!["Total"]!.GetValue<decimal>() == 4m);
        Check((await Invoke(client, "List", "[]"))["result"]!.AsArray().Count == 2);
        Check(!(await Invoke(client, "Add", "[0,\"food\",\"\"]"))["ok"]!.GetValue<bool>());
        Check(!(await Invoke(client, "Add", "[1,\"bad category\",\"\"]"))["ok"]!.GetValue<bool>());
        Check(!(await Invoke(client, "Add", "[1,\"food\",\"" + new string('x', 201) + "\"]"))["ok"]!.GetValue<bool>());
        Check(ledger.List().Length == 2);
        using (var oversized = await client.PostAsync("/agent/invoke?name=ExpenseLedger&method=Add", new StringContent(new string('x', 4097))))
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge);
        Check((await Invoke(client, "Delete", $"[{id}]"))["result"]!.GetValue<bool>());
        Check(!(await Invoke(client, "Delete", $"[{id}]"))["result"]!.GetValue<bool>());
        Check(ledger.Summary().Total == 0.75m);
        Check(!(await Get(client, "/agent/get?name=ExpenseLedger&path=AgentAccessKey"))["ok"]!.GetValue<bool>());
        var state = await client.GetStringAsync("/agent/state?name=ExpenseLedger");
        Check(!state.Contains(key, StringComparison.Ordinal));

        Console.WriteLine("CHECK HTTP key rotation and graceful Stop");
        using var refreshContent = new StringContent("{}");
        using var refreshResponse = await client.PostAsync("/agent/key/refresh", refreshContent);
        refreshResponse.EnsureSuccessStatusCode();
        var refreshed = JsonNode.Parse(await refreshResponse.Content.ReadAsStringAsync())!["newKey"]!.GetValue<string>();
        using (var oldKey = await client.GetAsync("/agent/registry")) Check(oldKey.StatusCode == HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        Check((await Invoke(client, "Stop", "[]"))["ok"]!.GetValue<bool>());
        Check(ledger.IsStopping);
        Reject(() => ledger.Add(1, "food", ""));
        Console.WriteLine("PASS all ledger, CLI and HTTP checks");
    }

    private static string Execute(ExpenseLedger ledger, string command)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Commands.Execute(ledger, command, output);
        return output.ToString();
    }

    private static async Task<JsonNode> Get(HttpClient client, string path)
        => JsonNode.Parse(await client.GetStringAsync(path))!;

    private static async Task<JsonNode> Invoke(HttpClient client, string method, string args)
    {
        using var body = new StringContent(args, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"/agent/invoke?name=ExpenseLedger&method={method}", body);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static void Check(bool condition, [CallerLineNumber] int line = 0)
    {
        if (condition) return;
        Console.Error.WriteLine($"Assertion failed at SelfTest.cs:{line}.");
        throw new InvalidOperationException("Assertion failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Expected validation failure.");
    }
}
