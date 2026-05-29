using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Core.Models;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Executes large orders using TWAP (Time-Weighted Average Price) or Iceberg strategy
/// to reduce market impact and avoid front-running.
///
/// TWAP: split total qty into N equal slices, execute one slice every intervalSeconds.
/// Iceberg: continuously post small visible portions until total qty is filled.
/// </summary>
public sealed class TwapExecutorService
{
    private readonly IExchangeGateway _gateway;
    private readonly Action<string>?  _logger;

    public TwapExecutorService(IExchangeGateway gateway, Action<string>? logger = null)
    {
        _gateway = gateway;
        _logger  = logger;
    }

    // ── TWAP ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a TWAP order: places <paramref name="slices"/> market orders of equal size,
    /// one every <paramref name="intervalSeconds"/> seconds.
    /// </summary>
    public async Task<TwapResult> ExecuteTwapAsync(
        string  symbol,
        string  side,
        decimal totalQty,
        int     slices,
        int     intervalSeconds,
        CancellationToken ct = default)
    {
        slices          = Math.Max(1, Math.Min(slices, 100));
        intervalSeconds = Math.Max(5, intervalSeconds);
        var sliceQty    = Math.Round(totalQty / slices, 8);

        var filled = new List<TwapSliceFill>(slices);
        var errors = new List<string>();

        Log($"[TWAP] {side} {totalQty} {symbol} in {slices} slices × {sliceQty} every {intervalSeconds}s");

        for (var i = 0; i < slices; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var order = new Order
                {
                    Symbol   = symbol,
                    Side     = side == "Buy"
                        ? CryptoAITerminal.Core.Enums.OrderSide.Buy
                        : CryptoAITerminal.Core.Enums.OrderSide.Sell,
                    Type     = CryptoAITerminal.Core.Enums.OrderType.Market,
                    Quantity = sliceQty,
                };
                var result = await _gateway.PlaceOrderAsync(order);
                filled.Add(new TwapSliceFill(i + 1, sliceQty, result.Id ?? string.Empty, DateTime.UtcNow));
                Log($"[TWAP] Slice {i + 1}/{slices} filled. OrderId={result.Id}");
            }
            catch (Exception ex)
            {
                errors.Add($"Slice {i + 1}: {ex.Message}");
                Log($"[TWAP] Slice {i + 1}/{slices} failed: {ex.Message}");
            }

            if (i < slices - 1)
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }

        var totalFilled = filled.Count * sliceQty;
        var success     = errors.Count == 0;
        Log($"[TWAP] Complete. Filled={totalFilled:0.########} / {totalQty}. Errors={errors.Count}");

        return new TwapResult(symbol, side, totalQty, totalFilled, filled, errors, success);
    }

    // ── Iceberg ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes an Iceberg order: places a small visible order, waits for fill,
    /// then places the next. Repeats until total qty is filled or cancelled.
    /// </summary>
    public async Task<TwapResult> ExecuteIcebergAsync(
        string  symbol,
        string  side,
        decimal totalQty,
        decimal visibleQty,
        int     pollIntervalSeconds = 10,
        CancellationToken ct = default)
    {
        visibleQty = Math.Min(visibleQty, totalQty);
        var remaining = totalQty;
        var filled    = new List<TwapSliceFill>();
        var errors    = new List<string>();

        Log($"[ICEBERG] {side} {totalQty} {symbol}, visible={visibleQty}/slice");

        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            var sliceQty = Math.Min(visibleQty, remaining);
            try
            {
                var order = new Order
                {
                    Symbol   = symbol,
                    Side     = side == "Buy"
                        ? CryptoAITerminal.Core.Enums.OrderSide.Buy
                        : CryptoAITerminal.Core.Enums.OrderSide.Sell,
                    Type     = CryptoAITerminal.Core.Enums.OrderType.Market,
                    Quantity = sliceQty,
                };
                var result = await _gateway.PlaceOrderAsync(order);
                filled.Add(new TwapSliceFill(filled.Count + 1, sliceQty, result.Id ?? string.Empty, DateTime.UtcNow));
                remaining -= sliceQty;
                Log($"[ICEBERG] Filled {sliceQty}. Remaining={remaining:0.########}");
            }
            catch (Exception ex)
            {
                errors.Add($"Slice {filled.Count + 1}: {ex.Message}");
                Log($"[ICEBERG] Slice failed: {ex.Message}");
            }

            if (remaining > 0)
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
        }

        var totalFilled = totalQty - remaining;
        Log($"[ICEBERG] Complete. Filled={totalFilled:0.########}/{totalQty}. Errors={errors.Count}");
        return new TwapResult(symbol, side, totalQty, totalFilled, filled, errors, errors.Count == 0);
    }

    private void Log(string msg) => _logger?.Invoke(msg);
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record TwapSliceFill(
    int      SliceNumber,
    decimal  Qty,
    string   OrderId,
    DateTime FilledAtUtc);

public sealed record TwapResult(
    string   Symbol,
    string   Side,
    decimal  TotalOrderedQty,
    decimal  TotalFilledQty,
    IReadOnlyList<TwapSliceFill> Fills,
    IReadOnlyList<string> Errors,
    bool     Success)
{
    public decimal FillPercent => TotalOrderedQty > 0
        ? TotalFilledQty / TotalOrderedQty * 100m
        : 0m;

    public string Summary => Success
        ? $"TWAP/Iceberg: filled {TotalFilledQty:0.########} / {TotalOrderedQty} {Symbol} ({FillPercent:F1}%)"
        : $"TWAP/Iceberg partial: {TotalFilledQty:0.########} filled, {Errors.Count} error(s)";
}
