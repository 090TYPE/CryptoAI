# Fast image: copies the host-published output onto the .NET runtime.
# Publish first:  dotnet publish CryptoAITerminal.CandleWorker -c Release -o CryptoAITerminal.CandleWorker/publish
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY publish/ ./
ENTRYPOINT ["dotnet", "CryptoAITerminal.CandleWorker.dll"]
