// Package contracts holds the wire types the two Real-Time Risk services address each other by -
// the Go port of real-time-risk/dotnet/Contracts. It is deliberately dependency-light (only the
// benzene-go core, for the Topic type) so both the Trade Ledger (producer) and the Risk Read
// Models (consumer) can share it without dragging a transport or SDK in.
//
// JSON field names are camelCase throughout, matching the .NET services' on-the-wire shape (the
// .NET port uses System.Text.Json defaults, which camelCase nothing by attribute but the ASP.NET
// host configures camelCase globally) - the shared black-box smoke test asserts on `.version`,
// `.projectedThroughVersion`, `.positions[].symbol`, and `.netQuantity`, so the casing is part of
// the contract, not a detail.
package contracts

import (
	"time"

	benzene "github.com/daniellepelley/benzene-go"
)

// TradeSide is which way a trade goes. It is a string type (not an int enum) so it marshals to
// and from the JSON strings "Buy"/"Sell" with no custom converter - the Go-idiomatic equivalent
// of the .NET enum's [JsonStringEnumConverter], which exists purely to get that same "Buy"/"Sell"
// wire form (see Contracts/TradeSide.cs).
type TradeSide string

const (
	// Buy adds to the net position and subtracts cost (cash out).
	Buy TradeSide = "Buy"
	// Sell subtracts from the net position and adds proceeds (cash in).
	Sell TradeSide = "Sell"
)

// Topic names the two services route by. Shared here so producer and consumer never drift on a
// topic string (mirrors Contracts/Topics.cs).
var (
	// BookTradeTopic is the command topic the Trade Ledger handles: book a trade to the ledger.
	BookTradeTopic = benzene.NewTopic("trade:book")
	// BookPositionsTopic is the query topic the Risk Read Models handles: current positions for a book.
	BookPositionsTopic = benzene.NewTopic("book:positions")
)

// TradeBookedEventType is the event-type discriminator stored on every ledger event
// (StoredEvent.EventType) and the DynamoDB item's `eventType` attribute. One event type today;
// cash movements and fees are future work per real-time-risk/README.md's build order.
const TradeBookedEventType = "TradeBooked"

// BookTradeRequest is the trade:book command request (POST /trades).
type BookTradeRequest struct {
	Book     string    `json:"book"`
	Symbol   string    `json:"symbol"`
	Side     TradeSide `json:"side"`
	Quantity float64   `json:"quantity"`
	Price    float64   `json:"price"`
}

// BookTradeResponse is the trade:book command response - the resulting ledger position.
type BookTradeResponse struct {
	// TradeId is the new event's id (a UUID string; the .NET port uses a Guid).
	TradeId string `json:"tradeId"`
	Book    string `json:"book"`
	// Version is the book's ledger stream version after this trade was appended (1-based).
	Version int64 `json:"version"`
}

// TradeBooked is the immutable event appended to the ledger for every booked trade (the JSON
// `payload` of the TradeBookedEventType event) - the Trade Ledger's write side and, via the
// event table's DynamoDB Stream, the Risk Read Models' projection input (mirrors
// Contracts/TradeBooked.cs).
type TradeBooked struct {
	TradeId  string    `json:"tradeId"`
	Book     string    `json:"book"`
	Symbol   string    `json:"symbol"`
	Side     TradeSide `json:"side"`
	Quantity float64   `json:"quantity"`
	Price    float64   `json:"price"`
	BookedAt time.Time `json:"bookedAt"`
}

// BookPositionsRequest is the book:positions query request (GET /books/{book}/positions).
type BookPositionsRequest struct {
	Book string `json:"book"`
}

// PositionView is one symbol's net position and realized cash within a book, as projected from
// the ledger.
type PositionView struct {
	Symbol string `json:"symbol"`
	// NetQuantity is net signed quantity: buys add, sells subtract.
	NetQuantity float64 `json:"netQuantity"`
	// RealizedCash is net cash flow from trading this symbol: a sell adds proceeds, a buy subtracts cost.
	RealizedCash float64 `json:"realizedCash"`
}

// BookPositionsResponse is the book:positions query response - one row per symbol traded in the book.
type BookPositionsResponse struct {
	Book string `json:"book"`
	// Positions is the per-symbol projection, sorted by symbol.
	Positions []PositionView `json:"positions"`
	// ProjectedThroughVersion is the highest ledger event version this projection has consumed
	// for the book, so a caller can tell whether a just-booked trade has been reflected yet (the
	// stream projection is eventually consistent, not read-your-writes).
	ProjectedThroughVersion int64 `json:"projectedThroughVersion"`
}
