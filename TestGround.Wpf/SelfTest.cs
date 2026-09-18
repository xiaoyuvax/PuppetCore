using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Puppet.Core;

namespace TestGround.Wpf;

internal static class SelfTest
{
    internal static async Task RunAsync(MainWindow window)
    {
        var model = (ReadingList)window.DataContext;
        Check(SynchronizationContext.Current is DispatcherSynchronizationContext
            && ReferenceEquals(PuppetRegistry.Resolve("ReadingList"), model), "Loaded registration on dispatcher");
        Check(model.TotalCount == 0 && model.OverallProgress == 0, "empty aggregate");
        Check(!model.UpdateProgress("0") && !model.RemoveSelectedBook() && !model.SelectBook(0), "no selection");
        foreach (var title in new[] { null, " \t ", "a\nb", new string('x', 121) })
            Check(!model.AddBook(title, "100"), "invalid title");
        foreach (var pages in new[] { null, "", "0", "-1", "1.5", "100001", "2147483648", "abc" })
            Check(!model.AddBook("invalid", pages), "invalid total pages");
        Check(model.AddBook("  The Little Prince  ", "100") && model.SelectedTitle == "The Little Prince", "trim and select");
        Check(!model.AddBook("the little prince", "20") && model.TotalCount == 1, "duplicate title");
        Check(model.AddBook(new string('x', 120), "100000"), "upper boundaries");
        Check(!model.SelectBook(-1) && !model.SelectBook(2) && model.SelectBook(0), "selection bounds");
        foreach (var pages in new[] { null, "", "-1", "101", "0.5", "2147483648" })
            Check(!model.UpdateProgress(pages) && model.SelectedReadPages == 0, "invalid read pages preserve state");
        Check(model.UpdateProgress("50") && model.SelectedProgress == 50 && model.ReadPages == 50
            && model.TotalPages == 100100, "selection and weighted aggregate");
        Check(Math.Abs(model.OverallProgress - 5000.0 / 100100) < 0.00001, "weighted percentage");
        Check(model.UpdateProgress("0") && model.SelectedReadPages == 0, "correct progress backwards");
        Check(!model.SetFilter("bad") && model.CurrentFilter == "全部", "invalid filter");
        Check(model.SetFilter("未读完") && model.UpdateProgress("100") && model.CompletedCount == 1
            && model.VisibleCount == 1 && !model.HasSelection, "completion hides selection");
        Check(model.SetFilter("已读完") && model.VisibleCount == 1 && model.SelectBook(0), "completed filter");
        Check(model.UpdateProgress("99") && model.VisibleCount == 0 && !model.HasSelection, "correction exits completed filter");
        Check(model.AddBook("One page", "1") && model.CurrentFilter == "全部" && model.UpdateProgress("1"), "add reveals and minimum boundary");
        while (model.TotalCount > 0)
            Check(model.SelectBook(0) && model.RemoveSelectedBook(), "remove real books");
        Check(model.ReadPages == 0 && model.TotalPages == 0 && model.OverallProgress == 0, "final removal resets aggregate");

        await FlushAsync(window);
        var titleBox = (TextBox)window.FindName("TitleBox");
        var totalBox = (TextBox)window.FindName("TotalBox");
        var readBox = (TextBox)window.FindName("ReadBox");
        var add = (Button)window.FindName("AddButton");
        var save = (Button)window.FindName("SaveButton");
        var remove = (Button)window.FindName("RemoveButton");
        var filter = (ComboBox)window.FindName("FilterBox");
        var list = (ListBox)window.FindName("BookList");
        Check(!save.IsEnabled && !remove.IsEnabled, "empty action binding");
        titleBox.Text = "通过界面阅读";
        totalBox.Text = "20";
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(window);
        Check(model.TotalCount == 1 && titleBox.Text == "" && totalBox.Text == ""
            && save.IsEnabled && list.SelectedItem != null, "add button and input bindings");
        readBox.Text = "21";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.SelectedReadPages == 0 && model.StatusText.Contains("整数", StringComparison.Ordinal), "UI validation");
        readBox.Text = "20";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(window);
        Check(model.CompletedCount == 1 && readBox.Text == "20", "save button and selection binding");
        filter.SelectedItem = "未读完";
        await FlushAsync(window);
        Check(model.VisibleCount == 0 && !remove.IsEnabled, "filter binding");
        filter.SelectedItem = "已读完";
        await FlushAsync(window);
        list.SelectedIndex = 0;
        await FlushAsync(window);
        Check(remove.IsEnabled && model.SelectedReadPages == 20, "list selection binding");
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await FlushAsync(window);
        Check(model.TotalCount == 0 && !save.IsEnabled, "remove button binding");
        model.SetFilter("全部");

        if (model.AgentStatus.StartsWith("Puppet 已禁用", StringComparison.Ordinal))
        {
            Console.WriteLine("PASS: local mode; HTTP skipped because no runtime key was supplied.");
            return;
        }
        Check(model.AgentStatus.Contains("127.0.0.1:19103", StringComparison.Ordinal), "server startup");
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:19103"), Timeout = TimeSpan.FromSeconds(10) };
        using (var denied = await client.GetAsync("/agent/get?name=ReadingList&path=TotalCount"))
            Check(denied.StatusCode == HttpStatusCode.NotFound, "unauthenticated request denied");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ((IPuppet)model).AgentAccessKey);
        using (var registry = await client.GetAsync("/agent/registry"))
        {
            registry.EnsureSuccessStatusCode();
            Check((await registry.Content.ReadAsStringAsync()).Contains("ReadingList", StringComparison.Ordinal), "HTTP registration");
        }
        foreach (var path in new[] { "_dispatcher", "_books", "Books", "AgentAccessKey", "PropertyChanged" })
        {
            using var hidden = await client.GetAsync($"/agent/get?name=ReadingList&path={path}");
            hidden.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync());
            Check(!result.RootElement.GetProperty("ok").GetBoolean(), "infrastructure is not accessible");
        }
        var observedOnDispatcher = false;
        System.ComponentModel.PropertyChangedEventHandler observer = (_, _) =>
            observedOnDispatcher = window.Dispatcher.CheckAccess();
        model.PropertyChanged += observer;
        try
        {
            await InvokeAsync(client, "AddBook", ["HTTP reading", "80"], true);
            Check(observedOnDispatcher && model.SelectedTitle == "HTTP reading", "HTTP mutation runs on main dispatcher");
            await InvokeAsync(client, "UpdateProgress", ["81"], false);
            await InvokeAsync(client, "UpdateProgress", ["40"], true);
            using var get = await client.GetAsync("/agent/get?name=ReadingList&path=SelectedReadPages");
            get.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Check(json.RootElement.GetProperty("value").GetInt32() == 40, "HTTP scalar observation");
            await FlushAsync(window);
            Check(readBox.Text == "40" && list.Items.Count == 1, "HTTP updates real WPF bindings");
            await InvokeAsync(client, "SetFilter", ["已读完"], true);
            Check(model.VisibleCount == 0 && !model.HasSelection, "HTTP filter clears hidden selection");
            await InvokeAsync(client, "SetFilter", ["全部"], true);
            await InvokeAsync(client, "SelectBook", [0], true);
            await InvokeAsync(client, "RemoveSelectedBook", [], true);
            Check(model.TotalCount == 0, "HTTP user-method cleanup");
        }
        finally
        {
            model.PropertyChanged -= observer;
        }
        Console.WriteLine("PASS: authenticated HTTP user operations, validation and dispatcher-to-WPF bindings.");
    }

    private static async Task InvokeAsync(HttpClient client, string method, object[] args, bool expected)
    {
        using var response = await client.PostAsJsonAsync($"/agent/invoke?name=ReadingList&method={method}", args);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(json.RootElement.GetProperty("ok").GetBoolean()
            && json.RootElement.GetProperty("result").GetBoolean() == expected, $"HTTP {method}");
    }

    private static async Task FlushAsync(Window window) => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
