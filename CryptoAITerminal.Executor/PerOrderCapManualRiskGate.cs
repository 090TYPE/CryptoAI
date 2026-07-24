using CryptoAITerminal.Core.Contracts;

namespace CryptoAITerminal.Executor;

/// <summary>Gate a manual order before it is placed. Mirrors the server bot risk gates.</summary>
public interface IManualRiskGate
{
    (bool Ok, string? Reason) Check(PlaceMarketCommand cmd, decimal price);
}

/// <summary>Rejects a manual order whose notional (qty × price) exceeds a per-order USD cap.</summary>
public sealed class PerOrderCapManualRiskGate : IManualRiskGate
{
    private readonly decimal _maxNotionalUsd;
    public PerOrderCapManualRiskGate(decimal maxNotionalUsd) => _maxNotionalUsd = maxNotionalUsd;

    public (bool Ok, string? Reason) Check(PlaceMarketCommand cmd, decimal price)
    {
        if (cmd.Quantity <= 0m) return (false, "Quantity must be positive.");
        if (price <= 0m) return (false, "No reference price to size the order.");
        var notional = cmd.Quantity * price;
        return notional > _maxNotionalUsd
            ? (false, $"Order notional {notional:C} exceeds per-order cap {_maxNotionalUsd:C}.")
            : (true, null);
    }
}
