# Fast image: copies the host-published output onto the ASP.NET runtime.
# Publish first:  dotnet publish CryptoAITerminal.Server.Api -c Release -o CryptoAITerminal.Server.Api/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ ./
ENTRYPOINT ["dotnet", "CryptoAITerminal.Server.Api.dll"]
