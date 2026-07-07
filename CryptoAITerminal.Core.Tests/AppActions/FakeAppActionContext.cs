using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;

namespace CryptoAITerminal.Core.Tests.AppActions;

/// <summary>Records every call so action tests can assert the right bridge method fired.</summary>
public sealed class FakeAppActionContext : IAppActionContext
{
    public readonly List<string> Calls = new();
    public decimal BalanceUsdt = 1000m;
    public ActionMarketSnapshot? Market = new("BTCUSDT", 99_990m, 100_010m, 100_000m);
    public IReadOnlyList<ActionPositionLine> Positions = new List<ActionPositionLine>();
    public string CurrentSymbol { get; set; } = "BTCUSDT";
    public decimal TicketNotionalUsd = 100m;
    public Task<decimal> GetTicketNotionalUsdAsync(CancellationToken ct) => Task.FromResult(TicketNotionalUsd);

    private AppActionResult Log(string call) { Calls.Add(call); return AppActionResult.Ok(call); }

    public IReadOnlyList<string> KnownSections => new[] { "trading", "aisignals", "markets", "portfolio", "bots", "settings" };
    public AppActionResult NavigateTo(string s) => Log($"NavigateTo:{s}");
    public Task<decimal> GetBalanceUsdtAsync(CancellationToken ct) { Calls.Add("GetBalance"); return Task.FromResult(BalanceUsdt); }
    public Task<IReadOnlyList<ActionPositionLine>> GetOpenPositionsAsync(CancellationToken ct) { Calls.Add("GetPositions"); return Task.FromResult(Positions); }
    public Task<ActionMarketSnapshot?> GetMarketAsync(string symbol, CancellationToken ct) { Calls.Add($"GetMarket:{symbol}"); return Task.FromResult(Market); }

    public AppActionResult SetTradingSymbol(string s) => Log($"SetSymbol:{s}");
    public AppActionResult SetTicketSide(bool b) => Log($"SetSide:{(b ? "buy" : "sell")}");
    public AppActionResult SetOrderType(string t) => Log($"SetOrderType:{t}");
    public AppActionResult SetMarketMode(string m) => Log($"SetMarketMode:{m}");
    public AppActionResult SetQuantity(decimal q) => Log($"SetQty:{q}");
    public AppActionResult SetUsdNotional(decimal u) => Log($"SetUsd:{u}");
    public AppActionResult SetLeverage(int l) => Log($"SetLeverage:{l}");
    public AppActionResult SetLimitPrice(decimal p) => Log($"SetLimit:{p}");
    public AppActionResult SetTakeProfit(decimal p) => Log($"SetTp:{p}");
    public AppActionResult SetStopLoss(decimal p) => Log($"SetSl:{p}");
    public AppActionResult ArmLimit(bool b) => Log($"ArmLimit:{(b ? "buy" : "sell")}");
    public AppActionResult ArmTakeProfit() => Log("ArmTp");
    public AppActionResult ArmStopLoss() => Log("ArmSl");
    public Task<AppActionResult> PlaceMarketAsync(bool b, CancellationToken ct) => Task.FromResult(Log($"PlaceMarket:{(b ? "buy" : "sell")}"));
    public Task<AppActionResult> ClosePositionAsync(CancellationToken ct) => Task.FromResult(Log("Close"));
    public Task<AppActionResult> SelectDexTokenAsync(string t, CancellationToken ct) => Task.FromResult(Log($"SelectDex:{t}"));
    public Task<AppActionResult> DexBuyAsync(decimal a, CancellationToken ct) => Task.FromResult(Log($"DexBuy:{a}"));
    public Task<AppActionResult> DexSellAsync(decimal a, CancellationToken ct) => Task.FromResult(Log($"DexSell:{a}"));
    public AppActionResult SetPerpLiveMode(bool l) => Log($"PerpLive:{l}");
    public Task<AppActionResult> ApplySignalToTicketAsync(string s, string side, CancellationToken ct) => Task.FromResult(Log($"ApplySignal:{s}:{side}"));
    public AppActionResult AddPriceAlert(string s, decimal p, bool above) => Log($"Alert:{s}:{p}:{(above ? "above" : "below")}");
    public AppActionResult ConfigureGridBot(string s, decimal lo, decimal hi, int n) => Log($"Grid:{s}:{lo}:{hi}:{n}");
    public AppActionResult SelectWalletNetwork(string net) => Log($"Wallet:{net}");
}
