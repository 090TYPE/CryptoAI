using System.Numerics;
using System.Reactive.Subjects;
using System.Threading;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace CryptoAITerminal.Gateway.DEX;

[Function("getAmountsOut", "uint256[]")]
public class V2GetAmountsOutFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("address[]", "path", 2)]
    public List<string> Path { get; set; } = new();
}

[Function("swapExactETHForTokens", "uint256[]")]
public class V2SwapExactETHForTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountOutMin", 1)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("address[]", "path", 2)]
    public List<string> Path { get; set; } = new();

    [Parameter("address", "to", 3)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 4)]
    public BigInteger Deadline { get; set; }
}

[Function("swapExactTokensForTokensSupportingFeeOnTransferTokens")]
public class V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("uint256", "amountOutMin", 2)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("address[]", "path", 3)]
    public List<string> Path { get; set; } = new();

    [Parameter("address", "to", 4)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 5)]
    public BigInteger Deadline { get; set; }
}

[Function("approve", "bool")]
public class ApproveFunction : FunctionMessage
{
    [Parameter("address", "spender", 1)]
    public string Spender { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 2)]
    public BigInteger Amount { get; set; }
}

[Function("swapExactTokensForETHSupportingFeeOnTransferTokens")]
public class V2SwapExactTokensForETHSupportingFeeOnTransferTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("uint256", "amountOutMin", 2)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("address[]", "path", 3)]
    public List<string> Path { get; set; } = new();

    [Parameter("address", "to", 4)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 5)]
    public BigInteger Deadline { get; set; }
}

[Function("decimals", "uint8")]
public class DecimalsFunction : FunctionMessage
{
}

[Function("balanceOf", "uint256")]
public class BalanceOfFunction : FunctionMessage
{
    [Parameter("address", "owner", 1)]
    public string Owner { get; set; } = string.Empty;
}

[Function("allowance", "uint256")]
public class AllowanceFunction : FunctionMessage
{
    [Parameter("address", "owner", 1)]
    public string Owner { get; set; } = string.Empty;

    [Parameter("address", "spender", 2)]
    public string Spender { get; set; } = string.Empty;
}

public class AerodromeRoute
{
    [Parameter("address", "from", 1)]
    public string From { get; set; } = string.Empty;

    [Parameter("address", "to", 2)]
    public string To { get; set; } = string.Empty;

    [Parameter("bool", "stable", 3)]
    public bool Stable { get; set; }

    [Parameter("address", "factory", 4)]
    public string Factory { get; set; } = string.Empty;
}

[Function("getAmountsOut", "uint256[]")]
public class AerodromeGetAmountsOutFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("tuple[]", "routes", 2)]
    public List<AerodromeRoute> Routes { get; set; } = new();
}

[Function("swapExactETHForTokensSupportingFeeOnTransferTokens")]
public class AerodromeSwapExactETHForTokensSupportingFeeOnTransferTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountOutMin", 1)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("tuple[]", "routes", 2)]
    public List<AerodromeRoute> Routes { get; set; } = new();

    [Parameter("address", "to", 3)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 4)]
    public BigInteger Deadline { get; set; }
}

[Function("swapExactTokensForTokensSupportingFeeOnTransferTokens")]
public class AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("uint256", "amountOutMin", 2)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("tuple[]", "routes", 3)]
    public List<AerodromeRoute> Routes { get; set; } = new();

    [Parameter("address", "to", 4)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 5)]
    public BigInteger Deadline { get; set; }
}

[Function("swapExactTokensForETHSupportingFeeOnTransferTokens")]
public class AerodromeSwapExactTokensForETHSupportingFeeOnTransferTokensFunction : FunctionMessage
{
    [Parameter("uint256", "amountIn", 1)]
    public BigInteger AmountIn { get; set; }

    [Parameter("uint256", "amountOutMin", 2)]
    public BigInteger AmountOutMin { get; set; }

    [Parameter("tuple[]", "routes", 3)]
    public List<AerodromeRoute> Routes { get; set; } = new();

    [Parameter("address", "to", 4)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "deadline", 5)]
    public BigInteger Deadline { get; set; }
}

public enum DexRouterKind
{
    UniswapV2Like,
    Aerodrome,
    UniswapV3
}

public sealed record DexRouterDefinition(
    string DexId,
    DexRouterKind Kind,
    string RouterAddress,
    string WrappedNativeAddress,
    string? FactoryAddress = null);

public sealed record DexGatewayNetworkDefinition(
    string NetworkName,
    string NativeSymbol,
    string DefaultDexId,
    IReadOnlyDictionary<string, DexRouterDefinition> RoutersByDexId);

public class DEXGateway : IDexTradeGateway
{
    private static readonly IReadOnlyDictionary<string, DexGatewayNetworkDefinition> NetworkDefinitions =
        new Dictionary<string, DexGatewayNetworkDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["BSC"] = new(
                "BSC",
                "BNB",
                "pancakeswap",
                new Dictionary<string, DexRouterDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pancakeswap"] = new(
                        "pancakeswap",
                        DexRouterKind.UniswapV2Like,
                        "0x10ED43C718714eb63d5aA57B78B54704E256024E",
                        "0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c"),
                    ["sushiswap"] = new(
                        "sushiswap",
                        DexRouterKind.UniswapV2Like,
                        "0x1b02dA8Cb0d097eB8D57A175b88c7D8b47997506",
                        "0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c")
                }),
            ["Ethereum"] = new(
                "Ethereum",
                "ETH",
                "uniswap",
                new Dictionary<string, DexRouterDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uniswap"] = new(
                        "uniswap",
                        DexRouterKind.UniswapV2Like,
                        "0x7a250d5630B4cF539739dF2C5dAcb4c659F2488D",
                        "0xC02aaA39b223FE8D0A0E5C4F27eAD9083C756Cc2"),
                    ["uniswap-v3"] = new(
                        "uniswap-v3",
                        DexRouterKind.UniswapV3,
                        "0x68b3465833fb72A70ecDF485E0e4C7bD8665Fc45", // SwapRouter02
                        "0xC02aaA39b223FE8D0A0E5C4F27eAD9083C756Cc2"),
                    ["sushiswap"] = new(
                        "sushiswap",
                        DexRouterKind.UniswapV2Like,
                        "0xd9e1cE17f2641f24aE83637ab66a2cca9C378B9F",
                        "0xC02aaA39b223FE8D0A0E5C4F27eAD9083C756Cc2")
                }),
            ["Base"] = new(
                "Base",
                "ETH",
                "aerodrome",
                new Dictionary<string, DexRouterDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["aerodrome"] = new(
                        "aerodrome",
                        DexRouterKind.Aerodrome,
                        "0xcF77a3Ba9A5CA399B7c97c74d54e5b9F59C5c10",
                        "0x4200000000000000000000000000000000000006",
                        "0x420DD381b31aEf6683db6B902084cB0FFECe40Da"),
                    ["uniswap"] = new(
                        "uniswap",
                        DexRouterKind.UniswapV2Like,
                        "0x4752ba5DBc23f44D87826276BF6Fd6b1C372aD24",
                        "0x4200000000000000000000000000000000000006"),
                    ["uniswap-v3"] = new(
                        "uniswap-v3",
                        DexRouterKind.UniswapV3,
                        "0x2626664c2603336E57B271c5C0b26F421741e481", // SwapRouter02
                        "0x4200000000000000000000000000000000000006")
                }),
            ["Polygon"] = new(
                "Polygon",
                "POL",
                "quickswap",
                new Dictionary<string, DexRouterDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["quickswap"] = new(
                        "quickswap",
                        DexRouterKind.UniswapV2Like,
                        "0xa5E0829CaCEd8fFDD4De3c43696c57F7D7A678ff",
                        "0x0d500B1d8E8eF31E21C99d1Db9A6444d3ADf1270"),
                    ["sushiswap"] = new(
                        "sushiswap",
                        DexRouterKind.UniswapV2Like,
                        "0x1b02dA8Cb0d097eB8D57A175b88c7D8b47997506",
                        "0x0d500B1d8E8eF31E21C99d1Db9A6444d3ADf1270")
                }),
            ["Arbitrum"] = new(
                "Arbitrum",
                "ETH",
                "sushiswap",
                new Dictionary<string, DexRouterDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sushiswap"] = new(
                        "sushiswap",
                        DexRouterKind.UniswapV2Like,
                        "0x1b02dA8Cb0d097eB8D57A175b88c7D8b47997506",
                        "0x82aF49447D8a07e3bd95BD0d56f35241523fBab1"),
                    ["camelot"] = new(
                        "camelot",
                        DexRouterKind.UniswapV2Like,
                        "0xc873fEcbd354f5A56E00E710B90EF4201db2448d",
                        "0x82aF49447D8a07e3bd95BD0d56f35241523fBab1")
                })
        };

    private readonly Web3 _web3;
    private readonly Account _account;
    private readonly Subject<MarketData> _marketDataSubject = new();
    private readonly DexGatewayNetworkDefinition _networkDefinition;
    private readonly string _originalRpcUrl;

    public IObservable<MarketData> MarketDataStream => _marketDataSubject;
    public string NetworkName => _networkDefinition.NetworkName;
    public string NativeSymbol => _networkDefinition.NativeSymbol;
    public string SupportedDexesLabel => string.Join(", ", _networkDefinition.RoutersByDexId.Keys.OrderBy(static item => item));
    public IReadOnlyList<DexQuoteAssetOption> SupportedQuoteAssets => DexQuoteAssetCatalog.GetOptions(NetworkName);

    /// <summary>Current MEV protection mode. Change at runtime to switch modes.</summary>
    public EvmMevMode MevMode { get; set; } = EvmMevMode.None;

    /// <summary>Effective RPC: original or MEV-protected endpoint.</summary>
    public string ActiveRpcUrl => EvmMevRpcResolver.Resolve(_networkDefinition.NetworkName, _originalRpcUrl, MevMode);

    /// <summary>True when Flashbots / BloxRoute protection is active.</summary>
    public bool IsMevProtected => MevMode != EvmMevMode.None;

    public string MevStatusLabel => MevMode switch
    {
        EvmMevMode.FlashbotsProtect => "🛡 Flashbots Protect",
        EvmMevMode.FlashbotsBuilder => "🛡 Flashbots Builder",
        EvmMevMode.BloxRoute        => "🛡 BloxRoute BDN",
        _                           => "No MEV protection"
    };

    public DEXGateway(string privateKey, string rpcUrl = "https://bsc-dataseed.binance.org/")
        : this(privateKey, rpcUrl, NetworkDefinitions["BSC"])
    {
    }

    private DEXGateway(string privateKey, string rpcUrl, DexGatewayNetworkDefinition networkDefinition)
    {
        _account = new Account(privateKey);
        _originalRpcUrl = rpcUrl;
        _web3 = new Web3(_account, rpcUrl);
        _networkDefinition = networkDefinition;
    }

    public static bool SupportsNetwork(string networkName) =>
        NetworkDefinitions.ContainsKey(networkName);

    public static DEXGateway CreateForNetwork(string networkName, string privateKey, string rpcUrl,
        EvmMevMode mevMode = EvmMevMode.None)
    {
        if (!NetworkDefinitions.TryGetValue(networkName, out var definition))
        {
            throw new NotSupportedException($"Network '{networkName}' is not wired for EVM DEX execution in this build.");
        }

        var effectiveRpc = EvmMevRpcResolver.Resolve(networkName, rpcUrl, mevMode);
        return new DEXGateway(privateKey, effectiveRpc, definition) { MevMode = mevMode };
    }

    public bool SupportsDex(string? dexId)
    {
        if (string.IsNullOrWhiteSpace(dexId))
        {
            return true;
        }

        return _networkDefinition.RoutersByDexId.ContainsKey(dexId);
    }

    public Task ConnectAsync() => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task<Order> PlaceOrderAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (string.IsNullOrWhiteSpace(order.Symbol))
        {
            throw new ArgumentException("DEX orders expect the token contract address in Symbol.", nameof(order));
        }

        if (order.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(order.Quantity), "DEX order quantity must be greater than zero.");
        }

        var transactionHash = order.Side switch
        {
            CryptoAITerminal.Core.Enums.OrderSide.Buy => await BuyTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            CryptoAITerminal.Core.Enums.OrderSide.Sell => await SellTokenAsync(order.Symbol, order.Quantity, 5m, null, null),
            _ => throw new NotSupportedException($"Unsupported DEX order side '{order.Side}'.")
        };

        order.Id = transactionHash;
        order.ClientOrderId = transactionHash;
        order.ExchangeType = ResolveRouter(null).DexId;
        order.TimeInForce = "IOC";
        order.FilledQuantity = order.Quantity;
        order.Status = CryptoAITerminal.Core.Enums.OrderStatus.Filled;
        return order;
    }

    public Task CancelOrderAsync(string orderId) => Task.CompletedTask;

    public async Task<decimal> GetBalanceAsync(string asset)
    {
        if (asset.Equals(_networkDefinition.NativeSymbol, StringComparison.OrdinalIgnoreCase))
        {
            var balance = await _web3.Eth.GetBalance.SendRequestAsync(_account.Address);
            return Web3.Convert.FromWei(balance);
        }

        return 0;
    }

    public async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 10)
    {
        await Task.CompletedTask;
        return new OrderBook { Symbol = symbol, Bids = new(), Asks = new() };
    }

    public Task<decimal> GetTokenPriceInBNBAsync(string tokenAddress, decimal bnbAmount) =>
        GetTokenPriceInNativeAsync(tokenAddress, bnbAmount);

    public async Task<decimal> GetTokenPriceInNativeAsync(string tokenAddress, decimal nativeAmount, string? dexId = null)
    {
        if (nativeAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeAmount), $"{NativeSymbol} amount must be greater than zero.");
        }

        var router = ResolveRouter(dexId);
        var amountInWei = Web3.Convert.ToWei(nativeAmount);

        return router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await GetTokenPriceFromV2RouterAsync(router, tokenAddress, amountInWei),
            DexRouterKind.Aerodrome => await GetTokenPriceFromAerodromeAsync(router, tokenAddress, amountInWei),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };
    }

    public Task<string> BuyTokenAsync(string tokenAddress, decimal bnbAmountToSpend, decimal slippagePercent = 5) =>
        BuyTokenAsync(tokenAddress, bnbAmountToSpend, slippagePercent, null, null);

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

        var router = ResolveRouter(request.DexId);
        var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
        var quoteAsset = ResolveQuoteAsset(request.SpendAssetSymbol);
        if (quoteAsset is not null && !quoteAsset.IsNative)
        {
            return await ExecuteConfirmedQuoteAssetBuyAsync(router, request, quoteAsset, slippagePercent);
        }

        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var tokenBalanceBeforeRaw = await GetTokenBalanceRawAsync(request.TokenAddress);
        var nativeAmountWei = Web3.Convert.ToWei(request.NativeAmountToSpend);
        var expectedAmountRaw = await GetTokenAmountOutRawAsync(router, request.TokenAddress, nativeAmountWei);
        var minimumAmountRaw = ApplySlippage(expectedAmountRaw, slippagePercent);

        var receipt = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await ExecuteBuyFromV2RouterAsync(router, request.TokenAddress, nativeAmountWei, minimumAmountRaw),
            DexRouterKind.Aerodrome => await ExecuteBuyFromAerodromeAsync(router, request.TokenAddress, nativeAmountWei, minimumAmountRaw),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"Buy transaction {receipt.TransactionHash} was mined but reverted on {router.DexId}.");
        }

        var tokenBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBeforeRaw);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var actualReceivedRaw = tokenBalanceAfterRaw >= tokenBalanceBeforeRaw
            ? tokenBalanceAfterRaw - tokenBalanceBeforeRaw
            : BigInteger.Zero;
        var tokenBalanceBefore = UnitConversion.Convert.FromWei(tokenBalanceBeforeRaw, tokenDecimals);
        var tokenBalanceAfter = UnitConversion.Convert.FromWei(tokenBalanceAfterRaw, tokenDecimals);
        var expectedTokenAmount = UnitConversion.Convert.FromWei(expectedAmountRaw, tokenDecimals);
        var minimumTokenAmount = UnitConversion.Convert.FromWei(minimumAmountRaw, tokenDecimals);
        var actualTokenAmount = UnitConversion.Convert.FromWei(actualReceivedRaw, tokenDecimals);
        var balanceVerified = actualReceivedRaw > 0;
        var hasUnexpectedOutput = balanceVerified && minimumTokenAmount > 0m && actualTokenAmount < minimumTokenAmount;
        var suspectedPartialFill = balanceVerified && expectedTokenAmount > 0m && actualTokenAmount < (expectedTokenAmount * 0.98m);
        var confirmed = balanceVerified && !hasUnexpectedOutput;
        var buyFeeNative = CalculateNetworkFeeNative(receipt);

        return new DexBuyExecutionResult(
            receipt.TransactionHash,
            confirmed,
            balanceVerified,
            hasUnexpectedOutput,
            suspectedPartialFill,
            tokenDecimals,
            Math.Max(0m, nativeBalanceBefore - nativeBalanceAfter),
            nativeBalanceBefore,
            nativeBalanceAfter,
            expectedTokenAmount,
            minimumTokenAmount,
            actualTokenAmount,
            tokenBalanceBefore,
            tokenBalanceAfter,
            BuildBuyExecutionNarrative(
                router,
                receipt.TransactionHash,
                actualTokenAmount,
                expectedTokenAmount,
                minimumTokenAmount,
                nativeBalanceBefore,
                nativeBalanceAfter,
                balanceVerified,
                hasUnexpectedOutput,
                suspectedPartialFill),
            router.DexId,
            buyFeeNative,
            (long?)receipt.GasUsed?.Value,
            receipt.EffectiveGasPrice?.Value.ToString(),
            ReceiptParsed: true,
            DecimalsVerified: tokenDecimals > 0,
            SlippageProtected: minimumTokenAmount > 0m,
            BalanceSynchronized: balanceVerified,
            SpendAssetSymbol: NativeSymbol,
            SpendAssetAmount: Math.Max(0m, nativeBalanceBefore - nativeBalanceAfter),
            UsedQuoteAsset: false);
    }

    public async Task<string> BuyTokenAsync(string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent = 5, string? dexId = null, string? spendAssetSymbol = null)
    {
        var result = await ExecuteConfirmedBuyAsync(new DexBuyExecutionRequest(tokenAddress, nativeAmountToSpend, slippagePercent, dexId, spendAssetSymbol));
        return result.TransactionHash;
    }

    public Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5) =>
        SellTokenAsync(tokenAddress, tokenAmountToSell, slippagePercent, null, null);

    public async Task<string> SellTokenAsync(string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent = 5, string? dexId = null, string? receiveAssetSymbol = null)
    {
        if (tokenAmountToSell <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenAmountToSell), "Token amount must be greater than zero.");
        }

        var result = await ExecuteConfirmedSellAsync(new DexSellExecutionRequest(tokenAddress, tokenAmountToSell, slippagePercent, dexId, receiveAssetSymbol));
        return result.TransactionHash;
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

        var router = ResolveRouter(request.DexId);
        var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
        var receiveAsset = ResolveQuoteAsset(request.ReceiveAssetSymbol);
        if (receiveAsset is not null && !receiveAsset.IsNative)
        {
            return await ExecuteConfirmedQuoteAssetSellAsync(router, request, receiveAsset, slippagePercent);
        }

        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var tokenBalanceBeforeRaw = await GetTokenBalanceRawAsync(request.TokenAddress);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var amountIn = UnitConversion.Convert.ToWei(request.TokenAmountToSell, tokenDecimals);
        var allowanceResult = await EnsureAllowanceAsync(request.TokenAddress, router.RouterAddress, amountIn);
        var expectedNativeOutRaw = await GetNativeAmountOutRawAsync(router, request.TokenAddress, amountIn);
        var minimumNativeOutRaw = ApplySlippage(expectedNativeOutRaw, slippagePercent);

        var receipt = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await ExecuteSellToV2RouterAsync(router, request.TokenAddress, amountIn, minimumNativeOutRaw),
            DexRouterKind.Aerodrome => await ExecuteSellToAerodromeAsync(router, request.TokenAddress, amountIn, minimumNativeOutRaw),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"Sell transaction {receipt.TransactionHash} was mined but reverted on {router.DexId}.");
        }

        var tokenBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBeforeRaw);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var soldRaw = tokenBalanceAfterRaw <= tokenBalanceBeforeRaw
            ? tokenBalanceBeforeRaw - tokenBalanceAfterRaw
            : BigInteger.Zero;
        var tokenBalanceBefore = UnitConversion.Convert.FromWei(tokenBalanceBeforeRaw, tokenDecimals);
        var tokenBalanceAfter = UnitConversion.Convert.FromWei(tokenBalanceAfterRaw, tokenDecimals);
        var actualTokenAmountSold = UnitConversion.Convert.FromWei(soldRaw, tokenDecimals);
        var expectedNativeAmount = Web3.Convert.FromWei(expectedNativeOutRaw);
        var minimumNativeAmount = Web3.Convert.FromWei(minimumNativeOutRaw);
        var actualNativeAmountReceived = Math.Max(0m, nativeBalanceAfter - nativeBalanceBefore);
        var sellFeeNative = CalculateNetworkFeeNative(receipt) + allowanceResult.FeeNative;
        var balanceVerified = soldRaw > 0 || actualNativeAmountReceived > 0m;
        var confirmed = balanceVerified;

        return new DexSellExecutionResult(
            receipt.TransactionHash,
            confirmed,
            balanceVerified,
            tokenDecimals,
            request.TokenAmountToSell,
            actualTokenAmountSold,
            expectedNativeAmount,
            minimumNativeAmount,
            actualNativeAmountReceived,
            nativeBalanceBefore,
            nativeBalanceAfter,
            tokenBalanceBefore,
            tokenBalanceAfter,
            $"Confirmed sell on {router.DexId}. Sold {actualTokenAmountSold:0.########} tokens for {actualNativeAmountReceived:0.########} {NativeSymbol}.",
            router.DexId,
            sellFeeNative,
            (long?)receipt.GasUsed?.Value,
            receipt.EffectiveGasPrice?.Value.ToString(),
            ReceiptParsed: true,
            DecimalsVerified: tokenDecimals > 0,
            SlippageProtected: minimumNativeAmount > 0m,
            BalanceSynchronized: balanceVerified,
            ApproveWasRequired: allowanceResult.WasRequired,
            ApproveTransactionHash: allowanceResult.TransactionHash,
            ApproveFeeNative: allowanceResult.FeeNative,
            ReceiveAssetSymbol: NativeSymbol,
            ActualReceiveAmount: actualNativeAmountReceived,
            ExpectedReceiveAmount: expectedNativeAmount,
            MinimumReceiveAmount: minimumNativeAmount,
            UsedQuoteAsset: false);
    }

    public async Task<int> GetTokenDecimalsAsync(string tokenAddress)
    {
        var handler = _web3.Eth.GetContractQueryHandler<DecimalsFunction>();
        return await handler.QueryAsync<int>(tokenAddress, new DecimalsFunction());
    }

    public async Task<decimal> GetTokenBalanceAsync(string tokenAddress)
    {
        var decimals = await GetTokenDecimalsAsync(tokenAddress);
        var balance = await GetTokenBalanceRawAsync(tokenAddress);
        return UnitConversion.Convert.FromWei(balance, decimals);
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
            var router = ResolveRouter(request.DexId);
            var slippagePercent = Math.Clamp(request.SlippagePercent, 0m, 99m);
            var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
            var requestedTokenAmount = Math.Max(0m, request.TokenAmountToSell ?? 0m);
            var ownedBalanceRaw = await GetTokenBalanceRawAsync(request.TokenAddress);

            if (requestedTokenAmount > 0m)
            {
                var requestedAmountRaw = UnitConversion.Convert.ToWei(requestedTokenAmount, tokenDecimals);
                if (ownedBalanceRaw < requestedAmountRaw)
                {
                    var ownedBalance = UnitConversion.Convert.FromWei(ownedBalanceRaw, tokenDecimals);
                    return new DexSellabilityProbeResult(
                        false,
                        false,
                        true,
                        requestedTokenAmount,
                        0m,
                        null,
                        $"Sell simulation blocked on {router.DexId}: wallet balance is {ownedBalance:0.########} tokens, below the requested {requestedTokenAmount:0.########}.");
                }

                return await SimulateOwnedSellAsync(
                    router,
                    request.TokenAddress,
                    requestedAmountRaw,
                    tokenDecimals,
                    slippagePercent,
                    request.PrimeAllowance,
                    request.NativeAmountToProbe,
                    ResolveQuoteAsset(request.QuoteAssetSymbol));
            }

            if (ownedBalanceRaw > 0)
            {
                return await SimulateOwnedSellAsync(
                    router,
                    request.TokenAddress,
                    ownedBalanceRaw,
                    tokenDecimals,
                    slippagePercent,
                    request.PrimeAllowance,
                    request.NativeAmountToProbe,
                    ResolveQuoteAsset(request.QuoteAssetSymbol));
            }

            var quoteAsset = ResolveQuoteAsset(request.QuoteAssetSymbol);
            var probeUsesQuote = quoteAsset is not null && !quoteAsset.IsNative;
            var nativeProbeAmount = Math.Max(0m, request.NativeAmountToProbe ?? 0m);
            var quoteProbeAmount = Math.Max(0m, request.QuoteAmountToProbe ?? 0m);
            if (!probeUsesQuote && nativeProbeAmount <= 0m)
            {
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    false,
                    0m,
                    0m,
                    null,
                    $"Sellability probe on {router.DexId} needs either an owned token balance or a native probe amount.",
                    request.QuoteAssetSymbol);
            }

            if (probeUsesQuote && quoteProbeAmount <= 0m)
            {
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    false,
                    0m,
                    0m,
                    null,
                    $"Sellability probe on {router.DexId} needs a positive {quoteAsset!.Symbol} probe amount when stable-quote mode is active.",
                    quoteAsset.Symbol);
            }

            var quotedTokenAmountRaw = probeUsesQuote
                ? await GetQuoteAssetTokenAmountOutRawAsync(router, quoteAsset!, request.TokenAddress, quoteProbeAmount)
                : await GetTokenAmountOutRawAsync(router, request.TokenAddress, Web3.Convert.ToWei(nativeProbeAmount));
            if (quotedTokenAmountRaw <= 0)
            {
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    false,
                    0m,
                    0m,
                    null,
                    probeUsesQuote
                        ? $"Quote-only preflight on {router.DexId} returned zero token output for {quoteProbeAmount:0.########} {quoteAsset!.Symbol}."
                        : $"Quote-only preflight on {router.DexId} returned zero token output for {nativeProbeAmount:0.########} {NativeSymbol}.",
                    probeUsesQuote ? quoteAsset!.Symbol : null);
            }

            var sellBackNativeRaw = probeUsesQuote
                ? await GetTokenAmountOutToQuoteRawAsync(router, request.TokenAddress, quoteAsset!, quotedTokenAmountRaw)
                : await GetNativeAmountOutRawAsync(router, request.TokenAddress, quotedTokenAmountRaw);
            var quotedTokenAmount = UnitConversion.Convert.FromWei(quotedTokenAmountRaw, tokenDecimals);
            var expectedNativeOutput = probeUsesQuote
                ? UnitConversion.Convert.FromWei(sellBackNativeRaw, await GetTokenDecimalsAsync(quoteAsset!.ContractAddress!))
                : Web3.Convert.FromWei(sellBackNativeRaw);
            var probeInputAmount = probeUsesQuote ? quoteProbeAmount : nativeProbeAmount;
            var probeSymbol = probeUsesQuote ? quoteAsset!.Symbol : NativeSymbol;

            return new DexSellabilityProbeResult(
                sellBackNativeRaw > 0,
                false,
                false,
                quotedTokenAmount,
                expectedNativeOutput,
                CalculateRoundTripLossPercent(probeInputAmount, expectedNativeOutput),
                $"Quote-only preflight on {router.DexId} returned {quotedTokenAmount:0.########} tokens and a sell-back quote of {expectedNativeOutput:0.########} {probeSymbol}. Full sell simulation still requires an owned wallet balance and will run immediately after fill.",
                probeUsesQuote ? quoteAsset!.Symbol : null);
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
                $"On-chain sellability probe failed on {NetworkName}: {ex.Message}");
        }
    }

    private DexRouterDefinition ResolveRouter(string? dexId)
    {
        if (string.IsNullOrWhiteSpace(dexId))
        {
            return _networkDefinition.RoutersByDexId[_networkDefinition.DefaultDexId];
        }

        if (_networkDefinition.RoutersByDexId.TryGetValue(dexId, out var router))
        {
            return router;
        }

        throw new NotSupportedException(
            $"DEX '{dexId}' is not wired for live execution on {_networkDefinition.NetworkName}. Supported dexes: {SupportedDexesLabel}.");
    }

    private DexQuoteAssetOption? ResolveQuoteAsset(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Equals(NativeSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var quoteAsset = DexQuoteAssetCatalog.Find(NetworkName, symbol);
        if (quoteAsset is null || quoteAsset.IsNative || string.IsNullOrWhiteSpace(quoteAsset.ContractAddress))
        {
            throw new NotSupportedException(
                $"{NetworkName} DEX execution does not have a live quote route for '{symbol}'. Supported spend assets: {string.Join(", ", SupportedQuoteAssets.Select(static asset => asset.Symbol))}.");
        }

        return quoteAsset;
    }

    private async Task<DexBuyExecutionResult> ExecuteConfirmedQuoteAssetBuyAsync(
        DexRouterDefinition router,
        DexBuyExecutionRequest request,
        DexQuoteAssetOption quoteAsset,
        decimal slippagePercent)
    {
        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var quoteDecimals = await GetTokenDecimalsAsync(quoteAsset.ContractAddress!);
        var tokenBalanceBeforeRaw = await GetTokenBalanceRawAsync(request.TokenAddress);
        var quoteBalanceBeforeRaw = await GetTokenBalanceRawAsync(quoteAsset.ContractAddress!);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var amountIn = UnitConversion.Convert.ToWei(request.NativeAmountToSpend, quoteDecimals);
        var allowanceResult = await EnsureAllowanceAsync(quoteAsset.ContractAddress!, router.RouterAddress, amountIn);
        var expectedAmountRaw = await GetQuoteAssetTokenAmountOutRawAsync(router, quoteAsset, request.TokenAddress, request.NativeAmountToSpend);
        var minimumAmountRaw = ApplySlippage(expectedAmountRaw, slippagePercent);

        var receipt = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await ExecuteQuoteAssetBuyFromV2RouterAsync(router, quoteAsset, request.TokenAddress, amountIn, minimumAmountRaw),
            DexRouterKind.Aerodrome => await ExecuteQuoteAssetBuyFromAerodromeAsync(router, quoteAsset, request.TokenAddress, amountIn, minimumAmountRaw),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"Buy transaction {receipt.TransactionHash} was mined but reverted on {router.DexId}.");
        }

        var tokenBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBeforeRaw);
        var quoteBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(quoteAsset.ContractAddress!, quoteBalanceBeforeRaw);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var actualReceivedRaw = tokenBalanceAfterRaw >= tokenBalanceBeforeRaw
            ? tokenBalanceAfterRaw - tokenBalanceBeforeRaw
            : BigInteger.Zero;
        var actualQuoteSpentRaw = quoteBalanceBeforeRaw >= quoteBalanceAfterRaw
            ? quoteBalanceBeforeRaw - quoteBalanceAfterRaw
            : BigInteger.Zero;
        var tokenBalanceBefore = UnitConversion.Convert.FromWei(tokenBalanceBeforeRaw, tokenDecimals);
        var tokenBalanceAfter = UnitConversion.Convert.FromWei(tokenBalanceAfterRaw, tokenDecimals);
        var expectedTokenAmount = UnitConversion.Convert.FromWei(expectedAmountRaw, tokenDecimals);
        var minimumTokenAmount = UnitConversion.Convert.FromWei(minimumAmountRaw, tokenDecimals);
        var actualTokenAmount = UnitConversion.Convert.FromWei(actualReceivedRaw, tokenDecimals);
        var spendAssetAmount = UnitConversion.Convert.FromWei(actualQuoteSpentRaw, quoteDecimals);
        var balanceVerified = actualReceivedRaw > 0;
        var hasUnexpectedOutput = balanceVerified && minimumTokenAmount > 0m && actualTokenAmount < minimumTokenAmount;
        var suspectedPartialFill = balanceVerified && expectedTokenAmount > 0m && actualTokenAmount < (expectedTokenAmount * 0.98m);
        var confirmed = balanceVerified && !hasUnexpectedOutput;
        var buyFeeNative = CalculateNetworkFeeNative(receipt) + allowanceResult.FeeNative;

        return new DexBuyExecutionResult(
            receipt.TransactionHash,
            confirmed,
            balanceVerified,
            hasUnexpectedOutput,
            suspectedPartialFill,
            tokenDecimals,
            spendAssetAmount,
            nativeBalanceBefore,
            nativeBalanceAfter,
            expectedTokenAmount,
            minimumTokenAmount,
            actualTokenAmount,
            tokenBalanceBefore,
            tokenBalanceAfter,
            $"Buy {receipt.TransactionHash[..Math.Min(10, receipt.TransactionHash.Length)]} on {router.DexId}: spent {spendAssetAmount:0.########} {quoteAsset.Symbol}, received {actualTokenAmount:0.########} tokens.",
            router.DexId,
            buyFeeNative,
            (long?)receipt.GasUsed?.Value,
            receipt.EffectiveGasPrice?.Value.ToString(),
            ReceiptParsed: true,
            DecimalsVerified: tokenDecimals > 0 && quoteDecimals > 0,
            SlippageProtected: minimumTokenAmount > 0m,
            BalanceSynchronized: balanceVerified,
            SpendAssetSymbol: quoteAsset.Symbol,
            SpendAssetAmount: spendAssetAmount,
            UsedQuoteAsset: true);
    }

    private async Task<DexSellExecutionResult> ExecuteConfirmedQuoteAssetSellAsync(
        DexRouterDefinition router,
        DexSellExecutionRequest request,
        DexQuoteAssetOption quoteAsset,
        decimal slippagePercent)
    {
        var tokenDecimals = await GetTokenDecimalsAsync(request.TokenAddress);
        var quoteDecimals = await GetTokenDecimalsAsync(quoteAsset.ContractAddress!);
        var tokenBalanceBeforeRaw = await GetTokenBalanceRawAsync(request.TokenAddress);
        var quoteBalanceBeforeRaw = await GetTokenBalanceRawAsync(quoteAsset.ContractAddress!);
        var nativeBalanceBefore = await GetBalanceAsync(NativeSymbol);
        var amountIn = UnitConversion.Convert.ToWei(request.TokenAmountToSell, tokenDecimals);
        var allowanceResult = await EnsureAllowanceAsync(request.TokenAddress, router.RouterAddress, amountIn);
        var expectedQuoteOutRaw = await GetTokenAmountOutToQuoteRawAsync(router, request.TokenAddress, quoteAsset, amountIn);
        var minimumQuoteOutRaw = ApplySlippage(expectedQuoteOutRaw, slippagePercent);

        var receipt = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await ExecuteQuoteAssetSellToV2RouterAsync(router, quoteAsset, request.TokenAddress, amountIn, minimumQuoteOutRaw),
            DexRouterKind.Aerodrome => await ExecuteQuoteAssetSellToAerodromeAsync(router, quoteAsset, request.TokenAddress, amountIn, minimumQuoteOutRaw),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"Sell transaction {receipt.TransactionHash} was mined but reverted on {router.DexId}.");
        }

        var tokenBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(request.TokenAddress, tokenBalanceBeforeRaw);
        var quoteBalanceAfterRaw = await WaitForTokenBalanceChangeAsync(quoteAsset.ContractAddress!, quoteBalanceBeforeRaw);
        var nativeBalanceAfter = await GetBalanceAsync(NativeSymbol);
        var soldRaw = tokenBalanceAfterRaw <= tokenBalanceBeforeRaw
            ? tokenBalanceBeforeRaw - tokenBalanceAfterRaw
            : BigInteger.Zero;
        var receivedQuoteRaw = quoteBalanceAfterRaw >= quoteBalanceBeforeRaw
            ? quoteBalanceAfterRaw - quoteBalanceBeforeRaw
            : BigInteger.Zero;
        var tokenBalanceBefore = UnitConversion.Convert.FromWei(tokenBalanceBeforeRaw, tokenDecimals);
        var tokenBalanceAfter = UnitConversion.Convert.FromWei(tokenBalanceAfterRaw, tokenDecimals);
        var actualTokenAmountSold = UnitConversion.Convert.FromWei(soldRaw, tokenDecimals);
        var expectedQuoteAmount = UnitConversion.Convert.FromWei(expectedQuoteOutRaw, quoteDecimals);
        var minimumQuoteAmount = UnitConversion.Convert.FromWei(minimumQuoteOutRaw, quoteDecimals);
        var actualQuoteAmountReceived = UnitConversion.Convert.FromWei(receivedQuoteRaw, quoteDecimals);
        var sellFeeNative = CalculateNetworkFeeNative(receipt) + allowanceResult.FeeNative;
        var balanceVerified = soldRaw > 0 || actualQuoteAmountReceived > 0m;

        return new DexSellExecutionResult(
            receipt.TransactionHash,
            balanceVerified,
            balanceVerified,
            tokenDecimals,
            request.TokenAmountToSell,
            actualTokenAmountSold,
            actualQuoteAmountReceived,
            minimumQuoteAmount,
            actualQuoteAmountReceived,
            nativeBalanceBefore,
            nativeBalanceAfter,
            tokenBalanceBefore,
            tokenBalanceAfter,
            $"Confirmed sell on {router.DexId}. Sold {actualTokenAmountSold:0.########} tokens for {actualQuoteAmountReceived:0.########} {quoteAsset.Symbol}.",
            router.DexId,
            sellFeeNative,
            (long?)receipt.GasUsed?.Value,
            receipt.EffectiveGasPrice?.Value.ToString(),
            ReceiptParsed: true,
            DecimalsVerified: tokenDecimals > 0 && quoteDecimals > 0,
            SlippageProtected: minimumQuoteAmount > 0m,
            BalanceSynchronized: balanceVerified,
            ApproveWasRequired: allowanceResult.WasRequired,
            ApproveTransactionHash: allowanceResult.TransactionHash,
            ApproveFeeNative: allowanceResult.FeeNative,
            ReceiveAssetSymbol: quoteAsset.Symbol,
            ActualReceiveAmount: actualQuoteAmountReceived,
            ExpectedReceiveAmount: expectedQuoteAmount,
            MinimumReceiveAmount: minimumQuoteAmount,
            UsedQuoteAsset: true);
    }

    private async Task<decimal> GetTokenPriceFromV2RouterAsync(DexRouterDefinition router, string tokenAddress, BigInteger amountInWei)
    {
        var tokenDecimals = await GetTokenDecimalsAsync(tokenAddress);
        var amountOut = await GetTokenAmountOutRawAsync(router, tokenAddress, amountInWei);
        return UnitConversion.Convert.FromWei(amountOut, tokenDecimals);
    }

    private async Task<decimal> GetTokenPriceFromAerodromeAsync(DexRouterDefinition router, string tokenAddress, BigInteger amountInWei)
    {
        var tokenDecimals = await GetTokenDecimalsAsync(tokenAddress);
        var amountOut = await GetTokenAmountOutRawAsync(router, tokenAddress, amountInWei);
        return UnitConversion.Convert.FromWei(amountOut, tokenDecimals);
    }

    private async Task<string> BuyFromV2RouterAsync(DexRouterDefinition router, string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent)
    {
        var nativeAmountWei = Web3.Convert.ToWei(nativeAmountToSpend);
        var expectedAmountWei = await GetTokenAmountOutRawAsync(router, tokenAddress, nativeAmountWei);
        var amountOutMin = ApplySlippage(expectedAmountWei, slippagePercent);
        var swapReceipt = await ExecuteBuyFromV2RouterAsync(router, tokenAddress, nativeAmountWei, amountOutMin);

        if (swapReceipt.Status.Value == 1)
        {
            return swapReceipt.TransactionHash;
        }

        throw new Exception("Swap transaction failed");
    }

    private async Task<string> BuyFromAerodromeAsync(DexRouterDefinition router, string tokenAddress, decimal nativeAmountToSpend, decimal slippagePercent)
    {
        var nativeAmountWei = Web3.Convert.ToWei(nativeAmountToSpend);
        var expectedAmountWei = await GetTokenAmountOutRawAsync(router, tokenAddress, nativeAmountWei);
        var amountOutMin = ApplySlippage(expectedAmountWei, slippagePercent);
        var swapReceipt = await ExecuteBuyFromAerodromeAsync(router, tokenAddress, nativeAmountWei, amountOutMin);

        if (swapReceipt.Status.Value == 1)
        {
            return swapReceipt.TransactionHash;
        }

        throw new Exception("Aerodrome swap transaction failed");
    }

    private async Task<string> SellToV2RouterAsync(DexRouterDefinition router, string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent)
    {
        var decimals = await GetTokenDecimalsAsync(tokenAddress);
        var amountIn = UnitConversion.Convert.ToWei(tokenAmountToSell, decimals);
        await EnsureAllowanceAsync(tokenAddress, router.RouterAddress, amountIn);
        var expectedNativeOut = await GetNativeAmountOutRawAsync(router, tokenAddress, amountIn);
        var swapReceipt = await ExecuteSellToV2RouterAsync(router, tokenAddress, amountIn, ApplySlippage(expectedNativeOut, slippagePercent));

        if (swapReceipt.Status.Value == 1)
        {
            return swapReceipt.TransactionHash;
        }

        throw new Exception("Token sell transaction failed");
    }

    private async Task<string> SellToAerodromeAsync(DexRouterDefinition router, string tokenAddress, decimal tokenAmountToSell, decimal slippagePercent)
    {
        var decimals = await GetTokenDecimalsAsync(tokenAddress);
        var amountIn = UnitConversion.Convert.ToWei(tokenAmountToSell, decimals);
        await EnsureAllowanceAsync(tokenAddress, router.RouterAddress, amountIn);
        var expectedNativeOut = await GetNativeAmountOutRawAsync(router, tokenAddress, amountIn);
        var swapReceipt = await ExecuteSellToAerodromeAsync(router, tokenAddress, amountIn, ApplySlippage(expectedNativeOut, slippagePercent));

        if (swapReceipt.Status.Value == 1)
        {
            return swapReceipt.TransactionHash;
        }

        throw new Exception("Aerodrome token sell transaction failed");
    }

    private async Task<BigInteger> GetTokenBalanceRawAsync(string tokenAddress)
    {
        var handler = _web3.Eth.GetContractQueryHandler<BalanceOfFunction>();
        return await handler.QueryAsync<BigInteger>(tokenAddress, new BalanceOfFunction
        {
            Owner = _account.Address
        });
    }

    private async Task<BigInteger> WaitForTokenBalanceChangeAsync(string tokenAddress, BigInteger balanceBefore)
    {
        var latestBalance = balanceBefore;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(900 * attempt));
            }

            latestBalance = await GetTokenBalanceRawAsync(tokenAddress);
            if (latestBalance != balanceBefore)
            {
                break;
            }
        }

        return latestBalance;
    }

    private async Task<BigInteger> GetTokenAllowanceRawAsync(string tokenAddress, string spender)
    {
        var handler = _web3.Eth.GetContractQueryHandler<AllowanceFunction>();
        return await handler.QueryAsync<BigInteger>(tokenAddress, new AllowanceFunction
        {
            Owner = _account.Address,
            Spender = spender
        });
    }

    private async Task<BigInteger> GetTokenAmountOutRawAsync(DexRouterDefinition router, string tokenAddress, BigInteger nativeAmountInWei)
    {
        var result = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await QueryV2AmountsOutAsync(
                router,
                nativeAmountInWei,
                [router.WrappedNativeAddress, tokenAddress]),
            DexRouterKind.Aerodrome => await QueryAerodromeAmountsOutAsync(
                router,
                nativeAmountInWei,
                BuildAerodromeRoutes(router, [router.WrappedNativeAddress, tokenAddress])),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        return result.Last();
    }

    private async Task<BigInteger> GetNativeAmountOutRawAsync(DexRouterDefinition router, string tokenAddress, BigInteger tokenAmountIn)
    {
        var result = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await QueryV2AmountsOutAsync(
                router,
                tokenAmountIn,
                [tokenAddress, router.WrappedNativeAddress]),
            DexRouterKind.Aerodrome => await QueryAerodromeAmountsOutAsync(
                router,
                tokenAmountIn,
                BuildAerodromeRoutes(router, [tokenAddress, router.WrappedNativeAddress])),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        return result.Last();
    }

    private async Task<BigInteger> GetQuoteAssetTokenAmountOutRawAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        decimal quoteAmount)
    {
        var quoteDecimals = await GetTokenDecimalsAsync(quoteAsset.ContractAddress!);
        var amountIn = UnitConversion.Convert.ToWei(quoteAmount, quoteDecimals);
        var path = new List<string> { quoteAsset.ContractAddress!, router.WrappedNativeAddress, tokenAddress };
        var result = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await QueryV2AmountsOutAsync(router, amountIn, path),
            DexRouterKind.Aerodrome => await QueryAerodromeAmountsOutAsync(router, amountIn, BuildAerodromeRoutes(router, path)),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        return result.Last();
    }

    private async Task<BigInteger> GetTokenAmountOutToQuoteRawAsync(
        DexRouterDefinition router,
        string tokenAddress,
        DexQuoteAssetOption quoteAsset,
        BigInteger tokenAmountIn)
    {
        var path = new List<string> { tokenAddress, router.WrappedNativeAddress, quoteAsset.ContractAddress! };
        var result = router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await QueryV2AmountsOutAsync(router, tokenAmountIn, path),
            DexRouterKind.Aerodrome => await QueryAerodromeAmountsOutAsync(router, tokenAmountIn, BuildAerodromeRoutes(router, path)),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };

        return result.Last();
    }

    private async Task<List<BigInteger>> QueryV2AmountsOutAsync(DexRouterDefinition router, BigInteger amountIn, List<string> path)
    {
        var getAmountsOut = new V2GetAmountsOutFunction
        {
            AmountIn = amountIn,
            Path = path
        };

        var handler = _web3.Eth.GetContractQueryHandler<V2GetAmountsOutFunction>();
        return await handler.QueryAsync<List<BigInteger>>(router.RouterAddress, getAmountsOut);
    }

    private async Task<List<BigInteger>> QueryAerodromeAmountsOutAsync(DexRouterDefinition router, BigInteger amountIn, List<AerodromeRoute> routes)
    {
        var getAmountsOut = new AerodromeGetAmountsOutFunction
        {
            AmountIn = amountIn,
            Routes = routes
        };

        var handler = _web3.Eth.GetContractQueryHandler<AerodromeGetAmountsOutFunction>();
        return await handler.QueryAsync<List<BigInteger>>(router.RouterAddress, getAmountsOut);
    }

    private async Task<ApproveExecutionResult> EnsureAllowanceAsync(string tokenAddress, string spender, BigInteger amount)
    {
        var currentAllowance = await GetTokenAllowanceRawAsync(tokenAddress, spender);
        if (currentAllowance >= amount)
        {
            return new ApproveExecutionResult(false, null, 0m, null);
        }

        return await ApproveAsync(tokenAddress, spender, amount);
    }

    private async Task<ApproveExecutionResult> ApproveAsync(string tokenAddress, string spender, BigInteger amount)
    {
        var approveFunction = new ApproveFunction
        {
            Spender = spender,
            Amount = amount,
            Gas = new HexBigInteger(150000)
        };

        var approveHandler = _web3.Eth.GetContractTransactionHandler<ApproveFunction>();
        var approveReceipt = await approveHandler.SendRequestAndWaitForReceiptAsync(tokenAddress, approveFunction);
        if (approveReceipt.Status.Value != 1)
        {
            throw new Exception("Approve transaction failed");
        }

        return new ApproveExecutionResult(
            true,
            approveReceipt.TransactionHash,
            CalculateNetworkFeeNative(approveReceipt),
            (long?)approveReceipt.GasUsed?.Value);
    }

    private async Task<TransactionReceipt> ExecuteBuyFromV2RouterAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger nativeAmountWei,
        BigInteger amountOutMin)
    {
        var swapFunction = BuildV2BuyFunction(router, tokenAddress, nativeAmountWei, amountOutMin);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactETHForTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteBuyFromAerodromeAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger nativeAmountWei,
        BigInteger amountOutMin)
    {
        var swapFunction = BuildAerodromeBuyFunction(router, tokenAddress, nativeAmountWei, amountOutMin);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactETHForTokensSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteSellToV2RouterAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumNativeOut)
    {
        var swapFunction = BuildV2SellFunction(router, tokenAddress, amountIn, minimumNativeOut, 0m);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactTokensForETHSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteSellToAerodromeAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumNativeOut)
    {
        var swapFunction = BuildAerodromeSellFunction(router, tokenAddress, amountIn, minimumNativeOut, 0m);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactTokensForETHSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteQuoteAssetBuyFromV2RouterAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumAmountOut)
    {
        var swapFunction = BuildV2QuoteAssetBuyFunction(router, quoteAsset, tokenAddress, amountIn, minimumAmountOut);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteQuoteAssetBuyFromAerodromeAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumAmountOut)
    {
        var swapFunction = BuildAerodromeQuoteAssetBuyFunction(router, quoteAsset, tokenAddress, amountIn, minimumAmountOut);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteQuoteAssetSellToV2RouterAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumAmountOut)
    {
        var swapFunction = BuildV2QuoteAssetSellFunction(router, quoteAsset, tokenAddress, amountIn, minimumAmountOut);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<TransactionReceipt> ExecuteQuoteAssetSellToAerodromeAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger minimumAmountOut)
    {
        var swapFunction = BuildAerodromeQuoteAssetSellFunction(router, quoteAsset, tokenAddress, amountIn, minimumAmountOut);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        return await swapHandler.SendRequestAndWaitForReceiptAsync(router.RouterAddress, swapFunction);
    }

    private async Task<DexSellabilityProbeResult> SimulateOwnedSellAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        int tokenDecimals,
        decimal slippagePercent,
        bool primeAllowance,
        decimal? nativeAmountToProbe,
        DexQuoteAssetOption? quoteAsset)
    {
        if (amountIn <= 0)
        {
            return new DexSellabilityProbeResult(
                false,
                false,
                true,
                0m,
                0m,
                null,
                $"Sell simulation on {router.DexId} needs a positive token amount.");
        }

        var expectedNativeOut = quoteAsset is not null
            ? await GetTokenAmountOutToQuoteRawAsync(router, tokenAddress, quoteAsset, amountIn)
            : await GetNativeAmountOutRawAsync(router, tokenAddress, amountIn);
        if (expectedNativeOut <= 0)
        {
            return new DexSellabilityProbeResult(
                false,
                false,
                true,
                0m,
                0m,
                null,
                quoteAsset is null
                    ? $"Sell quote on {router.DexId} returned zero {NativeSymbol} output."
                    : $"Sell quote on {router.DexId} returned zero {quoteAsset.Symbol} output.",
                quoteAsset?.Symbol);
        }

        var allowanceNarrative = "Allowance already covered the sell size.";
        if (primeAllowance)
        {
            var allowanceBefore = await GetTokenAllowanceRawAsync(tokenAddress, router.RouterAddress);
            if (allowanceBefore < amountIn)
            {
                await ApproveAsync(tokenAddress, router.RouterAddress, amountIn);
                allowanceNarrative = "Allowance was primed on-chain for the exact sell size.";
            }
        }
        else
        {
            var allowance = await GetTokenAllowanceRawAsync(tokenAddress, router.RouterAddress);
            if (allowance < amountIn)
            {
                var expectedNativeOutput = quoteAsset is null
                    ? Web3.Convert.FromWei(expectedNativeOut)
                    : UnitConversion.Convert.FromWei(expectedNativeOut, await GetTokenDecimalsAsync(quoteAsset.ContractAddress!));
                var probeNativeAmount = nativeAmountToProbe.GetValueOrDefault();
                return new DexSellabilityProbeResult(
                    false,
                    false,
                    true,
                    UnitConversion.Convert.FromWei(amountIn, tokenDecimals),
                    expectedNativeOutput,
                    probeNativeAmount > 0m
                        ? CalculateRoundTripLossPercent(probeNativeAmount, expectedNativeOutput)
                        : null,
                    $"Sell simulation on {router.DexId} needs ERC-20 allowance before the router can be probed.",
                    quoteAsset?.Symbol);
            }
        }

        return quoteAsset is not null
            ? router.Kind switch
            {
                DexRouterKind.UniswapV2Like => await SimulateV2QuoteAssetSellAsync(
                    router,
                    quoteAsset,
                    tokenAddress,
                    amountIn,
                    tokenDecimals,
                    expectedNativeOut,
                    slippagePercent,
                    allowanceNarrative,
                    nativeAmountToProbe),
                DexRouterKind.Aerodrome => await SimulateAerodromeQuoteAssetSellAsync(
                    router,
                    quoteAsset,
                    tokenAddress,
                    amountIn,
                    tokenDecimals,
                    expectedNativeOut,
                    slippagePercent,
                    allowanceNarrative,
                    nativeAmountToProbe),
                _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
            }
            : router.Kind switch
        {
            DexRouterKind.UniswapV2Like => await SimulateV2SellAsync(
                router,
                tokenAddress,
                amountIn,
                tokenDecimals,
                expectedNativeOut,
                slippagePercent,
                allowanceNarrative,
                nativeAmountToProbe),
            DexRouterKind.Aerodrome => await SimulateAerodromeSellAsync(
                router,
                tokenAddress,
                amountIn,
                tokenDecimals,
                expectedNativeOut,
                slippagePercent,
                allowanceNarrative,
                nativeAmountToProbe),
            _ => throw new NotSupportedException($"Router kind '{router.Kind}' is not implemented.")
        };
    }

    private async Task<DexSellabilityProbeResult> SimulateV2SellAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedNativeOut,
        decimal slippagePercent,
        string allowanceNarrative,
        decimal? nativeAmountToProbe)
    {
        var swapFunction = BuildV2SellFunction(router, tokenAddress, amountIn, expectedNativeOut, slippagePercent);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactTokensForETHSupportingFeeOnTransferTokensFunction>();
        var transactionInput = await swapHandler.CreateTransactionInputEstimatingGasAsync(router.RouterAddress, swapFunction);
        return BuildSuccessfulSimulationResult(
            router,
            amountIn,
            tokenDecimals,
            expectedNativeOut,
            transactionInput.Gas,
            allowanceNarrative,
            nativeAmountToProbe);
    }

    private async Task<DexSellabilityProbeResult> SimulateAerodromeSellAsync(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedNativeOut,
        decimal slippagePercent,
        string allowanceNarrative,
        decimal? nativeAmountToProbe)
    {
        var swapFunction = BuildAerodromeSellFunction(router, tokenAddress, amountIn, expectedNativeOut, slippagePercent);
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactTokensForETHSupportingFeeOnTransferTokensFunction>();
        var transactionInput = await swapHandler.CreateTransactionInputEstimatingGasAsync(router.RouterAddress, swapFunction);
        return BuildSuccessfulSimulationResult(
            router,
            amountIn,
            tokenDecimals,
            expectedNativeOut,
            transactionInput.Gas,
            allowanceNarrative,
            nativeAmountToProbe);
    }

    private async Task<DexSellabilityProbeResult> SimulateV2QuoteAssetSellAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedQuoteOut,
        decimal slippagePercent,
        string allowanceNarrative,
        decimal? nativeAmountToProbe)
    {
        var swapFunction = BuildV2QuoteAssetSellFunction(router, quoteAsset, tokenAddress, amountIn, ApplySlippage(expectedQuoteOut, slippagePercent));
        var swapHandler = _web3.Eth.GetContractTransactionHandler<V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        var transactionInput = await swapHandler.CreateTransactionInputEstimatingGasAsync(router.RouterAddress, swapFunction);
        return await BuildSuccessfulQuoteSimulationResult(router, quoteAsset, amountIn, tokenDecimals, expectedQuoteOut, transactionInput.Gas, allowanceNarrative, nativeAmountToProbe);
    }

    private async Task<DexSellabilityProbeResult> SimulateAerodromeQuoteAssetSellAsync(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedQuoteOut,
        decimal slippagePercent,
        string allowanceNarrative,
        decimal? nativeAmountToProbe)
    {
        var swapFunction = BuildAerodromeQuoteAssetSellFunction(router, quoteAsset, tokenAddress, amountIn, ApplySlippage(expectedQuoteOut, slippagePercent));
        var swapHandler = _web3.Eth.GetContractTransactionHandler<AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction>();
        var transactionInput = await swapHandler.CreateTransactionInputEstimatingGasAsync(router.RouterAddress, swapFunction);
        return await BuildSuccessfulQuoteSimulationResult(router, quoteAsset, amountIn, tokenDecimals, expectedQuoteOut, transactionInput.Gas, allowanceNarrative, nativeAmountToProbe);
    }

    private V2SwapExactTokensForETHSupportingFeeOnTransferTokensFunction BuildV2SellFunction(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger expectedNativeOut,
        decimal slippagePercent)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new V2SwapExactTokensForETHSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = ApplySlippage(expectedNativeOut, slippagePercent),
            Path = new List<string> { tokenAddress, router.WrappedNativeAddress },
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(700000)
        };
    }

    private V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction BuildV2QuoteAssetBuyFunction(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = amountOutMin,
            Path = [quoteAsset.ContractAddress!, router.WrappedNativeAddress, tokenAddress],
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(850000)
        };
    }

    private V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction BuildV2QuoteAssetSellFunction(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new V2SwapExactTokensForTokensSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = amountOutMin,
            Path = [tokenAddress, router.WrappedNativeAddress, quoteAsset.ContractAddress!],
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(850000)
        };
    }

    private V2SwapExactETHForTokensFunction BuildV2BuyFunction(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger nativeAmountWei,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new V2SwapExactETHForTokensFunction
        {
            AmountOutMin = amountOutMin,
            Path = new List<string> { router.WrappedNativeAddress, tokenAddress },
            To = _account.Address,
            Deadline = deadline,
            AmountToSend = nativeAmountWei,
            Gas = new HexBigInteger(500000)
        };
    }

    private AerodromeSwapExactTokensForETHSupportingFeeOnTransferTokensFunction BuildAerodromeSellFunction(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger expectedNativeOut,
        decimal slippagePercent)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new AerodromeSwapExactTokensForETHSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = ApplySlippage(expectedNativeOut, slippagePercent),
            Routes = BuildAerodromeRoutes(router, [tokenAddress, router.WrappedNativeAddress]),
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(800000)
        };
    }

    private AerodromeSwapExactETHForTokensSupportingFeeOnTransferTokensFunction BuildAerodromeBuyFunction(
        DexRouterDefinition router,
        string tokenAddress,
        BigInteger nativeAmountWei,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new AerodromeSwapExactETHForTokensSupportingFeeOnTransferTokensFunction
        {
            AmountOutMin = amountOutMin,
            Routes = BuildAerodromeRoutes(router, [router.WrappedNativeAddress, tokenAddress]),
            To = _account.Address,
            Deadline = deadline,
            AmountToSend = nativeAmountWei,
            Gas = new HexBigInteger(700000)
        };
    }

    private AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction BuildAerodromeQuoteAssetBuyFunction(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = amountOutMin,
            Routes = BuildAerodromeRoutes(router, [quoteAsset.ContractAddress!, router.WrappedNativeAddress, tokenAddress]),
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(950000)
        };
    }

    private AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction BuildAerodromeQuoteAssetSellFunction(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        string tokenAddress,
        BigInteger amountIn,
        BigInteger amountOutMin)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        return new AerodromeSwapExactTokensForTokensSupportingFeeOnTransferTokensFunction
        {
            AmountIn = amountIn,
            AmountOutMin = amountOutMin,
            Routes = BuildAerodromeRoutes(router, [tokenAddress, router.WrappedNativeAddress, quoteAsset.ContractAddress!]),
            To = _account.Address,
            Deadline = deadline,
            Gas = new HexBigInteger(950000)
        };
    }

    private DexSellabilityProbeResult BuildSuccessfulSimulationResult(
        DexRouterDefinition router,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedNativeOut,
        HexBigInteger? gasEstimate,
        string allowanceNarrative,
        decimal? nativeAmountToProbe)
    {
        var tokenAmount = UnitConversion.Convert.FromWei(amountIn, tokenDecimals);
        var expectedNativeOutput = Web3.Convert.FromWei(expectedNativeOut);
        var gasValue = gasEstimate?.Value;
        var gasLabel = gasValue is not null && gasValue > 0
            ? $", gas estimate {gasValue}"
            : string.Empty;
        var probeNativeAmount = nativeAmountToProbe.GetValueOrDefault();
        var roundTripLoss = probeNativeAmount > 0m
            ? CalculateRoundTripLossPercent(probeNativeAmount, expectedNativeOutput)
            : null;

        return new DexSellabilityProbeResult(
            true,
            true,
            true,
            tokenAmount,
            expectedNativeOutput,
            roundTripLoss,
            $"On-chain sell simulation passed on {router.DexId}. {allowanceNarrative} Expected output: {expectedNativeOutput:0.########} {NativeSymbol} for {tokenAmount:0.########} tokens{gasLabel}.");
    }

    private async Task<DexSellabilityProbeResult> BuildSuccessfulQuoteSimulationResult(
        DexRouterDefinition router,
        DexQuoteAssetOption quoteAsset,
        BigInteger amountIn,
        int tokenDecimals,
        BigInteger expectedQuoteOut,
        HexBigInteger? gasEstimate,
        string allowanceNarrative,
        decimal? probeAmount)
    {
        var quoteDecimals = await GetTokenDecimalsAsync(quoteAsset.ContractAddress!);
        var tokenAmount = UnitConversion.Convert.FromWei(amountIn, tokenDecimals);
        var expectedQuoteOutput = UnitConversion.Convert.FromWei(expectedQuoteOut, quoteDecimals);
        var gasValue = gasEstimate?.Value;
        var gasLabel = gasValue is not null && gasValue > 0
            ? $", gas estimate {gasValue}"
            : string.Empty;
        var inputAmount = probeAmount.GetValueOrDefault();
        var roundTripLoss = inputAmount > 0m
            ? CalculateRoundTripLossPercent(inputAmount, expectedQuoteOutput)
            : null;

        return new DexSellabilityProbeResult(
            true,
            true,
            true,
            tokenAmount,
            expectedQuoteOutput,
            roundTripLoss,
            $"On-chain sell simulation passed on {router.DexId}. {allowanceNarrative} Expected output: {expectedQuoteOutput:0.########} {quoteAsset.Symbol} for {tokenAmount:0.########} tokens{gasLabel}.",
            quoteAsset.Symbol);
    }

    private static List<AerodromeRoute> BuildAerodromeRoutes(DexRouterDefinition router, IReadOnlyList<string> path)
    {
        if (string.IsNullOrWhiteSpace(router.FactoryAddress))
        {
            throw new InvalidOperationException($"DEX '{router.DexId}' is missing a factory address for Aerodrome routing.");
        }

        if (path.Count < 2)
        {
            throw new ArgumentException("Aerodrome route path needs at least two addresses.", nameof(path));
        }

        var routes = new List<AerodromeRoute>();
        for (var index = 0; index < path.Count - 1; index++)
        {
            routes.Add(new AerodromeRoute
            {
                From = path[index],
                To = path[index + 1],
                Stable = false,
                Factory = router.FactoryAddress
            });
        }

        return routes;
    }

    private static BigInteger ApplySlippage(BigInteger expectedAmount, decimal slippagePercent)
    {
        var boundedSlippage = Math.Clamp(slippagePercent, 0m, 99m);
        var factor = (100m - boundedSlippage) / 100m;
        return (BigInteger)((decimal)expectedAmount * factor);
    }

    private static decimal? CalculateRoundTripLossPercent(decimal inputNativeAmount, decimal expectedNativeOutput)
    {
        if (inputNativeAmount <= 0m)
        {
            return null;
        }

        return ((inputNativeAmount - expectedNativeOutput) / inputNativeAmount) * 100m;
    }

    private static decimal CalculateNetworkFeeNative(TransactionReceipt receipt)
    {
        if (receipt.GasUsed?.Value is null || receipt.EffectiveGasPrice?.Value is null)
        {
            return 0m;
        }

        var feeWei = receipt.GasUsed.Value * receipt.EffectiveGasPrice.Value;
        return Web3.Convert.FromWei(feeWei);
    }

    private string BuildBuyExecutionNarrative(
        DexRouterDefinition router,
        string transactionHash,
        decimal actualTokenAmount,
        decimal expectedTokenAmount,
        decimal minimumTokenAmount,
        decimal nativeBalanceBefore,
        decimal nativeBalanceAfter,
        bool balanceVerified,
        bool hasUnexpectedOutput,
        bool suspectedPartialFill)
    {
        var spentNativeAmount = Math.Max(0m, nativeBalanceBefore - nativeBalanceAfter);
        var notes = new List<string>();
        if (!balanceVerified)
        {
            notes.Add("token balance did not move after the confirmed receipt");
        }

        if (hasUnexpectedOutput)
        {
            notes.Add($"received {actualTokenAmount:0.########} tokens, below protected minimum {minimumTokenAmount:0.########}");
        }
        else if (suspectedPartialFill)
        {
            notes.Add($"received {actualTokenAmount:0.########} tokens vs quoted {expectedTokenAmount:0.########}");
        }

        if (notes.Count == 0)
        {
            notes.Add($"confirmed receipt and synchronized {actualTokenAmount:0.########} tokens");
        }

        return $"Buy {transactionHash[..Math.Min(10, transactionHash.Length)]} on {router.DexId}: spent {spentNativeAmount:0.########} {NativeSymbol}, {string.Join("; ", notes)}.";
    }

    private sealed record ApproveExecutionResult(
        bool WasRequired,
        string? TransactionHash,
        decimal FeeNative,
        long? GasUsed);
}
