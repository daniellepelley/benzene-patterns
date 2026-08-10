# Build context is real-time-risk/dotnet/ (see docker-compose.yml).
#   docker build -f RiskReadModels/Dockerfile -t benzene-patterns-risk-read-models:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish RiskReadModels/Benzene.Patterns.RealTimeRisk.RiskReadModels.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Benzene.Patterns.RealTimeRisk.RiskReadModels.dll"]
