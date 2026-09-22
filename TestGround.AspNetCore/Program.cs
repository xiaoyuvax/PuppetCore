using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Puppet.Core;
using Puppet.Core.AppAgent;
using Puppet.Core.Usage;
using Puppet.Core.Web;
using Wima.Log;

namespace TestGround.AspNetCore;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var key = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_KEY");
        Environment.SetEnvironmentVariable("PUPPET_TESTGROUND_KEY", null);
        if (args.Length > 1 || (args.Length == 1 && args[0] != "--self-test"))
        {
            Console.Error.WriteLine("Usage: TestGround.AspNetCore [--self-test]");
            return 2;
        }
        var selfTest = args.Length == 1;
        var agentEnabled = key is not null;
        var inventory = new Inventory();
        WebApplication? app = null;
        PuppetWebServer? agent = null;
        var exitCode = 0;
        PuppetUsage.Enabled = false;
        if (key is not null) PuppetKeyVault.SetKey(key);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, 19204);
                options.Limits.MaxRequestBodySize = 4096;
            });
            builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            builder.Services.AddSingleton(inventory);
            app = builder.Build();
            app.UseStatusCodePages(async status =>
                await Results.Problem(statusCode: status.HttpContext.Response.StatusCode).ExecuteAsync(status.HttpContext));
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                if (agentEnabled && context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    var expected = "Bearer " + PuppetKeyVault.GlobalKey;
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes(expected)))
                    {
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await Results.Problem(statusCode: 401, title: Loc.T("需要有效的运行时密钥。", "A valid runtime key is required.")).ExecuteAsync(context);
                        return;
                    }
                }
                try { await next(context); }
                catch (Exception ex) when (!context.Response.HasStarted)
                {
                    var (status, title) = ex switch
                    {
                        BadHttpRequestException bad => (bad.StatusCode, Loc.T("请求体或路由值无效。", "Invalid request body or route value.")),
                        ArgumentException => (400, ex.Message),
                        KeyNotFoundException => (404, ex.Message),
                        InvalidOperationException => (409, ex.Message),
                        _ => (500, Loc.T("请求失败。", "Request failed."))
                    };
                    await Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
                }
            });
            using var htmlStream = typeof(Program).Assembly.GetManifestResourceStream("TestGround.AspNetCore.index.html")!;
            using var htmlReader = new StreamReader(htmlStream);
            var html = await htmlReader.ReadToEndAsync();
            app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
            app.MapGet("/api/inventory", (Inventory service) => service.Snapshot());
            app.MapPost("/api/stock", (CreateStockRequest input, Inventory service) =>
            {
                var stock = service.CreateStock(input.Sku, input.Name, input.Quantity);
                return Results.Created("/api/inventory", stock);
            });
            app.MapPost("/api/reservations", (ReserveRequest input, Inventory service) =>
            {
                var reservation = service.Reserve(input.Sku, input.Quantity);
                return Results.Created("/api/inventory", reservation);
            });
            app.MapDelete("/api/reservations/{id}", (Guid id, Inventory service) =>
            {
                service.Release(id);
                return Results.NoContent();
            });
            if (agentEnabled)
            {
                PuppetRegistry.Register(inventory);
                agent = new PuppetWebServer { LogMan = new WimaLogger("Inventory.Server", logMode: (LogMode)0) };
                agent.UseHandler(request =>
                {
                    var length = request.Headers?.Get("Content-Length");
                    if (!string.IsNullOrEmpty(request.Headers?.Get("Transfer-Encoding")) ||
                        (request.Method == "POST" && length is null) ||
                        (length is not null && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > 4096)))
                    {
                        request.Response.StatusCode = 413;
                        return true;
                    }
                    return false;
                });
                // 模式 C：独立 PuppetWebServer 上启用面向 B（CLI 式宿主，无 UI 线程，无 L1 文案桥）
                agent.UseAppAgent(o =>
                {
                    o.ProductName = "Inventory";
                    o.BlockAgentEndpoints = false; // 双服务器双面向并存演示
                });
                if (!agent.Start("127.0.0.1:19104")) throw new InvalidOperationException("Agent server start failed.");
            }
            await app.StartAsync();
            Console.WriteLine(agentEnabled
                ? Loc.T("库存 UI/API：http://127.0.0.1:19204 | Puppet：127.0.0.1:19104 / Inventory", "Inventory UI/API: http://127.0.0.1:19204 | Puppet: 127.0.0.1:19104 / Inventory")
                : Loc.T("库存 UI/API：http://127.0.0.1:19204 | Puppet 已禁用：未提供 PUPPET_TESTGROUND_KEY", "Inventory UI/API: http://127.0.0.1:19204 | Puppet disabled: no PUPPET_TESTGROUND_KEY"));
            if (selfTest)
            {
                if (!agentEnabled) throw new InvalidOperationException("Self-test requires PUPPET_TESTGROUND_KEY.");
                await SelfTest.Run(inventory, key!).WaitAsync(TimeSpan.FromSeconds(60));
            }
            else await app.WaitForShutdownAsync();
        }
        catch (Exception ex)
        {
            if (selfTest)
            {
                var frame = new System.Diagnostics.StackTrace(ex, true).GetFrames().FirstOrDefault(f => f.GetFileName()?.EndsWith("SelfTest.cs", StringComparison.Ordinal) == true);
                Console.Error.WriteLine($"Self-test failure: {ex.GetType().Name}, SelfTest.cs:{frame?.GetFileLineNumber()}.");
            }
            Console.Error.WriteLine(selfTest ? "Self-test failed; see last check/line." : "Host failed to start or run.");
            exitCode = 1;
        }
        finally
        {
            if (agentEnabled) PuppetRegistry.Unregister("Inventory");
            try
            {
                if (app is not null)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await app.StopAsync(stop.Token); }
                    finally { await app.DisposeAsync(); }
                }
            }
            catch (Exception) { Console.Error.WriteLine(Loc.T("应用程序关闭失败。", "Application shutdown failed.")); exitCode = 1; }
            try
            {
                if (agent is not null) await Task.Run(agent.Stop).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception) { Console.Error.WriteLine(Loc.T("Agent 关闭失败。", "Agent shutdown failed.")); exitCode = 1; }
            PuppetKeyVault.RefreshKey();
        }
            if (selfTest && exitCode == 0)
            {
                if (PuppetRegistry.Resolve("Inventory") is not null) return 1;
                Console.WriteLine("PASS lifecycle cleanup; self-test complete.");
            }
        return exitCode;
    }
}

internal sealed record CreateStockRequest(string Sku, string Name, int Quantity);
internal sealed record ReserveRequest(string Sku, int Quantity);
