using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Резервирование расхода до ответа модели.
///
/// Предел проверяется ДО вызова, а списывается ПОСЛЕ, и между этими точками помещается весь залп,
/// успевший прийти: сто двадцать одновременных запросов при нулевом счётчике проходили гейт все до
/// одного и перепрыгивали суточный потолок втрое за раз. Резерв закрывает именно это окно —
/// параллельные запросы видят счётчик уже занятым, а после ответа неизрасходованное возвращается.
/// </summary>
public class AiBudgetReserveTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void A_reservation_is_visible_to_everyone_else_immediately()
    {
        var budget = new AiBudget(1_000_000);

        budget.Adjust("lic", 40_000);

        // Соседний запрос читает счётчик до того, как первый получил ответ, — в этом весь смысл.
        Assert.Equal(40_000, budget.Used("lic"));
    }

    [Fact]
    public void The_unused_part_comes_back_when_the_answer_turns_out_cheaper()
    {
        var budget = new AiBudget(1_000_000);
        const long reserve = 40_000;
        const long actual = 1_500;

        budget.Adjust("lic", reserve);
        budget.Adjust("lic", actual - reserve);

        Assert.Equal(actual, budget.Used("lic"));
    }

    [Fact]
    public void A_call_that_never_answered_leaves_nothing_behind()
    {
        // Обрыв соединения или исключение: возврат идёт в finally, иначе резерв висел бы до
        // полуночи и отнимал у клиента квоту за вызов, которого не было.
        var budget = new AiBudget(1_000_000);
        const long reserve = 40_000;

        budget.Adjust("lic", reserve);
        budget.Adjust("lic", 0 - reserve);

        Assert.Equal(0, budget.Used("lic"));
    }

    [Fact]
    public void A_refund_after_midnight_does_not_hand_the_new_day_a_head_start()
    {
        // Возврат, пришедший уже в новых сутках, иначе увёл бы счётчик в минус — то есть выдал бы
        // бесплатную фору на весь следующий день.
        var clock = new Clock();
        var budget = new AiBudget(1_000_000, clock.Get);

        budget.Adjust("lic", 40_000);
        clock.Advance(TimeSpan.FromHours(13)); // за полночь UTC
        budget.Adjust("lic", -40_000);

        Assert.Equal(0, budget.Used("lic"));
        Assert.True(budget.HasHeadroom("lic", 1_000));
    }

    [Fact]
    public void A_burst_cannot_slip_past_the_ceiling_together()
    {
        // Одна и та же лицензия, десять одновременных вызовов: каждый резервирует до ответа,
        // поэтому потолок ловит их по ходу, а не после того, как все ушли наверх.
        var budget = new AiBudget(1_000_000);
        const long cap = 100_000;
        const long reserve = 40_000;
        var allowance = AiAllowance.Pick(true, 0, 0, cap);

        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            if (!allowance.Allows(budget.Used("lic"))) continue;
            budget.Adjust("lic", reserve);
            admitted++;
        }

        // Без резерва прошли бы все десять — 400k против потолка в 100k.
        Assert.Equal(3, admitted);
        Assert.True(budget.Used("lic") >= cap);
    }

    [Fact]
    public void Adjust_ignores_the_calls_that_mean_nothing()
    {
        var budget = new AiBudget(1_000_000);

        budget.Adjust("", 5_000);
        budget.Adjust("lic", 0);

        Assert.Equal(0, budget.Used(""));
        Assert.Equal(0, budget.Used("lic"));
    }
}
