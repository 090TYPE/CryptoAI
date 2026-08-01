using Dapper;
using Npgsql;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Owns the pooled <see cref="NpgsqlDataSource"/> for the server DB (Postgres + TimescaleDB).
/// One instance per process; hand it to the repositories.
/// </summary>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    /// <summary>
    /// Обработчики типов Dapper регистрируются здесь, потому что это единственная точка, через
    /// которую в процессе появляется доступ к базе: репозиторий, забывший позвать регистрацию сам,
    /// падал бы уже в рантайме и только на своём запросе — как и падал счётчик расхода.
    ///
    /// Статический конструктор, а не метод: он выполняется ровно один раз и до первого
    /// подключения, а <see cref="SqlMapper.AddTypeHandler{T}"/> при повторном вызове перезаписывает
    /// таблицу глобально.
    /// </summary>
    static Db() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

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
