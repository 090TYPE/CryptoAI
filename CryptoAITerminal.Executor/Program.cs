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

// ⚠️ StubBotOrderExecutor trades NO real money. Replace with a real exchange-order impl
// (decrypt key + gateway + RiskManager) before going live (see IBotOrderExecutor).
builder.Services.AddSingleton<IBotOrderExecutor, StubBotOrderExecutor>();
builder.Services.AddHostedService<BotExecutorService>();

builder.Build().Run();
