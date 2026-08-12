// Command lambda-trade-ledger is the AWS Lambda deployment of the Trade Ledger service: the same
// tradeledger.NewApp(store) command handler the local net/http host (cmd/trade-ledger) runs, but
// hosted by benzene-go's awslambda binding behind an API Gateway HTTP API (payload format 2.0)
// instead of a standalone HTTP server. POST /trades books a trade as an immutable event appended to
// the DynamoDB event log.
//
// -----------------------------------------------------------------------------------------------
// SAME HANDLER, DIFFERENT HOST. This entrypoint deliberately shares tradeledger.NewApp and
// eventstore.NewDynamoStore with cmd/trade-ledger - the composition root, the command handler, the
// event store, and the wire contract (topic + JSON body) are identical. Only the hosting changes:
//
//   - cmd/trade-ledger        -> httpbinding.Handler(builder, routes) on a net/http server (Docker Compose).
//   - cmd/lambda-trade-ledger -> awslambda.Start(awslambda.HTTPHandler(builder, routes)) (AWS Lambda).
//
// awslambda.HTTPHandler matches routes with the same httpbinding.RouteTable the net/http binding
// uses and dispatches through the same envelope pipeline, so a POST /trades that works locally works
// identically here - see benzene-go/awslambda/http.go.
//
// TWO DIVERGENCES FROM THE COMPOSE HOST, both because the Lambda runs against real AWS:
//
//  1. No table provisioning. On AWS the ledger table (with its stream) is pre-provisioned by the
//     shared Terraform (real-time-risk/deploy/terraform/dynamodb.tf), so this entrypoint does NOT
//     call tradeledger.EnsureTradesTableExists - that idempotent CreateTable-with-retries is the
//     Compose path's job (DynamoDB Local has no external provisioner). Calling it here would also
//     demand dynamodb:CreateTable on the execution role, which the deploy IAM deliberately does not
//     grant.
//  2. No endpoint override. dynamoclient.NewDynamoDB honours DYNAMODB_SERVICE_URL only for the
//     Compose path; unset (the Lambda case) it falls back to the default credential/region chain -
//     i.e. the function's execution role. Nothing Lambda-specific to do here; it just works.
//
// The binary IS the Lambda runtime client: awslambda.Start speaks the Lambda Runtime API directly
// (a hand-rolled custom-runtime loop, no aws-lambda-go dependency), so it is deployed on the
// provided.al2023 base image as `bootstrap` - see this command's Dockerfile.
package main

import (
	"context"
	"log"
	"net/http"

	"github.com/daniellepelley/benzene-go/awslambda"
	"github.com/daniellepelley/benzene-go/httpbinding"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/dynamoclient"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/eventstore"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/tradeledger"
)

func main() {
	ctx := context.Background()
	tableName := dynamoclient.TableName()

	// No DYNAMODB_SERVICE_URL on AWS, so this resolves to the default credential/region chain (the
	// Lambda execution role). The table is pre-provisioned by Terraform - see the package doc above.
	client, err := dynamoclient.NewDynamoDB(ctx)
	if err != nil {
		log.Fatalf("lambda-trade-ledger: build DynamoDB client: %v", err)
	}

	store := eventstore.NewDynamoStore(client, tableName)
	builder := tradeledger.NewApp(store).Run()

	routes := []httpbinding.Route{
		{Method: http.MethodPost, Path: "/trades", Topic: contracts.BookTradeTopic},
	}

	// Start never returns under normal operation: it is the Lambda Runtime API bootstrap loop.
	awslambda.Start(awslambda.HTTPHandler(builder, routes))
}
