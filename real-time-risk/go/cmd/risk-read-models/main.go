// Command risk-read-models hosts the Risk Read Models service on a net/http server: it runs the
// DynamoDB-Streams projector in the background and serves GET /books/{book}/positions from the
// projection. It is the Go counterpart of the .NET RiskReadModels/Program.cs.
//
// This service does NOT provision the table - the Trade Ledger owns it. The projector polls and
// retries until the table (and its stream) exists, so start order between the two services does not
// matter.
package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/dynamoclient"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/riskreadmodels"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	tableName := dynamoclient.TableName()

	dynamo, err := dynamoclient.NewDynamoDB(ctx)
	if err != nil {
		log.Fatalf("risk-read-models: build DynamoDB client: %v", err)
	}
	streams, err := dynamoclient.NewStreams(ctx)
	if err != nil {
		log.Fatalf("risk-read-models: build DynamoDB Streams client: %v", err)
	}

	// The projector and the query handler share one store instance: the poller writes to it, the
	// HTTP handler reads from it.
	store := riskreadmodels.NewBookPositionsStore()
	builder := riskreadmodels.NewApp(store).Run()

	projector := riskreadmodels.NewTradeStreamProjector(dynamo, streams, store, tableName)
	go projector.Run(ctx)

	// GET /books/{book}/positions goes through the custom dispatch adapter (see
	// PositionsHTTPHandler for why: benzene-go does not bind route params into the request model).
	mux := http.NewServeMux()
	mux.Handle("/books/", riskreadmodels.PositionsHTTPHandler(builder))

	server := &http.Server{
		Addr:              ":8080",
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Printf("risk-read-models listening on %s", server.Addr)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("risk-read-models: serve: %v", err)
		}
	}()

	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := server.Shutdown(shutdownCtx); err != nil {
		log.Printf("risk-read-models: shutdown: %v", err)
	}
	log.Print("risk-read-models stopped")
	_ = os.Stdout.Sync()
}
