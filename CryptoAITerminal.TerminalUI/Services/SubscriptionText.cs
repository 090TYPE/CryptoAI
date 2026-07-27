namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Wording for the subscription panel.
///
/// Separate from the view model because Russian plurals are a rule, not a switch, and the naive
/// version is wrong in a way nobody notices until a customer does: "осталось 21 дней" instead of
/// "остался 21 день". A monthly plan hits exactly that range every time it is renewed.
/// </summary>
public static class SubscriptionText
{
    /// <summary>день / дня / дней for a count, by the actual rule rather than by ranges.</summary>
    public static string DayWord(int days)
    {
        var n = System.Math.Abs(days);
        var lastTwo = n % 100;
        // 11–14 take the plural form regardless of their last digit — the exception that a
        // digit-only check gets wrong.
        if (lastTwo is >= 11 and <= 14) return "дней";
        return (n % 10) switch
        {
            1 => "день",
            2 or 3 or 4 => "дня",
            _ => "дней"
        };
    }

    /// <summary>"остался 1 день" / "осталось 3 дня" — the verb agrees too.</summary>
    public static string DaysLeft(int? days) => days switch
    {
        null => "—",
        <= 0 => "истекла",
        1 => "остался 1 день",
        var d => $"осталось {d} {DayWord(d.Value)}"
    };

    /// <summary>What happens next, in the customer's terms rather than the system's.</summary>
    public static string ExpiryNotice(int? days) => days switch
    {
        null => "",
        <= 0 => "Подписка истекла. Серверные функции отключены, терминал работает на встроенных расчётах.",
        1 => "Подписка заканчивается завтра. Продлите, чтобы не потерять серверные функции.",
        var d => $"Подписка заканчивается через {d} {DayWord(d.Value)}. Продлите заранее — автосписания нет."
    };
}
