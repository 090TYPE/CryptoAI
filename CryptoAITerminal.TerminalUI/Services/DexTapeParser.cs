using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Pure parser for the GeckoTerminal public pool-trades endpoint
/// (<c>GET /api/v2/networks/{network}/pools/{pool}/trades</c>) → on-chain DEX swaps.
/// Unlike a CEX tape, each swap carries the originating wallet (<c>tx_from_address</c>).
/// No network here, so it is fully unit-testable.
/// </summary>
public static class DexTapeParser
{
    public static IReadOnlyList<TapeTrade> Parse(string? json)
    {
        var trades = new List<TapeTrade>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return trades;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return trades;
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return trades;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("attributes", out var a) ||
                a.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var volumeUsd = ReadDecimal(a, "volume_in_usd");
            // The volatile leg's USD price is the larger of the two (the other leg ≈ $1 stable).
            var priceFrom = ReadDecimal(a, "price_from_in_usd");
            var priceTo = ReadDecimal(a, "price_to_in_usd");
            var assetPrice = Math.Max(priceFrom, priceTo);
            if (volumeUsd <= 0m && assetPrice <= 0m)
            {
                continue;
            }

            var quantity = assetPrice > 0m ? volumeUsd / assetPrice : ReadDecimal(a, "to_token_amount");
            var kind = ReadString(a, "kind");
            var time = ReadString(a, "block_timestamp");

            trades.Add(new TapeTrade(
                Id: 0,
                TimeUtc: ParseTimestamp(time),
                Side: string.Equals(kind, "sell", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY",
                Price: assetPrice,
                Quantity: quantity,
                QuoteQty: volumeUsd,
                Venue: "DEX",
                Trader: ReadString(a, "tx_from_address"),
                TxHash: ReadString(a, "tx_hash")));
        }

        return trades;
    }

    private static DateTime ParseTimestamp(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime
            : DateTime.UtcNow;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
        {
            return 0m;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m
        };
    }
}
