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

    [PuppetExpose]
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
