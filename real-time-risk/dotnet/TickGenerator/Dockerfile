# Build context is real-time-risk/dotnet/ (see docker-compose.yml).
#   docker build -f TickGenerator/Dockerfile -t benzene-patterns-tick-generator:local .
#
# A demo-only fixture standing in for a real market-data feed - see TickGenerator/Program.cs for why
# it is its own image rather than a flag inside the aggregator.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TickGenerator/Benzene.Patterns.RealTimeRisk.TickGenerator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Benzene.Patterns.RealTimeRisk.TickGenerator.dll"]
