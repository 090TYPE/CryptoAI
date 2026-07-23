using CryptoAITerminal.Core.Enums;

namespace CryptoAITerminal.Core.Contracts;

/// <summary>Command to open/close a CEX futures position at market. Quantity is a POSITIVE
/// base-asset magnitude; direction is Side + PositionSide. ClientOrderId drives idempotency.</summary>
public sealed record PlaceMarketCommand(
    string Exchange, string Symbol, OrderSide Side, decimal Quantity, bool ReduceOnly,
    int Leverage, FuturesMarginMode MarginMode, FuturesPositionSide PositionSide, string ClientOrderId);

public sealed record PlaceOrderResult(bool Accepted, string? OrderId, string? RejectReason);
public sealed record CancelResult(bool Ok, string? Error);

/// <summary>Open futures position. Quantity is SIGNED (long +, short -).</summary>
public sealed record FuturesPositionDto(
    string Symbol, decimal Quantity, decimal EntryPrice, decimal MarkPrice,
    decimal UnrealizedPnl, decimal LiquidationPrice, int Leverage);

public sealed record OrderStatusDto(
    string OrderId, string ClientOrderId, string Status, decimal FilledQty, decimal AvgPrice, System.DateTime UpdatedUtc);

public sealed record NotificationDto(string Kind, string Severity, string Message, System.DateTime AtUtc);
