using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Флагманская страница «AI Signals» переставала обращаться к модели после первого же открытия,
/// даже если модель тогда не ответила.
///
/// Флаг «уже загружено» ставился в тот момент, когда появлялись рынки — ДО обращения к модели.
/// Дальше <c>EnsureLoadedAsync</c> выходил по нему сразу, и страница до конца сеанса показывала
/// «offline · heuristic», пока человек не догадывался нажать REFRESH. Дефект был пойман живьём:
/// сервер записал два оплаченных вызова <c>signal_desk</c> к anthropic/claude-sonnet-5, а на
/// экране в это же время стояла эвристика.
///
/// Здесь проверяется само правило: пока живой ответ не оказался на экране, автогенерация обязана
/// повторяться при следующем открытии вкладки.
/// </summary>
public class AiSignalDeskRetryTests
{
    private static AiSignalDeskContext Context() => new(
        [new AiSignalMarketRow("BTC", "BTC/USDT", "Binance", 64000m, 1.2m, 0.01m, 0.8m, "B", "#f7931a")],
        []);

    [Fact]
    public async Task Opening_the_tab_again_retries_while_nothing_live_has_landed()
    {
        int passes = 0;
        var vm = new AiSignalDeskViewModel(() => { passes++; return Context(); }, service: null);

        await vm.EnsureLoadedAsync();
        await vm.EnsureLoadedAsync();
        await vm.EnsureLoadedAsync();

        // До правки здесь была единица: первый же проход, увидев рынки, запирал страницу.
        Assert.Equal(3, passes);
    }

    [Fact]
    public async Task The_badge_never_names_a_model_over_content_the_model_did_not_produce()
    {
        var vm = new AiSignalDeskViewModel(Context, service: null);

        await vm.EnsureLoadedAsync();

        Assert.Equal("offline · heuristic", vm.SourceLabel);
        Assert.Equal(string.Empty, vm.AiError);   // ничего не пытались — не о чем и докладывать
    }

    [Fact]
    public async Task The_feed_still_mirrors_the_tracked_markets_without_a_model()
    {
        var vm = new AiSignalDeskViewModel(Context, service: null);

        await vm.EnsureLoadedAsync();

        Assert.NotEmpty(vm.FeedItems);
        Assert.Contains(vm.FeedItems, c => c.Sym.Contains("BTC"));
    }

    [Fact]
    public async Task The_heatmap_is_built_even_without_a_model()
    {
        // Свечи — данные площадки, модель для них не нужна. Карта обязана быть осмысленной и
        // на эвристике, иначе нижний блок страницы не несёт информации вообще.
        var vm = new AiSignalDeskViewModel(Context, service: null);

        await vm.EnsureLoadedAsync();

        Assert.NotEmpty(vm.HeatmapRows);
        Assert.NotEmpty(vm.HeatmapTimeframes);
        Assert.All(vm.HeatmapRows, r => Assert.Equal(vm.HeatmapTimeframes.Count, r.Cells.Count));
    }

    [Fact]
    public async Task A_context_provider_that_throws_does_not_take_the_page_down()
    {
        // Провайдер контекста живёт в чужой вьюмодели и может упасть на любой мелочи; страница
        // обязана это пережить и показать эвристику.
        var vm = new AiSignalDeskViewModel(() => throw new System.InvalidOperationException("boom"),
            service: null);

        await vm.EnsureLoadedAsync();

        Assert.Equal("offline · heuristic", vm.SourceLabel);
    }
}
