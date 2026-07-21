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
builder.Services.AddSingleton(sp => new AiProxy(
    new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
    sp.GetRequiredService<ProviderKeyStore>(),
    cfg["ANTHROPIC_API_KEY"], cfg["OPENAI_API_KEY"]));
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

// Hard per-order USD cap — the minimum guardrail before any live order.
var maxOrderUsd = decimal.TryParse(cfg["BOT_MAX_ORDER_USD"], out var cap) ? cap : 100m;
builder.Services.AddSingleton<IRiskGate>(new PerOrderCapRiskGate(maxOrderUsd));

// Live is opt-in per node: BOT_LIVE_ENABLED=true wires the real exchange executor (which still
// only goes live for bots with mode=live, trade-only keys). Otherwise the paper stub moves no money.
if (string.Equals(cfg["BOT_LIVE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IBotOrderExecutor, ExchangeBotOrderExecutor>();
else
    builder.Services.AddSingleton<IBotOrderExecutor, StubBotOrderExecutor>();

builder.Services.AddHostedService<BotExecutorService>();

builder.Build().Run();
