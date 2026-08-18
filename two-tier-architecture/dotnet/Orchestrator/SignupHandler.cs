using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.TwoTier.Contracts;
using Benzene.Results;
using Benzene.Saga;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TwoTier.Orchestrator;

/// <summary>
/// The orchestrator tier: owns the <b>process</b>, owns no data.
/// </summary>
/// <remarks>
/// <para>
/// Signing a company up writes to three databases that no distributed transaction spans. The saga
/// gets atomicity anyway — not by holding a lock, but by pairing every forward action with the
/// compensation that undoes it and running the compensations in reverse if anything fails. The
/// invariant it guarantees is: <b>total success, or total rollback — never a half-applied process.</b>
/// </para>
/// <para>
/// Notice what each step's <c>Do</c> actually is: an ordinary <c>SendAsync(topic, request)</c>. The
/// saga is the orchestration; the core services do the work, and none of them knows a saga exists —
/// to Tenant, a compensation is just a delete.
/// </para>
/// <para>
/// The stage split is not stylistic. Tenant and Billing are <b>independent</b>, so they share stage 1
/// and run concurrently. The user needs the tenant id, so it goes in stage 2 and reads the earlier
/// result from the shared context. Stages express dependency; steps within one express parallelism.
/// </para>
/// </remarks>
[Message(Topics.SignupStart)]
[HttpEndpoint("POST", "/signups")]
public class SignupHandler : IMessageHandler<SignupRequest, SignupResponse>
{
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<SignupHandler> _logger;

    public SignupHandler(IBenzeneMessageSender sender, ILogger<SignupHandler> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task<IBenzeneResult<SignupResponse>> HandleAsync(SignupRequest request)
    {
        // Captured from the forward actions rather than read back off the saga: the engine threads
        // results between STAGES through its context, but does not expose that context to the caller
        // afterwards. Locals are the simple, honest way to keep what the process produced.
        TenantCreated? tenant = null;
        UserCreated? user = null;
        BillingAccountCreated? account = null;

        var saga = new SagaBuilder()
            .Stage(stage => stage
                .Step<TenantCreated>(step => step
                    .Do(_ => Capture(_sender.SendAsync<CreateTenantRequest, TenantCreated>(
                        Topics.TenantCreate, new CreateTenantRequest { Company = request.Company }),
                        x => tenant = x))
                    .Compensate(async (_, created) => await _sender.SendAsync<DeleteTenantRequest, Acknowledged>(
                        Topics.TenantDelete, new DeleteTenantRequest { TenantId = created.TenantId })))
                .Step<BillingAccountCreated>(step => step
                    .Do(_ => Capture(_sender.SendAsync<SetupBillingRequest, BillingAccountCreated>(
                        Topics.BillingSetup, new SetupBillingRequest { Company = request.Company, Plan = request.Plan }),
                        x => account = x))
                    .Compensate(async (_, created) => await _sender.SendAsync<TeardownBillingRequest, Acknowledged>(
                        Topics.BillingTeardown, new TeardownBillingRequest { AccountId = created.AccountId }))))
            .Stage(stage => stage
                .Step<UserCreated>(step => step
                    // Stage 2 reads stage 1's output from the shared context - the reason these are
                    // two stages rather than one.
                    .Do(ctx => Capture(_sender.SendAsync<CreateUserRequest, UserCreated>(
                        Topics.UserCreate, new CreateUserRequest
                        {
                            TenantId = ctx.Get<TenantCreated>().TenantId,
                            Email = request.Email
                        }),
                        x => user = x))
                    .Compensate(async (_, created) => await _sender.SendAsync<DeleteUserRequest, Acknowledged>(
                        Topics.UserDelete, new DeleteUserRequest { UserId = created.UserId }))))
            .Build();

        var result = await saga.RunAsync();

        var response = new SignupResponse
        {
            Outcome = result.Outcome.ToString(),
            FailedStage = result.FailedStageIndex,
            FailureStatus = result.Failure?.Status,
            Orphans = result.CompensationFailures.Select(x => x.GetType().Name).ToList()
        };

        if (result.IsSuccess)
        {
            response.TenantId = tenant!.TenantId;
            response.UserId = user!.UserId;
            response.AccountId = account!.AccountId;
            return BenzeneResult.Ok(response);
        }

        // A FAILED result's payload does not reach the caller: the HTTP binding maps a non-success
        // status onto problem details, whose body carries `errors`, not the handler's response type.
        // That is the wire working as specified, so the explanation goes where the wire actually
        // carries it rather than into a payload that gets dropped.
        if (result.Outcome == SagaOutcome.PartiallyRolledBack)
        {
            // The one outcome the invariant could NOT restore: something failed, and undoing it also
            // failed, so an effect may still be applied. Surfaced loudly, named rather than counted,
            // and never retried automatically - retrying on top of a possibly-applied effect is how
            // you double-charge a customer. A human or a repair process owns it from here.
            _logger.LogError(
                "Signup for {Company} left {Count} orphaned effect(s) - needs reconciliation",
                request.Company, result.CompensationFailures.Count);

            return BenzeneResult.Set<SignupResponse>(BenzeneResultStatus.UnexpectedError, new[]
            {
                $"Signup failed at stage {result.FailedStageIndex} and rollback did not fully succeed.",
                $"{result.CompensationFailures.Count} effect(s) may still be applied - reconciliation needed.",
                "This outcome is never retried automatically."
            });
        }

        // A clean RolledBack: the system is exactly as it was before. The caller gets the failing
        // step's own status, so "we do not support that plan" does not arrive as "something broke".
        _logger.LogInformation("Signup for {Company} rolled back cleanly at stage {Stage}",
            request.Company, result.FailedStageIndex);

        return BenzeneResult.Set<SignupResponse>(
            result.Failure?.Status ?? BenzeneResultStatus.UnexpectedError, new[]
            {
                $"Signup failed at stage {result.FailedStageIndex} and was rolled back cleanly.",
                "Nothing was left behind."
            });
    }

    /// <summary>
    /// Passes a forward action's result straight through, keeping the payload on the way past.
    /// </summary>
    /// <remarks>
    /// The engine threads results between STAGES through its own context but does not hand that
    /// context back afterwards, so the ids the process produced are captured here. Deliberately
    /// transparent: a failed result is returned unchanged, so the saga's own success/failure
    /// semantics decide what happens - this helper never turns a failure into an exception.
    /// </remarks>
    private static async Task<IBenzeneResult<T>> Capture<T>(Task<IBenzeneResult<T>> call, Action<T> keep)
    {
        var result = await call;
        if (result.IsSuccessful)
        {
            keep(result.Payload);
        }

        return result;
    }
}
