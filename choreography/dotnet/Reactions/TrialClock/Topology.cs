using RabbitMQ.Client;

namespace Benzene.Patterns.Choreography.Reactions.TrialClock;

/// <summary>
/// Declares this reaction's own queue and binds it to the shared exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>The consumer owns its subscription.</b> The emitter declares the exchange and stops there; each
/// reaction declares the queue it consumes and the binding that feeds it. Nothing central lists the
/// subscribers, so adding one changes no file anybody else owns — which is the operational half of
/// the decoupling the pattern claims, and the half that quietly disappears when topology lives in a
/// single shared template.
/// </para>
/// <para>
/// The exchange declaration is repeated here rather than assumed. It is idempotent with identical
/// arguments, and it means this service starts correctly whatever order the estate comes up in.
/// </para>
/// </remarks>
public static class Topology
{
    public const string Exchange = "domain-events";
    public const string Queue = "trial-clock";

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
