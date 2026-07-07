using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>Minimal read snapshot for the context (mirrors CopilotAgentService rows).</summary>
public sealed record ActionPositionLine(string Symbol, decimal Qty, decimal AvgEntry, decimal Mark, decimal UnrealizedPnl);
public sealed record ActionMarketSnapshot(string Symbol, decimal Bid, decimal Ask, decimal Last);

/// <summary>
/// The ONLY surface actions use to affect the app. Implemented by
/// <see cref="MainWindowAppActionContext"/> over the real view models (UI-thread marshalled),
/// and by a fake in tests. Every method is small and maps to an existing command/method.
/// Read methods return data; mutating methods return an <see cref="AppActionResult"/>.
/// </summary>
public interface IAppActionContext
{
    // ── Autonomous-guard support ──
    string CurrentSymbol { get; }
    Task<decimal> GetTicketNotionalUsdAsync(CancellationToken ct);

    // ── Navigation + reads ──
    AppActionResult NavigateTo(string sectionKey);
    IReadOnlyList<string> KnownSections { get; }
    Task<decimal> GetBalanceUsdtAsync(CancellationToken ct);
    Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct);
    Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct);

    // ── Trading ticket (CEX) ──
    AppActionResult SetTradingSymbol(string symbol);
    AppActionResult SetTicketSide(bool isBuy);
    AppActionResult SetOrderType(string type);          // "Market" | "Limit"
    AppActionResult SetMarketMode(string mode);         // "Spot" | "Futures"
    AppActionResult SetQuantity(decimal quantity);
    AppActionResult SetUsdNotional(decimal usd);        // converts to qty at current price
    AppActionResult SetLeverage(int leverage);
    AppActionResult SetLimitPrice(decimal price);
    AppActionResult SetTakeProfit(decimal price);
    AppActionResult SetStopLoss(decimal price);
    AppActionResult ArmLimit(bool isBuy);
    AppActionResult ArmTakeProfit();
    AppActionResult ArmStopLoss();
    Task<AppActionResult> PlaceMarketAsync(bool isBuy, CancellationToken ct);
    Task<AppActionResult> ClosePositionAsync(CancellationToken ct);

    // ── DEX / perps ──
    Task<AppActionResult> SelectDexTokenAsync(string tokenAddressOrSymbol, CancellationToken ct);
    Task<AppActionResult> DexBuyAsync(decimal amountNative, CancellationToken ct);
    Task<AppActionResult> DexSellAsync(decimal amountTokens, CancellationToken ct);
    AppActionResult SetPerpLiveMode(bool live);

    // ── Signals + alerts ──
    Task<AppActionResult> ApplySignalToTicketAsync(string symbol, string side, CancellationToken ct);
    AppActionResult AddPriceAlert(string symbol, decimal price, bool above);

    // ── Bots / settings / wallet ──
    AppActionResult ConfigureGridBot(string symbol, decimal lower, decimal upper, int levels);
    AppActionResult SelectWalletNetwork(string network);
}
