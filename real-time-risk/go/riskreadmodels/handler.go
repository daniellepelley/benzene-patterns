package riskreadmodels

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"

	benzene "github.com/daniellepelley/benzene-go"
	"github.com/daniellepelley/benzene-go/envelope"
	"github.com/daniellepelley/benzene-go/httpstatus"
	"github.com/daniellepelley/benzene-go/wire"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
)

// storeKey is the typed DI key for the BookPositionsStore dependency.
type storeKey struct{}

// NewApp is the composition root: it registers the projection store as a singleton and the
// book:positions query handler, then builds the router pipeline - the Go equivalent of the .NET
// StartUp's ConfigureServices + Configure. The store is shared with the background projector (which
// writes to the same instance), so main() constructs it once and passes it here.
func NewApp(store *BookPositionsStore) benzene.App[struct{}] {
	return benzene.App[struct{}]{
		GetConfiguration: func() struct{} { return struct{}{} },
		ConfigureServices: func(registry *benzene.Registry, container *benzene.Container, _ struct{}) {
			benzene.AddSingleton(container, storeKey{}, func(_ *benzene.Scope) *BookPositionsStore { return store })
			if err := benzene.Register(registry, contracts.BookPositionsTopic,
				benzene.Handler[contracts.BookPositionsRequest, contracts.BookPositionsResponse](BookPositionsQueryHandler)); err != nil {
				panic(fmt.Sprintf("riskreadmodels: register book:positions handler: %v", err))
			}
		},
		Configure: func(builder *benzene.ApplicationBuilder, _ struct{}) {
			builder.UsePipeline(benzene.NewPipeline(benzene.RouterMiddleware(builder.Registry)))
		},
	}
}

// BookPositionsQueryHandler serves a book's current positions from the in-memory projection.
// Mirrors .NET's BookPositionsQueryHandler.
func BookPositionsQueryHandler(ctx context.Context, req contracts.BookPositionsRequest) benzene.Result[contracts.BookPositionsResponse] {
	if req.Book == "" {
		return benzene.BadRequest[contracts.BookPositionsResponse]("Book is required.")
	}
	scope, ok := benzene.ScopeFromContext(ctx)
	if !ok {
		return benzene.UnexpectedError[contracts.BookPositionsResponse]("no DI scope on context")
	}
	store := benzene.GetService[*BookPositionsStore](scope, storeKey{})
	return benzene.Ok(store.Query(req.Book))
}

// PositionsHTTPHandler hosts GET /books/{book}/positions.
//
// -----------------------------------------------------------------------------------------------
// benzene-go PARITY GAP (see PARITY-NOTES.md #1): route params are not bound into the request
// model, and there is no handler-side accessor for inbound headers or route params.
//
// The .NET port binds the {book} route value straight into BookPositionsRequest.Book (ASP.NET
// model binding). Go's httpbinding delivers {book} only as a "route-book" wire *header*, and the
// framework exposes NO public API for a handler to read inbound headers or route params
// (InvocationContext is stashed under an unexported context key; only the outbound
// SetResponseHeader is exported). So a normal Benzene handler registered on this route literally
// cannot see {book} - it would receive an empty request body and reject every call.
//
// The workaround does NOT bypass Benzene dispatch: it extracts book from the URL path itself, then
// dispatches the book:positions topic through the same pipeline + container as any other request,
// via envelope.DispatchTopicResult, with a synthesized JSON body {"book":"<book>"}. The
// wire.Response is then mapped to a native HTTP response exactly the way httpbinding's own
// (unexported) writeNativeResponse does. The handler and pipeline stay identical to what a real
// Lambda deployment would run; only this thin front-door adapter is bespoke.
// -----------------------------------------------------------------------------------------------
func PositionsHTTPHandler(builder *benzene.ApplicationBuilder) http.HandlerFunc {
	const prefix, suffix = "/books/", "/positions"

	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		// (a) Extract {book} from r.URL.Path (/books/{book}/positions). Reject anything that isn't
		// exactly that shape, or a book spanning multiple path segments.
		path := r.URL.Path
		if !strings.HasPrefix(path, prefix) || !strings.HasSuffix(path, suffix) {
			http.NotFound(w, r)
			return
		}
		book := path[len(prefix) : len(path)-len(suffix)]
		if book == "" || strings.Contains(book, "/") {
			http.NotFound(w, r)
			return
		}
		if unescaped, err := url.PathUnescape(book); err == nil {
			book = unescaped
		}

		// (b) Dispatch through the Benzene pipeline with a synthesized request body.
		body, err := json.Marshal(contracts.BookPositionsRequest{Book: book})
		if err != nil {
			http.Error(w, "failed to build request", http.StatusInternalServerError)
			return
		}
		resp, _ := envelope.DispatchTopicResult(
			r.Context(), builder.Pipeline, builder.Container, contracts.BookPositionsTopic, nil, string(body))

		// (c) Map the wire.Response to a native HTTP response (replicates httpbinding.writeNativeResponse).
		writeNativeResponse(w, resp)
	}
}

// writeNativeResponse mirrors httpbinding's own unexported writeNativeResponse: response headers,
// then the Benzene-status-to-HTTP-code mapping, then the body. Kept in sync with that function by
// construction (it is copied because it is not exported - itself a small facet of the parity gap
// above).
func writeNativeResponse(w http.ResponseWriter, resp wire.Response) {
	for key, value := range resp.Headers {
		w.Header().Set(key, value)
	}
	w.WriteHeader(httpstatus.ToHTTP(benzene.Status(resp.StatusCode)))
	if resp.Body != "" {
		_, _ = w.Write([]byte(resp.Body))
	}
}
