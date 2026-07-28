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

        // У OpenAI роль класса несёт суффикс: mini и nano — дешёвые, без суффикса — основная модель.
        var small = low.Contains("mini") || low.Contains("nano");
        return role == AiModelRole.Background
            ? (small ? 2 : 1)
            : (small ? 1 : 2);
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

    /// <summary>Разбор значения настройки <c>ai.family</c>. Незнакомое значение — Claude.</summary>
    public static AiFamily ParseFamily(string? raw) =>
        raw?.Trim().ToLowerInvariant() is "chatgpt" or "openai" or "gpt" ? AiFamily.ChatGpt : AiFamily.Claude;

    public static string FamilyName(AiFamily family) => family == AiFamily.ChatGpt ? "chatgpt" : "claude";
}
