using CryptoAITerminal.Executor;
using CryptoAITerminal.Server.Common;
using CryptoAITerminal.Server.Data;

var builder = Host.CreateApplicationBuilder(args);
var cfg = builder.Configuration;

var conn = cfg["DB_CONN"] ?? "Host=localhost;Port=5432;Database=cryptoai;Username=postgres;Password=postgres";
builder.Services.AddSingleton(new Db(conn));
builder.Services.AddSingleton<WithdrawalsRepository>();
builder.Services.AddSingleton<SecretsRepository>();
builder.Services.AddSingleton<BotConfigRepository>();
builder.Services.AddSingleton<BotOrdersRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<InboxRepository>();
builder.Services.AddSingleton<ProviderKeyStore>();
builder.Services.AddSingleton<AiDigestRepository>();
// SettingsStore передаётся пятым аргументом, как в Server.Api и CandleWorker. Без него AiProxy не
// видит ни ai.anthropic.base_url, ни ai.family: на развёрнутом роутере пред-торговый ревьюер слал
// бы роутерный ключ прямо на api.anthropic.com, получал 401 и — при незаданном
// AI_PRETRADE_REQUIRED — пропускал КАЖДУЮ покупку как «проверка недоступна». Сейчас сервис в
// контур не поднимается, но три конструктора из трёх должны выглядеть одинаково.
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton(sp => new AiProxy(
    new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
    sp.GetRequiredService<ProviderKeyStore>(),
    cfg["ANTHROPIC_API_KEY"], cfg["OPENAI_API_KEY"],
    sp.GetRequiredService<SettingsStore>()));
builder.Services.AddSingleton<IPreTradeReviewer, AiPreTradeReviewer>();

// Envelope cipher: Vault Transit in production, local AES for dev/tests.
var vaultAddr = cfg["VAULT_ADDR"];
if (!string.IsNullOrWhiteSpace(vaultAddr))
    builder.Services.AddSingleton<IEnvelopeCipher>(new VaultTransitEnvelopeCipher(
        new HttpClient(), vaultAddr, cfg["VAULT_TOKEN"]!, cfg["VAULT_TRANSIT_KEY"] ?? "cryptoai-kek"));
else if (!string.IsNullOrWhiteSpace(cfg["CRYPTOAI_KEK_B64"]))
    builder.Services.AddSingleton<IEnvelopeCipher>(LocalAesEnvelopeCipher.FromBase64(cfg["CRYPTOAI_KEK_B64"]!));
else
    throw new InvalidOperationException("No envelope cipher configured (set VAULT_ADDR or CRYPTOAI_KEK_B64).");

// ⚠️ StubWithdrawalSigner moves NO funds. Replace with an MPC/threshold signer + policy engine
// before touching real money (see IWithdrawalSigner).
builder.Services.AddSingleton<IWithdrawalSigner, StubWithdrawalSigner>();
builder.Services.AddHostedService<WithdrawalExecutorService>();

// Bot order execution (Track 4 Phase 4.1). Collaborators for the real executor:
builder.Services.AddSingleton<IGatewayFactory, GatewayFactory>();
builder.Services.AddSingleton<ICexKeyProvider, SecretsCexKeyProvider>();
builder.Services.AddSingleton<IPriceSource>(new HttpPriceSource(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
// Best-execution routing (Track 4 Phase 4.4): public per-exchange quotes to pick the cheapest venue.
builder.Services.AddSingleton<IQuoteSource>(new MultiExchangeQuoteSource(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));

// Hard per-order USD cap — the minimum guardrail before any live order.
var maxOrderUsd = decimal.TryParse(cfg["BOT_MAX_ORDER_USD"], out var cap) ? cap : 100m;
builder.Services.AddSingleton<IRiskGate>(new PerOrderCapRiskGate(maxOrderUsd));

// Grid execution (Track 4 Phase 4.2): restart-safe order tracking + gateway resolution (paper/live).
builder.Services.AddSingleton<GridOrdersRepository>();
builder.Services.AddSingleton<IGridOrderStore, SqlGridOrderStore>();
builder.Services.AddSingleton<IGridGatewayProvider, GridGatewayProvider>();

// Trailing / TP-SL execution (Track 4 Phase 4.3): restart-safe managed-position state.
builder.Services.AddSingleton<TslPositionsRepository>();
builder.Services.AddSingleton<ITslPositionStore, SqlTslPositionStore>();

// Live-trading kill-switch (Track 4 Phase 4.5): per-user + global halt, checked before live orders.
builder.Services.AddSingleton<UserFlagsRepository>();

// Symbol allowlist (Track 4 §4): when set, live bots may only trade these symbols. Empty = no limit.
builder.Services.AddSingleton(new SymbolAllowlist(cfg["BOT_SYMBOL_ALLOWLIST"]));

// Live is opt-in per node: BOT_LIVE_ENABLED=true wires the real exchange executor (which still
// only goes live for bots with mode=live, trade-only keys). Otherwise the paper stub moves no money.
if (string.Equals(cfg["BOT_LIVE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IBotOrderExecutor, ExchangeBotOrderExecutor>();
else
    builder.Services.AddSingleton<IBotOrderExecutor, StubBotOrderExecutor>();

builder.Services.AddHostedService<BotExecutorService>();

builder.Build().Run();
