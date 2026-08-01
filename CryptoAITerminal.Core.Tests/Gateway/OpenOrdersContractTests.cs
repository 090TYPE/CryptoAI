using System;
using System.Reflection;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.Gateway.Bybit;
using CryptoAITerminal.Gateway.KuCoin;
using CryptoAITerminal.Gateway.OKX;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Gateway;

/// <summary>
/// Шлюз, умеющий разместить настоящий ордер, обязан уметь его и перечислить.
///
/// <c>IExchangeGateway.GetOpenOrdersAsync</c> объявлен с телом по умолчанию — <c>[]</c>. Это нужно
/// бумажному и DEX-шлюзам, но для биржевого превращается в тихую ловушку: класс просто не
/// реализует метод, компилятор молчит, и вызывающая сторона получает «живых ордеров нет» вместо
/// ответа биржи. Ровно так и было у спотовых Binance и KuCoin.
///
/// Цена ошибки — не «функция не работает», а торговля вслепую. GridBot считает лимитку
/// исполнившейся по её исчезновению из этого списка:
///   • при нескольких живых ордерах вечно пустой снимок упирается в защиту «снимок недостоверен»,
///     и сетка навсегда перестаёт замечать филлы, продолжая показывать себя работающей;
///   • при ОДНОМ отслеживаемом ордере та же пустота проходит подтверждение за два тика и даёт
///     фантомный филл — бот ставит встречный ордер против позиции, которой нет.
/// </summary>
public class OpenOrdersContractTests
{
    /// <summary>Биржевые шлюзы, размещающие настоящие ордера по ключам пользователя.</summary>
    public static TheoryData<Type> TradingGateways => new()
    {
        typeof(BinanceGateway), typeof(BinanceFuturesGateway),
        typeof(BybitGateway), typeof(BybitFuturesGateway),
        typeof(KucoinGateway), typeof(KucoinFuturesGateway),
        typeof(OKXGateway), typeof(OKXFuturesGateway),
    };

    [Theory]
    [MemberData(nameof(TradingGateways))]
    public void Every_trading_gateway_answers_for_itself(Type gateway)
    {
        var declared = gateway.GetMethod(nameof(IExchangeGateway.GetOpenOrdersAsync),
            BindingFlags.Public | BindingFlags.Instance, [typeof(string)]);

        Assert.True(declared is not null,
            $"{gateway.Name} размещает ордера, но не перечисляет их — сработает заглушка интерфейса " +
            "с пустым списком, и бот примет это за «все лимитки исполнились».");
    }

    /// <summary>Шлюз, живущий на заглушке интерфейса — ровно то состояние, которое ловит тест.</summary>
    private sealed class GatewayOnTheDefault : IExchangeGateway
    {
        public Task ConnectAsync() => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<Core.Models.Order> PlaceOrderAsync(Core.Models.Order order) => Task.FromResult(order);
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> GetBalanceAsync(string asset) => Task.FromResult(0m);
        public Task<Core.Models.OrderBook> GetOrderBookAsync(string symbol, int depth = 10) =>
            Task.FromResult(new Core.Models.OrderBook { Symbol = symbol });
        public IObservable<Core.Models.MarketData> MarketDataStream =>
            System.Reactive.Linq.Observable.Empty<Core.Models.MarketData>();
        public bool HasPrivateApiCredentials => false;
    }

    [Fact]
    public void The_check_itself_can_fail()
    {
        // Проверка через рефлексию тем и опасна, что легко становится пустой: если бы GetMethod
        // находил и унаследованную заглушку, тест выше проходил бы всегда и ничего не значил.
        Assert.Null(typeof(GatewayOnTheDefault).GetMethod(nameof(IExchangeGateway.GetOpenOrdersAsync),
            BindingFlags.Public | BindingFlags.Instance, [typeof(string)]));
    }

    [Fact]
    public void The_interface_default_still_exists_for_gateways_that_do_not_trade()
    {
        // Бумажный/DEX-шлюз и тестовые заглушки на этот метод не отвечают намеренно — заглушку
        // убирать нельзя, иначе они перестанут компилироваться. Тест фиксирует это как решение,
        // а не как недосмотр.
        var method = typeof(IExchangeGateway).GetMethod(nameof(IExchangeGateway.GetOpenOrdersAsync));
        Assert.NotNull(method);
        Assert.False(method!.IsAbstract);
    }
}
