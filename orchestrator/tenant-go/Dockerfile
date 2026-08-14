FROM golang:1.24 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -o /out/tenant .

FROM gcr.io/distroless/static-debian12
COPY --from=build /out/tenant /tenant
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["/tenant"]
