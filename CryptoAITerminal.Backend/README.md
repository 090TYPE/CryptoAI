# CryptoAITerminal.Backend

Lightweight key-proxy + license gate for selling the terminal to many users. Keeps paid API
keys **on the server** (never shipped in the desktop binary), gates every call behind a valid
license token, rate-limits per license, and caches upstream data so 100+ clients don't each hit
the paid APIs.

Licenses reuse the **existing** RSA token format (issued by `CryptoAITerminal.LicenseBot`,
validated offline by the app). This backend verifies the same token with the public key — no new
licensing system, no private key on the server.

## Run locally

```bash
cd CryptoAITerminal.Backend
# provide the real upstream keys (env vars override appsettings):
export ANTHROPIC_API_KEY=sk-ant-...
export COVALENT_API_KEY=cqt_...
dotnet run
# → http://localhost:5xxx  (see console for the port)
```

The license public key is already in `appsettings.json` (same one the app embeds). Override with
`LICENSE_PUBLIC_KEY_PEM` if you rotate keys.

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET  | `/health` | Liveness check |
| POST | `/api/license/verify` | Online license check (body = raw token). Returns validity/state. |
| POST | `/api/ai/message` | Proxies Anthropic Messages API with the server key. Body = a normal `/v1/messages` payload. |
| GET  | `/api/portfolio/{chainId}/{address}` | Proxies Covalent `balances_v2` (cached 30s). |

All `/api/*` calls (except `license/verify`) require an `X-License:` header with a valid token.
Responses: `401` invalid license, `402` expired, `429` rate-limited, `503` upstream key not set.

## Wiring the desktop app to it

Point the app's AI + portfolio calls at this backend instead of the vendor APIs, and send the
user's license token in `X-License`. Then you ship **zero** vendor keys in the client, and every
request is metered per paying customer.

## Deploy later (not local)

The project is a plain ASP.NET Core app — `dotnet publish` and run behind any reverse proxy, or
add a Dockerfile when you pick a host (Hetzner/DO/etc.). Put the real keys in the host's env, not
in `appsettings.json`.
