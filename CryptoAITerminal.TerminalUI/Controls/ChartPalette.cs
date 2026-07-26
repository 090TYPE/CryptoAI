using Avalonia.Media;

namespace CryptoAITerminal.TerminalUI.Controls;

/// <summary>
/// Доступ к палитре приложения для контролов, которые рисуют себя сами.
///
/// Кисти и перья графиков живут в static readonly полях ради отрисовки: цикл по свечам не должен
/// парсить цвет и аллоцировать кисть на каждый кадр. Поэтому токен читается отсюда ровно один раз —
/// из инициализатора такого поля — и дальше ведёт себя как константа. Из <c>Render</c> сюда ходить
/// нельзя: это чтение словаря ресурсов.
///
/// Фолбэк повторяет значение токена из <c>Styles/AppStyles.axaml</c> и нужен там, где
/// <see cref="Avalonia.Application.Current"/> ещё не поднят — превьюер дизайнера, тесты без
/// приложения. Так же устроен <c>ViewModels/SemanticColor</c>, который отдаёт те же токены
/// view-моделям.
/// </summary>
internal static class ChartPalette
{
    /// <summary>Цвет токена по имени ресурса, например <c>"Positive"</c> или <c>"Surface2"</c>.</summary>
    public static Color Get(string resource, string fallback)
        => CryptoAITerminal.TerminalUI.ViewModels.SemanticColor.TryPaletteBrush(resource, out var brush)
            && brush is ISolidColorBrush solid
                ? solid.Color
                : Color.Parse(fallback);

    /// <summary>Готовая кисть токена. Вызывать только из инициализаторов статических полей.</summary>
    public static SolidColorBrush Brush(string resource, string fallback) => new(Get(resource, fallback));

    /// <summary>Тот же цвет с другой альфой — для подложек и направляющих.</summary>
    public static Color Fade(Color color, byte alpha) => new(alpha, color.R, color.G, color.B);
}
