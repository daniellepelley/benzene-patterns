package riskreadmodels_test

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/contracts"
	"github.com/daniellepelley/benzene-patterns/real-time-risk/go/riskreadmodels"
)

// The custom GET adapter extracts {book} from the URL path, dispatches book:positions through the
// Benzene pipeline, and returns the projection as camelCase JSON - the whole route-param workaround
// end to end (see PositionsHTTPHandler / PARITY-NOTES.md #1).
func TestPositionsHTTPHandler_ExtractsBookAndProjects(t *testing.T) {
	store := riskreadmodels.NewBookPositionsStore()
	store.Apply(contracts.TradeBooked{Book: "desk-a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 100, Price: 150.25}, 1)

	builder := riskreadmodels.NewApp(store).Run()
	handler := riskreadmodels.PositionsHTTPHandler(builder)

	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/books/desk-a/positions", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body = %s", rec.Code, rec.Body.String())
	}

	// Assert on the raw camelCase keys the shared black-box test relies on.
	var raw struct {
		Book                    string `json:"book"`
		ProjectedThroughVersion int64  `json:"projectedThroughVersion"`
		Positions               []struct {
			Symbol      string  `json:"symbol"`
			NetQuantity float64 `json:"netQuantity"`
		} `json:"positions"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &raw); err != nil {
		t.Fatalf("decode body: %v; body = %s", err, rec.Body.String())
	}
	if raw.Book != "desk-a" || raw.ProjectedThroughVersion != 1 {
		t.Fatalf("got book=%q projectedThroughVersion=%d, want desk-a / 1", raw.Book, raw.ProjectedThroughVersion)
	}
	if len(raw.Positions) != 1 || raw.Positions[0].Symbol != "AAPL" || raw.Positions[0].NetQuantity != 100 {
		t.Errorf("positions = %+v, want one AAPL @ netQuantity 100", raw.Positions)
	}
}

// A URL-encoded book segment (e.g. a book id with a space) is decoded before dispatch.
func TestPositionsHTTPHandler_DecodesEscapedBook(t *testing.T) {
	store := riskreadmodels.NewBookPositionsStore()
	store.Apply(contracts.TradeBooked{Book: "desk a", Symbol: "AAPL", Side: contracts.Buy, Quantity: 1, Price: 1}, 1)

	handler := riskreadmodels.PositionsHTTPHandler(riskreadmodels.NewApp(store).Run())

	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/books/desk%20a/positions", nil))

	var got contracts.BookPositionsResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode body: %v", err)
	}
	if got.Book != "desk a" || got.ProjectedThroughVersion != 1 {
		t.Errorf("got = %+v, want book %q version 1", got, "desk a")
	}
}

// An unknown book projects to an empty, zero-version result (200, not 404) - so a client polling
// for eventual consistency sees projectedThroughVersion 0 rather than an error, exactly like the
// smoke test's warmup read.
func TestPositionsHTTPHandler_UnknownBookIsEmpty200(t *testing.T) {
	handler := riskreadmodels.PositionsHTTPHandler(riskreadmodels.NewApp(riskreadmodels.NewBookPositionsStore()).Run())

	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/books/warmup/positions", nil))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body = %s", rec.Code, rec.Body.String())
	}
	var got contracts.BookPositionsResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("decode body: %v", err)
	}
	if got.Book != "warmup" || len(got.Positions) != 0 || got.ProjectedThroughVersion != 0 {
		t.Errorf("got = %+v, want empty zero-version result", got)
	}
}

// A path that isn't the /books/{book}/positions shape is a 404.
func TestPositionsHTTPHandler_MalformedPathIs404(t *testing.T) {
	handler := riskreadmodels.PositionsHTTPHandler(riskreadmodels.NewApp(riskreadmodels.NewBookPositionsStore()).Run())

	for _, path := range []string{"/books//positions", "/books/desk-a/extra/positions", "/books/desk-a"} {
		rec := httptest.NewRecorder()
		handler.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))
		if rec.Code != http.StatusNotFound {
			t.Errorf("path %q: status = %d, want 404", path, rec.Code)
		}
	}
}

// Only GET is served; other methods are 405.
func TestPositionsHTTPHandler_NonGetIs405(t *testing.T) {
	handler := riskreadmodels.PositionsHTTPHandler(riskreadmodels.NewApp(riskreadmodels.NewBookPositionsStore()).Run())

	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, httptest.NewRequest(http.MethodPost, "/books/desk-a/positions", nil))
	if rec.Code != http.StatusMethodNotAllowed {
		t.Errorf("status = %d, want 405", rec.Code)
	}
}
