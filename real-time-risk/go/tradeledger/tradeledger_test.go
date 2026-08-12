package tradeledger_test

import (
	"context"
	"encoding/json"
	"net/http"
	"sync"
	"testing"

	"github.com/daniellepelley/benzene-go/benzenetest"
	"github.com/daniellepelley/benzene-go/httpbinding"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/eventstore"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/tradeledger"
)

// fakeStore is an in-memory eventstore.Store for the handler tests - real-DynamoDB integration is
// covered by the CI smoke test, not here. It reproduces the append's optimistic-concurrency
// semantics (append only at exactly expectedVersion+1..) so the handler's read-then-append flow is
// exercised faithfully.
type fakeStore struct {
	mu      sync.Mutex
	streams map[string][]eventstore.StoredEvent
}

func newFakeStore() *fakeStore { return &fakeStore{streams: make(map[string][]eventstore.StoredEvent)} }

func (f *fakeStore) Read(_ context.Context, streamID string) ([]eventstore.StoredEvent, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]eventstore.StoredEvent(nil), f.streams[streamID]...), nil
}

func (f *fakeStore) Append(_ context.Context, streamID string, expectedVersion int64, events []eventstore.Event) (int64, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	existing := f.streams[streamID]
	if int64(len(existing)) != expectedVersion {
		return 0, eventstore.ErrConcurrency
	}
	version := expectedVersion
	for _, e := range events {
		version++
		existing = append(existing, eventstore.StoredEvent{
			StreamID: streamID, Version: version, EventType: e.EventType, Payload: e.Payload,
		})
	}
	f.streams[streamID] = existing
	return version, nil
}

func routes() []httpbinding.Route {
	return []httpbinding.Route{{Method: http.MethodPost, Path: "/trades", Topic: contracts.BookTradeTopic}}
}

// The first trade on a fresh book is version 1, and the response body is camelCase (the smoke test
// asserts on `.version` and `.tradeId`).
func TestBookTrade_FirstTradeIsVersionOne(t *testing.T) {
	host := benzenetest.NewHost(tradeledger.NewApp(newFakeStore()), benzenetest.WithRoutes(routes()...))

	resp := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150.25}, nil)

	if resp.StatusCode != http.StatusOK {
		t.Fatalf("StatusCode = %d, want 200; body = %s", resp.StatusCode, resp.Body)
	}
	// Assert on the raw JSON keys, not just the decoded struct, so a casing regression is caught.
	var raw map[string]json.RawMessage
	if err := json.Unmarshal([]byte(resp.Body), &raw); err != nil {
		t.Fatalf("decode body: %v; body = %s", err, resp.Body)
	}
	if _, ok := raw["version"]; !ok {
		t.Errorf("response missing camelCase `version` key; body = %s", resp.Body)
	}
	var got contracts.BookTradeResponse
	if err := json.Unmarshal([]byte(resp.Body), &got); err != nil {
		t.Fatalf("decode body: %v", err)
	}
	if got.Version != 1 {
		t.Errorf("Version = %d, want 1", got.Version)
	}
	if got.Book != "desk-a" || got.TradeId == "" {
		t.Errorf("response = %+v, want book desk-a and a non-empty tradeId", got)
	}
}

// Successive trades on the same book advance the version.
func TestBookTrade_VersionAdvancesPerBook(t *testing.T) {
	host := benzenetest.NewHost(tradeledger.NewApp(newFakeStore()), benzenetest.WithRoutes(routes()...))

	first := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150}, nil)
	second := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-a", Symbol: "AAPL", Side: contracts.Sell, Quantity: 40, Price: 160}, nil)

	if v := versionOf(t, first); v != 1 {
		t.Errorf("first version = %d, want 1", v)
	}
	if v := versionOf(t, second); v != 2 {
		t.Errorf("second version = %d, want 2", v)
	}
}

// Separate books are separate streams, each starting at version 1.
func TestBookTrade_SeparateBooksAreSeparateStreams(t *testing.T) {
	host := benzenetest.NewHost(tradeledger.NewApp(newFakeStore()), benzenetest.WithRoutes(routes()...))

	a := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: 1}, nil)
	b := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-b", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: 1}, nil)

	if versionOf(t, a) != 1 || versionOf(t, b) != 1 {
		t.Errorf("each fresh book's first trade should be version 1")
	}
}

func TestBookTrade_ValidationRejectsBadInput(t *testing.T) {
	host := benzenetest.NewHost(tradeledger.NewApp(newFakeStore()), benzenetest.WithRoutes(routes()...))

	cases := map[string]contracts.BookTradeRequest{
		"empty book":        {Book: "", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: 1},
		"empty symbol":      {Book: "desk-a", Symbol: "", Side: contracts.Buy, Quantity: 1, Price: 1},
		"zero quantity":     {Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 0, Price: 1},
		"negative price":    {Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: -1},
		"negative quantity": {Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: -5, Price: 1},
	}
	for name, req := range cases {
		t.Run(name, func(t *testing.T) {
			resp := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades", req, nil)
			// StatusBadRequest -> HTTP 400.
			if resp.StatusCode != http.StatusBadRequest {
				t.Errorf("StatusCode = %d, want 400; body = %s", resp.StatusCode, resp.Body)
			}
		})
	}
}

// A concurrency conflict from the store surfaces as HTTP 409 (StatusConflict).
func TestBookTrade_ConcurrencyConflictIsHTTP409(t *testing.T) {
	app := tradeledger.NewApp(conflictStore{})
	host := benzenetest.NewHost(app, benzenetest.WithRoutes(routes()...))

	resp := benzenetest.SendHTTP(t, host, http.MethodPost, "/trades",
		contracts.BookTradeRequest{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: 1}, nil)

	if resp.StatusCode != http.StatusConflict {
		t.Errorf("StatusCode = %d, want 409; body = %s", resp.StatusCode, resp.Body)
	}
}

// conflictStore always fails the append with ErrConcurrency, to prove the handler's mapping.
type conflictStore struct{}

func (conflictStore) Read(context.Context, string) ([]eventstore.StoredEvent, error) { return nil, nil }
func (conflictStore) Append(context.Context, string, int64, []eventstore.Event) (int64, error) {
	return 0, eventstore.ErrConcurrency
}

var _ eventstore.Store = conflictStore{}

func versionOf(t *testing.T, resp benzenetest.HTTPResponse) int64 {
	t.Helper()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("StatusCode = %d, want 200; body = %s", resp.StatusCode, resp.Body)
	}
	var got contracts.BookTradeResponse
	if err := json.Unmarshal([]byte(resp.Body), &got); err != nil {
		t.Fatalf("decode body: %v; body = %s", err, resp.Body)
	}
	return got.Version
}
