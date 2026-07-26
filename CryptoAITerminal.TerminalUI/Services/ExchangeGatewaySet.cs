using System;
using System.Collections.Generic;
using System.Linq;
using CryptoAITerminal.Core.Interfaces;
using CryptoAITerminal.Gateway.Binance;
using CryptoAITerminal.Gateway.Bybit;
using CryptoAITerminal.Gateway.KuCoin;
using CryptoAITerminal.Gateway.OKX;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Every exchange gateway the terminal talks to, built in one place from one set of credentials.
///
/// This used to be forty-odd lines in the middle of MainWindowViewModel's constructor: ten
/// <c>new</c> expressions interleaved with credential locals, testnet lookups and two unrelated
/// commands. Pulling it out is the seam a container would plug into later — and it means the
/// question "which gateways exist and how are they configured?" has one answer in one file.
/// </summary>
public sealed class ExchangeGatewaySet
{
    public required BinanceGateway BinanceSpot { get; init; }
    public required BinanceFuturesGateway BinanceFutures { get; init; }
    public required IExchangeGateway BybitSpot { get; init; }
    public required IExchangeGateway BybitFutures { get; init; }
    public required IExchangeGateway OkxSpot { get; init; }
    public required IExchangeGateway OkxFutures { get; init; }
    public required IExchangeGateway KucoinSpot { get; init; }
    public required IExchangeGateway KucoinFutures { get; init; }

    /// <summary>Spot gateways by exchange name, for order book / price / placement routing.</summary>
    public IReadOnlyDictionary<string, IExchangeGateway> SpotByExchange => BuildMap(
        BinanceSpot, BybitSpot, OkxSpot, KucoinSpot);

    /// <summary>Futures gateways by exchange name, mirroring <see cref="SpotByExchange"/>.</summary>
    public IReadOnlyDictionary<string, IExchangeGateway> FuturesByExchange => BuildMap(
        BinanceFutures, BybitFutures, OkxFutures, KucoinFutures);

    private static Dictionary<string, IExchangeGateway> BuildMap(
        IExchangeGateway binance, IExchangeGateway bybit, IExchangeGateway okx, IExchangeGateway kucoin) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Binance"] = binance,
            ["Bybit"]   = bybit,
            ["OKX"]     = okx,
            ["KuCoin"]  = kucoin,
        };

    /// <summary>
    /// Builds the whole set. Testnet opt-ins are read once here because a gateway's environment is
    /// fixed when its client is built — switching takes a restart, and the Settings toggle says so.
    /// </summary>
    /// <param name="spotSymbols">Symbols the Binance spot socket subscribes to (defaults plus custom).</param>
    /// <param name="futuresSymbols">Symbols the futures and secondary gateways track.</param>
    public static ExchangeGatewaySet Create(
        IEnumerable<string> spotSymbols,
        IEnumerable<string> futuresSymbols,
        CredentialsService.AllCredentials creds)
    {
        ArgumentNullException.ThrowIfNull(creds);

        var spot = spotSymbols as IReadOnlyList<string> ?? spotSymbols.ToList();
        var futures = futuresSymbols as IReadOnlyList<string> ?? futuresSymbols.ToList();

        var testBinance = ExchangeEnvironmentStore.IsTestnet("binance");
        var testBybit   = ExchangeEnvironmentStore.IsTestnet("bybit");
        var testOkx     = ExchangeEnvironmentStore.IsTestnet("okx");

        return new ExchangeGatewaySet
        {
            BinanceSpot    = new BinanceGateway(spot, creds.BinanceKey, creds.BinanceSecret, testBinance),
            BinanceFutures = new BinanceFuturesGateway(futures, creds.BinanceKey, creds.BinanceSecret, testBinance),
            BybitSpot      = new BybitGateway(futures, creds.BybitKey, creds.BybitSecret, testBybit),
            BybitFutures   = new BybitFuturesGateway(futures, creds.BybitKey, creds.BybitSecret, testBybit),
            OkxSpot        = new OKXGateway(futures, creds.OkxKey, creds.OkxSecret, creds.OkxPassphrase, testOkx),
            OkxFutures     = new OKXFuturesGateway(futures, creds.OkxKey, creds.OkxSecret, creds.OkxPassphrase, testOkx),
            // KuCoin has no sandbox left — the exchange closed it and the client library dropped it.
            KucoinSpot     = new KucoinGateway(futures, creds.KucoinKey, creds.KucoinSecret, creds.KucoinPassphrase),
            KucoinFutures  = new KucoinFuturesGateway(futures, creds.KucoinKey, creds.KucoinSecret, creds.KucoinPassphrase),
        };
    }
}
