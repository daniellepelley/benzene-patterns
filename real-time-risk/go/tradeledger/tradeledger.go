// Package tradeledger is the Trade Ledger service (event sourcing, reference doc §5) - the book of
// record. Every booked trade is a command handler appending an immutable event to a
// DynamoDB-backed event log (one stream per book). It is the Go port of
// real-time-risk/dotnet/TradeLedger.
//
// Hosting: a plain net/http server for this local/Docker Compose slice - no Lambda, because the
// Trade Ledger has no event-source trigger of its own (it only serves POST /trades). On AWS this
// same handler would be hosted by awslambda.Start(awslambda.HTTPHandler(...)) behind API Gateway,
// unchanged; the wire contract (topic + JSON body) is identical either way.
package tradeledger

import (
	"context"
	"crypto/rand"
	"fmt"
	"time"

	benzene "github.com/daniellepelley/benzene-go"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/eventstore"
)

// storeKey is the typed DI key for the event Store dependency - a struct key can't collide with
// another package's key, unlike a bare string.
type storeKey struct{}

// jsonMarshal is a seam so BookTradeHandler can serialize the event body without importing the
// std json package name into the handler's line noise; it is just encoding/json.Marshal.
var jsonMarshal = defaultMarshal

// NewApp is the composition root: it registers the event Store as a singleton and the trade:book
// command handler, then builds the router pipeline - the Go equivalent of the .NET StartUp's
// ConfigureServices + Configure. main() supplies the concrete store (a DynamoStore in production, a
// fake in tests), so this package never has to know how the store is built.
func NewApp(store eventstore.Store) benzene.App[struct{}] {
	return benzene.App[struct{}]{
		GetConfiguration: func() struct{} { return struct{}{} },
		ConfigureServices: func(registry *benzene.Registry, container *benzene.Container, _ struct{}) {
			benzene.AddSingleton(container, storeKey{}, func(_ *benzene.Scope) eventstore.Store { return store })
			if err := benzene.Register(registry, contracts.BookTradeTopic,
				benzene.Handler[contracts.BookTradeRequest, contracts.BookTradeResponse](BookTradeHandler)); err != nil {
				panic(fmt.Sprintf("tradeledger: register trade:book handler: %v", err))
			}
		},
		Configure: func(builder *benzene.ApplicationBuilder, _ struct{}) {
			builder.UsePipeline(benzene.NewPipeline(benzene.RouterMiddleware(builder.Registry)))
		},
	}
}

// BookTradeHandler validates a trade and appends a TradeBooked event to the book's ledger stream
// (stream id = book id). Optimistic concurrency is handled by reading the stream's current version
// immediately before appending - fine for this demo's traffic; a high-contention book would instead
// have the caller supply the version it last saw and retry on eventstore.ErrConcurrency. This
// mirrors .NET's BookTradeHandler line for line.
func BookTradeHandler(ctx context.Context, req contracts.BookTradeRequest) benzene.Result[contracts.BookTradeResponse] {
	if req.Book == "" || req.Symbol == "" || req.Quantity <= 0 || req.Price <= 0 {
		return benzene.BadRequest[contracts.BookTradeResponse](
			"Book, Symbol, Quantity (> 0) and Price (> 0) are required.")
	}

	scope, ok := benzene.ScopeFromContext(ctx)
	if !ok {
		return benzene.UnexpectedError[contracts.BookTradeResponse]("no DI scope on context")
	}
	store := benzene.GetService[eventstore.Store](scope, storeKey{})

	streamID := req.Book
	history, err := store.Read(ctx, streamID)
	if err != nil {
		return benzene.UnexpectedError[contracts.BookTradeResponse]("read ledger: " + err.Error())
	}
	expectedVersion := int64(0)
	if len(history) > 0 {
		expectedVersion = history[len(history)-1].Version
	}

	tradeBooked := contracts.TradeBooked{
		TradeId:  newUUID(),
		Book:     req.Book,
		Symbol:   req.Symbol,
		Side:     req.Side,
		Quantity: req.Quantity,
		Price:    req.Price,
		BookedAt: time.Now().UTC(),
	}

	payload, err := jsonMarshal(tradeBooked)
	if err != nil {
		return benzene.UnexpectedError[contracts.BookTradeResponse]("serialize event: " + err.Error())
	}

	newVersion, err := store.Append(ctx, streamID, expectedVersion,
		[]eventstore.Event{{EventType: contracts.TradeBookedEventType, Payload: string(payload)}})
	if err != nil {
		// A concurrency conflict is a retryable, expected outcome under contention; everything
		// else is an unexpected store failure. Both are surfaced as non-success results, mapping
		// onto 409 and 500 respectively via httpstatus.
		if isConcurrency(err) {
			return benzene.Conflict[contracts.BookTradeResponse](
				"the book's ledger advanced concurrently; re-read and retry")
		}
		return benzene.UnexpectedError[contracts.BookTradeResponse]("append event: " + err.Error())
	}

	return benzene.Ok(contracts.BookTradeResponse{
		TradeId: tradeBooked.TradeId,
		Book:    req.Book,
		Version: newVersion,
	})
}

// newUUID returns a random RFC 4122 v4 UUID string. A tiny local generator (crypto/rand) avoids
// pulling in a UUID dependency for the one place the Ledger needs an id - the .NET port uses
// Guid.NewGuid(); this is the same "random 128-bit id, hex with dashes" on the wire.
func newUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		// crypto/rand.Read never returns an error on supported platforms; panic rather than emit a
		// predictable id if the impossible happens.
		panic("tradeledger: crypto/rand failed: " + err.Error())
	}
	b[6] = (b[6] & 0x0f) | 0x40 // version 4
	b[8] = (b[8] & 0x3f) | 0x80 // variant 10
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}
