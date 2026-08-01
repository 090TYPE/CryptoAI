using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Трейлинг-стоп не запрашивает цены сам: и тик, и команду на закрытие ему даёт главная
/// вьюмодель, а та работает с символом, открытым на торговой вкладке СЕЙЧАС. Пока стоп жил
/// только на торговой вкладке, это совпадало с ожиданием человека.
///
/// На вкладке «Боты» он стал строкой TRAIL с кнопкой запуска — то есть «ботом», который в голове
/// пользователя охраняет конкретную позицию. Привязки к символу при этом не было никакой:
/// вооружил на BTC, переключил график на ETH — и стоп продолжал тикать, трейля цену ETH от пика,
/// набранного на BTC. Сработав, он звал ExecuteClosePosition, а тот рыночно закрывает позицию по
/// АКТИВНОМУ символу. То есть стоп, поставленный на биткоин, продавал эфир.
/// </summary>
public class TrailingStopSymbolTests
{
    [Fact]
    public void Arming_remembers_the_symbol()
    {
        var vm = new AdvancedTrailingStopViewModel();

        vm.Arm(entryPrice: 64000m, symbol: "BTCUSDT");

        Assert.True(vm.IsArmed);
        Assert.Equal("BTCUSDT", vm.ArmedSymbol);
        Assert.Contains("BTCUSDT", vm.StatusLabel);
    }

    [Fact]
    public void The_stop_guards_the_symbol_it_was_armed_on_and_no_other()
    {
        var vm = new AdvancedTrailingStopViewModel();
        vm.Arm(64000m, "BTCUSDT");

        Assert.True(vm.Guards("BTCUSDT"));
        Assert.True(vm.Guards("btcusdt"));    // регистр с биржи приходит по-разному
        Assert.False(vm.Guards("ETHUSDT"));   // вот на этом и продавался чужой актив
        Assert.False(vm.Guards(null));
    }

    [Fact]
    public void Disarming_releases_the_symbol()
    {
        var vm = new AdvancedTrailingStopViewModel();
        vm.Arm(64000m, "BTCUSDT");

        vm.Disarm();

        Assert.False(vm.IsArmed);
        Assert.Equal(string.Empty, vm.ArmedSymbol);
    }

    [Fact]
    public void Disarming_can_say_why()
    {
        var vm = new AdvancedTrailingStopViewModel();
        vm.Arm(64000m, "BTCUSDT");

        vm.Disarm("Позиция закрыта вручную");

        Assert.Equal("Позиция закрыта вручную", vm.StatusLabel);
    }

    [Fact]
    public void Arming_without_a_symbol_guards_everything_as_before()
    {
        // Старый вызов с одной лишь ценой обязан работать по-прежнему: правка добавляет
        // ограничение там, где символ известен, а не ломает вызывающих, которые его не знают.
        var vm = new AdvancedTrailingStopViewModel();

        vm.Arm(64000m);

        Assert.Equal(string.Empty, vm.ArmedSymbol);
        Assert.True(vm.Guards("BTCUSDT"));
        Assert.True(vm.Guards("ETHUSDT"));
    }

    [Fact]
    public void Re_arming_on_another_symbol_moves_the_guard_across()
    {
        var vm = new AdvancedTrailingStopViewModel();
        vm.Arm(64000m, "BTCUSDT");

        vm.Arm(3200m, "ETHUSDT");

        Assert.True(vm.Guards("ETHUSDT"));
        Assert.False(vm.Guards("BTCUSDT"));
    }
}
