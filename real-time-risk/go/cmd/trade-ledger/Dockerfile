# Multi-stage build for the Trade Ledger service. Build context is real-time-risk/go/ (see
# docker-compose.yml) - the whole Go module, so `go build ./cmd/trade-ledger` resolves the local
# contracts/eventstore/tradeledger packages.
#   docker build -f cmd/trade-ledger/Dockerfile -t benzene-patterns-trade-ledger:local .
FROM golang:1.24 AS build
WORKDIR /src

# Cache module downloads before copying the rest of the source.
COPY go.mod go.sum ./
RUN go mod download

COPY . .
# CGO off + static build so it runs on the distroless/static base with no libc.
RUN CGO_ENABLED=0 GOOS=linux go build -o /app/trade-ledger ./cmd/trade-ledger

FROM gcr.io/distroless/static:nonroot
WORKDIR /app
COPY --from=build /app/trade-ledger /app/trade-ledger
EXPOSE 8080
ENTRYPOINT ["/app/trade-ledger"]
