using System.Text.Json;

namespace Benzene.Patterns.EventSourcing.Ledger;

/// <summary>
/// The event vocabulary. Past tense, immutable, and the only thing that is ever written.
/// </summary>
/// <remarks>
/// These strings go into the log and stay there forever, so they are the most permanent identifiers
/// in the system — more permanent than the classes below, which will be replaced while the strings
/// they discriminate keep meaning the same thing.
/// </remarks>
public static class EventTypes
{
    public const string AccountOpened = "account:opened";
    public const string MoneyDeposited = "money:deposited";
    public const string MoneyWithdrawn = "money:withdrawn";

    /// <summary>
    /// The deposit shape as it was written years ago, before the ledger was multi-currency.
    /// </summary>
    /// <remarks>
    /// It is still in the log, because a log is never rewritten. That is not a caveat about this
    /// example — it is the defining constraint of event sourcing, and the reason upcasting exists.
    /// </remarks>
    public const string MoneyDepositedV1 = "money:deposited:v1";
}

// ── Event payloads ──────────────────────────────────────────────────────────────────────────────

public class AccountOpened
{
    public string AccountId { get; set; } = string.Empty;
    public string Holder { get; set; } = string.Empty;
    public string Currency { get; set; } = "GBP";
}

public class MoneyDeposited
{
    public long Pence { get; set; }
    public string Currency { get; set; } = "GBP";
    public string Reference { get; set; } = string.Empty;
}

/// <summary>The historical shape: no currency, because there was only one.</summary>
public class MoneyDepositedV1
{
    public long Pence { get; set; }
    public string Reference { get; set; } = string.Empty;
}

public class MoneyWithdrawn
{
    public long Pence { get; set; }
    public string Currency { get; set; } = "GBP";
    public string Reference { get; set; } = string.Empty;
}

/// <summary>
/// Turns a stored event into the shape today's fold understands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Old events, new code.</b> A <c>money:deposited:v1</c> written before the ledger was
/// multi-currency has no currency field. It is not edited — it cannot be, the log is immutable — it
/// is <b>upcast on read</b> into the current shape, with the value the field would have had.
/// </para>
/// <para>
/// Benzene ships <c>AddPayloadVersioning</c>, which does exactly this at a <em>pipeline</em> edge and
/// validates the caster graph at start-up, so a missing conversion path fails at boot rather than on
/// a 2015 event in production. That is the right tool for the projection half, where events arrive
/// as messages. Rehydration reads the store directly rather than through a pipeline, so the upcast
/// lives here — app code, as the pattern doc says it will be.
/// </para>
/// </remarks>
public static class Upcaster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The currency the ledger had before it had currencies. Never inferred, always stated.</summary>
    public const string LegacyCurrency = "GBP";

    public static (string EventType, string Payload) Upcast(string eventType, string payload)
    {
        if (eventType != EventTypes.MoneyDepositedV1)
        {
            return (eventType, payload);
        }

        var old = JsonSerializer.Deserialize<MoneyDepositedV1>(payload, Json)!;
        var current = new MoneyDeposited
        {
            Pence = old.Pence,
            Currency = LegacyCurrency,
            Reference = old.Reference
        };

        return (EventTypes.MoneyDeposited, JsonSerializer.Serialize(current, Json));
    }
}
