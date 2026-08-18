namespace Benzene.Patterns.ModularMonolith.Contracts;

/// <summary>
/// The payload types that cross module boundaries.
/// </summary>
/// <remarks>
/// Rule 3: everything here must survive serialization. No live entities, no delegates, no handles to
/// another module's state - if it cannot be JSON it cannot cross, in process or out of it. The
/// in-process transport serializes by default precisely so that this rule is enforced on day one
/// rather than discovered at extraction.
/// </remarks>
public class PlaceOrderRequest
{
    public string Customer { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class PlaceOrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public string ChargeId { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int RemainingStock { get; set; }

    /// <summary>
    /// Where the order's cross-module calls were served from - <c>in-process</c> or <c>http</c>.
    /// </summary>
    /// <remarks>
    /// Read from configuration by the Orders module, NOT discovered at runtime, because the whole
    /// claim of this example is that the handler cannot tell. It exists so a reader (and the smoke
    /// test) can see which deployment answered while comparing two responses that are otherwise
    /// required to be identical.
    /// </remarks>
    public string Deployment { get; set; } = string.Empty;
}

public class ChargeCardRequest
{
    public string OrderId { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class ChargeCardResponse
{
    public string ChargeId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class RefundChargeRequest
{
    public string ChargeId { get; set; } = string.Empty;
}

public class RefundChargeResponse
{
    public string ChargeId { get; set; } = string.Empty;
    public bool Refunded { get; set; }
}

public class ReserveStockRequest
{
    public string OrderId { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public class ReserveStockResponse
{
    public string ReservationId { get; set; } = string.Empty;
    public int RemainingStock { get; set; }
}
