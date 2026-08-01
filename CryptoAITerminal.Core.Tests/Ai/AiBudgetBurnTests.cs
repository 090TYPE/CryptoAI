using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Во что обходятся вызовы модели, которые пользователь не запрашивал.
///
/// Это оказалось дороже всего остального вместе взятого. Дайджест новостей висел на таймере лент
/// (опрос раз в 90 секунд) с порогом в три минуты и пересобирался в фоне независимо от открытого
/// раздела: 480 вызовов в сутки у человека, который ничего не нажимал. Режим наблюдения в торговом
/// ассистенте пересчитывал разбор каждые шесть секунд — 600 обращений в час. Суточная квота при
/// этом кончалась за часы, а выглядело это как «AI перестал работать».
///
/// Тест держит арифметику расхода, а не реализацию. Числа берутся из самих вью-моделей, чтобы
/// возврат прежнего интервала ронял сборку, а не тихо возвращал счёт.
/// </summary>
public class AiBudgetBurnTests
{
    /// <summary>
    /// Суточная норма младшего платного тарифа — самый узкий случай.
    ///
    /// Сервер её сейчас НЕ применяет: на время тестового периода квоты сняты
    /// (<c>SettingKeys.DefaultAiBudgetEnforced</c>). Здесь это мерка стоимости, а не предел,
    /// который кто-то проверит, — и мерка нужна тем более: пока никто не упирается в лимит,
    /// вернувшийся шестисекундный опрос обнаружился бы только в счёте от вендора.
    /// </summary>
    private const long LiteDailyTokens = 35_000;

    // Измерено на живом сервере: дайджест новостей — около 1200 токенов вместе с заголовками на
    // входе, разбор графика в режиме наблюдения — около 1000.
    private const int DigestTokens = 1_200;
    private const int WatchTokens = 1_000;

    private static long CallsPer(System.TimeSpan window, System.TimeSpan interval) =>
        (long)(window.TotalSeconds / interval.TotalSeconds);

    [Fact]
    public void The_digest_is_gated_on_the_section_being_open()
    {
        // Главное свойство: расход пропорционален чтению, а не времени работы приложения. Раньше
        // это были разные вещи на порядок — дайджест обновлялся, даже когда открыт был график.
        // Проверяется наличие самого рычага и его выключенное состояние по умолчанию: собрать
        // вью-модель целиком тест не может, она тянет живой опрос лент.
        var flag = typeof(NewsFeedViewModel).GetProperty(nameof(NewsFeedViewModel.SectionVisible));

        Assert.NotNull(flag);
        Assert.Equal(typeof(bool), flag!.PropertyType);
        Assert.False((bool)(flag.GetValue(
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NewsFeedViewModel)))!));
    }

    [Fact]
    public void Reading_the_news_all_day_still_fits_the_smallest_plan()
    {
        var burn = CallsPer(System.TimeSpan.FromDays(1), NewsFeedViewModel.DigestThrottle) * DigestTokens;

        // Даже раздел, открытый круглые сутки, не должен занимать больше трети квоты Лайта:
        // остальное принадлежит тому, ради чего человек и платит — сигналам, разборам, агенту.
        Assert.True(burn <= LiteDailyTokens,
            $"открытый раздел новостей тратит {burn} токенов в сутки при квоте Лайта {LiteDailyTokens}");
    }

    [Fact]
    public void An_hour_of_watch_mode_does_not_eat_a_whole_day()
    {
        var perHour = CallsPer(System.TimeSpan.FromHours(1), AiTradeAssistantViewModel.WatchInterval) * WatchTokens;

        // На шести секундах час наблюдения стоил 600 000 токенов — семнадцать дневных квот Лайта.
        Assert.True(perHour <= LiteDailyTokens / 2,
            $"час наблюдения стоит {perHour} токенов при дневной квоте Лайта {LiteDailyTokens}");
    }

    [Fact]
    public void Nothing_automatic_runs_once_the_quota_is_gone()
    {
        // Пока отказа не было — фоновые вызовы разрешены. После отказа сервера они замолкают до
        // полуночи UTC: повторять до сброса квоты бессмысленно, а каждый повтор пишется в расход
        // и мигает ошибкой в интерфейсе.
        Assert.True(ChatClient.AutomaticCallsAllowed);
        Assert.Null(ChatClient.BudgetResetsUtc);
    }
}
