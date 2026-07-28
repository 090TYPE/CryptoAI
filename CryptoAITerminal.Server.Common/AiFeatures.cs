namespace CryptoAITerminal.Server.Common;

/// <summary>One AI-using feature of the product, as the server knows it.</summary>
/// <param name="Id">Stable identifier the terminal sends in <c>X-AI-Feature</c>.</param>
/// <param name="Title">Human label for the admin panel.</param>
/// <param name="DefaultMaxTokens">What the client asks for when nothing is configured — shown so
/// an operator setting a limit can see what they are changing from.</param>
public sealed record AiFeature(string Id, string Title, int DefaultMaxTokens);

/// <summary>
/// The catalogue behind per-feature model control.
///
/// Until now the terminal chose the model and the server only checked it against an allow-list,
/// which meant an operator could not move one feature to a cheaper model without shipping a new
/// build to every installed copy. The client now names the FEATURE and the server decides the
/// model, so the choice lives in one place that can be changed while the product is running.
///
/// The ids are duplicated as string literals in the client (CryptoAITerminal.AIEngine). That is
/// deliberate: making the desktop client reference a server assembly to share nineteen constants
/// would be a worse trade than the duplication. They must stay in sync — an unknown id is not an
/// error, it simply means no override is configured and the client's own request stands, so a
/// typo degrades to today's behaviour rather than breaking the call.
/// </summary>
public static class AiFeatures
{
    public static readonly IReadOnlyList<AiFeature> All =
    [
        // Значения обязаны совпадать с тем, сколько клиент реально просит: админка показывает их
        // как ориентир, и оператор, набравший показанное число, своими руками воссоздаёт обрыв
        // ответа. Здесь это уже случилось — 2000 у signal_desk были ровно тем числом, на котором
        // панель обрывалась.
        new("signal_desk",         "AI Signal Desk — полный разбор рынка",        5240),
        new("ui_translate",        "Перевод интерфейса",                          6000),
        new("agent",               "Агент (одна итерация)",                       4096),
        new("market_ranking",      "Ранжирование рынка",                           700),
        new("dex_trending",        "Тренды DEX",                                   700),
        new("rule_builder",        "Конструктор правил",                           600),
        new("portfolio_rebalance", "Ребалансировка портфеля",                     2200),
        new("trade_journal_coach", "Разбор торгового журнала",                     600),
        new("signal_desk_stream",  "Signal Desk — поток сигналов",                 500),
        new("market_insight",      "Обзор рынка",                                  400),
        new("bot_parameters",      "Подбор параметров бота",                       400),
        new("backtest_review",     "Разбор бэктеста",                              400),
        new("token_security",      "Проверка безопасности токена",                 320),
        new("statarb_pair",        "Поиск пар для статарбитража",                  320),
        new("execution_schedule",  "График исполнения заявки",                     320),
        new("dynamic_tpsl",        "Динамические тейк-профит и стоп-лосс",         300),
        new("signal",              "Торговый сигнал",                              256),
        new("news_digest",         "Сводка новостей",                              256),
        new("alert_spec",          "Разбор условия алерта",                        200),
    ];

    private static readonly HashSet<string> Ids =
        new(All.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    /// <summary>
    /// Whether a header value is safe to build a settings key from. Rejects anything that is not a
    /// short identifier, so a hostile client cannot reach into unrelated settings by sending a
    /// crafted feature name. Unknown-but-well-formed ids are allowed through and simply find no
    /// override — that is what keeps a client newer than the server working.
    /// </summary>
    public static bool IsWellFormed(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 64 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
