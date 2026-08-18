using System.Collections.Concurrent;

namespace Benzene.Patterns.Choreography.Reactions.CacheWarmer;

/// <summary>What this reaction did, and under which correlation id.</summary>
/// <remarks>
/// The correlation id is recorded on purpose. It is the same id the emitter returned to its caller,
/// carried on the event's headers across the broker hop - which is the concrete, checkable form of
/// the claim that trace context survives choreography, and therefore that the mesh can draw the
/// graph of who reacts to what from real traffic rather than from a diagram somebody maintains.
/// </remarks>
public class Journal
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public void Record(string what, string correlationId) => _entries.Enqueue(new Entry(what, correlationId));

    public JournalView Read()
    {
        var entries = _entries.ToArray();
        return new JournalView(entries.Length, entries);
    }
}

public record Entry(string What, string CorrelationId);

public record JournalView(int Count, IReadOnlyCollection<Entry> Entries);
