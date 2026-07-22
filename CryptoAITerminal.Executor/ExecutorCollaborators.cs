using System.Globalization;
using System.Text.Json;
using CryptoAITerminal.Server.Data;

namespace CryptoAITerminal.Executor;

/// <summary>DB-backed grid order store — adapts <see cref="GridOrdersRepository"/> to the runner's port.</summary>
public sealed class SqlGridOrderStore : IGridOrderStore
{
    private readonly GridOrdersRepository _repo;
    public SqlGridOrderStore(GridOrdersRepository repo) => _repo = repo;

    public Task AddOpenAsync(Guid botId, Guid userId, int level, string side, decimal price, decimal qty, string? extRef, CancellationToken ct)
        => _repo.AddOpenAsync(botId, userId, level, side, price, qty, extRef, ct);

    public async Task<IReadOnlyList<TrackedGridOrder>> GetOpenAsync(Guid botId, CancellationToken ct)
        => (await _repo.GetOpenAsync(botId, ct))
            .Select(r => new TrackedGridOrder(r.Id, r.BotId, r.Level, r.Side, r.Price, r.Qty, r.ExtRef, r.Status))
            .ToList();

    public Task MarkFilledAsync(Guid id, CancellationToken ct) => _repo.MarkFilledAsync(id, ct);
}

/// <summary>DB-backed trailing position store — adapts <see cref="TslPositionsRepository"/> to the runner's port.</summary>
public sealed class SqlTslPositionStore : ITslPositionStore
{
    private readonly TslPositionsRepository _repo;
    public SqlTslPositionStore(TslPositionsRepository repo) => _repo = repo;

    public async Task<TrailingPosition?> LoadAsync(Guid botId, CancellationToken ct)
    {
        var r = await _repo.LoadAsync(botId, ct);
        if (r is null) return null;
        var state = new ServerTrailingStop.TslState(r.Peak, r.Sl, r.RemainingQty, r.TpPercent, r.Closed, r.PartialDone);
        return new TrailingPosition(r.BotId, r.Symbol, r.IsLong, r.Entry, r.Futures, state, r.Closed, r.NativeSl, r.SlOrderId);
    }

    public Task SaveAsync(TrailingPosition p, CancellationToken ct)
    {
        // UserId isn't carried on the runner's position record — reload keeps the stored one; on first
        // save the dispatcher writes the full row, so SaveAsync only ever updates an existing position.
        var s = p.State;
        return _repo.UpsertAsync(new TslPositionRow(
            p.BotId, Guid.Empty, p.Symbol, p.IsLong, p.Entry, p.Futures,
            s.Peak, s.Sl, s.RemainingQty, s.TpPercent, s.PartialDone, p.Closed, p.NativeSl, p.SlOrderId), ct);
    }
}

/// <summary>Production key provider — reads the user's CEX trade key (ciphertext + permissions) from the DB.</summary>
public sealed class SecretsCexKeyProvider : ICexKeyProvider
{
    private readonly SecretsRepository _secrets;
    public SecretsCexKeyProvider(SecretsRepository secrets) => _secrets = secrets;

    public async Task<CexKeyMaterial?> FindAsync(Guid userId, string exchange, CancellationToken ct)
    {
        var m = await _secrets.FindCexKeyAsync(userId, exchange, ct);
        return m is null ? null : new CexKeyMaterial(m.Ciphertext, m.WrappedDek, m.Permissions);
    }
}

/// <summary>
/// Production price source for order sizing (notional → quantity). Uses the Binance public spot
/// ticker — venue-agnostic enough for sizing a DCA buy; not used for execution price.
/// </summary>
public sealed class HttpPriceSource : IPriceSource
{
    private readonly HttpClient _http;
    public HttpPriceSource(HttpClient http) => _http = http;

    public async Task<decimal> GetPriceAsync(string exchange, string symbol, CancellationToken ct)
    {
        var url = $"https://api.binance.com/api/v3/ticker/price?symbol={symbol}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("price", out var p)
            && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var px)
            ? px : 0m;
    }
}
