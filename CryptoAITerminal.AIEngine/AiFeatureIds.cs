namespace CryptoAITerminal.AIEngine;

/// <summary>
/// What each AI call is FOR, sent to the server as <c>X-AI-Feature</c>.
///
/// The terminal no longer decides which model runs a feature — it says what it is doing and the
/// server decides. That is what lets an operator move one panel onto a cheaper model without
/// shipping a build to every installed copy.
///
/// These strings must match <c>CryptoAITerminal.Server.Common.AiFeatures</c>. They are duplicated
/// rather than shared because making the desktop client reference a server assembly for nineteen
/// constants is the worse trade. A mismatch is not fatal: the server finds no override for an
/// unknown id and the terminal's own request stands, which is exactly the pre-feature behaviour.
///
/// When the terminal runs on the customer's own vendor key there is no server in the path, no
/// header is sent anywhere, and the model comes from settings as it always did.
/// </summary>
public static class AiFeatureIds
{
    public const string SignalDesk         = "signal_desk";
    public const string SignalDeskStream   = "signal_desk_stream";
    public const string UiTranslate        = "ui_translate";
    public const string Agent              = "agent";
    public const string MarketRanking      = "market_ranking";
    public const string DexTrending        = "dex_trending";
    public const string RuleBuilder        = "rule_builder";
    public const string PortfolioRebalance = "portfolio_rebalance";
    public const string TradeJournalCoach  = "trade_journal_coach";
    public const string MarketInsight      = "market_insight";
    public const string BotParameters      = "bot_parameters";
    public const string BacktestReview     = "backtest_review";
    public const string TokenSecurity      = "token_security";
    public const string StatArbPair        = "statarb_pair";
    public const string ExecutionSchedule  = "execution_schedule";
    public const string DynamicTpSl        = "dynamic_tpsl";
    public const string Signal             = "signal";
    public const string NewsDigest         = "news_digest";
    public const string AlertSpec          = "alert_spec";

    /// <summary>
    /// Название раздела для человека. Нужно там, где сбой показывается не внутри своей панели, а
    /// одной общей строкой: «ai_signal_desk» в интерфейсе покупателя — это идентификатор для
    /// разработчика, а не объяснение.
    ///
    /// Незнакомый идентификатор возвращается как есть: сборка клиента может оказаться новее этого
    /// списка, и показать сырое имя честнее, чем «неизвестно».
    /// </summary>
    public static string Title(string? feature) => feature switch
    {
        SignalDesk         => "AI Signal Desk",
        SignalDeskStream   => "вопрос к Signal Desk",
        UiTranslate        => "перевод интерфейса",
        Agent              => "агент",
        MarketRanking      => "ранжирование рынка",
        DexTrending        => "тренды DEX",
        RuleBuilder        => "конструктор правил",
        PortfolioRebalance => "ребалансировка портфеля",
        TradeJournalCoach  => "разбор журнала сделок",
        MarketInsight      => "обзор рынка",
        BotParameters      => "подбор параметров бота",
        BacktestReview     => "разбор бэктеста",
        TokenSecurity      => "проверка токена",
        StatArbPair        => "пара для статарбитража",
        ExecutionSchedule  => "план исполнения заявки",
        DynamicTpSl        => "TP/SL по волатильности",
        Signal             => "торговый сигнал",
        NewsDigest         => "сводка новостей",
        AlertSpec          => "разбор условия алерта",
        null or ""         => "AI",
        _                  => feature,
    };
}
