// Command tenant is the Tenant core service of the orchestrator pattern example, in Go.
//
// A core service is "a guarded database" (docs/patterns/core-services.md): it owns one
// aggregate, exposes a small set of topics over it, and contains no cross-service business
// process. All of that lives in the signup orchestrator, which calls this service.
//
// Two topics, and the pairing matters: tenant:create is the forward action of the saga's
// first step, tenant:delete is its compensation. tenant:delete is idempotent - deleting an
// already-deleted (or never-created) tenant is a success, not an error - because a
// compensation may run after a partial failure and may be retried
// (docs/patterns/orchestrators.md, "Designing compensations").
package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"strings"
	"sync"

	benzene "github.com/daniellepelley/benzene-go"
	"github.com/daniellepelley/benzene-go/healthcheck"
	"github.com/daniellepelley/benzene-go/httpbinding"
)

// TenantStore is the port. An in-memory adapter is enough for a pattern demo; swapping in a
// real database would not touch the handlers.
type TenantStore interface {
	Create(name string) (string, bool)
	Delete(id string)
}

type inMemoryTenantStore struct {
	mu      sync.Mutex
	tenants map[string]string // id -> name
	byName  map[string]string // name -> id, so create is idempotent per company name
	seq     int
}

func newInMemoryTenantStore() *inMemoryTenantStore {
	return &inMemoryTenantStore{tenants: map[string]string{}, byName: map[string]string{}}
}

// Create returns (id, created). created is false when a tenant of that name already exists -
// the caller turns that into a conflict, which is what makes the saga's failure path
// exercisable on demand.
func (s *inMemoryTenantStore) Create(name string) (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if id, exists := s.byName[name]; exists {
		return id, false
	}
	s.seq++
	id := "tenant-" + itoa(s.seq)
	s.tenants[id] = name
	s.byName[name] = id
	return id, true
}

func (s *inMemoryTenantStore) Delete(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if name, ok := s.tenants[id]; ok {
		delete(s.byName, name)
		delete(s.tenants, id)
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	return string(b)
}

type tenantStoreKey struct{}

type createTenantRequest struct {
	CompanyName string `json:"companyName"`
}

type createTenantResponse struct {
	TenantID    string `json:"tenantId"`
	CompanyName string `json:"companyName"`
}

type deleteTenantRequest struct {
	TenantID string `json:"tenantId"`
}

type deleteTenantResponse struct {
	TenantID string `json:"tenantId"`
	Deleted  bool   `json:"deleted"`
}

func createTenantHandler(ctx context.Context, req createTenantRequest) benzene.Result[createTenantResponse] {
	if strings.TrimSpace(req.CompanyName) == "" {
		return benzene.BadRequest[createTenantResponse]("companyName is required")
	}

	scope, ok := benzene.ScopeFromContext(ctx)
	if !ok {
		return benzene.UnexpectedError[createTenantResponse]("no DI scope on context")
	}
	store := benzene.GetService[TenantStore](scope, tenantStoreKey{})

	id, created := store.Create(req.CompanyName)
	if !created {
		// A real failure the orchestrator's saga must handle - not an exception, a Result.
		return benzene.Conflict[createTenantResponse]("a tenant named '" + req.CompanyName + "' already exists")
	}

	return benzene.Created(createTenantResponse{TenantID: id, CompanyName: req.CompanyName})
}

// deleteTenantHandler is the compensation for createTenantHandler. Deliberately idempotent:
// deleting an unknown id succeeds with deleted=false rather than returning not-found, so a
// compensation that runs twice (or for a step that never completed) cannot itself fail the
// rollback and turn a clean RolledBack into a PartiallyRolledBack.
func deleteTenantHandler(ctx context.Context, req deleteTenantRequest) benzene.Result[deleteTenantResponse] {
	scope, ok := benzene.ScopeFromContext(ctx)
	if !ok {
		return benzene.UnexpectedError[deleteTenantResponse]("no DI scope on context")
	}
	store := benzene.GetService[TenantStore](scope, tenantStoreKey{})

	store.Delete(req.TenantID)
	return benzene.Ok(deleteTenantResponse{TenantID: req.TenantID, Deleted: true})
}

func newApp() benzene.App[struct{}] {
	return benzene.App[struct{}]{
		GetConfiguration: func() struct{} { return struct{}{} },
		ConfigureServices: func(registry *benzene.Registry, container *benzene.Container, _ struct{}) {
			benzene.AddSingleton(container, tenantStoreKey{}, func(_ *benzene.Scope) TenantStore {
				return newInMemoryTenantStore()
			})
			if err := benzene.Register(registry, benzene.NewTopic("tenant:create"),
				benzene.Handler[createTenantRequest, createTenantResponse](createTenantHandler)); err != nil {
				log.Fatalf("register tenant:create: %v", err)
			}
			if err := benzene.Register(registry, benzene.NewTopic("tenant:delete"),
				benzene.Handler[deleteTenantRequest, deleteTenantResponse](deleteTenantHandler)); err != nil {
				log.Fatalf("register tenant:delete: %v", err)
			}
		},
		Configure: func(builder *benzene.ApplicationBuilder, _ struct{}) {
			checks := []healthcheck.Check{
				healthcheck.NamedCheck("store", func(context.Context) healthcheck.CheckResult {
					return healthcheck.CheckResult{Status: healthcheck.StatusOk, Type: "in-memory"}
				}),
			}
			builder.UsePipeline(benzene.NewPipeline(
				healthcheck.Middleware(checks),
				benzene.RouterMiddleware(builder.Registry),
			))
		},
	}
}

// newHandler mounts only the two well-known surfaces this service needs to be reachable by
// the orchestrator: the wire-envelope endpoint (/benzene/invoke) it is actually called on,
// and health. There is deliberately no REST route table - a core service in this example is
// addressed by *topic*, not by URL, which is what lets the orchestrator call a Go service
// and a Python service through the identical client code.
func newHandler(builder *benzene.ApplicationBuilder) http.Handler {
	mux := http.NewServeMux()
	mux.Handle(httpbinding.EnvelopePath, httpbinding.EnvelopeHandler(builder))
	mux.Handle(httpbinding.HealthPath, httpbinding.Handler(builder, []httpbinding.Route{
		{Method: http.MethodGet, Path: httpbinding.HealthPath, Topic: benzene.NewTopic(healthcheck.ReservedTopic)},
	}))
	return mux
}

func main() {
	builder := newApp().Run()

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}

	log.Printf("tenant (go) listening on :%s - topics tenant:create, tenant:delete", port)
	log.Fatal(http.ListenAndServe(":"+port, newHandler(builder)))
}
