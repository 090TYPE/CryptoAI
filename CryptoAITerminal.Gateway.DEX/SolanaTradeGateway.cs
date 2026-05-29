using System.Reactive.Subjects;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using Solana.Unity.Programs;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Wallet;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class SolanaTradeGateway : IDexTradeGateway, IDisposable
{
    private const string NativeMint = "So11111111111111111111111111111111111111112";
    private const string UsdcMint = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    private const decimal MinimumProbeFeeBalanceSol = 0.00001m;
    private static readonly HashSet<string> SupportedDexes = new(StringComparer.OrdinalIgnoreCase)
    {
        "raydium",
        "pumpswap",
        "orca",
        "jupiter"
    };

    private readonly SolanaRpcClient _rpcClient;
    private readonly JupiterSwapClient _jupiterClient;
    private readonly JitoTipManager _jitoClient = new();
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly string _walletAddress;
    private readonly string? _normalizedSecretMaterial;

    /// <summary>Jito MEV protection. When enabled, transactions are sent as Jito bundles.</summary>
    public SolanaMevMode MevMode { get; set; } = SolanaMevMode.None;

    /// <summary>Jito tip in lamports. Default = normal priority.</summary>
    public long JitoTipLamports { get; set; } = JitoTipManager.NormalTipLamports;

    public bool IsMevProtected => MevMode == SolanaMevMode.Jito;

    public string MevStatusLabel => MevMode == SolanaMevMode.Jito
        ? $"🛡 Jito (tip {JitoTipLamports / 1e9:F5} SOL)"
        : "No MEV protection";

    public SolanaTradeGateway(string walletAddress, string rpcUrl = "https://api.mainnet-beta.solana.com",
        string? normalizedSecretMaterial = null, SolanaMevMode mevMode = SolanaMevMode.None)
    {
        _walletAddress = walletAddress;
        _rpcClient = new SolanaRpcClient(rpcUrl);
        _jupiterClient = new JupiterSwapClient();
        _normalizedSecretMaterial = normalizedSecretMaterial;
        MevMode = mevMode;
    }

    public string NetworkName => "Solana";
    public string NativeSymbol => "SOL";
    public string SupportedDexesLabel => string.Join(", ", SupportedDexes.OrderBy(static item => item));
    public IReadOnlyList<DexQuoteAssetOption> SupportedQuoteAssets => DexQuoteAssetCatalog.GetOptions(NetworkName);
    public bool HasSigningMaterial => !string.IsNullOrWhiteSpace(_normalizedSecretMaterial);
    public bool CanSignTransactions => TryCreateSigningAccount() is not null;
    public bool CanExecuteLiveSwaps => CanSignTransactions;
    public IObservable<MarketData> MarketDataStream => _marketDataSubject;

    public Task ConnectAsync() => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            throw new ArgumentException("Solana DEX orders expect the token mint in Symbol.", nameof(order));
        }

        if (order.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(order.Quantity), "Solana order quantity must be greater than zero.");
        }

        var transactionHash = order.Side switch
        {
            CryptoAITerminal.Core.Enums.OrderSide.Buy => await BuyTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            CryptoAITerminal.Core.Enums.OrderSide.Sell => await SellTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            _ => throw new NotSupportedException($"Unsupported Solana order side '{order.Side}'.")
        };

        order.Id = transactionHash;
        order.ClientOrderId = transactionHash;
        order.ExchangeType = "jupiter";
        order.TimeInForce = "IOC";
        order.FilledQuantity = order.Quantity;
        order.Status = CryptoAITerminal.Core.Enums.OrderStatus.Filled;
        return order;
    }

    public Task CancelOrderAsync(string orderId) => Task.CompletedTask;

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        if (!asset.Equals("SOL", StringComparison.OrdinalIgnoreCase))
        {
            return 0m;
        }

        return await _rpcClient.GetNativeBalanceAsync(_walletAddress);
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        await Task.CompletedTask;
        return new OrderBook { Symbol = symbol, Bids = new(), Asks = new() };
    }

    public bool SupportsDex(string? dexId)
    {
        if (string.IsNullOrWhiteSpace(dexId))
        {
            return true;
        }

        return SupportedDexes.Contains(dexId);
    }

    private static string ResolveQuoteMint(string? symbol)
    {
        var quote = DexQuoteAssetCatalog.Find("Solana", symbol);
        return quote?.IsNative != false ? NativeMint : quote.ContractAddress!;
    }

    private static string ResolveQuoteSymbol(string? symbol)
    {
        var quote = DexQuoteAssetCatalog.Find("Solana", symbol);
        return quote?.Symbol ?? "SOL";
    }

    public async Task<decimal> GetTokenPriceInNativeAsync(string tokenAddress, decimal nativeAmount, string? dexId = null)
    {
        var quote = await BuildSwapQuoteAsync(NativeMint, tokenAddress, nativeAmount, dexId, 5m);
        return quote.ExpectedOutputAmount;
    }

    public async Task<string> BuyTokenAsync(string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent = 5, string? dexId = null, string? spendAssetSymbol = null)
    {
        var result = await ExecuteConfirmedBuyAsync(new DexBuyExecutionRequest(tokenAddress, nativeAmountToSpend, slippagePercent, dexId, spendAssetSymbol));
        return result.TransactionHash;
    }

    public async Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5, string? dexId = null, string? receiveAssetSymbol = null)
    {
        var plan = await BuildSellExecutionPlanAsync(tokenAddress, ResolveQuoteMint(receiveAssetSymbol), tokenAmountToSell, slippagePercent, dexId);
        var swap = await _jupiterClient.BuildSwapTransactionAsync(_walletAddress, plan.SourceQuotePayload.DeepClone().AsObject());
        var signedTransaction = SignVersionedTransactionBase64(swap.SwapTransaction);
        var submit = await SubmitSerializedTransactionAsync(signedTransaction);
        if (!submit.Success)
        {
            throw new InvalidOperationException($"Solana sell submission failed: {submit.Narrative}");
        }

        return submit.Signature;
    }

    public Task<int> GetTokenDecimalsAsync(string tokenAddress) =>
        _rpcClient.GetTokenDecimalsAsync(tokenAddress);

    public Task<decimal> GetTokenBalanceAsync(string tokenAddress) =>
        _rpcClient.GetTokenBalanceAsync(_walletAddress, tokenAddress);

    public async Task<DexBuyExecutionResult> ExecuteConfirmedBuyAsync(DexBuyExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for confirmed buys.", nameof(request));
        }

        if (request.NativeAmountToSpend <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request.NativeAmountToSpend), $"{NativeSymbol} amount must be greater than zero.");
        }

        var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
        var spendMint = ResolveQuoteMint(request.SpendAssetSymbol);
        var spendAssetSymbol = ResolveQuoteSymbol(request.SpendAssetSymbol);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var spendBalanceBefore = spendMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? nativeBalanceBefore
            : await GetTokenBalanceAsync(spendMint);
        var tokenBalanceBefore = await GetTokenBalanceAsync(request.TokenAddress);
        var outputDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var plan = await BuildBuyExecutionPlanAsync(spendMint, request.TokenAddress, request.NativeAmountToSpend, slippagePercent, request.DexId);
        var swap = await _jupiterClient.BuildSwapTransactionAsync(_walletAddress, plan.SourceQuotePayload.DeepClone().AsObject());
        var signedTransaction = SignVersionedTransactionBase64(swap.SwapTransaction);
        var submit = await SubmitSerializedTransactionAsync(signedTransaction);
        if (!submit.Success)
        {
            throw new InvalidOperationException($"Solana buy submission failed: {submit.Narrative}");
        }

        var confirmation = await _rpcClient.WaitForSignatureConfirmationAsync(submit.Signature);
        var tokenBalanceAfter = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var spendBalanceAfter = spendMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? nativeBalanceAfter
            : await WaitForTokenBalanceChangeAsync(spendMint, spendBalanceBefore);
        var actualTokenAmount = Math.Max(0m, tokenBalanceAfter - tokenBalanceBefore);
        var spendAssetAmount = Math.Max(0m, spendBalanceBefore - spendBalanceAfter);
        var balanceVerified = actualTokenAmount > 0m;
        var hasUnexpectedOutput = balanceVerified && plan.Quote.MinimumOutputAmount > 0m && actualTokenAmount < plan.Quote.MinimumOutputAmount;
        var suspectedPartialFill = balanceVerified && plan.Quote.ExpectedOutputAmount > 0m && actualTokenAmount < (plan.Quote.ExpectedOutputAmount * 0.98m);
        var confirmed = confirmation.Confirmed && balanceVerified && !hasUnexpectedOutput;
        var parsedTransaction = await _rpcClient.GetParsedTransactionAsync(submit.Signature, CancellationToken.None);
        var feeNative = parsedTransaction is null ? 0m : parsedTransaction.FeeLamports / 1_000_000_000m;

        return new DexBuyExecutionResult(
            submit.Signature,
            confirmed,
            balanceVerified,
            hasUnexpectedOutput,
            suspectedPartialFill,
            outputDecimals,
            spendAssetAmount,
            nativeBalanceBefore,
            nativeBalanceAfter,
            plan.Quote.ExpectedOutputAmount,
            plan.Quote.MinimumOutputAmount,
            actualTokenAmount,
            tokenBalanceBefore,
            tokenBalanceAfter,
            BuildBuyExecutionNarrative(
                submit.Signature,
                plan.Quote.DexId,
                actualTokenAmount,
                plan.Quote.ExpectedOutputAmount,
                plan.Quote.MinimumOutputAmount,
                nativeBalanceBefore,
                nativeBalanceAfter,
                confirmation,
                balanceVerified,
                hasUnexpectedOutput,
                suspectedPartialFill),
            plan.Quote.DexId,
            feeNative,
            null,
            null,
            ReceiptParsed: confirmation.Confirmed,
            DecimalsVerified: outputDecimals > 0,
            SlippageProtected: plan.Quote.MinimumOutputAmount > 0m,
            BalanceSynchronized: balanceVerified,
            SpendAssetSymbol: spendAssetSymbol,
            SpendAssetAmount: spendAssetAmount,
            UsedQuoteAsset: !spendMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DexSellExecutionResult> ExecuteConfirmedSellAsync(DexSellExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for confirmed sells.", nameof(request));
        }

        if (request.TokenAmountToSell <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TokenAmountToSell), "Token amount must be greater than zero.");
        }

        var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
        var receiveMint = ResolveQuoteMint(request.ReceiveAssetSymbol);
        var receiveAssetSymbol = ResolveQuoteSymbol(request.ReceiveAssetSymbol);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var receiveBalanceBefore = receiveMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? nativeBalanceBefore
            : await GetTokenBalanceAsync(receiveMint);
        var tokenBalanceBefore = await GetTokenBalanceAsync(request.TokenAddress);
        var outputDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var plan = await BuildSellExecutionPlanAsync(request.TokenAddress, receiveMint, request.TokenAmountToSell, slippagePercent, request.DexId);
        var swap = await _jupiterClient.BuildSwapTransactionAsync(_walletAddress, plan.SourceQuotePayload.DeepClone().AsObject());
        var signedTransaction = SignVersionedTransactionBase64(swap.SwapTransaction);
        var submit = await SubmitSerializedTransactionAsync(signedTransaction);
        if (!submit.Success)
        {
            throw new InvalidOperationException($"Solana sell submission failed: {submit.Narrative}");
        }

        var confirmation = await _rpcClient.WaitForSignatureConfirmationAsync(submit.Signature);
        var tokenBalanceAfter = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBefore);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var receiveBalanceAfter = receiveMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? nativeBalanceAfter
            : await WaitForTokenBalanceChangeAsync(receiveMint, receiveBalanceBefore);
        var actualTokenAmountSold = Math.Max(0m, tokenBalanceBefore - tokenBalanceAfter);
        var actualNativeAmountReceived = Math.Max(0m, receiveBalanceAfter - receiveBalanceBefore);
        var balanceVerified = actualTokenAmountSold > 0m || actualNativeAmountReceived > 0m;
        var parsedTransaction = await _rpcClient.GetParsedTransactionAsync(submit.Signature, CancellationToken.None);
        var feeNative = parsedTransaction is null ? 0m : parsedTransaction.FeeLamports / 1_000_000_000m;

        return new DexSellExecutionResult(
            submit.Signature,
            confirmation.Confirmed && balanceVerified,
            balanceVerified,
            outputDecimals,
            request.TokenAmountToSell,
            actualTokenAmountSold,
            plan.Quote.ExpectedOutputAmount,
            plan.Quote.MinimumOutputAmount,
            actualNativeAmountReceived,
            nativeBalanceBefore,
            nativeBalanceAfter,
            tokenBalanceBefore,
            tokenBalanceAfter,
            $"Confirmed Solana sell through {plan.Quote.DexId}. Sold {actualTokenAmountSold:0.########} tokens for {actualNativeAmountReceived:0.########} {receiveAssetSymbol}.",
            plan.Quote.DexId,
            feeNative,
            null,
            null,
            ReceiptParsed: confirmation.Confirmed,
            DecimalsVerified: outputDecimals > 0,
            SlippageProtected: plan.Quote.MinimumOutputAmount > 0m,
            BalanceSynchronized: balanceVerified,
            ApproveWasRequired: false,
            ReceiveAssetSymbol: receiveAssetSymbol,
            ActualReceiveAmount: actualNativeAmountReceived,
            ExpectedReceiveAmount: plan.Quote.ExpectedOutputAmount,
            MinimumReceiveAmount: plan.Quote.MinimumOutputAmount,
            UsedQuoteAsset: !receiveMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DexSellabilityProbeResult> ProbeSellabilityAsync(DexSellabilityProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.TokenAddress))
        {
            throw new ArgumentException("Token address is required for sellability probes.", nameof(request));
        }

        try
        {
            var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
            var requestedTokenAmount = Math.Max(0m, request.TokenAmountToSell ?? 0m);
            var ownedBalance = await GetTokenBalanceAsync(request.TokenAddress);

            if (requestedTokenAmount > 0m || ownedBalance > 0m)
            {
                var amountToSimulate = requestedTokenAmount > 0m ? requestedTokenAmount : ownedBalance;
                if (ownedBalance < amountToSimulate)
                {
                    return new DexSellabilityProbeResult(
                        false,
                        false,
                        true,
                        amountToSimulate,
                        0m,
                        null,
                        $"Sell simulation blocked on Solana: wallet balance is {ownedBalance:0.########} tokens, below the requested {amountToSimulate:0.########}.");
                }

                if (!CanSignTransactions)
                {
                    return new DexSellabilityProbeResult(
                        false,
                        false,
                        true,
                        amountToSimulate,
                        0m,
                        null,
                        "Sell simulation on Solana requires a signable wallet session. The current session can quote but cannot sign a Jupiter route.");
                }

                var receiveMint = ResolveQuoteMint(request.QuoteAssetSymbol);
                var receiveSymbol = ResolveQuoteSymbol(request.QuoteAssetSymbol);
                var plan = await BuildSellExecutionPlanAsync(request.TokenAddress, receiveMint, amountToSimulate, slippagePercent, request.DexId);
                var swap = await _jupiterClient.BuildSwapTransactionAsync(_walletAddress, plan.SourceQuotePayload.DeepClone().AsObject());
                var signedTransaction = SignVersionedTransactionBase64(swap.SwapTransaction);
                var simulation = await SimulateSerializedTransactionAsync(signedTransaction);
                var probeNativeAmount = receiveMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
                    ? request.NativeAmountToProbe.GetValueOrDefault()
                    : request.QuoteAmountToProbe.GetValueOrDefault();

                return new DexSellabilityProbeResult(
                    simulation.Success,
                    true,
                    true,
                    amountToSimulate,
                    plan.Quote.ExpectedOutputAmount,
                    probeNativeAmount > 0m
                        ? CalculateRoundTripLossPercent(probeNativeAmount, plan.Quote.ExpectedOutputAmount)
                        : null,
                    simulation.Success
                    ? $"Signed Solana sell simulation passed through {plan.Quote.DexId}. Expected output: {plan.Quote.ExpectedOutputAmount:0.########} {receiveSymbol}. {simulation.Narrative}"
                    : $"Signed Solana sell simulation failed through {plan.Quote.DexId}: {simulation.Narrative}",
                    request.QuoteAssetSymbol);
            }

            var quoteMint = ResolveQuoteMint(request.QuoteAssetSymbol);
            var quoteSymbol = ResolveQuoteSymbol(request.QuoteAssetSymbol);
            var probeAmount = quoteMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0m, request.NativeAmountToProbe ?? 0m)
                : Math.Max(0m, request.QuoteAmountToProbe ?? 0m);
            if (probeAmount <= 0m)
            {
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    false,
                    0m,
                    0m,
                    null,
                    $"Sellability probe on Solana needs either an owned token balance or a {quoteSymbol} probe amount.",
                    request.QuoteAssetSymbol);
            }

            var buyQuote = await BuildSwapQuoteAsync(quoteMint, request.TokenAddress, probeAmount, request.DexId, slippagePercent);
            var sellQuote = await BuildSwapQuoteAsync(request.TokenAddress, quoteMint, buyQuote.ExpectedOutputAmount, request.DexId, slippagePercent);

            return new DexSellabilityProbeResult(
                sellQuote.ExpectedOutputAmount > 0m,
                false,
                false,
                buyQuote.ExpectedOutputAmount,
                sellQuote.ExpectedOutputAmount,
                CalculateRoundTripLossPercent(probeAmount, sellQuote.ExpectedOutputAmount),
                $"Quote-only Solana preflight returned {buyQuote.ExpectedOutputAmount:0.########} tokens and a sell-back quote of {sellQuote.ExpectedOutputAmount:0.########} {quoteSymbol}. Full signed sell simulation will run after fill when the wallet holds the token.",
                request.QuoteAssetSymbol);
        }
        catch (Exception ex)
        {
            return new DexSellabilityProbeResult(
                false,
                false,
                false,
                0m,
                0m,
                null,
                $"Solana sellability probe failed: {ex.Message}");
        }
    }

    public async Task<SolanaSwapQuote> BuildSwapQuoteAsync(
        string inputMint,
        string outputMint,
        decimal inputAmount,
        string? dexId = null,
        decimal slippagePercent = 5m)
    {
        if (!SolanaRpcClient.IsValidAddress(inputMint))
        {
            throw new ArgumentException("Invalid Solana input mint.", nameof(inputMint));
        }

        if (!SolanaRpcClient.IsValidAddress(outputMint))
        {
            throw new ArgumentException("Invalid Solana output mint.", nameof(outputMint));
        }

        if (inputAmount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(inputAmount), "Input amount must be greater than zero.");
        }

        var resolvedDex = ResolveDex(dexId);
        var inputDecimals = inputMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? 9
            : await GetTokenDecimalsAsync(inputMint);
        var outputDecimals = outputMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? 9
            : await GetTokenDecimalsAsync(outputMint);
        var amountAtomic = ToAtomicAmount(inputAmount, inputDecimals);
        var quote = await _jupiterClient.GetQuoteAsync(
            inputMint,
            outputMint,
            amountAtomic,
            ToSlippageBps(slippagePercent));

        return new SolanaSwapQuote(
            resolvedDex.Equals("jupiter", StringComparison.OrdinalIgnoreCase) ? resolvedDex : "jupiter",
            quote.InputMint,
            quote.OutputMint,
            FromAtomicAmount(quote.InAmount, inputDecimals),
            FromAtomicAmount(quote.OutAmount, outputDecimals),
            FromAtomicAmount(quote.MinimumOutAmount, outputDecimals),
            slippagePercent,
            quote.Legs.Count == 0
                ? [new SolanaSwapLeg("jupiter", inputMint, outputMint, resolvedDex)]
                : quote.Legs);
    }

    public async Task<SolanaSwapExecutionPlan> BuildBuyExecutionPlanAsync(
        string inputMint,
        string outputMint,
        decimal amountToSpend,
        decimal slippagePercent = 5m,
        string? dexId = null)
    {
        var inputDecimals = inputMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? 9
            : await GetTokenDecimalsAsync(inputMint);
        var quote = await BuildSwapQuoteAsync(inputMint, outputMint, amountToSpend, dexId, slippagePercent);
        var jupiterQuote = await _jupiterClient.GetQuoteAsync(
            inputMint,
            outputMint,
            ToAtomicAmount(amountToSpend, inputDecimals),
            ToSlippageBps(slippagePercent));
        var blockhash = await _rpcClient.GetLatestBlockhashAsync();
        return new SolanaSwapExecutionPlan(
            quote,
            _walletAddress,
            blockhash,
            $"Quote prepared through Jupiter. Route legs: {quote.Legs.Count}. Recent blockhash acquired. Signing core is {(CanSignTransactions ? "ready" : "waiting for a 64-byte keypair secret")}. Swap execution can be submitted when the session is armed.",
            !CanSignTransactions,
            jupiterQuote.RawQuote);
    }

    public async Task<SolanaSwapExecutionPlan> BuildSellExecutionPlanAsync(
        string inputMint,
        string outputMint,
        decimal tokenAmountToSell,
        decimal slippagePercent = 5m,
        string? dexId = null)
    {
        var outputDecimals = outputMint.Equals(NativeMint, StringComparison.OrdinalIgnoreCase)
            ? 9
            : await GetTokenDecimalsAsync(outputMint);
        var quote = await BuildSwapQuoteAsync(inputMint, outputMint, tokenAmountToSell, dexId, slippagePercent);
        var inputDecimals = await GetTokenDecimalsAsync(inputMint);
        var jupiterQuote = await _jupiterClient.GetQuoteAsync(
            inputMint,
            outputMint,
            ToAtomicAmount(tokenAmountToSell, inputDecimals),
            ToSlippageBps(slippagePercent));
        var blockhash = await _rpcClient.GetLatestBlockhashAsync();
        return new SolanaSwapExecutionPlan(
            quote,
            _walletAddress,
            blockhash,
            $"Exit quote prepared through Jupiter. Route legs: {quote.Legs.Count}. Recent blockhash acquired. Signing core is {(CanSignTransactions ? "ready" : "waiting for a 64-byte keypair secret")}. Swap execution can be submitted when the session is armed.",
            !CanSignTransactions,
            jupiterQuote.RawQuote);
    }

    public Task<SolanaSimulationResult> SimulateSerializedTransactionAsync(string base64Transaction) =>
        _rpcClient.SimulateRawTransactionAsync(base64Transaction);

    public Task<SolanaSubmitResult> SubmitSerializedTransactionAsync(string base64Transaction) =>
        MevMode == SolanaMevMode.Jito
            ? _rpcClient.SendRawTransactionWithJitoAsync(base64Transaction, JitoTipLamports)
            : _rpcClient.SendRawTransactionAsync(base64Transaction);

    public async Task<string> BuildSignedMemoProbeTransactionAsync(string? memoText = null)
    {
        var signer = GetSigningAccountOrThrow();
        var recentBlockhash = await _rpcClient.GetLatestBlockhashAsync();
        var memo = string.IsNullOrWhiteSpace(memoText)
            ? $"Crypto AI Terminal Solana probe {DateTimeOffset.UtcNow:O}"
            : memoText.Trim();

        return BuildSignedTransactionBase64(
            signer,
            recentBlockhash,
            [MemoProgram.NewMemoV2(memo, signer)]);
    }

    public async Task<SolanaSimulationResult> SimulateSignedMemoProbeAsync(string? memoText = null)
    {
        var transaction = await BuildSignedMemoProbeTransactionAsync(memoText);
        return await SimulateSerializedTransactionAsync(transaction);
    }

    public async Task<SolanaSimulationResult> SimulateSignedSwapProbeAsync(
        string outputMint = UsdcMint,
        decimal nativeAmountToSpend = 0.001m,
        decimal slippagePercent = 5m)
    {
        var plan = await BuildBuyExecutionPlanAsync(NativeMint, outputMint, nativeAmountToSpend, slippagePercent, "jupiter");
        var swap = await _jupiterClient.BuildSwapTransactionAsync(_walletAddress, plan.SourceQuotePayload.DeepClone().AsObject());
        var signedTransaction = SignVersionedTransactionBase64(swap.SwapTransaction);
        return await SimulateSerializedTransactionAsync(signedTransaction);
    }

    public async Task<SolanaExecutionDiagnostics> RunExecutionDiagnosticsAsync()
    {
        var steps = new List<SolanaDiagnosticStep>();

        try
        {
            var nativeBalance = await _rpcClient.GetNativeBalanceAsync(_walletAddress);
            steps.Add(new SolanaDiagnosticStep(
                "Wallet + RPC",
                true,
                $"Connected to Solana RPC. Native balance loaded: {nativeBalance:N6} SOL."));

            if (nativeBalance < MinimumProbeFeeBalanceSol)
            {
                steps.Add(new SolanaDiagnosticStep(
                    "Fee Readiness",
                    false,
                    $"The wallet balance is too low for a realistic probe. Deposit at least {MinimumProbeFeeBalanceSol:N5} SOL for network fees."));
                return BuildDiagnosticsResult(steps);
            }

            steps.Add(new SolanaDiagnosticStep(
                "Fee Readiness",
                true,
                $"The wallet has enough SOL for simulation and fee checks ({nativeBalance:N6} SOL)."));
        }
        catch (Exception ex)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Wallet + RPC",
                false,
                $"RPC balance check failed: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var blockhash = await _rpcClient.GetLatestBlockhashAsync();
            steps.Add(new SolanaDiagnosticStep(
                "Recent Blockhash",
                true,
                $"Recent blockhash acquired: {TrimForUi(blockhash, 20)}."));
        }
        catch (Exception ex)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Recent Blockhash",
                false,
                $"Blockhash request failed: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        if (!CanSignTransactions)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Signing Material",
                false,
                "Trade-enabled Solana session is not armed with a matching signable secret yet. Supported inputs: a 64-byte keypair or a 32-byte seed that derives the same wallet address."));
            return BuildDiagnosticsResult(steps);
        }

        steps.Add(new SolanaDiagnosticStep(
            "Signing Material",
            true,
            "Signable Solana account is armed and ready."));

        try
        {
            var memoSimulation = await SimulateSignedMemoProbeAsync();
            steps.Add(new SolanaDiagnosticStep(
                "Signed Memo Simulation",
                memoSimulation.Success,
                memoSimulation.Narrative));
            if (!memoSimulation.Success)
            {
                return BuildDiagnosticsResult(steps);
            }
        }
        catch (Exception ex)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Signed Memo Simulation",
                false,
                $"Signed memo probe failed: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var quote = await BuildSwapQuoteAsync(NativeMint, UsdcMint, 0.001m, "jupiter", 5m);
            steps.Add(new SolanaDiagnosticStep(
                "Jupiter Quote",
                true,
                $"Quote ready. Expected output: {quote.ExpectedOutputAmount:N6} {TrimForUi(UsdcMint, 10)}."));
        }
        catch (Exception ex)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Jupiter Quote",
                false,
                $"Jupiter quote failed: {ex.Message}"));
            return BuildDiagnosticsResult(steps);
        }

        try
        {
            var swapSimulation = await SimulateSignedSwapProbeAsync();
            steps.Add(new SolanaDiagnosticStep(
                "Signed Swap Simulation",
                swapSimulation.Success,
                swapSimulation.Narrative));
        }
        catch (Exception ex)
        {
            steps.Add(new SolanaDiagnosticStep(
                "Signed Swap Simulation",
                false,
                $"Signed Jupiter swap simulation failed: {ex.Message}"));
        }

        return BuildDiagnosticsResult(steps);
    }

    public void Dispose()
    {
        _marketDataSubject.Dispose();
        _jupiterClient.Dispose();
        _rpcClient.Dispose();
    }

    private string ResolveDex(string? dexId)
    {
        if (string.IsNullOrWhiteSpace(dexId))
        {
            return "jupiter";
        }

        if (!SupportsDex(dexId))
        {
            throw new NotSupportedException($"Solana DEX '{dexId}' is not wired in this build. Supported: {SupportedDexesLabel}.");
        }

        return dexId;
    }

    private async Task<decimal> WaitForTokenBalanceChangeAsync(string tokenAddress, decimal balanceBefore)
    {
        var latestBalance = balanceBefore;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(900 * attempt));
            }

            latestBalance = await GetTokenBalanceAsync(tokenAddress);
            if (Math.Abs(latestBalance - balanceBefore) > 0.00000001m)
            {
                break;
            }
        }

        return latestBalance;
    }

    private string SignVersionedTransactionBase64(string unsignedTransactionBase64)
    {
        var signer = GetSigningAccountOrThrow();
        var versionedTransaction = VersionedTransaction.Deserialize(unsignedTransactionBase64);
        var compiledMessage = versionedTransaction.CompileMessage();
        var signature = signer.Sign(compiledMessage);
        var signed = VersionedTransaction.Populate(
            VersionedMessage.Deserialize(compiledMessage),
            [signature]);
        return Convert.ToBase64String(signed.Serialize());
    }

    private string BuildSignedTransactionBase64(
        Account signer,
        string recentBlockhash,
        IReadOnlyList<TransactionInstruction> instructions)
    {
        if (instructions.Count == 0)
        {
            throw new InvalidOperationException("Cannot sign an empty Solana transaction.");
        }

        var builder = new TransactionBuilder()
            .SetFeePayer(signer)
            .SetRecentBlockHash(recentBlockhash);

        foreach (var instruction in instructions)
        {
            builder.AddInstruction(instruction);
        }

        var serialized = builder.Build(signer);
        return Convert.ToBase64String(serialized);
    }

    private Account GetSigningAccountOrThrow() =>
        TryCreateSigningAccount()
        ?? throw new InvalidOperationException("This Solana session does not have a signable 64-byte keypair secret yet.");

    private Account? TryCreateSigningAccount()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_normalizedSecretMaterial))
            {
                return null;
            }

            var keyMaterial = SolanaKeyMaterial.ParseNormalizedSecret(_normalizedSecretMaterial);
            if (keyMaterial.Length == 64)
            {
                var publicKeyBytes = keyMaterial[32..64];
                var publicKey = SolanaKeyMaterial.EncodeBase58(publicKeyBytes);
                var account = new Account(_normalizedSecretMaterial, publicKey);
                return string.Equals(account.PublicKey.Key, _walletAddress, StringComparison.Ordinal)
                    ? account
                    : null;
            }

            if (keyMaterial.Length == 32)
            {
                foreach (var seedMode in new[] { SeedMode.Bip39, SeedMode.Ed25519Bip32 })
                {
                    try
                    {
                        var wallet = new Wallet(keyMaterial, string.Empty, seedMode);
                        var candidate = wallet.Account;
                        if (string.Equals(candidate.PublicKey.Key, _walletAddress, StringComparison.Ordinal))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                        // Try the next compatible seed mode.
                    }
                }

                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ulong ToAtomicAmount(decimal amount, int decimals)
    {
        var scaled = amount * Pow10(decimals);
        if (scaled <= 0m)
        {
            throw new InvalidOperationException("Atomic amount must be greater than zero.");
        }

        return decimal.ToUInt64(decimal.Truncate(scaled));
    }

    private static decimal FromAtomicAmount(ulong amount, int decimals) =>
        amount / Pow10(decimals);

    private static decimal Pow10(int power)
    {
        decimal result = 1m;
        for (var i = 0; i < power; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static int ToSlippageBps(decimal slippagePercent) =>
        Math.Max(1, (int)Math.Round(slippagePercent * 100m, MidpointRounding.AwayFromZero));

    private static SolanaExecutionDiagnostics BuildDiagnosticsResult(IReadOnlyList<SolanaDiagnosticStep> steps)
    {
        var success = steps.Count > 0 && steps.All(static step => step.Success);
        var summary = success
            ? "Solana execution diagnostics passed end-to-end."
            : $"Solana execution diagnostics stopped at '{steps.Last().Title}'.";
        return new SolanaExecutionDiagnostics(success, summary, steps);
    }

    private string BuildBuyExecutionNarrative(
        string signature,
        string dexId,
        decimal actualTokenAmount,
        decimal expectedTokenAmount,
        decimal minimumTokenAmount,
        decimal nativeBalanceBefore,
        decimal nativeBalanceAfter,
        SolanaSignatureConfirmationResult confirmation,
        bool balanceVerified,
        bool hasUnexpectedOutput,
        bool suspectedPartialFill)
    {
        var spentNativeAmount = Math.Max(0m, nativeBalanceBefore - nativeBalanceAfter);
        var notes = new List<string>
        {
            confirmation.Narrative
        };

        if (!balanceVerified)
        {
            notes.Add("token balance did not move after the confirmed signature");
        }
        else if (hasUnexpectedOutput)
        {
            notes.Add($"received {actualTokenAmount:0.########} tokens, below protected minimum {minimumTokenAmount:0.########}");
        }
        else if (suspectedPartialFill)
        {
            notes.Add($"received {actualTokenAmount:0.########} tokens vs quoted {expectedTokenAmount:0.########}");
        }
        else
        {
            notes.Add($"confirmed fill of {actualTokenAmount:0.########} tokens");
        }

        return $"Buy {signature[..Math.Min(10, signature.Length)]} through {dexId}: spent {spentNativeAmount:0.########} {NativeSymbol}, {string.Join("; ", notes)}.";
    }

    private static decimal? CalculateRoundTripLossPercent(decimal inputNativeAmount, decimal expectedNativeOutput)
    {
        if (inputNativeAmount <= 0m)
        {
            return null;
        }

        return ((inputNativeAmount - expectedNativeOutput) / inputNativeAmount) * 100m;
    }

    private static string TrimForUi(string value, int visibleTail)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= visibleTail)
        {
            return value;
        }

        return $"{value[..visibleTail]}...";
    }
}
