using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>One open perp position on Hyperliquid.</summary>
public sealed record HyperliquidPosition(
    string Coin,
    decimal Size,          // signed: + long, - short
    decimal EntryPrice,
    decimal PositionValueUsd,
    decimal UnrealizedPnl,
    decimal LiquidationPrice,
    int Leverage,
    string LeverageType,
    decimal MarginUsed)
{
    public bool IsLong => Size >= 0m;
    public string Side => IsLong ? "LONG" : "SHORT";
}

/// <summary>Snapshot of a wallet's Hyperliquid perp account (cross margin summary + positions).</summary>
public sealed record HyperliquidAccountState(
    decimal AccountValueUsd,
    decimal TotalMarginUsedUsd,
    decimal WithdrawableUsd,
    IReadOnlyList<HyperliquidPosition> Positions)
{
    public static readonly HyperliquidAccountState Empty = new(0m, 0m, 0m, Array.Empty<HyperliquidPosition>());
}

/// <summary>
/// Real Hyperliquid perp integration.
///
/// READS (keyless, live now): <c>clearinghouseState</c> returns a wallet's real on-chain
/// positions, margin and PnL — parsed by a pure, unit-tested method.
///
/// ORDER PLACEMENT is intentionally gated: Hyperliquid signs each action with an
/// msgpack action-hash + EIP-712 typed-data scheme that MUST be validated against a
/// funded testnet account before it can move real money. The order-wire builder here is
/// pure and tested, but <see cref="PlaceOrderAsync"/> refuses to send until that
/// validation is wired — so a shipped build can never fire an unproven signed order.
/// </summary>
public sealed class HyperliquidPerpClient
{
    private const string MainnetInfoUrl = "https://api.hyperliquid.xyz/info";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>Fetches a wallet's live perp account state. Returns Empty on any failure.</summary>
    public async Task<HyperliquidAccountState> GetAccountStateAsync(string walletAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return HyperliquidAccountState.Empty;
        }

        try
        {
            var body = $"{{\"type\":\"clearinghouseState\",\"user\":\"{walletAddress}\"}}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(MainnetInfoUrl, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return HyperliquidAccountState.Empty;
            }

            return ParseAccountState(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            return HyperliquidAccountState.Empty;
        }
    }

    /// <summary>Pure parser for a Hyperliquid clearinghouseState response.</summary>
    public static HyperliquidAccountState ParseAccountState(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return HyperliquidAccountState.Empty;
            }

            var accountValue = 0m;
            var marginUsed = 0m;
            if (root.TryGetProperty("marginSummary", out var ms) && ms.ValueKind == JsonValueKind.Object)
            {
                accountValue = Dec(ms, "accountValue");
                marginUsed = Dec(ms, "totalMarginUsed");
            }
            var withdrawable = Dec(root, "withdrawable");

            var positions = new List<HyperliquidPosition>();
            if (root.TryGetProperty("assetPositions", out var aps) && aps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ap in aps.EnumerateArray())
                {
                    if (!ap.TryGetProperty("position", out var p) || p.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var coin = Str(p, "coin");
                    var size = Dec(p, "szi");
                    if (string.IsNullOrWhiteSpace(coin) || size == 0m)
                    {
                        continue;
                    }

                    var lev = 1;
                    var levType = "cross";
                    if (p.TryGetProperty("leverage", out var l) && l.ValueKind == JsonValueKind.Object)
                    {
                        if (l.TryGetProperty("value", out var lv) && lv.ValueKind == JsonValueKind.Number) lev = lv.GetInt32();
                        levType = Str(l, "type") is { Length: > 0 } lt ? lt : "cross";
                    }

                    positions.Add(new HyperliquidPosition(
                        coin!,
                        size,
                        Dec(p, "entryPx"),
                        Dec(p, "positionValue"),
                        Dec(p, "unrealizedPnl"),
                        Dec(p, "liquidationPx"),
                        lev,
                        levType,
                        Dec(p, "marginUsed")));
                }
            }

            return new HyperliquidAccountState(accountValue, marginUsed, withdrawable, positions);
        }
        catch
        {
            return HyperliquidAccountState.Empty;
        }
    }

    /// <summary>
    /// Builds the canonical Hyperliquid order action JSON (exact field order the signer hashes).
    /// Pure and tested. <paramref name="tif"/>: "Gtc" | "Ioc" | "Alo".
    /// </summary>
    public static string BuildOrderActionJson(
        int assetIndex, bool isBuy, decimal limitPrice, decimal size,
        bool reduceOnly, string tif = "Gtc")
    {
        var inv = CultureInfo.InvariantCulture;
        var px = limitPrice.ToString("0.########", inv);
        var sz = size.ToString("0.########", inv);
        var b = isBuy ? "true" : "false";
        var r = reduceOnly ? "true" : "false";
        return "{\"type\":\"order\",\"orders\":[{" +
               $"\"a\":{assetIndex},\"b\":{b},\"p\":\"{px}\",\"s\":\"{sz}\",\"r\":{r}," +
               $"\"t\":{{\"limit\":{{\"tif\":\"{tif}\"}}}}}}],\"grouping\":\"na\"}}";
    }

    /// <summary>Millisecond nonce as required by the exchange endpoint.</summary>
    public static long BuildNonce() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Placing a real order is gated: the msgpack action-hash + EIP-712 signing scheme must be
    /// validated on a funded Hyperliquid testnet account before any customer build can send it.
    /// Until then this throws instead of firing an unproven signed order.
    /// </summary>
    public Task<string> PlaceOrderAsync(
        string walletPrivateKey, int assetIndex, bool isBuy, decimal limitPrice,
        decimal size, bool reduceOnly, string tif = "Gtc", CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Hyperliquid live order placement is not enabled in this build. The order payload builder " +
            "is ready, but the action-hash/EIP-712 signing must first be validated against a funded " +
            "testnet account to guarantee no mis-signed order can move real funds.");
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Dec(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return 0m;
        }

        var raw = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
}
