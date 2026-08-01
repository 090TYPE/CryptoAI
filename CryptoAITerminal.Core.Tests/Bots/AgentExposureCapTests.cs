using System.Collections.Generic;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Потолок общей экспозиции — последний заслон перед отправкой РЕАЛЬНОГО ордера ключом
/// пользователя. Считался он так:
///
///     open.Sum(p =&gt; Math.Abs(p.Quantity) * mid)
///
/// где mid — цена символа, который агент торгует ПРЯМО СЕЙЧАС. Ею оценивались все открытые
/// позиции сразу, независимо от того, в какой монете каждая. Ошибка снимает потолок в обе стороны,
/// и обе стороны плохи: дешёвая торгуемая монета обнуляет вес дорогих позиций и агент проезжает
/// сквозь лимит, дорогая — раздувает дешёвые до сотен миллионов и агент запирается навсегда.
/// </summary>
public class AgentExposureCapTests
{
    private static FuturesPosition Pos(string symbol, decimal qty, decimal mark = 0m, decimal entry = 0m) =>
        new() { Symbol = symbol, Quantity = qty, MarkPrice = mark, EntryPrice = entry };

    [Fact]
    public void Every_position_is_valued_at_its_own_price()
    {
        var open = new List<FuturesPosition>
        {
            Pos("BTCUSDT", 1m, mark: 64_000m),
            Pos("ETHUSDT", 10m, mark: 3_200m),
        };

        // Агент торгует DOGE по 0.37 — на оценку чужих позиций это влиять не должно.
        var exposure = AiTraderAgentService.TotalExposureUsd(open, "DOGEUSDT", mid: 0.37m);

        Assert.Equal(96_000m, exposure);   // 64 000 + 32 000
    }

    [Fact]
    public void A_cheap_traded_symbol_no_longer_hides_an_expensive_book()
    {
        // Ровно тот случай, который проезжал сквозь потолок: 1 BTC при торговле DOGE оценивался
        // в 0.37 USD, и лимит в 500 USD пропускал заявку.
        var open = new List<FuturesPosition> { Pos("BTCUSDT", 1m, mark: 64_000m) };

        var exposure = AiTraderAgentService.TotalExposureUsd(open, "DOGEUSDT", mid: 0.37m);

        Assert.Equal(64_000m, exposure);
        Assert.True(exposure > 500m, "позиция на 64 000 USD обязана упереться в потолок 500 USD");
    }

    [Fact]
    public void An_expensive_traded_symbol_no_longer_inflates_a_cheap_book()
    {
        // Обратная сторона: 10 000 DOGE при торговле биткоином давали 640 000 000 USD и
        // блокировали агента навсегда.
        var open = new List<FuturesPosition> { Pos("DOGEUSDT", 10_000m, mark: 0.37m) };

        var exposure = AiTraderAgentService.TotalExposureUsd(open, "BTCUSDT", mid: 64_000m);

        Assert.Equal(3_700m, exposure);
    }

    [Fact]
    public void Entry_price_stands_in_when_the_venue_sends_no_mark()
    {
        var open = new List<FuturesPosition> { Pos("BTCUSDT", 2m, entry: 60_000m) };

        Assert.Equal(120_000m, AiTraderAgentService.TotalExposureUsd(open, "ETHUSDT", mid: 3_200m));
    }

    [Fact]
    public void Mark_price_wins_over_entry_price()
    {
        var open = new List<FuturesPosition> { Pos("BTCUSDT", 1m, mark: 64_000m, entry: 60_000m) };

        Assert.Equal(64_000m, AiTraderAgentService.TotalExposureUsd(open, "BTCUSDT", mid: 1m));
    }

    [Fact]
    public void With_no_price_at_all_the_mid_is_used_only_for_the_traded_pair()
    {
        var open = new List<FuturesPosition>
        {
            Pos("BTCUSDT", 1m),   // торгуемая пара — mid относится к ней
            Pos("ETHUSDT", 5m),   // чужая пара без цены — оценить её mid'ом было бы выдумкой
        };

        Assert.Equal(64_000m, AiTraderAgentService.TotalExposureUsd(open, "BTCUSDT", mid: 64_000m));
    }

    [Fact]
    public void A_short_counts_toward_exposure_the_same_as_a_long()
    {
        var open = new List<FuturesPosition> { Pos("BTCUSDT", -1m, mark: 64_000m) };

        Assert.Equal(64_000m, AiTraderAgentService.TotalExposureUsd(open, "BTCUSDT", mid: 64_000m));
    }

    [Fact]
    public void An_empty_book_is_zero_exposure()
    {
        Assert.Equal(0m, AiTraderAgentService.TotalExposureUsd([], "BTCUSDT", mid: 64_000m));
    }
}
