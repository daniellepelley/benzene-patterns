using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.TwoTier.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TwoTier.Core.BillingService;

/// <summary>The Billing account aggregate's own store. Share-nothing.</summary>
public class BillingStore
{
    private static readonly HashSet<string> SupportedPlans =
        new(StringComparer.OrdinalIgnoreCase) { "standard", "enterprise" };

    private readonly ConcurrentDictionary<string, string> _accounts = new();

    public static bool IsSupportedPlan(string plan) => SupportedPlans.Contains(plan);

    public string Create(string plan)
    {
        var id = $"acc-{Guid.NewGuid():N}"[..12];
        _accounts[id] = plan;
        return id;
    }

    public bool Delete(string accountId) => _accounts.TryRemove(accountId, out _);

    public int Count => _accounts.Count;
}

/// <summary>
/// Sets up billing. Runs in the saga's FIRST stage, concurrently with tenant creation, because the
/// two are independent — neither needs the other's output.
/// </summary>
[Message(Topics.BillingSetup)]
public class SetupBillingHandler : IMessageHandler<SetupBillingRequest, BillingAccountCreated>
{
    private readonly BillingStore _store;

    public SetupBillingHandler(BillingStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<BillingAccountCreated>> HandleAsync(SetupBillingRequest request)
    {
        // An unsupported plan fails stage 1. Its sibling step (tenant creation) may well have
        // succeeded already, since the two run concurrently - so this is the case that shows a stage
        // compensating its OWN succeeded steps, not just earlier stages'.
        if (!BillingStore.IsSupportedPlan(request.Plan))
        {
            return BenzeneResult.ValidationError<BillingAccountCreated>(
                $"Unsupported plan '{request.Plan}'.").AsTask();
        }

        return BenzeneResult.Ok(new BillingAccountCreated
        {
            AccountId = _store.Create(request.Plan),
            Plan = request.Plan
        }).AsTask();
    }
}

[Message(Topics.BillingTeardown)]
public class TeardownBillingHandler : IMessageHandler<TeardownBillingRequest, Acknowledged>
{
    private readonly BillingStore _store;
    private readonly ILogger<TeardownBillingHandler> _logger;

    public TeardownBillingHandler(BillingStore store, ILogger<TeardownBillingHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<IBenzeneResult<Acknowledged>> HandleAsync(TeardownBillingRequest request)
    {
        _store.Delete(request.AccountId);
        _logger.LogInformation("Compensated: tore down billing account {AccountId}", request.AccountId);
        return BenzeneResult.Ok(new Acknowledged()).AsTask();
    }
}

[Message("billing:list")]
[Benzene.Http.HttpEndpoint("GET", "/accounts")]
public class ListAccountsHandler : IMessageHandler<Acknowledged, AccountListResponse>
{
    private readonly BillingStore _store;

    public ListAccountsHandler(BillingStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<AccountListResponse>> HandleAsync(Acknowledged request)
        => BenzeneResult.Ok(new AccountListResponse { Count = _store.Count }).AsTask();
}

public class AccountListResponse
{
    public int Count { get; set; }
}
