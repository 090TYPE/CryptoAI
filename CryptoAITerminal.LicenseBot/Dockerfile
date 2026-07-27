# Self-contained multi-stage build. Context is the repo root (see docker-compose.yml).
#
# The bot is a console app, not a web app, so this lands on the runtime image rather than aspnet:
# it opens no listening socket at all. Telegram is reached by long polling and Crypto Pay by
# outbound HTTPS, which is the whole reason selling licences needs no public endpoint, no webhook
# and no inbound firewall rule.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CryptoAITerminal.LicenseBot/CryptoAITerminal.LicenseBot.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./

# SQLite order history lives on a volume, never in the image layer: it is the record of money
# already taken from customers, and a rebuild must not roll it back. BOT_DB_PATH points here.
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "CryptoAITerminal.LicenseBot.dll"]
