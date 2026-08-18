using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.TwoTier.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TwoTier.Core.UserService;

/// <summary>The User aggregate's own store. Share-nothing.</summary>
public class UserStore
{
    private readonly ConcurrentDictionary<string, string> _users = new();

    public string Create(string email)
    {
        var id = $"usr-{Guid.NewGuid():N}"[..12];
        _users[id] = email;
        return id;
    }

    public bool Delete(string userId) => _users.TryRemove(userId, out _);

    public int Count => _users.Count;

    public IReadOnlyCollection<string> Emails => _users.Values.ToList();
}

/// <summary>
/// Creates a user for a tenant. Runs in the saga's SECOND stage, because it needs the tenant id the
/// first stage produced — which is exactly why stages exist.
/// </summary>
[Message(Topics.UserCreate)]
public class CreateUserHandler : IMessageHandler<CreateUserRequest, UserCreated>
{
    private readonly UserStore _store;

    public CreateUserHandler(UserStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<UserCreated>> HandleAsync(CreateUserRequest request)
    {
        // An address without an @ is the demo's way to fail the SECOND stage, so a caller can watch
        // two already-committed effects (tenant, billing) get compensated in reverse.
        if (!request.Email.Contains('@'))
        {
            return BenzeneResult.ValidationError<UserCreated>("Email must contain '@'.").AsTask();
        }

        return BenzeneResult.Ok(new UserCreated
        {
            UserId = _store.Create(request.Email),
            TenantId = request.TenantId,
            Email = request.Email
        }).AsTask();
    }
}

[Message(Topics.UserDelete)]
public class DeleteUserHandler : IMessageHandler<DeleteUserRequest, Acknowledged>
{
    private readonly UserStore _store;
    private readonly ILogger<DeleteUserHandler> _logger;

    public DeleteUserHandler(UserStore store, ILogger<DeleteUserHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<IBenzeneResult<Acknowledged>> HandleAsync(DeleteUserRequest request)
    {
        _store.Delete(request.UserId);
        _logger.LogInformation("Compensated: deleted user {UserId}", request.UserId);
        return BenzeneResult.Ok(new Acknowledged()).AsTask();
    }
}

[Message("user:list")]
[Benzene.Http.HttpEndpoint("GET", "/users")]
public class ListUsersHandler : IMessageHandler<Acknowledged, UserListResponse>
{
    private readonly UserStore _store;

    public ListUsersHandler(UserStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<UserListResponse>> HandleAsync(Acknowledged request)
        => BenzeneResult.Ok(new UserListResponse
        {
            Count = _store.Count,
            Emails = _store.Emails.OrderBy(x => x, StringComparer.Ordinal).ToList()
        }).AsTask();
}

public class UserListResponse
{
    public int Count { get; set; }
    public List<string> Emails { get; set; } = new();
}
