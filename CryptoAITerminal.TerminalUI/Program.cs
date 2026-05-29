using Avalonia;
using ReactiveUI.Avalonia;

namespace CryptoAITerminal.TerminalUI;

class Program
{
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
    }
}
