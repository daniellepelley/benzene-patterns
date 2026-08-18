# Build context is real-time-risk/dotnet/ (see docker-compose.yml) - this service is an ordinary
# NuGet consumer, so unlike benzene-dotnet's own example Dockerfiles it does not need the whole
# benzene-dotnet source tree in its build context.
#   docker build -f PricingService/Dockerfile -t benzene-patterns-pricing:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish PricingService/Benzene.Patterns.RealTimeRisk.PricingService.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# HTTP/2 with no TLS (h2c). A gRPC service normally runs over TLS, but inside a Compose network with
# no certificates to distribute, plaintext is what lets `grpcurl -plaintext` reach it in one command.
# Kestrel needs telling explicitly: its default protocol for a plaintext endpoint is HTTP/1.1, which
# a gRPC client cannot use at all.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_KESTREL__ENDPOINTDEFAULTS__PROTOCOLS=Http2
EXPOSE 8080
ENTRYPOINT ["dotnet", "Benzene.Patterns.RealTimeRisk.PricingService.dll"]
