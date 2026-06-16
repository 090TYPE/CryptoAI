using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// One anonymous public trade from the exchange tape (everyone's fills on a symbol).
/// <see cref="Side"/> is the aggressor side: BUY when the taker lifted the ask,
/// SELL when the taker hit the bid.
/// </summary>
public sealed record TapeTrade(
    long     Id,
    DateTime TimeUtc,
    string   Side,
    decimal  Price,
    decimal  Quantity,
    decimal  QuoteQty);

/// <summary>
/// Pure parser for the Binance public recent-trades endpoint
/// (<c>GET /api/v3/trades</c>). No network here so it is fully unit-testable.
/// Response shape: <c>[{"id":..,"price":"..","qty":"..","quoteQty":"..","time":ms,"isBuyerMaker":bool}]</c>.
/// </summary>
public static class MarketTapeParser
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
            // The endpoint returns an array; clone the root so it outlives the document.
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return trades; // malformed payload → empty tape, never throw at the UI
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return trades;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var price = ReadDecimal(item, "price");
            var qty = ReadDecimal(item, "qty");
            if (price <= 0m || qty <= 0m)
            {
                continue;
            }

            var quote = TryGet(item, "quoteQty", out var q) ? ParseDecimal(q) : price * qty;
            var isBuyerMaker = TryGet(item, "isBuyerMaker", out var m) && m.ValueKind == JsonValueKind.True;
            var id = TryGet(item, "id", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0L;
            var timeMs = TryGet(item, "time", out var t) && t.TryGetInt64(out var ms) ? ms : 0L;

            trades.Add(new TapeTrade(
                Id: id,
                TimeUtc: timeMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime : DateTime.UtcNow,
                Side: isBuyerMaker ? "SELL" : "BUY", // buyer is the maker ⇒ aggressor sold
                Price: price,
                Quantity: qty,
                QuoteQty: quote));
        }

        return trades;
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value) =>
        obj.TryGetProperty(name, out value);

    private static decimal ReadDecimal(JsonElement obj, string name) =>
        TryGet(obj, name, out var el) ? ParseDecimal(el) : 0m;

    private static decimal ParseDecimal(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
        JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m,
        _ => 0m
    };
}

/// <summary>Aggregate read-out of a window of tape trades (buy/sell pressure + large prints).</summary>
public sealed record MarketTapeStats(
    int     TradeCount,
    decimal BuyQuoteVolume,
    decimal SellQuoteVolume,
    int     LargePrintCount)
{
    /// <summary>Buy share of total quote volume in [0,1]; 0.5 when balanced, 0 when empty.</summary>
    public decimal BuyPressure
    {
        get
        {
            var total = BuyQuoteVolume + SellQuoteVolume;
            return total <= 0m ? 0m : BuyQuoteVolume / total;
        }
    }

    public static MarketTapeStats Compute(IEnumerable<TapeTrade> trades, decimal largePrintQuoteThreshold)
    {
        var count = 0;
        decimal buy = 0m, sell = 0m;
        var large = 0;

        foreach (var t in trades)
        {
            count++;
            if (t.Side == "SELL")
            {
                sell += t.QuoteQty;
            }
            else
            {
                buy += t.QuoteQty;
            }

            if (largePrintQuoteThreshold > 0m && t.QuoteQty >= largePrintQuoteThreshold)
            {
                large++;
            }
        }

        return new MarketTapeStats(count, buy, sell, large);
    }
}
