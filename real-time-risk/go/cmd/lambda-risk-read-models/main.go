// Command lambda-risk-read-models is the AWS Lambda deployment of the Risk Read Models service. It
// hosts BOTH sides of the CQRS read model in ONE Lambda function:
//
//   - the DynamoDB-Streams projection: fold each TradeBooked INSERT off the ledger table's stream
//     into the in-memory BookPositionsStore (the event-source-mapping trigger), and
//   - the query: serve GET /books/{book}/positions from that same store (the API Gateway trigger).
//
// It reuses the SAME riskreadmodels store and query handler the local net/http host
// (cmd/risk-read-models) runs - only the hosting changes.
//
// ================================================================================================
// WHY A HAND-WRITTEN MULTIPLEXER (and why risk-read-models is still ONE function for every language)
// ================================================================================================
// benzene-go's awslambda.Start takes a SINGLE awslambda.HandlerFunc, and benzene-go is otherwise
// one-trigger-per-binary: awslambda.HTTPHandler adapts API Gateway events, awsdynamodb.Handler
// adapts DynamoDB-stream events, and each is its own HandlerFunc (PARITY-FINDINGS.md §3.4). .NET, TS
// and Python instead ship a host that dispatches on the event SHAPE inside one handler.
//
// Rather than split Go into two functions (which would force the shared Terraform to special-case a
// Go-only "two images, one trigger each" layout), we recover the multi-trigger shape ourselves: the
// multiplex HandlerFunc below inspects the raw invocation JSON and delegates to the right binding.
// AWS delivers a DynamoDB-stream event and an API Gateway v2 event as distinguishable JSON shapes,
// so a cheap structural probe tells them apart:
//
//   - DynamoDB stream: a top-level "Records" array whose records carry "eventSource":"aws:dynamodb"
//     and an "eventName" (INSERT/MODIFY/REMOVE)             -> awsdynamodb.Handler(builder).
//   - API Gateway HTTP API v2: a "requestContext" object plus "routeKey"/"rawPath" and no such
//     Records array                                          -> the HTTP query adapter below.
//
// The result is that risk-read-models is a single Lambda function in Go too, so the shared stack
// (one risk-read-models function, one stream event-source mapping AND one API route both pointing at
// it) is uniform across ALL languages - no Go-specific variant. This multiplexer is the only bespoke
// piece; the store, the query handler, and the projection math are exactly the local ones.
//
// ================================================================================================
// HONEST LIMITATION: an in-memory read model does not survive Lambda's horizontal scaling
// ================================================================================================
// A single warm Lambda instance both projects (stream trigger) and serves (API trigger) into ONE
// process-local BookPositionsStore, so within that instance a query sees what that instance
// projected. But Lambda scales to MANY concurrent instances, each with its OWN empty store, and AWS
// routes a stream shard and an API request to whichever instance it likes. So a query can hit an
// instance that never received the projecting record and return stale/empty positions, and a cold
// start starts from an empty store. This is fine for the local Compose slice (one process) and for
// proving the wiring, but it is NOT a correct production read model. The production answer is a
// SHARED store (project into DynamoDB/ElastiCache; the query reads from it) - out of scope for this
// slice, which exists to prove the hosting shape. Documented in go/PARITY-NOTES.md §6.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"

	benzene "github.com/daniellepelley/benzene-go"
	"github.com/daniellepelley/benzene-go/awsdynamodb"
	"github.com/daniellepelley/benzene-go/awslambda"
	"github.com/daniellepelley/benzene-go/envelope"
	"github.com/daniellepelley/benzene-go/httpbinding"
	"github.com/daniellepelley/benzene-go/httpstatus"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/dynamoclient"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/riskreadmodels"
)

func main() {
	tableName := dynamoclient.TableName()

	// One store, shared by both triggers within this instance (see the "in-memory" limitation above).
	// riskreadmodels.NewApp registers the book:positions query handler against it; we additionally
	// register the stream-projection handler on the same registry below.
	store := riskreadmodels.NewBookPositionsStore()
	builder := riskreadmodels.NewApp(store).Run()

	// The stream projection as a Benzene handler. On AWS the DynamoDB-stream event-source mapping
	// delivers change records to awsdynamodb.Handler, which dispatches each one on topic
	// "{tableName}:{eventName}" (e.g. "rtr-go-trades:INSERT") with the record's NewImage already
	// unmarshalled from AttributeValue format into plain JSON. We register a handler for exactly that
	// topic that folds the record into the shared store - the Lambda-hosted analogue of the local
	// TradeStreamProjector's apply step, minus the manual stream polling AWS now does for us.
	streamTopic := benzene.NewTopic(tableName + ":" + insertEventName)
	if err := benzene.Register(builder.Registry, streamTopic,
		benzene.Handler[streamImage, streamAck](projectionHandler(store))); err != nil {
		log.Fatalf("lambda-risk-read-models: register %q handler: %v", streamTopic, err)
	}

	// The two inner bindings the multiplexer delegates to. httpRoutes reuses the same route table the
	// net/http host and awslambda.HTTPHandler use; see queryRoutes for the route-param caveat.
	streamHandler := awsdynamodb.Handler(builder)
	httpHandler := httpQueryHandler(builder, queryRoutes())

	awslambda.Start(multiplex(streamHandler, httpHandler))
}

// insertEventName is the DynamoDB-stream change type the append-only ledger emits. The ledger only
// ever appends, so INSERT is the only event a TradeBooked record arrives as; MODIFY/REMOVE never
// happen, which is also why the shared Terraform filters the event-source mapping to INSERT
// (deploy/terraform/lambda.tf). A stray non-INSERT would route to a topic with no handler and be
// reported as a batch-item failure (retried) - acceptable given it cannot occur here, and it matches
// the local projector and the .NET port, which likewise only handle INSERT.
const insertEventName = "INSERT"

// -----------------------------------------------------------------------------------------------
// The multiplexer.
// -----------------------------------------------------------------------------------------------

// eventProbe carries just the fields needed to classify an invocation by shape - a structural sniff,
// not a full decode (each inner binding re-parses the event into its own complete type).
type eventProbe struct {
	// Records is the DynamoDB-stream (and other batch sources') top-level array. We only read each
	// record's eventSource to confirm it is a DynamoDB stream.
	Records []struct {
		EventSource string `json:"eventSource"`
	} `json:"Records"`
	// RequestContext / RouteKey / RawPath are the API Gateway HTTP API v2 markers.
	RequestContext json.RawMessage `json:"requestContext"`
	RouteKey       string          `json:"routeKey"`
	RawPath        string          `json:"rawPath"`
}

// multiplex returns the single HandlerFunc awslambda.Start drives: it classifies each invocation by
// event shape and delegates to the DynamoDB-stream binding or the HTTP query adapter. An
// unrecognised event is a Go error (the sanctioned Lambda Runtime API error shape) - it is a
// misconfiguration (a trigger wired to this function that we do not host), not an application
// outcome.
func multiplex(streamHandler, httpHandler awslambda.HandlerFunc) awslambda.HandlerFunc {
	return func(ctx context.Context, event json.RawMessage) (json.RawMessage, error) {
		var probe eventProbe
		if err := json.Unmarshal(event, &probe); err != nil {
			return nil, fmt.Errorf("lambda-risk-read-models: malformed invocation event: %w", err)
		}

		if len(probe.Records) > 0 && probe.Records[0].EventSource == "aws:dynamodb" {
			return streamHandler(ctx, event)
		}
		if probe.RouteKey != "" || probe.RawPath != "" || len(probe.RequestContext) > 0 {
			return httpHandler(ctx, event)
		}
		return nil, fmt.Errorf(
			"lambda-risk-read-models: unrecognised event shape (not a DynamoDB stream or API Gateway v2 event)")
	}
}

// -----------------------------------------------------------------------------------------------
// Stream projection handler (the DynamoDB-stream trigger).
// -----------------------------------------------------------------------------------------------

// streamImage is the request shape for the projection handler: the fields of the ledger event item
// as awsdynamodb delivers them. awsdynamodb.Handler converts the record's NewImage from DynamoDB
// AttributeValue format into plain JSON, so the item's `eventType`/`payload` string attributes
// arrive as JSON strings and its `version` number attribute as a JSON number - matching the item
// shape eventstore writes (pk/version/eventType/payload/timestamp).
type streamImage struct {
	EventType string `json:"eventType"`
	// Payload is the TradeBooked event body, itself a JSON string (a DynamoDB S attribute), so it is
	// unmarshalled a second time into a TradeBooked below - exactly what the local projector does.
	Payload string `json:"payload"`
	Version int64  `json:"version"`
}

// streamAck is the (empty) success payload. The projection has no response body; the awsdynamodb
// binding only needs the result's success flag to decide ack-vs-retry for the batch.
type streamAck struct{}

// projectionHandler folds one TradeBooked INSERT into the shared store, reusing store.Apply (so the
// net-position/realized-cash math is the SAME code the local projector and the query read). It
// mirrors riskreadmodels.TradeStreamProjector.apply, minus the AttributeValue decoding awsdynamodb
// has already done. A non-TradeBooked record (there are none today) is a successful no-op; an
// unparseable payload is a BadRequest so the batch reports it for redelivery rather than silently
// dropping a ledger event.
func projectionHandler(store *riskreadmodels.BookPositionsStore) benzene.Handler[streamImage, streamAck] {
	return func(_ context.Context, image streamImage) benzene.Result[streamAck] {
		if image.EventType != contracts.TradeBookedEventType {
			return benzene.Ok(streamAck{})
		}
		var trade contracts.TradeBooked
		if err := json.Unmarshal([]byte(image.Payload), &trade); err != nil {
			return benzene.BadRequest[streamAck](
				fmt.Sprintf("unparseable TradeBooked payload at version %d: %v", image.Version, err))
		}
		store.Apply(trade, image.Version)
		log.Printf("lambda-risk-read-models: projected trade %s into book %s at version %d",
			trade.TradeId, trade.Book, image.Version)
		return benzene.Ok(streamAck{})
	}
}

// -----------------------------------------------------------------------------------------------
// HTTP query adapter (the API Gateway trigger) - the route-param workaround, ported to Lambda.
// -----------------------------------------------------------------------------------------------
//
// benzene-go PARITY GAP (see PARITY-NOTES.md §2), UNCHANGED by awslambda.HTTPHandler. HTTPHandler
// uses the SAME httpbinding.RouteTable as the net/http binding, and binds a captured {book} route
// param only as a "route-book" wire HEADER - which a Benzene handler still has no public API to
// read. So awslambda.HTTPHandler(builder, routes) alone would dispatch book:positions with an empty
// request body and the query would reject every call, exactly as the local net/http binding does.
// HTTPHandler is therefore no better at route params than httpbinding - the gap is in the core, not
// the transport.
//
// So this adapter reproduces the local PositionsHTTPHandler workaround, but for the API Gateway v2
// event instead of an *http.Request: it (a) matches the event's method+path against the SAME route
// table to capture {book} (rather than string-slicing the path), then (b) dispatches the
// book:positions topic through the SAME Benzene pipeline via envelope.DispatchTopicResult with a
// synthesized {"book":"<book>"} body, and (c) marshals the wire.Response into the API Gateway v2
// response shape. Benzene dispatch is not bypassed - the handler and pipeline are exactly what the
// query serves anywhere; only this thin front door is bespoke.

// queryRoutes is the route table for the query. Kept as its own function so the multiplexer and any
// future test share one definition; it is the GET counterpart of the ledger's POST /trades route.
func queryRoutes() []httpbinding.Route {
	return []httpbinding.Route{
		{Method: "GET", Path: "/books/{book}/positions", Topic: contracts.BookPositionsTopic},
	}
}

// apiGatewayV2Event carries the API Gateway HTTP API v2 fields this adapter needs: the method (nested
// under requestContext.http) and the path (rawPath). This is the same subset awslambda.HTTPHandler
// reads; we parse it here because we need the captured route param, which HTTPHandler keeps to
// itself (as the unreadable "route-book" header).
type apiGatewayV2Event struct {
	RawPath        string `json:"rawPath"`
	RequestContext struct {
		HTTP struct {
			Method string `json:"method"`
			Path   string `json:"path"`
		} `json:"http"`
	} `json:"requestContext"`
}

// apiGatewayV2Response is the API Gateway HTTP API v2 (payload format 2.0) response shape - the same
// shape awslambda.HTTPHandler emits for a v2 event.
type apiGatewayV2Response struct {
	StatusCode int               `json:"statusCode"`
	Headers    map[string]string `json:"headers,omitempty"`
	Body       string            `json:"body"`
}

// httpQueryHandler builds the HTTP branch's HandlerFunc over the query's route table.
func httpQueryHandler(builder *benzene.ApplicationBuilder, routes []httpbinding.Route) awslambda.HandlerFunc {
	table := httpbinding.NewRouteTable(routes)

	return func(ctx context.Context, event json.RawMessage) (json.RawMessage, error) {
		var req apiGatewayV2Event
		if err := json.Unmarshal(event, &req); err != nil {
			return nil, fmt.Errorf("lambda-risk-read-models: malformed API Gateway event: %w", err)
		}
		method := req.RequestContext.HTTP.Method
		path := req.RequestContext.HTTP.Path
		if path == "" {
			path = req.RawPath
		}

		topic, params, ok := table.Match(method, path)
		if !ok {
			return marshalV2(httpstatus.ToHTTP(benzene.StatusNotFound), nil, "not found")
		}

		// Extract {book} from the captured route params - the piece HTTPHandler would hide in the
		// unreadable "route-book" header - and dispatch the query with a synthesized body so the
		// handler's plain request.Book is populated. This is the route-param workaround, verbatim in
		// spirit to the local PositionsHTTPHandler.
		body, err := json.Marshal(contracts.BookPositionsRequest{Book: params["book"]})
		if err != nil {
			return marshalV2(httpstatus.ToHTTP(benzene.StatusUnexpectedError), nil, "failed to build request")
		}
		resp, _ := envelope.DispatchTopicResult(ctx, builder.Pipeline, builder.Container, topic, nil, string(body))

		return marshalV2(httpstatus.ToHTTP(benzene.Status(resp.StatusCode)), resp.Headers, resp.Body)
	}
}

// marshalV2 renders an API Gateway HTTP API v2 response, mirroring awslambda.HTTPHandler's own
// (unexported) marshalResponse for the v2 shape.
func marshalV2(status int, headers map[string]string, body string) (json.RawMessage, error) {
	return json.Marshal(apiGatewayV2Response{StatusCode: status, Headers: headers, Body: body})
}
