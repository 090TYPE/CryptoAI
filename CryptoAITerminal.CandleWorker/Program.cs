using CryptoAITerminal.CandleWorker;
using CryptoAITerminal.CandleWorker.Collectors;
using CryptoAITerminal.CandleWorker.Sources;
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
builder.Services.AddSingleton<AiScoreRepository>();
builder.Services.AddSingleton<AiAnomalyRepository>();
builder.Services.AddSingleton<AiDigestRepository>();
builder.Services.AddSingleton<PersonalAiRepository>();
builder.Services.AddSingleton<ApiReadRepository>();
builder.Services.AddSingleton(sp => new AiProxy(
    new HttpClient { Timeout = TimeSpan.FromSeconds(120) },
    sp.GetRequiredService<ProviderKeyStore>(),
    builder.Configuration["ANTHROPIC_API_KEY"],
    builder.Configuration["OPENAI_API_KEY"]));
builder.Services.AddSingleton<NotificationRepository>();
builder.Services.AddSingleton<InboxRepository>();
builder.Services.AddSingleton<INotifier, Notifier>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<CollectorRunsRepository>();
builder.Services.AddSingleton<ProviderKeyStore>();

// Reusable DEX clients (net8.0, shared from Gateway.DEX)
builder.Services.AddSingleton(new GeckoTerminalClient());
builder.Services.AddSingleton(new DexScreenerClient());
builder.Services.AddSingleton(new TokenSecurityService());
builder.Services.AddSingleton(new DeployerWalletAnalyzer());

// Candle sources, tried in this order. Free and keyless first, so a paid key is only spent when
// the free path could not answer — a token GeckoTerminal does not index, a rate-limit, an outage.
// A keyed source with no key in provider_keys is skipped, so adding a key later needs no redeploy.
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
builder.Services.AddSingleton<ICandleSource>(sp => new GeckoTerminalCandleSource(sp.GetRequiredService<GeckoTerminalClient>()));
builder.Services.AddSingleton<ICandleSource>(sp => new CoinGeckoOnchainCandleSource(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<ICandleSource>(sp => new BirdeyeCandleSource(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<CandleSourceChain>();

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
builder.Services.AddSingleton<IDataCollector, AiScoreCollector>();
builder.Services.AddSingleton<IDataCollector, AiAnomalyCollector>();
builder.Services.AddSingleton<IDataCollector, PortfolioReviewJob>();
// Shared AI digests — one model call each, read by every user.
builder.Services.AddSingleton<IDataCollector, DailyDigestJob>();
builder.Services.AddSingleton<IDataCollector, MoversDigestJob>();
builder.Services.AddSingleton<IDataCollector, NarrativeDigestJob>();
builder.Services.AddSingleton<IDataCollector, NewsImpactDigestJob>();
builder.Services.AddSingleton<IDataCollector, WeeklyRecapJob>();
builder.Services.AddSingleton<IDataCollector, NewListingRadarJob>();
builder.Services.AddSingleton<IDataCollector, WhaleInterpreterJob>();
builder.Services.AddSingleton<IDataCollector, GasAdvisorJob>();
builder.Services.AddSingleton<IDataCollector, RugPostMortemJob>();
// risk & safety lens
builder.Services.AddSingleton<IDataCollector, LiquidityWatchJob>();
builder.Services.AddSingleton<IDataCollector, VolumeAnomalyJob>();
builder.Services.AddSingleton<IDataCollector, HolderConcentrationJob>();
builder.Services.AddSingleton<IDataCollector, SecurityRoundupJob>();
builder.Services.AddSingleton<IDataCollector, DeployerBlacklistJob>();
builder.Services.AddSingleton<IDataCollector, WorstScoredJob>();
builder.Services.AddSingleton<IDataCollector, TopScoredJob>();
builder.Services.AddSingleton<IDataCollector, DeadTokensJob>();
builder.Services.AddSingleton<IDataCollector, RiskDashboardJob>();
// opportunity & market structure lens
builder.Services.AddSingleton<IDataCollector, ChainRotationJob>();
builder.Services.AddSingleton<IDataCollector, OnchainPulseJob>();
builder.Services.AddSingleton<IDataCollector, SentimentReadJob>();
builder.Services.AddSingleton<IDataCollector, LiquidationReportJob>();
builder.Services.AddSingleton<IDataCollector, StablecoinFlowJob>();
builder.Services.AddSingleton<IDataCollector, VolatilityReportJob>();
builder.Services.AddSingleton<IDataCollector, MomentumScanJob>();
builder.Services.AddSingleton<IDataCollector, ReversalWatchJob>();
builder.Services.AddSingleton<IDataCollector, LiquidityLeadersJob>();
builder.Services.AddSingleton<IDataCollector, MicrocapRadarJob>();
builder.Services.AddSingleton<IDataCollector, NewsPriceGapJob>();
builder.Services.AddSingleton<IDataCollector, GasVsActivityJob>();
builder.Services.AddSingleton<IDataCollector, TokenOfTheDayJob>();
builder.Services.AddHostedService<CollectorRunner>();

builder.Build().Run();
