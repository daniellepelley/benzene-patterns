// Command trade-ledger hosts the Trade Ledger service on a net/http server: POST /trades books a
// trade as an immutable event appended to the DynamoDB event log. It is the Go counterpart of the
// .NET TradeLedger/Program.cs.
//
// This service OWNS the ledger table, so it provisions it (idempotently, with retries for the
// window right after `docker compose up`) before accepting any requests - there is no separate
// init container.
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

	"github.com/daniellepelley/benzene-go/httpbinding"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/dynamoclient"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/eventstore"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/tradeledger"
)

func main() {
	ctx := context.Background()
	tableName := dynamoclient.TableName()

	client, err := dynamoclient.NewDynamoDB(ctx)
	if err != nil {
		log.Fatalf("trade-ledger: build DynamoDB client: %v", err)
	}

	// Provision the table (with its stream) before serving - see EnsureTradesTableExists.
	if err := tradeledger.EnsureTradesTableExists(ctx, client, tableName); err != nil {
		log.Fatalf("trade-ledger: provision %q table: %v", tableName, err)
	}

	store := eventstore.NewDynamoStore(client, tableName)
	builder := tradeledger.NewApp(store).Run()

	routes := []httpbinding.Route{
		{Method: http.MethodPost, Path: "/trades", Topic: contracts.BookTradeTopic},
	}
	server := &http.Server{
		Addr:              ":8080",
		Handler:           httpbinding.Handler(builder, routes),
		ReadHeaderTimeout: 5 * time.Second,
	}
	serve(server)
}

// serve runs server until SIGINT/SIGTERM, then drains in-flight requests - the same graceful
// shutdown shape as benzene-go's http-helloworld example.
func serve(server *http.Server) {
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	go func() {
		log.Printf("trade-ledger listening on %s", server.Addr)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("trade-ledger: serve: %v", err)
		}
	}()

	<-ctx.Done()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := server.Shutdown(shutdownCtx); err != nil {
		log.Printf("trade-ledger: shutdown: %v", err)
	}
	log.Print("trade-ledger stopped")
	_ = os.Stdout.Sync()
}
