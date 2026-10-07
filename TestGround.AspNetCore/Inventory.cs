using System.Collections.Immutable;
using Common.Logging;
using Puppet.Core;

namespace TestGround.AspNetCore;

public sealed record Stock(string Sku, string Name, int Total, int Reserved)
{
    public int Available => Total - Reserved;
}

public sealed record Reservation(Guid Id, string Sku, int Quantity);
public sealed record InventorySnapshot(ImmutableArray<Stock> Stock, ImmutableArray<Reservation> Reservations);

public sealed class Inventory : IPuppet
{
    [PuppetIgnore] private readonly object _gate = new();
    [PuppetIgnore] private readonly Dictionary<string, Stock> _stock = new(StringComparer.Ordinal);
    [PuppetIgnore] private readonly Dictionary<Guid, Reservation> _reservations = [];

    [PuppetIgnore] private readonly object _shotGate = new();
    [PuppetIgnore] private long _shotSeen;
    [PuppetIgnore] private long _shotActive;
    [PuppetIgnore] private long _shotSeq;
    [PuppetIgnore] private int _shotX;
    [PuppetIgnore] private int _shotY;
    [PuppetIgnore] private int _shotWidth;
    [PuppetIgnore] private int _shotHeight;
    [PuppetIgnore] private byte[]? _shotPng;
    [PuppetIgnore] private TaskCompletionSource? _shotReady;

    [PuppetIgnore] string IPuppet.AgentAccessKey => PuppetKeyVault.GlobalKey;
    [PuppetIgnore] ILog IPuppet.AgentLog => new Common.Logging.Simple.NoOpLogger();
    [PuppetIgnore] string IPuppet.AgentInstanceName => "Inventory";

    [PuppetExpose]
    public InventorySnapshot Snapshot()
    {
        lock (_gate) return new(_stock.Values.OrderBy(s => s.Sku, StringComparer.Ordinal).ToImmutableArray(),
            _reservations.Values.ToImmutableArray());
    }

    [PuppetExpose]
    public Stock CreateStock(string sku, string name, int quantity)
    {
        ValidateSku(sku);
        ValidateQuantity(quantity);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
            name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new ArgumentException("Name must be 1..80 characters without control or format characters.");
        lock (_gate)
        {
            if (_stock.ContainsKey(sku)) throw new InvalidOperationException("SKU already exists.");
            if (_stock.Count >= 1000) throw new InvalidOperationException("Stock capacity is 1000 SKUs.");
            var stock = new Stock(sku, name.Trim(), quantity, 0);
            _stock.Add(sku, stock);
            return stock;
        }
    }

    /// <summary>模式 C 演示：复杂返回类型（范式外）以 [PuppetAction] 覆盖收录进 B 面向，name 采用动词小写契约</summary>
    [PuppetExpose]
    [Puppet.Core.AppAgent.PuppetAction("reserve", Desc = "Reserve stock by SKU")]
    public Reservation Reserve(string sku, int quantity)
    {
        ValidateSku(sku);
        ValidateQuantity(quantity);
        lock (_gate)
        {
            if (!_stock.TryGetValue(sku, out var stock)) throw new KeyNotFoundException("SKU not found.");
            if (quantity > stock.Available) throw new InvalidOperationException("Insufficient available stock.");
            if (_reservations.Count >= 10000) throw new InvalidOperationException("Capacity is 10000 active reservations.");
            var reservation = new Reservation(Guid.NewGuid(), sku, quantity);
            _reservations.Add(reservation.Id, reservation);
            _stock[sku] = stock with { Reserved = stock.Reserved + quantity };
            return reservation;
        }
    }

    [PuppetExpose]
    [Puppet.Core.AppAgent.PuppetAction("release", Desc = "Release a reservation by id")]
    public Reservation Release(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Reservation ID must be a nonempty UUID.");
        lock (_gate)
        {
            if (!_reservations.Remove(id, out var reservation)) throw new KeyNotFoundException("Active reservation not found.");
            var stock = _stock[reservation.Sku];
            _stock[stock.Sku] = stock with { Reserved = stock.Reserved - reservation.Quantity };
            return reservation;
        }
    }

    [PuppetIgnore]
    internal Puppet.Core.AppAgent.PuppetArtifact CaptureScreen(int x, int y, int width, int height)
    {
        TaskCompletionSource ready;
        lock (_shotGate)
        {
            if (Environment.TickCount64 - _shotSeen > 3000)
                throw new InvalidOperationException("Screenshot capture is off. It is not a standing capability: ask the user to enable the page screenshot switch, then retry.");
            if (_shotReady is not null)
                throw new InvalidOperationException("A screenshot request is already in progress.");
            _shotActive = ++_shotSeq;
            _shotX = x;
            _shotY = y;
            _shotWidth = width;
            _shotHeight = height;
            _shotPng = null;
            _shotReady = ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var delivered = ready.Task.Wait(TimeSpan.FromSeconds(5));
        byte[]? png;
        lock (_shotGate)
        {
            png = _shotPng;
            _shotPng = null;
            if (ReferenceEquals(_shotReady, ready)) _shotReady = null;
            _shotActive = 0;
        }
        if (!delivered || png is null)
            throw new InvalidOperationException("The page did not deliver a screenshot in time. Keep the page open and the screenshot switch on.");
        return new Puppet.Core.AppAgent.PuppetArtifact("screenshot.png", "image/png", png);
    }

    [PuppetIgnore]
    internal ScreenshotRequest? ScreenshotPending()
    {
        lock (_shotGate)
        {
            _shotSeen = Environment.TickCount64;
            if (_shotReady is null || _shotActive == 0) return null;
            return new ScreenshotRequest(_shotActive, _shotX, _shotY, _shotWidth, _shotHeight);
        }
    }

    [PuppetIgnore]
    internal void SubmitScreenshot(long id, string? png)
    {
        const string prefix = "data:image/png;base64,";
        if (png is null || !png.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("A data:image/png;base64 image is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(png[prefix.Length..]); }
        catch (FormatException) { throw new ArgumentException("Image data is not valid base64."); }
        if (bytes.Length is < 8 or > 1500000) throw new ArgumentException("Image payload size is out of range.");
        if (bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
            throw new ArgumentException("Image payload is not a PNG.");
        TaskCompletionSource ready;
        lock (_shotGate)
        {
            if (id != _shotActive || _shotReady is null)
                throw new ArgumentException("Screenshot request is unknown or already completed.");
            _shotPng = bytes;
            ready = _shotReady;
        }
        ready.TrySetResult();
    }

    private static void ValidateSku(string sku)
    {
        if (sku is null || sku.Length is < 1 or > 24 || sku.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("SKU must be 1..24 uppercase ASCII letters, digits or '-'.");
    }

    private static void ValidateQuantity(int quantity)
    {
        if (quantity is < 1 or > 1000000) throw new ArgumentException("Quantity must be 1..1000000.");
    }
}

internal sealed record ScreenshotRequest(long Id, int X, int Y, int Width, int Height);
internal sealed record ScreenshotSubmission(long Id, string? Png);
