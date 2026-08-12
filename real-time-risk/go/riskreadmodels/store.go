// Package riskreadmodels is the Risk Read Models service (CQRS off DynamoDB Streams, reference doc
// §3): it consumes the Trade Ledger's DynamoDB Stream in the background and projects it into a
// per-book, per-symbol net-position + realized-cash view, served by GET /books/{book}/positions.
// It is the Go port of real-time-risk/dotnet/RiskReadModels.
package riskreadmodels

import (
	"sort"
	"sync"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
)

// BookPositionsStore is the projection this service builds: per book, per symbol net position +
// realized cash, folded from TradeBooked events as the projector consumes them off the ledger's
// DynamoDB Stream. In-memory only for this first slice (resets on restart/redeploy) - a real store
// isn't needed to prove the pattern yet. Mirrors .NET's BookPositionsStore.
//
// A single mutex guards all books. That is deliberately coarse: the write side is one background
// poller applying records serially, and the read side is a cheap snapshot copy, so contention is a
// non-issue at this demo's scale and a per-book lock would be premature.
type BookPositionsStore struct {
	mu    sync.Mutex
	books map[string]*book
}

type book struct {
	positions map[string]contracts.PositionView
	// applied is the set of stream versions already folded in, so a redelivered record is a no-op.
	applied                 map[int64]struct{}
	projectedThroughVersion int64
}

// NewBookPositionsStore returns an empty projection store.
func NewBookPositionsStore() *BookPositionsStore {
	return &BookPositionsStore{books: make(map[string]*book)}
}

// Apply folds one TradeBooked event into the book's projection. It is idempotent by
// (book, version): DynamoDB Streams is at-least-once and the projector re-reads from TRIM_HORIZON
// on restart, so a redelivered record must not be double-applied. The math mirrors .NET exactly:
//
//	signedQuantity = Buy ? +q : -q
//	cashFlow       = Sell ? +(q*p) : -(q*p)
func (s *BookPositionsStore) Apply(trade contracts.TradeBooked, version int64) {
	s.mu.Lock()
	defer s.mu.Unlock()

	b, ok := s.books[trade.Book]
	if !ok {
		b = &book{positions: make(map[string]contracts.PositionView), applied: make(map[int64]struct{})}
		s.books[trade.Book] = b
	}
	if _, seen := b.applied[version]; seen {
		return
	}
	b.applied[version] = struct{}{}

	current := b.positions[trade.Symbol]
	signedQuantity := trade.Quantity
	if trade.Side != contracts.Buy {
		signedQuantity = -trade.Quantity
	}
	cashFlow := -(trade.Quantity * trade.Price)
	if trade.Side == contracts.Sell {
		cashFlow = trade.Quantity * trade.Price
	}

	b.positions[trade.Symbol] = contracts.PositionView{
		Symbol:       trade.Symbol,
		NetQuantity:  current.NetQuantity + signedQuantity,
		RealizedCash: current.RealizedCash + cashFlow,
	}

	if version > b.projectedThroughVersion {
		b.projectedThroughVersion = version
	}
}

// Query reads a book's current projection, positions sorted by symbol. An unknown book returns an
// empty (zero-trade) result, matching .NET.
func (s *BookPositionsStore) Query(bookID string) contracts.BookPositionsResponse {
	s.mu.Lock()
	defer s.mu.Unlock()

	b, ok := s.books[bookID]
	if !ok {
		return contracts.BookPositionsResponse{Book: bookID, Positions: []contracts.PositionView{}}
	}

	positions := make([]contracts.PositionView, 0, len(b.positions))
	for _, p := range b.positions {
		positions = append(positions, p)
	}
	sort.Slice(positions, func(i, j int) bool { return positions[i].Symbol < positions[j].Symbol })

	return contracts.BookPositionsResponse{
		Book:                    bookID,
		Positions:               positions,
		ProjectedThroughVersion: b.projectedThroughVersion,
	}
}
