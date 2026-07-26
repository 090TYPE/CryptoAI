using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>
/// The real <see cref="IAppActionContext"/> over the live <see cref="MainWindowViewModel"/>.
/// Every member marshals to the UI thread and delegates to a thin <c>*FromAgent</c> wrapper
/// on the relevant view model, so all existing RiskManager / WalletVM / testnet gates run —
/// the agent cannot bypass them. This class contains NO trading logic of its own.
/// </summary>
public sealed class MainWindowAppActionContext : IAppActionContext
{
    private readonly MainWindowViewModel _vm;

    public MainWindowAppActionContext(MainWindowViewModel vm)
        => _vm = vm ?? throw new ArgumentNullException(nameof(vm));

    // ── UI-thread marshalling helpers ───────────────────────────────────────
    private static T OnUi<T>(Func<T> func) =>
        Dispatcher.UIThread.CheckAccess()
            ? func()
            : Dispatcher.UIThread.InvokeAsync(func).GetAwaiter().GetResult();

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }
    }

    private static async Task<T> OnUiAsync<T>(Func<Task<T>> func) =>
        Dispatcher.UIThread.CheckAccess()
            ? await func().ConfigureAwait(false)
            : await Dispatcher.UIThread.InvokeAsync(func).ConfigureAwait(false);

    // ── Autonomous-guard support ────────────────────────────────────────────
    public string CurrentSymbol => _vm.SelectedTradingSymbol;

    public Task<decimal> GetTicketNotionalUsdAsync(CancellationToken ct)
        => Task.FromResult(OnUi(() => _vm.TradeNotional));

    // ── Navigation + reads ──────────────────────────────────────────────────
    public IReadOnlyList<string> KnownSections => _vm.KnownSectionKeys;

    public AppActionResult NavigateTo(string sectionKey)
    {
        var key = sectionKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!_vm.KnownSectionKeys.Contains(key))
        {
            return AppActionResult.Fail($"unknown section '{sectionKey}'");
        }

        Dispatcher.UIThread.Post(() => _vm.SelectMainTabCommand.Execute(key).Subscribe());
        return AppActionResult.Ok($"Navigating to {key}");
    }

    public Task<decimal> GetBalanceUsdtAsync(CancellationToken ct)
        => Task.FromResult(OnUi(() => _vm.AvailableBalanceUsdt));

    // Reports only the active manual-desk symbol (SelectedTradingSymbol): the manual trading desk
    // is single-symbol by design, so multi-symbol exposure is not surfaced here.
    public Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct)
        => Task.FromResult(OnUi<IReadOnlyList<ActionPositionLine>>(() =>
            _vm.PositionQuantity != 0m
                ? new[]
                {
                    new ActionPositionLine(
                        _vm.SelectedTradingSymbol,
                        _vm.PositionQuantity,
                        _vm.AverageEntryPrice,
                        _vm.CurrentTradePrice,
                        _vm.UnrealizedPnl),
                }
                : Array.Empty<ActionPositionLine>()));

    public Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct)
        => Task.FromResult(OnUi(() =>
        {
            var row = _vm.MarketFeedVM.Markets.FirstOrDefault(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            return row is null
                ? (ActionMarketSnapshot?)null
                : new ActionMarketSnapshot(row.Symbol, row.BestBid, row.BestAsk, row.LastPrice);
        }));

    // ── Trading ticket (CEX) ────────────────────────────────────────────────
    public AppActionResult SetTradingSymbol(string symbol) => OnUi(() => _vm.TrySelectTradingSymbol(symbol));

    public AppActionResult SetTicketSide(bool isBuy)
    {
        OnUi(() => _vm.SetTicketSideFromAgent(isBuy));
        return AppActionResult.Ok($"Ticket side set to {(isBuy ? "BUY" : "SELL")}");
    }

    public AppActionResult SetOrderType(string type)
    {
        OnUi(() => _vm.SelectOrderTypeFromAgent(type));
        return AppActionResult.Ok($"Order type set to {_vm.SelectedOrderType}");
    }

    public AppActionResult SetMarketMode(string mode)
    {
        OnUi(() => _vm.SelectMarketModeFromAgent(mode));
        return AppActionResult.Ok($"Market mode set to {_vm.SelectedCexMarketMode}");
    }

    public AppActionResult SetQuantity(decimal quantity)
    {
        if (quantity <= 0m)
        {
            return AppActionResult.Fail("quantity must be > 0");
        }

        OnUi(() => _vm.SetQuantityFromAgent(quantity));
        return AppActionResult.Ok($"Quantity set to {quantity}");
    }

    public AppActionResult SetUsdNotional(decimal usd) => OnUi(() => _vm.SetTradeUsdFromAgent(usd));

    public AppActionResult SetLeverage(int leverage)
    {
        if (leverage < 1)
        {
            return AppActionResult.Fail("leverage must be >= 1");
        }

        OnUi(() => _vm.SetLeverageFromAgent(leverage));
        return AppActionResult.Ok($"Leverage set to {_vm.ManualFuturesLeverage}x");
    }

    public AppActionResult SetLimitPrice(decimal price)
    {
        if (price <= 0m)
        {
            return AppActionResult.Fail("limit price must be > 0");
        }

        OnUi(() => _vm.SetLimitPriceFromAgent(price));
        return AppActionResult.Ok($"Limit price set to {price}");
    }

    public AppActionResult SetTakeProfit(decimal price)
    {
        if (price <= 0m)
        {
            return AppActionResult.Fail("take-profit price must be > 0");
        }

        OnUi(() => _vm.SetTakeProfitFromAgent(price));
        return AppActionResult.Ok($"Take-profit price set to {price}");
    }

    public AppActionResult SetStopLoss(decimal price)
    {
        if (price <= 0m)
        {
            return AppActionResult.Fail("stop-loss price must be > 0");
        }

        OnUi(() => _vm.SetStopLossFromAgent(price));
        return AppActionResult.Ok($"Stop-loss price set to {price}");
    }

    public AppActionResult ArmLimit(bool isBuy) => OnUi(() => _vm.ArmLimitFromAgent(isBuy));

    public AppActionResult ArmTakeProfit() => OnUi(() => _vm.ArmTakeProfitFromAgent());

    public AppActionResult ArmStopLoss() => OnUi(() => _vm.ArmStopLossFromAgent());

    public Task<AppActionResult> PlaceMarketAsync(bool isBuy, CancellationToken ct)
        => OnUiAsync(() => _vm.PlaceMarketFromAgent(isBuy));

    public Task<AppActionResult> ClosePositionAsync(CancellationToken ct)
        => OnUiAsync(() => _vm.ClosePositionFromAgent());

    // ── DEX / perps ─────────────────────────────────────────────────────────
    public Task<AppActionResult> SelectDexTokenAsync(string tokenAddressOrSymbol, CancellationToken ct)
        => OnUiAsync(() => _vm.DexDeskVM.Swap.SelectTokenFromAgent(tokenAddressOrSymbol));

    public Task<AppActionResult> DexBuyAsync(decimal amountNative, CancellationToken ct)
        => OnUiAsync(() => _vm.DexDeskVM.Swap.DexBuyFromAgent(amountNative));

    public Task<AppActionResult> DexSellAsync(decimal amountTokens, CancellationToken ct)
        => OnUiAsync(() => _vm.DexDeskVM.Swap.DexSellFromAgent(amountTokens));

    public AppActionResult SetPerpLiveMode(bool live) => OnUi(() => _vm.SetPerpLiveFromAgent(live));

    // ── Signals + alerts ────────────────────────────────────────────────────
    public Task<AppActionResult> ApplySignalToTicketAsync(string symbol, string side, CancellationToken ct)
        => Task.FromResult(OnUi(() => _vm.ApplySignalToTicketFromAgent(symbol, side)));

    public AppActionResult AddPriceAlert(string symbol, decimal price, bool above)
        => OnUi(() => _vm.AddPriceAlertFromAgent(symbol, price, above));

    // ── Bots / settings / wallet ────────────────────────────────────────────
    public AppActionResult ConfigureGridBot(string symbol, decimal lower, decimal upper, int levels)
        => OnUi(() => _vm.GridBotVM.ConfigureFromAgent(symbol, lower, upper, levels));

    public AppActionResult SelectWalletNetwork(string network)
        => OnUi(() => _vm.WalletVM.SelectNetworkFromAgent(network));
}
