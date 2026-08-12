# Multi-stage build for the Risk Read Models service. Build context is real-time-risk/go/ (see
# docker-compose.yml).
#   docker build -f cmd/risk-read-models/Dockerfile -t benzene-patterns-risk-read-models:local .
FROM golang:1.24 AS build
WORKDIR /src

# Cache module downloads before copying the rest of the source.
COPY go.mod go.sum ./
RUN go mod download

COPY . .
# CGO off + static build so it runs on the distroless/static base with no libc.
RUN CGO_ENABLED=0 GOOS=linux go build -o /app/risk-read-models ./cmd/risk-read-models

FROM gcr.io/distroless/static:nonroot
WORKDIR /app
COPY --from=build /app/risk-read-models /app/risk-read-models
EXPOSE 8080
ENTRYPOINT ["/app/risk-read-models"]
