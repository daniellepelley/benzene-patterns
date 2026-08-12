# AWS Lambda container image for the Trade Ledger service (Go custom runtime).
#
# Build context is real-time-risk/go/ - the whole Go module, so `go build ./cmd/lambda-trade-ledger`
# resolves the local contracts/eventstore/tradeledger packages and the published benzene-go awslambda
# binding.
#   docker build -f cmd/lambda-trade-ledger/Dockerfile -t rtr-go-trade-ledger:lambda .
#
# This is the container form of AWS's documented "Go on the provided.al2023 runtime" pattern
# (https://docs.aws.amazon.com/lambda/latest/dg/go-image.html): Go has no managed Lambda runtime, so
# the binary IS the runtime client - benzene-go's awslambda.Start speaks the Lambda Runtime API
# itself. The binary must be named `bootstrap` and live on the provided.al2023 base image; the base's
# ENTRYPOINT execs ${LAMBDA_RUNTIME_DIR}/bootstrap, so CMD names it.
FROM golang:1.24 AS build
WORKDIR /src

# Cache module downloads before copying the rest of the source.
COPY go.mod go.sum ./
RUN go mod download

COPY . .
# CGO off + GOOS=linux: a static bootstrap that runs on the provided.al2023 base with no libc.
RUN CGO_ENABLED=0 GOOS=linux go build -o /bootstrap ./cmd/lambda-trade-ledger

FROM public.ecr.aws/lambda/provided:al2023
# ${LAMBDA_RUNTIME_DIR} is /var/runtime on this base; the base image's ENTRYPOINT runs the file named
# by CMD from there. Deploy the binary as `bootstrap`.
COPY --from=build /bootstrap ${LAMBDA_RUNTIME_DIR}/bootstrap
CMD [ "bootstrap" ]
