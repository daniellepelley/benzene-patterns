namespace Benzene.Patterns.ModularMonolith.Contracts;

/// <summary>
/// Every topic a module can be reached on.
/// </summary>
/// <remarks>
/// This class, and the payload types beside it, are the <b>only</b> thing one module may reference
/// from another - rule 1 of the pattern. No module references another's handlers, entities or
/// stores, and there is nothing here that would let it: these are strings and DTOs.
/// </remarks>
public static class Topics
{
    /// <summary>Command: place an order. Handled by the Orders module.</summary>
    public const string OrderPlace = "order:place";

    /// <summary>Command: charge a customer. Handled by the Billing module.</summary>
    public const string BillingCharge = "billing:charge";

    /// <summary>Command: refund a charge - the compensation when a later step fails.</summary>
    public const string BillingRefund = "billing:refund";

    /// <summary>Command: reserve stock. Handled by the Shipping module.</summary>
    public const string ShippingReserve = "shipping:reserve";
}
