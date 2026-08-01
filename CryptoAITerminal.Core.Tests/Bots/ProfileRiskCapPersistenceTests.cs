using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Профиль конфигурации сохранял AI-бота и грид подробно, у DCA — только биржу, а настройки
/// AI-трейдера, автономного агента и трейлинг-стопа не сохранял ВООБЩЕ. Первые два — это
/// риск-лимиты, под которыми ходят настоящие деньги.
///
/// Наружу это выходило так: человек выставляет на вкладке «Боты» свои $10 за ордер и $100 общей
/// экспозиции, сохраняет профиль, видит подтверждение. После перезапуска загружает профиль, снова
/// видит подтверждение — а движки поднимаются с заводскими значениями. То есть лимиты молча
/// РАСШИРЯЮТСЯ, и подтверждение об успешной загрузке этому не мешает.
///
/// Отдельная тонкость — совместимость: у профилей, сохранённых до появления этих полей, все они
/// нулевые. Ноль применять нельзя, иначе загрузка старого файла обнулит лимит, а ноль в потолке
/// экспозиции читается как «без потолка» — то самое расширение, только с другой стороны.
/// </summary>
public class ProfileRiskCapPersistenceTests
{
    [Fact]
    public void The_agent_risk_caps_are_part_of_a_profile_now()
    {
        var p = new TradingProfile
        {
            AiTraderMaxOrderUsd = 10m,
            AiTraderMaxExposureUsd = 100m,
            AiTraderMaxOpenPositions = 2,
            AiTraderMaxDailyLossUsd = 20m,
        };

        // Round-trip через сам тип: поля обязаны существовать и переживать сериализацию.
        var json = System.Text.Json.JsonSerializer.Serialize(p);
        var back = System.Text.Json.JsonSerializer.Deserialize<TradingProfile>(json)!;

        Assert.Equal(10m, back.AiTraderMaxOrderUsd);
        Assert.Equal(100m, back.AiTraderMaxExposureUsd);
        Assert.Equal(2, back.AiTraderMaxOpenPositions);
        Assert.Equal(20m, back.AiTraderMaxDailyLossUsd);
    }

    [Fact]
    public void An_old_profile_carries_zeros_which_must_never_be_applied()
    {
        // Так выглядит файл, сохранённый предыдущей версией: новых ключей в нём просто нет.
        var old = System.Text.Json.JsonSerializer.Deserialize<TradingProfile>(
            """{"Name":"legacy","BotSymbol":"BTCUSDT","GridLevels":10}""")!;

        Assert.Equal(0m, old.AiTraderMaxOrderUsd);
        Assert.Equal(0m, old.AiTraderMaxExposureUsd);
        Assert.Equal(0, old.AiTraderMaxOpenPositions);
        Assert.Equal(0m, old.AgentSessionBudgetUsd);
        Assert.Equal(0m, old.DcaBudgetPerCycle);
        Assert.Equal(string.Empty, old.AiTraderExchange);

        // −1 у режима трейлинга — тот же смысл «профиль этого не знает», но 0 там валидный индекс.
        Assert.Equal(-1, old.TrailTypeIndex);
    }

    [Fact]
    public void The_dca_schedule_is_part_of_a_profile_now()
    {
        var p = new TradingProfile { DcaBudgetPerCycle = 250m, DcaIntervalType = "Weeks", DcaIntervalValue = 2 };

        var back = System.Text.Json.JsonSerializer.Deserialize<TradingProfile>(
            System.Text.Json.JsonSerializer.Serialize(p))!;

        Assert.Equal(250m, back.DcaBudgetPerCycle);
        Assert.Equal("Weeks", back.DcaIntervalType);
        Assert.Equal(2, back.DcaIntervalValue);
    }

    [Fact]
    public void The_agent_session_guardrails_are_part_of_a_profile_now()
    {
        var p = new TradingProfile { AgentSessionBudgetUsd = 75m, AgentMaxTrades = 4, AgentAllowlist = "BTC,ETH" };

        var back = System.Text.Json.JsonSerializer.Deserialize<TradingProfile>(
            System.Text.Json.JsonSerializer.Serialize(p))!;

        Assert.Equal(75m, back.AgentSessionBudgetUsd);
        Assert.Equal(4, back.AgentMaxTrades);
        Assert.Equal("BTC,ETH", back.AgentAllowlist);
    }

    [Fact]
    public void The_trailing_stop_setup_is_part_of_a_profile_now()
    {
        var p = new TradingProfile { TrailTypeIndex = 0, TrailPctDistance = 2.5m, TrailAtrPeriod = 21, TrailAtrMultiplier = 3m };

        var back = System.Text.Json.JsonSerializer.Deserialize<TradingProfile>(
            System.Text.Json.JsonSerializer.Serialize(p))!;

        Assert.Equal(0, back.TrailTypeIndex);          // 0 — валидный режим, не «неизвестно»
        Assert.Equal(2.5m, back.TrailPctDistance);
        Assert.Equal(21, back.TrailAtrPeriod);
        Assert.Equal(3m, back.TrailAtrMultiplier);
    }
}
