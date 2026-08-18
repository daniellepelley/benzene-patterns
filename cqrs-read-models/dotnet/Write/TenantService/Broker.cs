using RabbitMQ.Client;

namespace Benzene.Patterns.Cqrs.Write.TenantService;

/// <summary>
/// Opens the connection and declares the one piece of topology the emitter owns: the exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>The emitter declares the exchange and nothing else.</b> No queues, no bindings — it does not
/// know what queues exist, and that is the pattern rather than an omission. Each reaction declares
/// its own queue and binds it, so a new reaction needs no change here, no change to any other
/// reaction, and no central topology file that would quietly become the thing every team has to
/// edit.
/// </para>
/// <para>
/// The exchange is a <c>fanout</c>: every bound queue gets its own copy of every event. That is what
/// makes the three reactions independent — a slow or failing consumer holds up its own queue and
/// nobody else's.
/// </para>
/// </remarks>
public static class Broker
{
    public const string Exchange = "domain-events";

    /// <summary>Connects, retrying, then declares the fanout exchange.</summary>
    /// <remarks>
    /// Retrying is not defensive padding: under compose the emitter starts alongside the broker, and
    /// a first connection attempt is expected to fail. Failing to start would be the wrong answer for
    /// something whose dependency is a shared broker.
    /// </remarks>
    public static async Task<IChannel> ConnectAsync(string host, int port, string user, string password)
    {
        var factory = new ConnectionFactory { HostName = host, Port = port, UserName = user, Password = password };
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var connection = await factory.CreateConnectionAsync();
                var channel = await connection.CreateChannelAsync();
                await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
                return channel;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException($"Could not reach RabbitMQ at {host}:{port}", last);
    }
}
