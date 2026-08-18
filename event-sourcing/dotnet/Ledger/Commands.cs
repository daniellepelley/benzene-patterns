using System.Text.Json;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.EventSourcing;
using Benzene.Http;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.EventSourcing.Ledger;

// ── Requests and responses ──────────────────────────────────────────────────────────────────────

public class OpenAccountRequest
{
    public string AccountId { get; set; } = string.Empty;
    public string Holder { get; set; } = string.Empty;
    public string Currency { get; set; } = "GBP";
}

public class MoveMoneyRequest
{
    public string AccountId { get; set; } = string.Empty;
    public long Pence { get; set; }
    public string Reference { get; set; } = string.Empty;

    /// <summary>
    /// The version the caller last saw. Omit (0) to let the handler read the head itself.
    /// </summary>
    /// <remarks>
    /// Supplying it is what turns a lost update into a rejected one. Two clients that both read
    /// version 7 and both withdraw will both claim 7; the store accepts one and the other gets a
    /// conflict, so a balance cannot be quietly overwritten by whoever wrote last.
    /// </remarks>
    public long ExpectedVersion { get; set; }
}

public class LedgerAccepted
{
    public string AccountId { get; set; } = string.Empty;
    public long Version { get; set; }
    public long BalancePence { get; set; }
}

// ── Handlers ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Opens an account by appending one event to an empty stream.
/// </summary>
/// <remarks>
/// <c>expectedVersion: 0</c> means "this stream must not exist yet", so opening the same account
/// twice is a conflict rather than a second opening event that the fold would have to arbitrate.
/// The uniqueness constraint is the append, not a separate check-then-write with a race in the middle.
/// </remarks>
[Message("account:open")]
[HttpEndpoint("POST", "/accounts")]
public class OpenAccountHandler : IMessageHandler<OpenAccountRequest, LedgerAccepted>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventStore _store;
    private readonly ILogger<OpenAccountHandler> _logger;

    public OpenAccountHandler(IEventStore store, ILogger<OpenAccountHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<IBenzeneResult<LedgerAccepted>> HandleAsync(OpenAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AccountId) || string.IsNullOrWhiteSpace(request.Holder))
        {
            return BenzeneResult.ValidationError<LedgerAccepted>("AccountId and Holder are required.");
        }

        var opened = new AccountOpened
        {
            AccountId = request.AccountId,
            Holder = request.Holder,
            Currency = request.Currency
        };

        try
        {
            var version = await _store.AppendAsync(request.AccountId, 0, new[]
            {
                new EventEnvelope(EventTypes.AccountOpened, JsonSerializer.Serialize(opened, Json))
            });

            _logger.LogInformation("Opened {AccountId} at v{Version}", request.AccountId, version);
            return BenzeneResult.Ok(new LedgerAccepted { AccountId = request.AccountId, Version = version });
        }
        catch (EventStoreConcurrencyException)
        {
            return BenzeneResult.SetFailed<LedgerAccepted>(BenzeneResultStatus.Conflict,
                new[] { $"Account '{request.AccountId}' already exists." });
        }
    }
}

/// <summary>
/// Deposits money: rehydrate, decide, append.
/// </summary>
/// <remarks>
/// Three steps, in that order, and the middle one is the only place a decision is made. The state it
/// decides against is a fold of the log — never a stored balance, because a stored balance is a
/// second source of truth that can drift from the events that produced it.
/// </remarks>
[Message("money:deposit")]
[HttpEndpoint("POST", "/deposits")]
public class DepositHandler : IMessageHandler<MoveMoneyRequest, LedgerAccepted>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventStore _store;
    private readonly Rehydrator _rehydrator;

    public DepositHandler(IEventStore store, Rehydrator rehydrator)
    {
        _store = store;
        _rehydrator = rehydrator;
    }

    public async Task<IBenzeneResult<LedgerAccepted>> HandleAsync(MoveMoneyRequest request)
    {
        if (request.Pence <= 0)
        {
            return BenzeneResult.ValidationError<LedgerAccepted>("Pence must be positive.");
        }

        var current = await _rehydrator.CurrentAsync(request.AccountId);
        if (!current.State.Exists)
        {
            return BenzeneResult.NotFound<LedgerAccepted>();
        }

        var deposited = new MoneyDeposited
        {
            Pence = request.Pence,
            Currency = current.State.Currency,
            Reference = request.Reference
        };

        return await LedgerAppend.TryAppendAsync(_store, request, current,
            new EventEnvelope(EventTypes.MoneyDeposited, JsonSerializer.Serialize(deposited, Json)),
            current.State.BalancePence + request.Pence);
    }
}

/// <summary>
/// Withdraws money — the handler with a rule, and therefore the one worth reading.
/// </summary>
/// <remarks>
/// <para>
/// Insufficient funds is a <b>returned result</b>, not a thrown exception: the failure stays in the
/// type, and the HTTP binding maps the status. A refused withdrawal is an ordinary outcome of asking,
/// not an error in the system.
/// </para>
/// <para>
/// And note what it does NOT do: no event is appended when the withdrawal is refused. The log records
/// what happened to the account, not what somebody asked for. (A domain that must audit refusals
/// appends a refusal event deliberately — that is a domain decision, and it belongs in the log
/// exactly the same way.)
/// </para>
/// </remarks>
[Message("money:withdraw")]
[HttpEndpoint("POST", "/withdrawals")]
public class WithdrawHandler : IMessageHandler<MoveMoneyRequest, LedgerAccepted>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventStore _store;
    private readonly Rehydrator _rehydrator;

    public WithdrawHandler(IEventStore store, Rehydrator rehydrator)
    {
        _store = store;
        _rehydrator = rehydrator;
    }

    public async Task<IBenzeneResult<LedgerAccepted>> HandleAsync(MoveMoneyRequest request)
    {
        if (request.Pence <= 0)
        {
            return BenzeneResult.ValidationError<LedgerAccepted>("Pence must be positive.");
        }

        var current = await _rehydrator.CurrentAsync(request.AccountId);
        if (!current.State.Exists)
        {
            return BenzeneResult.NotFound<LedgerAccepted>();
        }

        if (current.State.BalancePence < request.Pence)
        {
            return BenzeneResult.SetFailed<LedgerAccepted>(BenzeneResultStatus.ValidationError, new[]
            {
                "insufficient-funds",
                $"Balance is {current.State.BalancePence}p and {request.Pence}p was requested."
            });
        }

        var withdrawn = new MoneyWithdrawn
        {
            Pence = request.Pence,
            Currency = current.State.Currency,
            Reference = request.Reference
        };

        return await LedgerAppend.TryAppendAsync(_store, request, current,
            new EventEnvelope(EventTypes.MoneyWithdrawn, JsonSerializer.Serialize(withdrawn, Json)),
            current.State.BalancePence - request.Pence);
    }
}

/// <summary>The append both movement handlers share, including how a concurrency clash is reported.</summary>
internal static class LedgerAppend
{
    public static async Task<IBenzeneResult<LedgerAccepted>> TryAppendAsync(
        IEventStore store, MoveMoneyRequest request, Rehydrated current, EventEnvelope envelope,
        long newBalance)
    {
        // A caller that supplied a version is asserting what it decided against. One that did not gets
        // the version this handler just rehydrated - which is honest but narrower: it only rules out a
        // write that landed between this handler's read and its append.
        var expected = request.ExpectedVersion > 0 ? request.ExpectedVersion : current.State.Version;

        try
        {
            var version = await store.AppendAsync(request.AccountId, expected, new[] { envelope });
            return BenzeneResult.Ok(new LedgerAccepted
            {
                AccountId = request.AccountId,
                Version = version,
                BalancePence = newBalance
            });
        }
        catch (EventStoreConcurrencyException ex)
        {
            // Rejected, not merged and not retried. Retrying here would re-run the decision against
            // state the caller never saw - which is exactly how a "check balance, then withdraw" pair
            // turns into an overdraft. The caller re-reads, re-decides, and asks again.
            return BenzeneResult.SetFailed<LedgerAccepted>(BenzeneResultStatus.Conflict, new[]
            {
                "concurrent-modification",
                $"Expected version {ex.ExpectedVersion} but the stream is at {ex.ActualVersion}.",
                "Re-read the account and decide again - this write was not applied."
            });
        }
    }
}
