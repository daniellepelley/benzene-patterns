# Build context is real-time-risk/dotnet/ (see docker-compose.yml).
#   docker build -f MarketDataAggregator/Dockerfile -t benzene-patterns-market-data-aggregator:local .
#
# Unlike TradeLedger/RiskReadModels this build is NOT a pure nuget.org restore yet: the context also
# carries local-packages/, the folder feed holding the unreleased Benzene.Kafka.Streaming package
# (benzene-dotnet PR #17). Pack it before building - see ../nuget.config for the exact command - or
# the restore fails loudly with NU1101 rather than silently building something else.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish MarketDataAggregator/Benzene.Patterns.RealTimeRisk.MarketDataAggregator.csproj -c Release -o /app

# The plain runtime image, not aspnet: this service is a worker with no HTTP surface at all.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Benzene.Patterns.RealTimeRisk.MarketDataAggregator.dll"]
