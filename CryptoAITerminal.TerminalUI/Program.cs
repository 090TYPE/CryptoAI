using Avalonia;
using ReactiveUI.Avalonia;

namespace CryptoAITerminal.TerminalUI;

class Program
{
    public static void Main(string[] args)
    {
        // Must be the very first thing: services Velopack install/update hook launches.
        Velopack.VelopackApp.Build().Run();

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
