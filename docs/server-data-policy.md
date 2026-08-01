# Where each piece of data lives

The rule for deciding whether something touches the server at all, and if it does, how it is
cached. Written down because the wrong default is expensive in both directions: proxying what the
client could fetch itself wastes egress and puts our IP on the critical path, while letting the
client fetch what needs our key leaks the key.

## The decision rule

Route it through the server **only if at least one of these is true**:

1. **It must be collected while the user's PC is off.** 24/7 candle history, price alerts.
2. **It needs a key we own.** Birdeye, CoinGecko Pro, Covalent, Glassnode, Coinglass, explorers,
   Anthropic, OpenAI.
3. **It is the same for many users and costs real money or time to produce.** Computing once and
   serving everyone is the whole point — AI digests, token risk scores.
4. **It must be aggregated over a window longer than one session.** Derived timeframes, on-chain
   trends.

If none apply: **the client does it, and the server is not involved.**

## A — Server collects, stores and shares

Cached in `SharedResponseCache` with a TTL matched to the collector cadence. One database read per
TTL regardless of how many terminals ask. See `SharedEndpoints.cs`.

| Data | Source | Why the server | Endpoint |
|---|---|---|---|
| DEX 1m OHLCV + 5m/15m/1h/4h/1d | GeckoTerminal | 24/7 presence; PCs are off | `/api/dex/candles` |
| Token metadata, security, holders, deployer | GoPlus / Honeypot / RugCheck / Moralis / explorers | our keys | `/api/dex/token/…` |
| AI score, AI anomaly | our Anthropic key | our key + identical for all users | in token detail |
| AI digests | our Anthropic key | one model call read by everyone | `/api/digests` |
| News, sentiment | CryptoPanic + RSS, fear&greed | our key, aggregation | `/api/news`, `/api/sentiment` |
| Gas per chain | explorers | our keys | `/api/gas` |
| On-chain metrics | Glassnode / CoinMetrics | our keys | `/api/onchain` |
| Whale transfers | explorers | our keys | `/api/whales` |
| Liquidations | Coinglass | our key | `/api/liquidations` |
| Price alerts | our own watcher | must fire with the PC off | `/api/alerts` |

## B — Client fetches directly, server NOT involved

None of the four conditions apply, so routing these through the server would only add latency,
egress cost and a dependency on us being up.

- **CEX public market data** — klines, tickers, order books, and the trade/depth WebSocket streams
  from Binance, Bybit, OKX, KuCoin, spot and futures. The desktop already has a gateway per
  exchange, the endpoints need no key, and nothing needs aggregating. **Do not proxy these.**
- **Indicators and analytics over data the client already holds** — RSI/SMA, chart rendering,
  backtests, Monte Carlo. Shipping candles to the server to compute a moving average is pure loss.
- **The user's own wallet reads against their own RPC**, when they have configured one.
- **Anything the user's own exchange API key can read** — balances, their positions, their order
  history. Their key, their call. The server only enters when *we* must execute (see D).

## Client audit — measured against the rule above

### Already correct: AI routing

`ChatClient.ServerBaseUrl` (set at startup from `ServerEndpoint.ResolveBaseUrl()`) reroutes
`ChatClient` **and** both agent runners to `/api/ai/message` / `/api/ai/openai`, and
`ChatClientServerRoutingTests` / `AgentRunnerServerRoutingTests` pin it. The client-held
`ANTHROPIC_API_KEY` in `CredentialsService` is the bring-your-own-key path for an *unbound*
terminal, which is the intended fallback. **Do not change this.**

### Switched: client now reads from the server, with the direct path kept as fallback

Each duplicated category A work. A hundred terminals polling the same providers means a hundred
times the quota for identical data, and it forces users to supply keys for providers we already pay
for. Each now tries `ServerDataClient` first when bound and falls through to its untouched direct
fetch on null, so an unbound terminal behaves exactly as before.

| Client service | Reads | Status |
|---|---|---|
| `NewsFeedService` | `/api/news` | ✅ switched |
| `SentimentService` | `/api/sentiment` (fear & greed only) | ✅ switched |
| `GasMonitorService` | `/api/gas` | ✅ switched |
| `GasOracleService` | `/api/gas` | ✅ switched |

`SentimentService`'s long/short ratio and open interest stay direct in both modes — they are Binance
futures data with no server endpoint, i.e. category B.

### Examined and deliberately NOT switched

An earlier revision of this document listed these as violations. That was wrong: it was written from
file names and a URL grep rather than from the code. Reading each one shows a switch would break it.
Recorded here so the mistake is not repeated.

| Client service | Why it must stay direct |
|---|---|
| `OnChainMetricsService` | The server's `onchain_metrics` and the client's metric set are **disjoint** — no field-level overlap. A switch yields an all-null snapshot and a visibly broken On-Chain tab. |
| `LiquidationDataService` | The client model is keyed on **price**; `liquidations` has no price column (`db/003_more_data.sql:55-63`). No mapping can bridge it. |
| `WhaleTokenEnricher` | Needs a price-change field. `WhaleTx` has none. |
| `DexTrendingService` | Its job is **discovery** — which tokens are hot right now. `/api/dex/tokens` requires ids the caller does not yet have and 400s on an empty list; `/api/watchlist` only knows what the user already picked. |
| `DexExchangeDataService` | Venue-level perp data: per-venue market lists, funding rates, max leverage, venue 24h volume. `TokenDetail` carries none of it. Wrong data domain. |
| `DexCandleBuilder` | Pure computation over an in-memory list. No fetch to reroute. |
| `DexRefreshPolicy` | Pure policy — one enum and one string check. |
| `DexWatchlistStore` | Would duplicate `FavoritesSyncService`, which already pushes and pulls `/api/favorites` and is wired into `DexTradingViewModel`. |

The lesson generalises: **a service belongs on the server only when the server's data can actually
answer its question.** Same domain is not the same as same shape.

### Trading and bots: client-side, permanently

Owner's decision. Order placement, cancellation, position reads and every bot run on the user's
machine. Nothing in `CryptoAITerminal.Executor` is routed through the server, `USE_SERVER_TRADING`
stays off, and the server therefore never needs to hold a user's exchange key or wallet private key
— so the isolated-executor requirement in `db/004_custodial.sql` does not arise and one server node
is sufficient. Do not "also move trading to the server" as an optimisation.

### Correct as-is: stays on the client, server must not be involved

None of the four conditions apply — proxying any of these would add latency and cost for nothing.

- `MarketTapeService`, `LiquidationStreamService` — live WebSocket streams straight from the
  exchange. Real-time, keyless, nothing to aggregate. Note the split from
  `LiquidationDataService` above: the **stream** is the client's, the **history** is ours.
- `FundingArbitrageService`, `CrossExchangeArbitrageService`, `BestExecutionRouterService`,
  `DeribitOptionsService` — public CEX/derivatives endpoints, read live.
- `BacktestEngine`, `MonteCarloSimulator`, `WalkForwardOptimizer`, `CorrelationMatrixService`,
  and every strategy (`Rsi`, `Macd`, `Bollinger`, `Vwap`, `Breakout`, `SimpleMa`) — compute over
  candles the client already holds. Shipping them to the server to average numbers is pure loss.
- `TaxReportService`, `PreTradeRiskService`, `TwapExecutorService`, `BalanceRefresher`,
  `Wallet*Service` — the user's own keys reading the user's own accounts.

## C — Server proxies, does not store

The server holds a secret the client must never see, so the call goes through us — but the result
is not our data to keep.

| Path | Secret held | Controls |
|---|---|---|
| `/api/ai/message`, `/api/ai/openai` | Anthropic / OpenAI key | `AiRequestPolicy` + `AiBudget` |
| `/api/ai/ask` | same, server-composed prompt | same budget |
| `/api/portfolio/{chainId}/{address}` (Backend) | Covalent key | `TtlCache` 30 s + rate limiter |

**The AI proxy is a spend surface, not just a key surface.** Forwarding the body verbatim let a
client choose the model, the output length and the context size while we paid. Enforced now:

- allowlisted model — `AI_ALLOWED_ANTHROPIC_MODELS`, `AI_ALLOWED_OPENAI_MODELS`
- `max_tokens` clamped, and pinned when absent — `AI_MAX_TOKENS_CAP` (default 1500)
- request body capped — `AI_MAX_REQUEST_BYTES` (default 128 KiB)
- conversation length capped — `AI_MAX_MESSAGES` (default 40)
- `stream` forced off (the proxy buffers, so SSE would come back unusable)
- per-licence daily token cap charged from the vendor's own `usage` block —
  `AI_DAILY_TOKENS_PER_LICENSE` (default 200 000); clients can read their allowance from
  `/api/ai/budget` instead of discovering it as a 429

**The daily cap is currently off.** `ai.budget.enforced` ships as `false` (`SettingKeys.DefaultAiBudgetEnforced`)
because the per-call cost has not been measured: the old tier numbers predate the agent and the
long-context panels and were spent in minutes. What stays on is the *accounting* — `AiBudget.Charge`
and the `ai_usage` row per call — because that is what the price list will be derived from.
`/api/ai/budget` answers `unlimited: true` while this holds, and the terminal labels the figure as
spend rather than as a remaining allowance. Re-enabling is one toggle in `/admin`, and the per-tier
numbers must be recomputed from `ai_usage` first rather than taken from the code defaults.

## D — Server holds, never returns

| Secret | Guard |
|---|---|
| `provider_keys.api_key` | `/api/keys` masks to last 4; admin-gated and **fails closed** when `ADMIN_TOKEN` is unset |
| `secrets.ciphertext`, `wrapped_dek` | `SecretsRepository.ListForUserAsync` does not select them |
| Notification bot token | the `/api/notifications` handler never echoes it |
| 2FA TOTP secret | envelope-encrypted; returned exactly once at setup |
| Licence signing private key | not on the server — licences are pre-signed offline from a pool |
| CEX keys / wallet private keys | envelope-encrypted; **only the isolated executor node decrypts** (see `db/004_custodial.sql`) |

## Caching rule for anything in A

Cache granularity must follow the **sharing boundary**, not the page boundary.

Caching an assembled per-user page is the trap: a hundred users means a hundred entries with zero
reuse, memory grows with the client count instead of with the data, and every miss costs N joins.

So for a page that is per-user in composition but shared in content — the watchlist / AI-signals
tab — split it (`CompositeEndpoints.cs`):

- the **list of ids** is per-user → plain indexed read, not cached
- each **token's block** is shared → one entry per token, reused by everyone watching it, and it is
  the same entry `/api/dex/token/{chain}/{token}` serves
- **assembly** is concatenation of already-serialized fragments — no database, no re-serialization
- the **aggregate ETag** is built from the parts' ETags, so an unchanged tab is answered 304
  without assembling a body

Two rules that are easy to break:

- **Never put a generated-at timestamp in a cached body.** It changes every request and silently
  disables 304 forever.
- **Never let a per-user value into a shared cache key.** If a key can contain a uid, it belongs in
  `CompositeEndpoints`, not `SharedEndpoints`.

## Demand-driven collection

Being on the server is not a licence to poll forever. `tracked_tokens.last_read_utc` is stamped
when a user actually reads a token — on a cache **miss**, so it costs at most one update per TTL —
and `ClaimDueAsync` reschedules by demand: watched in the last 5 minutes keeps full cadence, within
the hour drops to 5 minutes, otherwise 15. Tokens with an active `price_alerts` row are exempt,
because the alert watcher needs a fresh snapshot regardless of who is looking.
