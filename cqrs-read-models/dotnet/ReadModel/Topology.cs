using RabbitMQ.Client;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// The read model declares its own queue and binds it to the write side's exchange.
/// </summary>
/// <remarks>
/// Neither write service is involved, and neither knows this queue exists. That is what "add a new
/// view without touching any core service" means in practice — the subscription is declared by the
/// subscriber.
/// </remarks>
public static class Topology
{
    public const string Exchange = "domain-events";
    public const string Queue = "read-model";

    public static async Task DeclareAsync(string host, int port, string user, string password)
    {
        var factory = new ConnectionFactory { HostName = host, Port = port, UserName = user, Password = password };
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false);
                await channel.QueueBindAsync(Queue, Exchange, routingKey: string.Empty);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException($"Could not declare {Queue} on RabbitMQ at {host}:{port}", last);
    }
}
