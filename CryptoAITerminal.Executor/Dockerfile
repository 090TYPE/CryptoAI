# Fast image: copies the host-published output onto the .NET runtime.
# Publish first:  dotnet publish CryptoAITerminal.Executor -c Release -o CryptoAITerminal.Executor/publish
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY publish/ ./
ENTRYPOINT ["dotnet", "CryptoAITerminal.Executor.dll"]
