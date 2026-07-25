using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Side of the position that was liquidated (already normalised across venues).</summary>
public enum LiquidationSide { Long, Short }

public enum LiquidationStreamState { Stopped, Connecting, Connected, Reconnecting }

/// <summary>Exchange a liquidation came from.</summary>
public enum LiquidationVenue { Binance, Bybit, Okx }

/// <summary>One exchange-reported forced liquidation. Every field comes off the wire.</summary>
public sealed record LiquidationEvent(
    string Symbol,
    LiquidationSide Side,
    decimal Price,
    decimal Quantity,
    decimal NotionalUsd,
    DateTime TimeUtc,
    LiquidationVenue Venue)
{
    public string SideLabel => Side == LiquidationSide.Long ? "LONG" : "SHORT";
    public string TimeLabel => TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string VenueLabel => Venue switch
    {
        LiquidationVenue.Binance => "BINANCE",
        LiquidationVenue.Bybit => "BYBIT",
        _ => "OKX",
    };
}

/// <summary>Aggregates over whatever the stream has actually delivered — never estimated.</summary>
public sealed record LiquidationStreamStats(
    int Events,
    decimal TotalUsd,
    decimal LongUsd,
    decimal ShortUsd,
    LiquidationEvent? Largest,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc)
{
    public static readonly LiquidationStreamStats Empty = new(0, 0m, 0m, 0m, null, null, null);

    /// <summary>Share of liquidated notional that was long liquidations (0..1). 0 when nothing arrived yet.</summary>
    public double LongShare => TotalUsd > 0m ? (double)(LongUsd / TotalUsd) : 0.0;

    public TimeSpan Window => WindowStartUtc is { } s && WindowEndUtc is { } e && e > s ? e - s : TimeSpan.Zero;
}

/// <summary>Per-venue connection health, so a silent feed is never shown as a working one.</summary>
public sealed record LiquidationVenueStatus(
    LiquidationVenue Venue,
    LiquidationStreamState State,
    long Received,
    DateTime? ConnectedSinceUtc,
    string? Error)
{
    public string VenueLabel => Venue switch
    {
        LiquidationVenue.Binance => "Binance",
        LiquidationVenue.Bybit => "Bybit",
        _ => "OKX",
    };

    /// <summary>
    /// Open socket that has never delivered anything for a while. On some networks the venue's
    /// futures host completes the TLS handshake but never forwards frames — that must not read
    /// as a healthy feed.
    /// </summary>
    public bool Silent => State == LiquidationStreamState.Connected
                          && Received == 0
                          && ConnectedSinceUtc is { } t
                          && DateTime.UtcNow - t > LiquidationStreamService.SilenceGrace;

    public string Label => State switch
    {
        LiquidationStreamState.Connected => Silent ? "connected · no data" : "connected",
        LiquidationStreamState.Connecting => "connecting",
        LiquidationStreamState.Reconnecting => "reconnecting",
        _ => "stopped",
    };
}

/// <summary>
/// Real liquidation feed built from the public futures WebSockets of Binance, Bybit and OKX.
/// All three are keyless and unauthenticated, and all three are polled at once: a venue that is
/// blocked or silent on the user's network simply contributes nothing while the others keep
/// working. None of them offers a keyless REST history for forced orders, so when every socket
/// is down the service reports that instead of inventing rows.
/// Reconnects on its own with an exponential pause, keeps the last <see cref="BufferCap"/> events
/// (max 24 h) in one shared ring buffer and is safe to read from any thread.
/// </summary>
public sealed class LiquidationStreamService : IDisposable
{
    /// <summary>Shown in the UI so a real feed is never confused with the estimated level map.</summary>
    public const string SourceLabel = "Binance · Bybit · OKX liquidations (live)";

    /// <summary>How long an open-but-empty socket is given before it is called out as silent.</summary>
    public static readonly TimeSpan SilenceGrace = TimeSpan.FromSeconds(90);

    private const int BufferCap = 500;
    private const int MaxFrameBytes = 1 << 20;   // a liquidation frame is tiny; guard against a runaway one

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Kept subscribed on Bybit alongside the symbol on screen, so the tape is not empty on a quiet alt.</summary>
    private static readonly string[] BybitBaseSymbols =
        { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT" };

    private readonly object _gate = new();
    private readonly Queue<LiquidationEvent> _buffer = new();   // oldest first
    private readonly VenueFeed[] _feeds;

    private bool _disposed;

    public LiquidationStreamService()
    {
        _feeds = new[]
        {
            new VenueFeed(LiquidationVenue.Binance, this),
            new VenueFeed(LiquidationVenue.Bybit, this),
            new VenueFeed(LiquidationVenue.Okx, this),
        };
    }

    // ── status ───────────────────────────────────────────────────────────────

    public IReadOnlyList<LiquidationVenueStatus> VenueStatuses
        => _feeds.Select(f => f.Status).ToList();

    /// <summary>Worst-case roll-up: connected when at least one venue is actually delivering.</summary>
    public LiquidationStreamState State
    {
        get
        {
            var statuses = _feeds.Select(f => f.Status).ToList();
            if (statuses.Any(s => s.State == LiquidationStreamState.Connected && !s.Silent))
                return LiquidationStreamState.Connected;
            if (statuses.Any(s => s.State == LiquidationStreamState.Connected))
                return LiquidationStreamState.Connected;   // open, but nothing delivered — Silent flags it
            if (statuses.Any(s => s.State == LiquidationStreamState.Connecting))
                return LiquidationStreamState.Connecting;
            if (statuses.Any(s => s.State == LiquidationStreamState.Reconnecting))
                return LiquidationStreamState.Reconnecting;
            return LiquidationStreamState.Stopped;
        }
    }

    public string StateLabel
    {
        get
        {
            var live = _feeds.Select(f => f.Status).Where(s => s.State == LiquidationStreamState.Connected && !s.Silent).ToList();
            if (live.Count > 0) return "connected · " + string.Join(" + ", live.Select(s => s.VenueLabel));
            return State switch
            {
                LiquidationStreamState.Connected => "connected · no data",
                LiquidationStreamState.Connecting => "connecting",
                LiquidationStreamState.Reconnecting => "reconnecting",
                _ => "stopped",
            };
        }
    }

    /// <summary>Per-venue one-liner for the UI, e.g. "Binance connected · no data · Bybit connected".</summary>
    public string VenueSummary
        => string.Join(" · ", _feeds.Select(f => f.Status).Select(s => s.VenueLabel + " " + s.Label));

    /// <summary>Total events accepted since <see cref="Start"/>, including ones aged out of the buffer.</summary>
    public long ReceivedCount => _feeds.Sum(f => f.Status.Received);

    /// <summary>Last transport error across venues, or null while at least one socket is healthy.</summary>
    public string? LastError
    {
        get
        {
            var statuses = _feeds.Select(f => f.Status).ToList();
            if (statuses.Any(s => s.State == LiquidationStreamState.Connected && !s.Silent)) return null;
            var err = statuses.FirstOrDefault(s => !string.IsNullOrEmpty(s.Error));
            return err is null ? null : err.VenueLabel + ": " + err.Error;
        }
    }

    public DateTime? StartedUtc { get; private set; }

    /// <summary>Subscription currently requested ("all symbols" or the symbol), null when stopped.</summary>
    public string? Subscription { get; private set; }

    // ── lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts (or re-points) every venue socket. Pass a symbol to focus the venues that need one,
    /// null to take whatever each venue publishes market-wide. Idempotent for the same symbol.
    /// </summary>
    public void Start(string? symbol = null)
    {
        if (_disposed) return;

        var normalised = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant();
        if (Subscription is not null && string.Equals(Subscription, normalised ?? "all symbols", StringComparison.Ordinal))
            return;

        Subscription = normalised ?? "all symbols";
        StartedUtc ??= DateTime.UtcNow;

        foreach (var feed in _feeds) feed.Start(normalised);
    }

    /// <summary>Stops every socket. Never throws and never blocks on a receive loop.</summary>
    public void Stop()
    {
        Subscription = null;
        StartedUtc = null;
        foreach (var feed in _feeds) feed.Stop();
    }

    // ── reads ────────────────────────────────────────────────────────────────

    /// <summary>Most recent events first. Optionally narrowed to one symbol.</summary>
    public IReadOnlyList<LiquidationEvent> Snapshot(int max = BufferCap, string? symbol = null)
    {
        if (max <= 0) return Array.Empty<LiquidationEvent>();
        lock (_gate)
        {
            IEnumerable<LiquidationEvent> src = _buffer;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var s = symbol.Trim().ToUpperInvariant();
                src = src.Where(e => e.Symbol == s);
            }
            return src.Reverse().Take(max).ToList();
        }
    }

    /// <summary>Aggregates over the buffered window (up to 24 h / <see cref="BufferCap"/> events).</summary>
    public LiquidationStreamStats GetStats(string? symbol = null)
    {
        lock (_gate)
        {
            IEnumerable<LiquidationEvent> src = _buffer;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var s = symbol.Trim().ToUpperInvariant();
                src = src.Where(e => e.Symbol == s);
            }

            var list = src as IList<LiquidationEvent> ?? src.ToList();
            if (list.Count == 0) return LiquidationStreamStats.Empty;

            decimal longUsd = 0m, shortUsd = 0m;
            LiquidationEvent? largest = null;
            foreach (var e in list)
            {
                if (e.Side == LiquidationSide.Long) longUsd += e.NotionalUsd; else shortUsd += e.NotionalUsd;
                if (largest is null || e.NotionalUsd > largest.NotionalUsd) largest = e;
            }

            return new LiquidationStreamStats(
                list.Count, longUsd + shortUsd, longUsd, shortUsd, largest,
                list[0].TimeUtc, list[^1].TimeUtc);
        }
    }

    private void Publish(IReadOnlyList<LiquidationEvent> events)
    {
        if (events.Count == 0) return;
        lock (_gate)
        {
            foreach (var e in events) _buffer.Enqueue(e);
            var cutoff = DateTime.UtcNow - Retention;
            while (_buffer.Count > BufferCap) _buffer.Dequeue();
            while (_buffer.Count > 0 && _buffer.Peek().TimeUtc < cutoff) _buffer.Dequeue();
        }
    }

    private static string TrimError(string? message)
    {
        message = (message ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return message.Length <= 120 ? message : message[..120] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        foreach (var feed in _feeds) feed.Dispose();
        lock (_gate) _buffer.Clear();
    }

    // ═══════════════════════ one socket per venue ════════════════════════════

    /// <summary>
    /// A single venue's socket: connect, subscribe, keep alive, parse, hand events to the owner.
    /// Failures stay inside one feed — the other venues are untouched.
    /// </summary>
    private sealed class VenueFeed : IDisposable
    {
        private readonly LiquidationVenue _venue;
        private readonly LiquidationStreamService _owner;
        private readonly object _lock = new();

        private CancellationTokenSource? _cts;
        private string? _symbol;
        private long _received;
        private LiquidationStreamState _state = LiquidationStreamState.Stopped;
        private string? _error;
        private DateTime? _connectedSince;
        private bool _disposed;

        public VenueFeed(LiquidationVenue venue, LiquidationStreamService owner)
        {
            _venue = venue;
            _owner = owner;
        }

        public LiquidationVenueStatus Status
        {
            get
            {
                lock (_lock) return new LiquidationVenueStatus(_venue, _state, _received, _connectedSince, _error);
            }
        }

        public void Start(string? symbol)
        {
            if (_disposed) return;
            Stop();

            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_disposed) return;
                _cts = cts = new CancellationTokenSource();
                _symbol = symbol;
                _state = LiquidationStreamState.Connecting;
                _error = null;
                _connectedSince = null;
            }

            _ = Task.Run(() => RunAsync(symbol, cts), cts.Token);
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                cts = _cts;
                _cts = null;
                _state = LiquidationStreamState.Stopped;
                _connectedSince = null;
            }
            if (cts is null) return;
            try { cts.Cancel(); } catch { /* already disposed */ }
            cts.Dispose();
        }

        private async Task RunAsync(string? symbol, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            var backoff = MinBackoff;
            var firstAttempt = true;

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    SetState(firstAttempt ? LiquidationStreamState.Connecting : LiquidationStreamState.Reconnecting, cts, connected: false);
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await ws.ConnectAsync(new Uri(Url), ct).ConfigureAwait(false);

                    foreach (var sub in SubscribeFrames(symbol))
                        await SendAsync(ws, sub, ct).ConfigureAwait(false);

                    firstAttempt = false;
                    backoff = MinBackoff;                 // a clean connect resets the pause
                    lock (_lock) { if (ReferenceEquals(_cts, cts)) _error = null; }
                    SetState(LiquidationStreamState.Connected, cts, connected: true);

                    using var keepAlive = StartKeepAlive(ws, ct);
                    await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    firstAttempt = false;
                    lock (_lock) { if (ReferenceEquals(_cts, cts)) _error = TrimError(ex.Message); }
                }
                finally
                {
                    try { ws?.Dispose(); } catch { /* nothing left to do */ }
                }

                if (ct.IsCancellationRequested) break;

                // Venues also drop the socket periodically by design — the same path handles it.
                SetState(LiquidationStreamState.Reconnecting, cts, connected: false);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var next = backoff + backoff;
                backoff = next < MaxBackoff ? next : MaxBackoff;
            }

            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts)) { _state = LiquidationStreamState.Stopped; _connectedSince = null; }
            }
        }

        private string Url => _venue switch
        {
            // Binance publishes every symbol's forced orders on one keyless stream.
            LiquidationVenue.Binance => "wss://fstream.binance.com/ws/!forceOrder@arr",
            LiquidationVenue.Bybit => "wss://stream.bybit.com/v5/public/linear",
            _ => "wss://ws.okx.com:8443/ws/v5/public",
        };

        /// <summary>Frames to send right after connecting. Binance needs none — the URL is the subscription.</summary>
        private IEnumerable<string> SubscribeFrames(string? symbol)
        {
            switch (_venue)
            {
                case LiquidationVenue.Bybit:
                {
                    // Bybit needs an explicit symbol per topic, so the on-screen symbol is added to a
                    // small default set — otherwise a quiet alt would show an empty tape.
                    var symbols = new List<string>(BybitBaseSymbols);
                    if (!string.IsNullOrWhiteSpace(symbol) && !symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                        symbols.Insert(0, symbol);
                    var args = string.Join(",", symbols.Select(s => "\"allLiquidation." + s + "\""));
                    yield return "{\"op\":\"subscribe\",\"args\":[" + args + "]}";
                    break;
                }
                case LiquidationVenue.Okx:
                    yield return "{\"op\":\"subscribe\",\"args\":[{\"channel\":\"liquidation-orders\",\"instType\":\"SWAP\"}]}";
                    break;
            }
        }

        /// <summary>
        /// Bybit drops a socket with no client ping within ~20 s and OKX within ~30 s; Binance sends
        /// its own protocol pings, which ClientWebSocket answers for us.
        /// </summary>
        private CancellationTokenSource? StartKeepAlive(ClientWebSocket ws, CancellationToken ct)
        {
            var (interval, payload) = _venue switch
            {
                LiquidationVenue.Bybit => (TimeSpan.FromSeconds(18), "{\"op\":\"ping\"}"),
                LiquidationVenue.Okx => (TimeSpan.FromSeconds(24), "ping"),
                _ => (TimeSpan.Zero, ""),
            };
            if (interval == TimeSpan.Zero) return null;

            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = linked.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        await Task.Delay(interval, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested || ws.State != WebSocketState.Open) break;
                        await SendAsync(ws, payload, token).ConfigureAwait(false);
                    }
                }
                catch { /* the receive loop owns error reporting and reconnects */ }
            }, token);
            return linked;
        }

        private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var chunk = new byte[8192];
            using var frame = new MemoryStream(8192);

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                frame.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;   // server closed: reconnect
                    if (result.Count > 0) frame.Write(chunk, 0, result.Count);
                    if (frame.Length > MaxFrameBytes) return;                        // absurd frame: reconnect
                }
                while (!result.EndOfMessage);

                if (frame.Length == 0) continue;
                Ingest(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
            }
        }

        private void Ingest(string json)
        {
            if (json.Length == 0 || json == "pong") return;

            List<LiquidationEvent>? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                switch (_venue)
                {
                    case LiquidationVenue.Binance: ParseBinance(root, ref parsed); break;
                    case LiquidationVenue.Bybit: ParseBybit(root, ref parsed); break;
                    default: ParseOkx(root, ref parsed); break;
                }
            }
            catch (JsonException) { return; }   // malformed frame — drop it, the socket stays up

            if (parsed is null || parsed.Count == 0) return;

            lock (_lock) _received += parsed.Count;
            _owner.Publish(parsed);
        }

        private void SetState(LiquidationStreamState state, CancellationTokenSource cts, bool connected)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_cts, cts)) return;   // a newer Start/Stop owns the status now
                _state = state;
                _connectedSince = connected ? DateTime.UtcNow : null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ── per-venue parsing ────────────────────────────────────────────────

        /// <summary>
        /// {"e":"forceOrder","o":{"s":"BTCUSDT","S":"SELL","ap":"9910.0","z":"0.014","T":1568014460893}}
        /// Binance reports the side of the liquidation ORDER, which is the opposite of the position
        /// that blew up: SELL closes (liquidates) a long, BUY closes a short.
        /// </summary>
        private static void ParseBinance(JsonElement root, ref List<LiquidationEvent>? into)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray()) AddBinance(el, ref into);
                return;
            }
            AddBinance(root, ref into);
        }

        private static void AddBinance(JsonElement el, ref List<LiquidationEvent>? into)
        {
            if (el.ValueKind != JsonValueKind.Object) return;
            if (el.TryGetProperty("e", out var kind) && kind.ValueKind == JsonValueKind.String
                && !string.Equals(kind.GetString(), "forceOrder", StringComparison.Ordinal)) return;
            if (!el.TryGetProperty("o", out var o) || o.ValueKind != JsonValueKind.Object) return;

            var symbol = Str(o, "s");
            if (symbol.Length == 0) return;

            var orderSide = Str(o, "S");
            LiquidationSide side;
            if (string.Equals(orderSide, "SELL", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Long;
            else if (string.Equals(orderSide, "BUY", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Short;
            else return;

            var price = Dec(o, "ap");                  // average fill price
            if (price <= 0m) price = Dec(o, "p");      // fall back to the order price
            var qty = Dec(o, "z");                     // filled quantity
            if (qty <= 0m) qty = Dec(o, "q");          // fall back to the ordered quantity
            if (price <= 0m || qty <= 0m) return;

            var ms = Int64(o, "T");
            var time = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;

            (into ??= new()).Add(new LiquidationEvent(
                symbol.ToUpperInvariant(), side, price, qty, price * qty, time, LiquidationVenue.Binance));
        }

        /// <summary>
        /// {"topic":"allLiquidation.BTCUSDT","data":[{"T":1739502302925,"s":"BTCUSDT","S":"Sell","v":"0.001","p":"96500"}]}
        /// Bybit's v5 allLiquidation reports the side of the liquidation ORDER, same convention as
        /// Binance: "Sell" closes (liquidates) a long, "Buy" closes a short. Quantity ("v") is in the
        /// base coin, so notional is a plain price × size.
        /// </summary>
        private static void ParseBybit(JsonElement root, ref List<LiquidationEvent>? into)
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
            if (root.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.String
                && topic.GetString()?.StartsWith("allLiquidation", StringComparison.Ordinal) != true) return;

            foreach (var el in data.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var symbol = Str(el, "s");
                if (symbol.Length == 0) continue;

                var orderSide = Str(el, "S");
                LiquidationSide side;
                if (string.Equals(orderSide, "Sell", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Long;
                else if (string.Equals(orderSide, "Buy", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Short;
                else continue;

                var price = Dec(el, "p");
                var qty = Dec(el, "v");
                if (price <= 0m || qty <= 0m) continue;

                var ms = Int64(el, "T");
                var time = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;

                (into ??= new()).Add(new LiquidationEvent(
                    symbol.ToUpperInvariant(), side, price, qty, price * qty, time, LiquidationVenue.Bybit));
            }
        }

        /// <summary>
        /// {"arg":{"channel":"liquidation-orders"},"data":[{"instId":"BTC-USDT-SWAP",
        ///   "details":[{"side":"buy","posSide":"short","bkPx":"64000","sz":"13","ts":"1600002936783"}]}]}
        /// OKX states the liquidated POSITION directly in "posSide", so no inversion is needed; "side"
        /// (the order side) is only a fallback and is inverted like the other venues.
        /// Size is in contracts, so notional needs the instrument's contract value — events for an
        /// instrument whose contract value is unknown are skipped rather than reported at a wrong size.
        /// </summary>
        private static void ParseOkx(JsonElement root, ref List<LiquidationEvent>? into)
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
            if (root.TryGetProperty("arg", out var arg) && arg.ValueKind == JsonValueKind.Object
                && Str(arg, "channel") is { Length: > 0 } ch
                && !string.Equals(ch, "liquidation-orders", StringComparison.Ordinal)) return;

            foreach (var inst in data.EnumerateArray())
            {
                if (inst.ValueKind != JsonValueKind.Object) continue;
                var instId = Str(inst, "instId");
                if (instId.Length == 0) continue;
                if (!inst.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array) continue;

                var ctVal = OkxContractValues.Get(instId);
                if (ctVal is not { } contractValue || contractValue <= 0m) continue;   // unknown size — skip, never guess

                var symbol = OkxSymbol(instId);

                foreach (var d in details.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;

                    LiquidationSide side;
                    var posSide = Str(d, "posSide");
                    if (string.Equals(posSide, "long", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Long;
                    else if (string.Equals(posSide, "short", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Short;
                    else
                    {
                        var orderSide = Str(d, "side");
                        if (string.Equals(orderSide, "sell", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Long;
                        else if (string.Equals(orderSide, "buy", StringComparison.OrdinalIgnoreCase)) side = LiquidationSide.Short;
                        else continue;
                    }

                    var price = Dec(d, "bkPx");
                    var contracts = Dec(d, "sz");
                    if (price <= 0m || contracts <= 0m) continue;

                    var qty = contracts * contractValue;
                    var ms = Int64(d, "ts");
                    var time = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;

                    (into ??= new()).Add(new LiquidationEvent(
                        symbol, side, price, qty, price * qty, time, LiquidationVenue.Okx));
                }
            }
        }

        /// <summary>BTC-USDT-SWAP → BTCUSDT, so OKX rows line up with the rest of the terminal.</summary>
        private static string OkxSymbol(string instId)
        {
            var parts = instId.Split('-');
            return parts.Length >= 2
                ? (parts[0] + parts[1]).ToUpperInvariant()
                : instId.ToUpperInvariant();
        }

        private static string Str(JsonElement o, string name)
            => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static decimal Dec(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return 0m;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
                JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
                _ => 0m,
            };
        }

        private static long Int64(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return 0L;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0L,
                JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0L,
                _ => 0L,
            };
        }
    }

    /// <summary>
    /// OKX reports liquidation size in contracts, so turning it into coins needs each instrument's
    /// contract value. The public instruments endpoint is fetched once and cached; until it answers,
    /// OKX events are skipped rather than sized incorrectly.
    /// </summary>
    private static class OkxContractValues
    {
        private static readonly ConcurrentDictionary<string, decimal> Values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static int _loading;
        private static bool _loaded;

        public static decimal? Get(string instId)
        {
            if (Values.TryGetValue(instId, out var v)) return v;
            EnsureLoading();
            return null;
        }

        private static void EnsureLoading()
        {
            if (_loaded) return;
            if (Interlocked.Exchange(ref _loading, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var json = await Http.GetStringAsync("https://www.okx.com/api/v5/public/instruments?instType=SWAP")
                                         .ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;

                    foreach (var el in data.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var id = el.TryGetProperty("instId", out var i) ? i.GetString() : null;
                        var raw = el.TryGetProperty("ctVal", out var c) ? c.GetString() : null;
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(raw)) continue;
                        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0m)
                            Values[id] = val;
                    }
                    _loaded = Values.Count > 0;
                }
                catch { /* retried on the next event */ }
                finally { Interlocked.Exchange(ref _loading, 0); }
            });
        }
    }
}
