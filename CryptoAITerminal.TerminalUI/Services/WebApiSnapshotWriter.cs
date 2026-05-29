using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CryptoAITerminal.Core.Models;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Дампит snapshot состояния (позиции, кандидаты снайпера, PnL summary) в shared
/// JSON-файл, который читает <c>CryptoAITerminal.WebApi</c>. Контракт — `Models/WebApiDtos.cs`
/// в WebApi-проекте; имена полей сериализуются в camelCase.
///
/// Запускается из <see cref="ViewModels.MainWindowViewModel"/>; работает в фоновом
/// потоке через <see cref="Timer"/>; не зависит от Avalonia UI thread.
/// </summary>
public sealed class WebApiSnapshotWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _snapshotPath;
    private readonly Func<SnapshotPayload> _snapshotProvider;
    private readonly Timer _timer;
    private int _writing; // 0 — idle, 1 — writing (Interlocked гард)

    public string SnapshotPath => _snapshotPath;

    public WebApiSnapshotWriter(Func<SnapshotPayload> snapshotProvider, TimeSpan? interval = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CryptoAITerminal", "webapi");
        Directory.CreateDirectory(dir);
        _snapshotPath = Path.Combine(dir, "snapshot.json");

        var dueTime = TimeSpan.FromSeconds(2);
        var period  = interval ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Tick(), null, dueTime, period);
    }

    public void WriteNow() => Tick();

    private void Tick()
    {
        if (Interlocked.Exchange(ref _writing, 1) == 1) return;
        try
        {
            var payload = _snapshotProvider();
            var json = JsonSerializer.Serialize(new
            {
                updatedUtc = DateTime.UtcNow,
                positions  = payload.Positions,
                candidates = payload.Candidates,
                pnl        = payload.Pnl,
                balances   = payload.Balances,
            }, JsonOpts);

            var tmp = _snapshotPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _snapshotPath, overwrite: true);
        }
        catch
        {
            // не валим терминал из-за фонового снапшота
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    // ── Payload contract — должен совпадать по camelCase именам с
    //    CryptoAITerminal.WebApi.Models.WebApiDtos. ───────────────────────────

    public sealed class SnapshotPayload
    {
        public List<PositionDto>        Positions  { get; init; } = new();
        public List<SniperCandidateDto> Candidates { get; init; } = new();
        public PnlSummaryDto            Pnl        { get; init; } = new();
        public List<BalanceDto>         Balances   { get; init; } = new();
    }

    public sealed class BalanceDto
    {
        public string  Exchange { get; set; } = "";
        public string  Market   { get; set; } = "";
        public string  Asset    { get; set; } = "";
        public decimal Amount   { get; set; }
    }

    public sealed class PositionDto
    {
        public string  Source         { get; set; } = "";
        public string  Symbol         { get; set; } = "";
        public string  Side           { get; set; } = "";
        public decimal Quantity       { get; set; }
        public decimal EntryPrice     { get; set; }
        public decimal MarkPrice      { get; set; }
        public decimal UnrealizedPnl  { get; set; }
        public decimal LeverageOrOne  { get; set; } = 1m;
    }

    public sealed class SniperCandidateDto
    {
        public string  Chain          { get; set; } = "";
        public string  Symbol         { get; set; } = "";
        public string  Name           { get; set; } = "";
        public string  PairAddress    { get; set; } = "";
        public decimal PriceUsd       { get; set; }
        public decimal LiquidityUsd   { get; set; }
        public decimal Volume24h      { get; set; }
        public int     RankScore      { get; set; }
        public string  RankBand       { get; set; } = "";
        public string  RiskBand       { get; set; } = "";
        public bool    IsOpenPosition { get; set; }
    }

    public sealed class PnlSummaryDto
    {
        public decimal RealizedPnlUsd   { get; set; }
        public decimal UnrealizedPnlUsd { get; set; }
        public int     TradesToday      { get; set; }
        public int     OpenPositions    { get; set; }
        public decimal WinRatePercent   { get; set; }
    }

    // ── Mapping helpers — used by MainWindowViewModel snapshot provider. ─────

    public static PositionDto FromFuturesPosition(string source, FuturesPosition p) => new()
    {
        Source        = source,
        Symbol        = p.Symbol,
        Side          = p.PositionSide.ToString(),
        Quantity      = p.Quantity,
        EntryPrice    = p.EntryPrice,
        MarkPrice     = p.MarkPrice,
        UnrealizedPnl = p.UnrealizedPnl,
        LeverageOrOne = Math.Max(1m, p.Leverage),
    };

    public static SniperCandidateDto FromCandidate(SniperCandidateViewModel c) => new()
    {
        Chain          = c.TokenInfo.ChainId,
        Symbol         = c.TokenInfo.Symbol,
        Name           = c.TokenInfo.Name,
        PairAddress    = c.TokenInfo.PairAddress,
        PriceUsd       = c.TokenInfo.PriceUsd,
        LiquidityUsd   = c.TokenInfo.LiquidityUsd,
        Volume24h      = c.TokenInfo.Volume24h,
        RankScore      = c.RankScore,
        RankBand       = c.RankScoreBand,
        RiskBand       = c.RiskBand,
        IsOpenPosition = c.IsOpenPosition,
    };
}
