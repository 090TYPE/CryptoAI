namespace CryptoAITerminal.Server.Common;

/// <summary>
/// Сколько токенов в сутки положено одной лицензии — и действует ли ограничение вообще.
///
/// Отдельный тип, а не голое число, потому что «ноль» раньше означал сразу две вещи: «тариф не
/// настроен, подставь общий предел» и «тратить нельзя». Пока это было одно и то же число, любая
/// попытка выключить квоту одним значением приводила ко второму смыслу — то есть к молчаливой
/// блокировке всех платных вызовов вместо их освобождения.
///
/// Сейчас квоты выключены: цена вызова ещё не измерена, а числа, которые стояли раньше
/// (35k/70k/105k в сутки), выбирались до того, как появились агент и панели с длинным контекстом,
/// и съедались за считанные минуты. Считать расход при этом никто не перестаёт — <c>ai_usage</c>
/// пишется по-прежнему, и именно из него берутся цифры для будущего прайса.
/// </summary>
/// <param name="Enforced">False — предел не проверяется; счётчик всё равно ведётся.</param>
/// <param name="DailyTokens">Суточный предел, когда он действует. При <c>Enforced=false</c> не значит ничего.</param>
public readonly record struct AiAllowance(bool Enforced, long DailyTokens)
{
    /// <summary>Тестовый период: расход считается, предел не применяется.</summary>
    public static readonly AiAllowance Unlimited = new(false, 0);

    /// <summary>Можно ли делать ещё один вызов при таком уже израсходованном объёме.</summary>
    public bool Allows(long used) => !Enforced || used < DailyTokens;

    /// <summary>Остаток. Ноль при выключенной квоте — там остатка не существует, а не «не осталось».</summary>
    public long Remaining(long used) => Enforced ? Math.Max(0, DailyTokens - used) : 0;

    /// <summary>
    /// Итоговый предел из трёх источников по убыванию приоритета: настройка тарифа в админке →
    /// число, зашитое для этого тарифа → общий предел сервера.
    ///
    /// Неположительное значение на каждом шаге означает «не задано» и пропускается, а не
    /// «запретить»: незаполненный тариф — это наша забытая настройка, и обнаруживать её не должен
    /// платящий клиент.
    /// </summary>
    public static AiAllowance Pick(bool enforced, long configuredForTier, long shippedForTier, long defaultCap)
    {
        if (!enforced) return Unlimited;
        if (configuredForTier > 0) return new(true, configuredForTier);
        if (shippedForTier > 0) return new(true, shippedForTier);
        return new(true, defaultCap);
    }
}
