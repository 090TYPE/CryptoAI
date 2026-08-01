using System.Data;
using CryptoAITerminal.Server.Data;
using Npgsql;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Передача <see cref="DateOnly"/> в параметре запроса.
///
/// Без обработчика Dapper отказывается отправлять этот тип, и запрос падает целиком. Стоило это
/// суточного счётчика расхода: <c>AiBudgetRepository</c> и читает, и пишет по колонке <c>day</c>,
/// оба вызова падали, вызывающий ловил исключение и писал «starting from zero» — то есть
/// долговечность счётчика, ради которой репозиторий и написан, не работала ни разу, и увидеть это
/// можно было только в логах сервера.
///
/// Проверяется без базы: важно ровно то, что делает обработчик с параметром, а падало именно на
/// этом шаге, до отправки запроса.
/// </summary>
public class DateOnlyParameterTests
{
    [Fact]
    public void A_date_is_sent_as_a_date_not_a_timestamp()
    {
        // Параметр настоящий, npgsql-овский: обработчик выставляет ему DbType, и подделка с другим
        // поведением сеттера проверяла бы саму себя, а не то, что уедет в базу.
        //
        // Колонка в Postgres имеет тип date. DateTime без явного DbType уезжает как timestamp, и
        // сравнение с датой начинает зависеть от приведения на стороне сервера.
        var parameter = new NpgsqlParameter();

        new DateOnlyTypeHandler().SetValue(parameter, new DateOnly(2026, 8, 1));

        Assert.Equal(DbType.Date, parameter.DbType);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0), parameter.Value);
    }

    [Fact]
    public void What_the_driver_returns_is_read_back_as_the_same_day()
    {
        var handler = new DateOnlyTypeHandler();

        Assert.Equal(new DateOnly(2026, 8, 1), handler.Parse(new DateTime(2026, 8, 1, 13, 45, 0)));
        Assert.Equal(new DateOnly(2026, 8, 1), handler.Parse(new DateOnly(2026, 8, 1)));
        Assert.Equal(new DateOnly(2026, 8, 1), handler.Parse("2026-08-01"));
    }

    [Fact]
    public void An_unreadable_value_says_so_instead_of_guessing_a_day()
    {
        // Подставленная «сегодняшняя» дата на нечитаемом значении молча смешала бы расход разных
        // суток в одну строку.
        Assert.Throws<InvalidCastException>(() => new DateOnlyTypeHandler().Parse(42));
    }
}
