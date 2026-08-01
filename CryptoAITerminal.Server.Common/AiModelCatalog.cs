using System.Text.RegularExpressions;

namespace CryptoAITerminal.Server.Common;

/// <summary>Какое семейство моделей обслуживает сервер. Единственный выбор, который делает оператор.</summary>
public enum AiFamily
{
    Claude,
    ChatGpt,
}

/// <summary>
/// Для чего берётся модель. Разница не косметическая: фоновых вызовов в сутки на два порядка
/// больше, чем терминальных, и они короткие и строго форматированные — дешёвая модель там не
/// компромисс, а правильный выбор. Одна модель на всё means либо переплата, либо слабые ответы там,
/// где их читает человек.
/// </summary>
public enum AiModelRole
{
    /// <summary>Вызовы из терминала: их читает пользователь.</summary>
    Foreground,

    /// <summary>Фоновые задачи сервера: 31 дайджест, скоринг, аномалии, портфельные ревью.</summary>
    Background,
}

/// <summary>
/// Выбор конкретной модели из того, что провайдер реально отдаёт.
///
/// Появилось потому, что имена моделей — единственная часть переключения провайдера, которую нельзя
/// узнать со стороны сервера: они видны только в личном кабинете провайдера, у роутеров идут с
/// префиксом вендора, и ошибка в имени возвращается как «model is not allowed» или 404, ни один из
/// которых не подсказывает правильное имя. Раньше это означало 19 полей в админке, которые надо
/// заполнить руками и ни одно не забыть.
///
/// Правила намеренно грубые и работают по подстроке, а не по точному списку: новые версии выходят
/// чаще, чем выкладывается сервер, и список известных имён в коде устаревал бы ровно так же, как
/// устарел <c>claude-3-5-haiku-20241022</c>.
/// </summary>
public static class AiModelCatalog
{
    /// <summary>
    /// Не текстовые модели. Они попадают в тот же <c>/v1/models</c> и без отсева легко выигрывают
    /// отбор по номеру версии — запрос к эмбеддингам в формате чата вернёт ошибку, которая выглядит
    /// как поломка провайдера.
    /// </summary>
    private static readonly string[] NotChat =
    {
        "embed", "whisper", "tts", "dall-e", "dalle", "moderation", "audio", "realtime",
        "transcribe", "image", "rerank", "guard",
    };

    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Лучшая модель семейства для этой роли, или null, если провайдер не отдал ни одной подходящей.
    /// Null означает «оставить как есть», а не «взять что попало»: молча подставленная чужая модель
    /// хуже честного отказа.
    /// </summary>
    public static string? Pick(IEnumerable<string> available, AiFamily family, AiModelRole role)
    {
        string? best = null;
        var bestRank = (Tier: int.MinValue, Major: int.MinValue, Minor: int.MinValue);

        foreach (var id in available)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var low = id.ToLowerInvariant();

            if (NotChat.Any(low.Contains)) continue;
            if (!Belongs(low, family)) continue;

            var tier = Tier(low, family, role);
            if (tier < 0) continue;

            var (major, minor) = Version(low);
            var rank = (tier, major, minor);
            if (best is null || Compare(rank, bestRank) > 0)
            {
                best = id;
                bestRank = rank;
            }
        }

        return best;
    }

    private static int Compare((int Tier, int Major, int Minor) a, (int Tier, int Major, int Minor) b) =>
        a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier)
        : a.Major != b.Major ? a.Major.CompareTo(b.Major)
        : a.Minor.CompareTo(b.Minor);

    /// <summary>
    /// Относится ли имя модели к семейству.
    ///
    /// Публично, потому что этим проверяются не только кандидаты из списка провайдера, но и имена,
    /// заданные руками: прибитое <c>claude-sonnet-4-6</c>, оставшееся от прежней настройки, не
    /// должно переживать переключение на ChatGPT. Иначе переключатель срабатывал бы для одних
    /// функций и молча не срабатывал для других — худший из возможных исходов.
    /// </summary>
    public static bool BelongsTo(string? id, AiFamily family) =>
        !string.IsNullOrWhiteSpace(id) && Belongs(id.ToLowerInvariant(), family);

    private static bool Belongs(string low, AiFamily family) => family == AiFamily.Claude
        ? low.Contains("claude")
        : low.Contains("gpt") || low.Contains("o1-") || low.Contains("o3-");

    /// <summary>
    /// Насколько модель подходит роли. Больше — лучше; отрицательное — не годится.
    ///
    /// Для фоновых задач дешёвый класс стоит выше сильного, для терминала наоборот. Самый сильный
    /// класс (opus, а у OpenAI — рассуждающие модели) не берётся автоматически никогда: разница в
    /// цене у него не в разы, а на порядок, и такое решение должно приниматься человеком.
    /// </summary>
    private static int Tier(string low, AiFamily family, AiModelRole role)
    {
        if (family == AiFamily.Claude)
        {
            var haiku = low.Contains("haiku");
            var sonnet = low.Contains("sonnet");
            if (low.Contains("opus")) return -1;
            if (!haiku && !sonnet) return -1;
            return role == AiModelRole.Background
                ? (haiku ? 2 : 1)
                : (sonnet ? 2 : 1);
        }

        // Рассуждающие модели не берутся автоподбором — ровно по той же причине, что и opus выше.
        // Это и было написано в комментарии, но не сделано в коде: Belongs пускает o1-/o3-, Tier
        // давал им высший разряд для терминала, и достаточно было провайдеру показать такую модель,
        // чтобы сервер начал назначать её всем — с ценой на порядок выше и другим набором
        // параметров запроса.
        if (Reasoning(low)) return -1;

        // Класс -pro отсекается здесь, а не в Reasoning: параметры запроса он принимает обычные,
        // так что RejectsLegacyParameters его касаться не должно, а вот в автоподбор он попадать не
        // должен — цена у него того же порядка, что у opus, и решение принимает человек. Без этой
        // строки достаточно было провайдеру показать gpt-5-pro, чтобы сервер назначил его всем.
        if (low.Contains("-pro")) return -1;

        // У OpenAI роль класса несёт суффикс: mini и nano — дешёвые, без суффикса — основная модель.
        var small = low.Contains("mini") || low.Contains("nano");
        return role == AiModelRole.Background
            ? (small ? 2 : 1)
            : (small ? 1 : 2);
    }

    private static bool Reasoning(string low) =>
        low.Contains("o1-") || low.Contains("o3-") || low.Contains("o4-") ||
        low.StartsWith("o1") || low.StartsWith("o3") || low.StartsWith("o4");

    /// <summary>
    /// Отвергает ли модель параметры, которые терминал шлёт всем: <c>max_tokens</c> и
    /// <c>temperature</c> не равную единице.
    ///
    /// Нужно потому, что у OpenAI новые линейки их не принимают: <c>max_tokens</c> отвечает
    /// «Unsupported parameter … use max_completion_tokens», температура — «does not support 0.4
    /// with this model». Все ~16 провайдеров терминала шлют и то и другое, поэтому без переписи
    /// параметра каждый вызов на такой модели возвращает 400, а панель молча уходит в собственный
    /// расчёт.
    ///
    /// Сегодня это не срабатывает: сервер смотрит на роутер, который старые имена параметров
    /// принимает. Сработает ровно в тот момент, когда в админке нажмут «Вернуть напрямую к
    /// вендорам» — то есть когда роутер и так уже подвёл и разбираться будет некогда.
    ///
    /// По подстроке, а не по списку: имена новых линеек выходят чаще, чем выкладывается сервер.
    /// </summary>
    public static bool RejectsLegacyParameters(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var low = model.ToLowerInvariant();
        return Reasoning(low) || low.Contains("gpt-5");
    }

    /// <summary>
    /// Первые два числа в имени как версия: claude-sonnet-4.6 → (4, 6), gpt-4o → (4, 0).
    ///
    /// Только первые два: у Anthropic в имени часто есть дата сборки
    /// (claude-haiku-4-5-20251001), и она на порядки больше номера версии — учитывая её, отбор
    /// выбирал бы модель по дате релиза, а не по поколению.
    /// </summary>
    private static (int Major, int Minor) Version(string low)
    {
        var nums = Number.Matches(low);
        var major = nums.Count > 0 && int.TryParse(nums[0].Value, out var a) ? a : 0;
        var minor = nums.Count > 1 && int.TryParse(nums[1].Value, out var b) ? b : 0;
        // Дата сборки не версия: четырёхзначное и длиннее — это 20251001, а не минорный номер.
        if (minor > 999) minor = 0;
        return (major, minor);
    }

    /// <summary>
    /// Чем заменить имя модели, присланное клиентом, когда сервер ничего не выбрал сам.
    ///
    /// Null означает «оставить как есть» — обычный случай: терминал прислал имя своего семейства, и
    /// переписывать его не за чем. Не-null появляется только при расхождении формата и семейства:
    /// оператор запретил выбор в терминале, или сборка на руках старше заголовка семейства. Тогда
    /// присланное имя гарантированно чужое (<c>claude-sonnet-4-6</c> у OpenAI — это 404), и вопрос
    /// не в том, подставлять ли, а в том, отвечать ли вообще.
    ///
    /// Здесь, а не в прокси, потому что это правило про имена моделей и семейства, а не про HTTP —
    /// и потому что проверить его надо тестом, а не развёрнутым сервером.
    /// </summary>
    public static string? ForegroundFallback(string? clientModel, AiFamily family) =>
        string.IsNullOrWhiteSpace(clientModel) || BelongsTo(clientModel, family)
            ? null
            : family == AiFamily.Claude
                ? SettingKeys.DefaultForegroundModel
                : SettingKeys.DefaultForegroundChatGptModel;

    /// <summary>Разбор значения настройки <c>ai.family</c>. Незнакомое значение — Claude.</summary>
    public static AiFamily ParseFamily(string? raw) =>
        raw?.Trim().ToLowerInvariant() is "chatgpt" or "openai" or "gpt" ? AiFamily.ChatGpt : AiFamily.Claude;

    /// <summary>
    /// Строгий разбор: распознано или нет, без подстановки Claude по умолчанию.
    ///
    /// Нужен там, где значение приходит из заголовка запроса. Мягкий разбор в этом месте означал бы,
    /// что пустой или искажённый заголовок молча перебивает выбор, сделанный на сервере, — то есть
    /// одна опечатка в клиенте переводит пользователя на другое семейство без единого следа.
    /// </summary>
    public static bool TryParseFamily(string? raw, out AiFamily family)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "claude" or "anthropic":
                family = AiFamily.Claude;
                return true;
            case "chatgpt" or "openai" or "gpt":
                family = AiFamily.ChatGpt;
                return true;
            default:
                family = AiFamily.Claude;
                return false;
        }
    }

    public static string FamilyName(AiFamily family) => family == AiFamily.ChatGpt ? "chatgpt" : "claude";
}
