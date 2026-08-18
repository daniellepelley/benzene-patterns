using System.Text.Json;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.EventSourcing;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Patterns.EventSourcing.Ledger;

/// <summary>
/// Appends a deposit in the OLD event shape, exactly as a build from before the ledger was
/// multi-currency would have written it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint is a time machine, and it is the only dishonest thing in the example</b> — no
/// production system has a route for writing history in a retired format. It exists because the
/// property worth demonstrating (a decade-old event, read by today's code, without the log being
/// touched) otherwise takes a decade to set up.
/// </para>
/// <para>
/// What happens next is the real point: the fold never sees this shape. <see cref="Upcaster"/>
/// converts it on read, the balance includes it, and <c>GET /accounts/{id}/history</c> still shows
/// <c>money:deposited:v1</c> with its original payload — because the log was not rewritten and must
/// never be.
/// </para>
/// </remarks>
[Message("money:deposit:legacy")]
[HttpEndpoint("POST", "/legacy-deposits")]
public class LegacyDepositHandler : IMessageHandler<MoveMoneyRequest, LedgerAccepted>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventStore _store;
    private readonly Rehydrator _rehydrator;

    public LegacyDepositHandler(IEventStore store, Rehydrator rehydrator)
    {
        _store = store;
        _rehydrator = rehydrator;
    }

    public async Task<IBenzeneResult<LedgerAccepted>> HandleAsync(MoveMoneyRequest request)
    {
        var current = await _rehydrator.CurrentAsync(request.AccountId);
        if (!current.State.Exists)
        {
            return BenzeneResult.NotFound<LedgerAccepted>();
        }

        // No Currency field. There was no such field.
        var legacy = new MoneyDepositedV1 { Pence = request.Pence, Reference = request.Reference };

        var version = await _store.AppendAsync(request.AccountId, current.State.Version, new[]
        {
            new EventEnvelope(EventTypes.MoneyDepositedV1, JsonSerializer.Serialize(legacy, Json))
        });

        return BenzeneResult.Ok(new LedgerAccepted
        {
            AccountId = request.AccountId,
            Version = version,
            BalancePence = current.State.BalancePence + request.Pence
        });
    }
}
