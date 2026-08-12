package riskreadmodels

import (
	"testing"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
)

// A single 100-share AAPL buy folds into a +100 net quantity and -15025 cash (cost out), and the
// projection reports the applied version - the shape the smoke test asserts on.
func TestApply_SingleBuy(t *testing.T) {
	store := NewBookPositionsStore()

	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150.25}, 1)

	got := store.Query("desk-a")
	if got.ProjectedThroughVersion != 1 {
		t.Fatalf("ProjectedThroughVersion = %d, want 1", got.ProjectedThroughVersion)
	}
	if len(got.Positions) != 1 {
		t.Fatalf("len(Positions) = %d, want 1", len(got.Positions))
	}
	p := got.Positions[0]
	if p.Symbol != "AAPL" || p.NetQuantity != 100 {
		t.Errorf("position = %+v, want AAPL netQuantity 100", p)
	}
	if p.RealizedCash != -15025 {
		t.Errorf("RealizedCash = %v, want -15025 (100 * 150.25 cost out)", p.RealizedCash)
	}
}

// Buy then Sell of the same symbol nets the quantity and accumulates cash both ways.
func TestApply_BuyThenSellNetsPositionAndCash(t *testing.T) {
	store := NewBookPositionsStore()

	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150}, 1)
	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Sell, Quantity: 40, Price: 160}, 2)

	p := store.Query("desk-a").Positions[0]
	if p.NetQuantity != 60 {
		t.Errorf("NetQuantity = %v, want 60 (100 - 40)", p.NetQuantity)
	}
	// -15000 (buy cost) + 6400 (sell proceeds) = -8600.
	if p.RealizedCash != -8600 {
		t.Errorf("RealizedCash = %v, want -8600", p.RealizedCash)
	}
}

// A redelivered version must not double-apply: DynamoDB Streams is at-least-once and the projector
// re-reads from TRIM_HORIZON on restart.
func TestApply_IdempotentByVersion(t *testing.T) {
	store := NewBookPositionsStore()

	trade := contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150.25}
	store.Apply(trade, 1)
	store.Apply(trade, 1) // redelivery of the same version
	store.Apply(trade, 1)

	p := store.Query("desk-a").Positions[0]
	if p.NetQuantity != 100 {
		t.Errorf("NetQuantity = %v, want 100 (redelivery must not double-apply)", p.NetQuantity)
	}
}

// Positions come back sorted by symbol, and each symbol accumulates independently.
func TestQuery_MultipleSymbolsSortedBySymbol(t *testing.T) {
	store := NewBookPositionsStore()

	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "MSFT", Side: contracts.Buy, Quantity: 10, Price: 400}, 1)
	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 5, Price: 150}, 2)

	got := store.Query("desk-a")
	if len(got.Positions) != 2 {
		t.Fatalf("len(Positions) = %d, want 2", len(got.Positions))
	}
	if got.Positions[0].Symbol != "AAPL" || got.Positions[1].Symbol != "MSFT" {
		t.Errorf("symbols = [%s %s], want sorted [AAPL MSFT]", got.Positions[0].Symbol, got.Positions[1].Symbol)
	}
	if got.ProjectedThroughVersion != 2 {
		t.Errorf("ProjectedThroughVersion = %d, want 2", got.ProjectedThroughVersion)
	}
}

// An unknown book is an empty (zero-trade) result, not an error - so a caller polling for
// eventual consistency gets projectedThroughVersion 0 rather than a 404.
func TestQuery_UnknownBookIsEmpty(t *testing.T) {
	store := NewBookPositionsStore()

	got := store.Query("no-such-book")
	if got.Book != "no-such-book" || len(got.Positions) != 0 || got.ProjectedThroughVersion != 0 {
		t.Errorf("got = %+v, want empty zero-version result", got)
	}
}
