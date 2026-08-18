using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.Cqrs.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.Cqrs.Write.UserService;

/// <summary>
/// The User aggregate. Holds a tenant id and nothing else about tenants — reference by id.
/// </summary>
/// <remarks>
/// It cannot validate that the tenant exists without calling the Tenant service, and it deliberately
/// does not: a core service holds no cross-service process. The read model will discover, from two
/// independent event streams, that these two things belong together.
/// </remarks>
public class UserStore
{
    private readonly ConcurrentDictionary<string, (string TenantId, string Email)> _users = new();

    public string Create(string tenantId, string email)
    {
        var id = $"usr-{Guid.NewGuid():N}"[..12];
        _users[id] = (tenantId, email);
        return id;
    }

    public int Count => _users.Count;
}

[Message(Topics.UserCreate)]
[HttpEndpoint("POST", "/users")]
public class CreateUserHandler : IMessageHandler<CreateUserRequest, UserAccepted>
{
    private readonly UserStore _store;
    private readonly EventLog _log;
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<CreateUserHandler> _logger;

    public CreateUserHandler(UserStore store, EventLog log, IBenzeneMessageSender sender,
        ILogger<CreateUserHandler> logger)
    {
        _store = store;
        _log = log;
        _sender = sender;
        _logger = logger;
    }

    public async Task<IBenzeneResult<UserAccepted>> HandleAsync(CreateUserRequest request)
    {
        if (!request.Email.Contains('@'))
        {
            return BenzeneResult.ValidationError<UserAccepted>("Email must contain '@'.");
        }

        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return BenzeneResult.ValidationError<UserAccepted>("TenantId is required.");
        }

        var userId = _store.Create(request.TenantId, request.Email);
        var @event = new UserCreated
        {
            UserId = userId,
            TenantId = request.TenantId,
            Email = request.Email
        };

        _log.Append(Topics.UserCreated, @event);

        var times = Math.Clamp(request.EmitTimes, 1, 10);
        for (var i = 0; i < times; i++)
        {
            await _sender.SendAsync<UserCreated, Void>(Topics.UserCreated, @event);
        }

        _logger.LogInformation("Created user {UserId} for tenant {TenantId}", userId, request.TenantId);

        return BenzeneResult.Accepted(new UserAccepted
        {
            UserId = userId,
            TenantId = request.TenantId,
            Email = request.Email,
            Emitted = times
        });
    }
}

[Message("user:events")]
[HttpEndpoint("GET", "/events")]
public class ReadEventLogHandler : IMessageHandler<Void, EventLogResponse>
{
    private readonly EventLog _log;

    public ReadEventLogHandler(EventLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<EventLogResponse>> HandleAsync(Void request)
        => BenzeneResult.Ok(_log.Read()).AsTask();
}
