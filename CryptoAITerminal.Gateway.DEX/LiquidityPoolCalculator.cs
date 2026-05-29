using System;
using System.Numerics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Impermanent Loss and LP position calculator for Uniswap V2 and V3.
///
/// V2: constant product formula x·y = k
/// V3: concentrated liquidity between tick range [lower, upper]
///
/// Reads on-chain LP positions via RPC (EVM) for wallet-specific queries.
/// </summary>
public sealed class LiquidityPoolCalculator
{
    // ── V2 Impermanent Loss ────────────────────────────────────────────────────

    /// <summary>
    /// Calculates Uniswap V2 impermanent loss given entry and current price ratio.
    /// priceRatio = currentPrice / entryPrice (e.g. 2.0 means price doubled).
    /// Returns IL as a negative percentage (e.g. -5.7 means you lost 5.7% vs HODL).
    /// </summary>
    public static decimal CalculateV2ImpermanentLoss(decimal priceRatio)
    {
        if (priceRatio <= 0m) return 0m;
        // IL = 2√k / (1+k) - 1  where k = priceRatio
        var k      = (double)priceRatio;
        var il     = 2.0 * Math.Sqrt(k) / (1.0 + k) - 1.0;
        return (decimal)(il * 100.0); // negative percent
    }

    /// <summary>
    /// Full V2 LP position P&amp;L breakdown.
    /// </summary>
    public static V2LpPositionResult CalculateV2Position(
        decimal token0EntryQty,
        decimal token1EntryQty,
        decimal entryPrice,    // token0 price in token1 (e.g. ETH price in USDC)
        decimal currentPrice,
        decimal feesEarned0 = 0m,
        decimal feesEarned1 = 0m)
    {
        if (entryPrice <= 0m || currentPrice <= 0m)
            return V2LpPositionResult.Zero;

        var priceRatio = currentPrice / entryPrice;

        // Current LP holdings (after price movement, constant product)
        var sqrtRatio      = (decimal)Math.Sqrt((double)priceRatio);
        var currentToken0  = token0EntryQty / sqrtRatio;
        var currentToken1  = token1EntryQty * sqrtRatio;

        // HODL value at current price
        var hodlValue      = token0EntryQty * currentPrice + token1EntryQty;

        // LP value at current price
        var lpValue        = currentToken0 * currentPrice + currentToken1;

        // Add fees
        var feesValue      = feesEarned0 * currentPrice + feesEarned1;
        var lpValueWithFees = lpValue + feesValue;

        var il             = lpValue - hodlValue;
        var ilPercent      = hodlValue > 0 ? il / hodlValue * 100m : 0m;
        var netPnl         = lpValueWithFees - hodlValue;
        var netPnlPercent  = hodlValue > 0 ? netPnl / hodlValue * 100m : 0m;

        return new V2LpPositionResult(
            Token0Qty:      currentToken0,
            Token1Qty:      currentToken1,
            LpValueUsd:     lpValue,
            HodlValueUsd:   hodlValue,
            ImpermanentLoss: il,
            IlPercent:      ilPercent,
            FeesEarned:     feesValue,
            NetPnl:         netPnl,
            NetPnlPercent:  netPnlPercent,
            PriceRatio:     priceRatio);
    }

    // ── V3 Impermanent Loss ────────────────────────────────────────────────────

    /// <summary>
    /// Calculates Uniswap V3 impermanent loss for a concentrated position.
    /// All prices in same units (e.g. USDC per ETH).
    /// </summary>
    public static decimal CalculateV3ImpermanentLoss(
        decimal entryPrice,
        decimal currentPrice,
        decimal priceLower,
        decimal priceUpper)
    {
        if (entryPrice <= 0m || priceLower <= 0m || priceUpper <= 0m || priceLower >= priceUpper)
            return 0m;

        // Convert to sqrt prices for V3 math
        var sqrtCurrent = Math.Sqrt((double)currentPrice);
        var sqrtLower   = Math.Sqrt((double)priceLower);
        var sqrtUpper   = Math.Sqrt((double)priceUpper);
        var sqrtEntry   = Math.Sqrt((double)entryPrice);

        // Liquidity L from entry position (assume $1 of entry value for normalization)
        // L = 1 / (sqrtEntry - sqrtLower + (1/sqrtEntry - 1/sqrtUpper) * entryPrice)
        var denom  = sqrtEntry - sqrtLower + (1.0 / sqrtEntry - 1.0 / sqrtUpper) * (double)entryPrice;
        if (denom <= 0) return 0m;
        var L = 1.0 / denom;

        // Value of position at entry (normalized to $1)
        var entryVal = 1.0;

        // Value at current price
        double currentVal;
        var sqrtC = Math.Clamp(sqrtCurrent, sqrtLower, sqrtUpper);
        if (currentPrice <= priceLower)
        {
            // All in token0
            currentVal = L * (1.0 / sqrtLower - 1.0 / sqrtUpper) * (double)currentPrice;
        }
        else if (currentPrice >= priceUpper)
        {
            // All in token1
            currentVal = L * (sqrtUpper - sqrtLower);
        }
        else
        {
            // In range
            var val0 = L * (1.0 / sqrtC - 1.0 / sqrtUpper) * (double)currentPrice;
            var val1 = L * (sqrtC - sqrtLower);
            currentVal = val0 + val1;
        }

        // HODL value: half token0, half token1 at entry
        var hodlVal = 0.5 * (double)currentPrice / (double)entryPrice + 0.5;

        var il = (currentVal - hodlVal) / hodlVal * 100.0;
        return (decimal)il;
    }

    // ── On-chain position query (EVM) ─────────────────────────────────────────

    public static async Task<V2PoolInfo?> GetV2PoolInfoAsync(
        string pairAddress,
        string rpcUrl,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // Call getReserves() on the pair contract
        // getReserves() returns (uint112 reserve0, uint112 reserve1, uint32 blockTimestampLast)
        var getReservesData = "0x0902f1ac"; // keccak256("getReserves()")
        var payload = $$"""
            {"jsonrpc":"2.0","method":"eth_call","params":[
              {"to":"{{pairAddress}}","data":"{{getReservesData}}"},
              "latest"
            ],"id":1}
            """;

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(rpcUrl, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonNode.Parse(body)?["result"]?.GetValue<string>();
        if (string.IsNullOrEmpty(result) || result.Length < 130) return null;

        // Decode: 32 bytes reserve0, 32 bytes reserve1, 32 bytes timestamp
        var hex = result[2..]; // strip 0x
        var r0 = BigInteger.Parse("0" + hex[..64], System.Globalization.NumberStyles.HexNumber);
        var r1 = BigInteger.Parse("0" + hex[64..128], System.Globalization.NumberStyles.HexNumber);

        var reserve0 = (decimal)r0 / 1e18m;
        var reserve1 = (decimal)r1 / 1e18m;
        var price    = reserve0 > 0 ? reserve1 / reserve0 : 0m;

        return new V2PoolInfo(pairAddress, reserve0, reserve1, price);
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record V2LpPositionResult(
    decimal Token0Qty,
    decimal Token1Qty,
    decimal LpValueUsd,
    decimal HodlValueUsd,
    decimal ImpermanentLoss,
    decimal IlPercent,
    decimal FeesEarned,
    decimal NetPnl,
    decimal NetPnlPercent,
    decimal PriceRatio)
{
    public static readonly V2LpPositionResult Zero =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    public string IlLabel    => $"{IlPercent:+0.00;-0.00;0}%";
    public string IlBrush    => IlPercent >= 0 ? "#21E6C1" : "#FF6B6B";
    public string NetPnlLabel => $"{(NetPnl >= 0 ? "+" : "")}{NetPnl:F4} ({NetPnlPercent:+0.00;-0.00;0}%)";
    public string NetPnlBrush => NetPnl >= 0 ? "#3DDC84" : "#FF6B6B";
    public string PriceRatioLabel => $"×{PriceRatio:0.###}";
    public bool   IsOutOfRange => PriceRatio <= 0.01m || PriceRatio >= 100m;
}

public sealed record V2PoolInfo(
    string  PairAddress,
    decimal Reserve0,
    decimal Reserve1,
    decimal PriceToken1PerToken0)
{
    public string PriceLabel => $"{PriceToken1PerToken0:0.######}";
}
