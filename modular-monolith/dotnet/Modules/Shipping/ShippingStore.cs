using System.Collections.Concurrent;

namespace Benzene.Patterns.ModularMonolith.Modules.Shipping;

/// <summary>The Shipping module's own data. Nothing outside this module reads it (rule 2).</summary>
public class ShippingStore
{
    private readonly ConcurrentDictionary<string, int> _stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIDGET"] = 100,
        ["GIZMO"] = 5,
        ["SPROCKET"] = 0
    };

    public bool IsKnownSku(string sku) => _stock.ContainsKey(sku);

    /// <summary>
    /// Takes stock if there is enough, atomically.
    /// </summary>
    /// <returns>The remaining stock, or null when there was not enough.</returns>
    public int? TryReserve(string sku, int quantity)
    {
        while (true)
        {
            if (!_stock.TryGetValue(sku, out var available))
            {
                return null;
            }

            if (available < quantity)
            {
                return null;
            }

            // Compare-and-swap rather than read-then-write: two concurrent orders for the last item
            // must not both succeed. In one process this is the only concurrency that exists; once
            // extracted it is still the only concurrency this module has, because the module owns
            // its data outright.
            if (_stock.TryUpdate(sku, available - quantity, available))
            {
                return available - quantity;
            }
        }
    }
}
