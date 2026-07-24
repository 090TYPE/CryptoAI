# Self-contained multi-stage build. Context is the repo root (see docker-compose.yml),
# so sibling project references (Server.Data, Gateway.DEX, WhaleTracker) resolve during publish.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CryptoAITerminal.CandleWorker/CryptoAITerminal.CandleWorker.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "CryptoAITerminal.CandleWorker.dll"]
