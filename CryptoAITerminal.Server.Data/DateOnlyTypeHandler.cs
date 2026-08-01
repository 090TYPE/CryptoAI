using System.Data;
using System.Globalization;
using Dapper;

namespace CryptoAITerminal.Server.Data;

/// <summary>
/// Учит Dapper передавать <see cref="DateOnly"/> в параметре запроса.
///
/// Без него любой запрос с параметром этого типа падает на
/// <c>NotSupportedException: The member day of type System.DateOnly cannot be used as a parameter
/// value</c> — Dapper не знает, каким <see cref="DbType"/> его отправить, и до Npgsql значение
/// вообще не доходит.
///
/// Стоило это молчаливой потери суточного счётчика расхода: <c>AiBudgetRepository</c> и читает, и
/// пишет <c>ai_budget_daily</c> по колонке <c>day</c>, оба вызова падали, а вызывающий ловил
/// исключение и писал в лог «starting from zero». То есть заявленная долговечность счётчика не
/// работала ни разу: после каждого перезапуска расход начинался с нуля, и «Расход AI» в терминале
/// показывал не сутки, а время жизни процесса. Список крупнейших потребителей в админке не
/// работал по той же причине.
///
/// <see cref="DbType.Date"/> задан явно: колонка в Postgres имеет тип <c>date</c>, а DateTime без
/// указания типа уезжает как <c>timestamp</c> — сравнение с датой тогда зависит от приведения на
/// стороне сервера, а не от того, что здесь написано.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        // Драйвер может отдать дату строкой, если колонка пришла из выражения без явного типа.
        string s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"нельзя прочитать DateOnly из {value?.GetType().Name ?? "null"}"),
    };
}
