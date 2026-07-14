using Npgsql;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Owns the pooled <see cref="NpgsqlDataSource"/> for the server DB (Postgres + TimescaleDB).
/// One instance per process; hand it to the repositories.
/// </summary>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public Db(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        _source = NpgsqlDataSource.Create(connectionString);
    }

    public NpgsqlConnection OpenConnection() => _source.OpenConnection();

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        _source.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
