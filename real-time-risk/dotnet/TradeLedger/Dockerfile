# Build context is real-time-risk/dotnet/ (see docker-compose.yml) - this service is an ordinary
# NuGet consumer, so unlike benzene-dotnet's own example Dockerfiles it does not need the whole
# benzene-dotnet source tree in its build context.
#   docker build -f TradeLedger/Dockerfile -t benzene-patterns-trade-ledger:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish TradeLedger/Benzene.Patterns.RealTimeRisk.TradeLedger.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Benzene.Patterns.RealTimeRisk.TradeLedger.dll"]
