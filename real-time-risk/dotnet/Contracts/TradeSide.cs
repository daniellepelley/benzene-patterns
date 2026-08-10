using System.Text.Json.Serialization;

namespace Benzene.Patterns.RealTimeRisk.Contracts;

// Benzene's default ISerializer (Benzene.Core.MessageHandlers.Serialization.JsonSerializer) uses
// plain System.Text.Json defaults, which serialize enums as integers unless told otherwise. The
// attribute-based converter applies regardless of the caller's JsonSerializerOptions, so "Buy"/"Sell"
// round-trip on the wire without every consumer needing to know to register a string-enum converter.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TradeSide
{
    Buy,
    Sell
}
