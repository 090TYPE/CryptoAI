using CryptoAITerminal.CandleWorker;
using CryptoAITerminal.CandleWorker.Collectors;
using CryptoAITerminal.Gateway.DEX;
using CryptoAITerminal.Server.Data;

var builder = Host.CreateApplicationBuilder(args);

// DB_CONN env var overrides; falls back to local dev default.
var conn = builder.Configuration["DB_CONN"]
           ?? builder.Configuration.GetConnectionString("Db")
           ?? "Host=localhost;Port=5432;Database=cryptoai;Username=postgres;Password=postgres";

// Data layer
builder.Services.AddSingleton(new Db(conn));
builder.Services.AddSingleton<TrackedTokenRepository>();
builder.Services.AddSingleton<CandleRepository>();
builder.Services.AddSingleton<MetadataRepository>();
builder.Services.AddSingleton<SecurityRepository>();
builder.Services.AddSingleton<NewsRepository>();
builder.Services.AddSingleton<SentimentRepository>();
builder.Services.AddSingleton<GasRepository>();
builder.Services.AddSingleton<DeployerRepository>();
builder.Services.AddSingleton<HoldersRepository>();
builder.Services.AddSingleton<WhaleRepository>();
builder.Services.AddSingleton<OnChainRepository>();
builder.Services.AddSingleton<LiquidationsRepository>();
builder.Services.AddSingleton<PriceAlertsRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<CollectorRunsRepository>();
builder.Services.AddSingleton<ProviderKeyStore>();

// Reusable DEX clients (net8.0, shared from Gateway.DEX)
builder.Services.AddSingleton(new GeckoTerminalClient());
builder.Services.AddSingleton(new DexScreenerClient());
builder.Services.AddSingleton(new TokenSecurityService());
builder.Services.AddSingleton(new DeployerWalletAnalyzer());

// Candle worker (1m OHLCV for favorites)
builder.Services.AddHostedService<CandlePollingService>();

// Data collectors — add a source by registering another IDataCollector here.
builder.Services.AddSingleton<IDataCollector, MarketDataCollector>();
builder.Services.AddSingleton<IDataCollector, SecurityCollector>();
builder.Services.AddSingleton<IDataCollector, SocialsCollector>();
builder.Services.AddSingleton<IDataCollector, BirdeyeCollector>();
builder.Services.AddSingleton<IDataCollector, CoinGeckoCollector>();
builder.Services.AddSingleton<IDataCollector, GasCollector>();
builder.Services.AddSingleton<IDataCollector, DeployerCollector>();
builder.Services.AddSingleton<IDataCollector, OnChainCollector>();
builder.Services.AddSingleton<IDataCollector, WhalesCollector>();
builder.Services.AddSingleton<IDataCollector, LiquidationsCollector>();
builder.Services.AddSingleton<IDataCollector, NewsCollector>();
builder.Services.AddSingleton<IDataCollector, SentimentCollector>();
builder.Services.AddSingleton<IDataCollector, CryptoPanicCollector>();
builder.Services.AddSingleton<IDataCollector, AlertCollector>();
builder.Services.AddHostedService<CollectorRunner>();

builder.Build().Run();
