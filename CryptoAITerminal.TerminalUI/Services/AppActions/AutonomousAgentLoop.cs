using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services.AppActions;

/// <summary>
/// Drives the agent autonomously: every interval it runs one agent turn (which acts through a
/// guarded, guardrail-enforcing context). The loop halts when the session guardrails trip
/// (budget / max-trades) or when <see cref="Stop"/> (kill-switch) is called or the outer token
/// cancels. The turn delegate is injected so this is unit-testable without a network model.
/// </summary>
public sealed class AutonomousAgentLoop
{
    private readonly Func<string, CancellationToken, Task<string>> _runTurn;
    private readonly AgentGuardrails _rails;
    private CancellationTokenSource? _cts;

    public AutonomousAgentLoop(Func<string, CancellationToken, Task<string>> runTurn, AgentGuardrails rails)
    {
        _runTurn = runTurn;
        _rails = rails;
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Status line stream for the UI (started / each turn's reply / stopped:reason).</summary>
    public event Action<string>? OnStatus;

    public async Task RunAsync(string objective, TimeSpan interval, CancellationToken ct)
    {
        _rails.Reset();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        OnStatus?.Invoke("Autonomous session started.");
        try
        {
            while (!token.IsCancellationRequested && !_rails.Stopped)
            {
                try
                {
                    var reply = await _runTurn(objective, token).ConfigureAwait(false);
                    OnStatus?.Invoke(reply);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { OnStatus?.Invoke("error: " + ex.Message); }

                if (_rails.Stopped || token.IsCancellationRequested) break;

                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            OnStatus?.Invoke(_rails.Stopped ? $"Stopped: {_rails.StopReason}" : "Stopped.");
            _cts.Cancel();
        }
    }

    /// <summary>Kill-switch: stop immediately; no further turns run.</summary>
    public void Stop() => _cts?.Cancel();
}
