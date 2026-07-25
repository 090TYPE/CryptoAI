using System;
using System.Collections.Generic;
using System.Globalization;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Health figures for the desk footers. It reports only what the app can really observe:
/// how many CEX gateways hold usable API credentials, and how many orders were actually
/// broadcast in the last minute. The order counter stays null until an execution path
/// calls <see cref="RecordOrderSent"/> — nothing here is estimated or back-filled.
/// </summary>
public sealed class GatewayHealthService
{
    public static GatewayHealthService Instance { get; } = new();

    /// <summary>Binance, Bybit, OKX, KuCoin — the CEX gateways the shell wires up.</summary>
    private const int GatewayCount = 4;

    private static readonly TimeSpan CredentialTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrderWindow = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly List<DateTime> _sentUtc = new();
    private bool _counterFed;

    private DateTime _credentialsAt = DateTime.MinValue;
    private int _credentialed;

    /// <summary>
    /// "usable/total" over the CEX gateways: a gateway counts as usable when its API key is
    /// present, either as an environment variable or in the saved credentials file. The
    /// credentials file is decrypted at most once a minute.
    /// </summary>
    public string GatewaysLabel
    {
        get
        {
            RefreshCredentials();
            lock (_lock)
                return _credentialed.ToString(CultureInfo.InvariantCulture) + "/" +
                       GatewayCount.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void RefreshCredentials()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _credentialsAt < CredentialTtl) return;
            _credentialsAt = DateTime.UtcNow;
            try
            {
                var r = CredentialsService.Load();
                int n = 0;
                if (r.BinanceSource != CredentialsService.CredentialSource.None) n++;
                if (r.BybitSource != CredentialsService.CredentialSource.None) n++;
                if (r.OkxSource != CredentialsService.CredentialSource.None) n++;
                if (r.KucoinSource != CredentialsService.CredentialSource.None) n++;
                _credentialed = n;
            }
            catch
            {
                // credentials file missing or being rewritten — keep the previous count
            }
        }
    }

    /// <summary>Call once per order actually broadcast to an exchange. Thread-safe.</summary>
    public void RecordOrderSent()
    {
        lock (_lock)
        {
            _counterFed = true;
            _sentUtc.Add(DateTime.UtcNow);
            Prune();
        }
    }

    /// <summary>
    /// Orders broadcast in the last minute, or null while nothing feeds the counter — the
    /// caller must not read null as zero.
    /// </summary>
    public int? OrdersLastMinute
    {
        get
        {
            lock (_lock)
            {
                if (!_counterFed) return null;
                Prune();
                return _sentUtc.Count;
            }
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - OrderWindow;
        _sentUtc.RemoveAll(t => t < cutoff);
    }
}
