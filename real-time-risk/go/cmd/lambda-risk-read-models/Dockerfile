# AWS Lambda container image for the Risk Read Models service (Go custom runtime).
#
# Build context is real-time-risk/go/ - the whole Go module, so `go build ./cmd/lambda-risk-read-models`
# resolves the local contracts/riskreadmodels packages and the published benzene-go awslambda +
# awsdynamodb bindings.
#   docker build -f cmd/lambda-risk-read-models/Dockerfile -t rtr-go-risk-read-models:lambda .
#
# Same Go-on-provided.al2023 container pattern as cmd/lambda-trade-ledger/Dockerfile: the binary is
# the Lambda Runtime API client (awslambda.Start), named `bootstrap` on the provided.al2023 base. This
# ONE image serves both of the risk-read-models function's triggers - the DynamoDB-stream projection
# and the API Gateway query - because main.go multiplexes on the event shape (see its package doc).
FROM golang:1.24 AS build
WORKDIR /src

# Cache module downloads before copying the rest of the source.
COPY go.mod go.sum ./
RUN go mod download

COPY . .
# CGO off + GOOS=linux: a static bootstrap that runs on the provided.al2023 base with no libc.
RUN CGO_ENABLED=0 GOOS=linux go build -o /bootstrap ./cmd/lambda-risk-read-models

FROM public.ecr.aws/lambda/provided:al2023
COPY --from=build /bootstrap ${LAMBDA_RUNTIME_DIR}/bootstrap
CMD [ "bootstrap" ]
