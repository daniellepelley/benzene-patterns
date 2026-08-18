using System.Collections.Concurrent;

namespace Benzene.Patterns.ModularMonolith.Modules.Billing;

/// <summary>
/// The Billing module's own data. Nothing outside this module reads it.
/// </summary>
/// <remarks>
/// Rule 2, share-nothing data, and the rule most worth enforcing in review: <b>a shared table is the
/// one coupling a routing table cannot fix later.</b> Extraction splits compute for free; it does not
/// split a join. In one process nothing would stop Orders reading this dictionary directly - which is
/// exactly why the rule has to be kept by discipline rather than by the compiler.
/// </remarks>
public class BillingStore
{
    private readonly ConcurrentDictionary<string, decimal> _charges = new();
    private readonly ConcurrentDictionary<string, bool> _refunded = new();

    /// <summary>Customers this module knows. An unknown one is a NotFound, not a silent success.</summary>
    private static readonly HashSet<string> KnownCustomers =
        new(StringComparer.OrdinalIgnoreCase) { "alice", "bob", "carol" };

    public bool IsKnownCustomer(string customer) => KnownCustomers.Contains(customer);

    public string Charge(decimal amount)
    {
        var chargeId = $"chg-{Guid.NewGuid():N}"[..12];
        _charges[chargeId] = amount;
        return chargeId;
    }

    public bool Refund(string chargeId) =>
        _charges.ContainsKey(chargeId) && _refunded.TryAdd(chargeId, true);

    public decimal NetCharged => _charges
        .Where(c => !_refunded.ContainsKey(c.Key))
        .Sum(c => c.Value);
}
