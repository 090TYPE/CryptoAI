namespace CryptoAITerminal.Executor;

/// <summary>Approves or blocks a bot order before it reaches the exchange. Blocked → recorded and user notified.</summary>
public interface IRiskGate
{
    (bool Ok, string? Reason) Check(BotIntent intent);
}

/// <summary>Hard per-order USD notional cap — the minimum guardrail before enabling live trading.</summary>
public sealed class PerOrderCapRiskGate : IRiskGate
{
    private readonly decimal _maxNotionalUsd;
    public PerOrderCapRiskGate(decimal maxNotionalUsd) => _maxNotionalUsd = maxNotionalUsd;

    public (bool Ok, string? Reason) Check(BotIntent intent)
    {
        var notional = intent.NotionalUsd.GetValueOrDefault();
        if (notional > _maxNotionalUsd)
            return (false, $"order ${notional} exceeds per-order cap ${_maxNotionalUsd}");
        return (true, null);
    }
}
