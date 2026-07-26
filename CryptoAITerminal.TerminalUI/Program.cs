using System;
using Avalonia;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.TerminalUI.Services;
using ReactiveUI.Avalonia;

namespace CryptoAITerminal.TerminalUI;

class Program
{
    public static void Main(string[] args)
    {
        // Must be the very first thing: services Velopack install/update hook launches.
        Velopack.VelopackApp.Build().Run();

        // Before anything that can throw. Until now a crash left nothing behind to read.
        CrashLog.Install();
        CrashLog.Write("INFO", $"terminal starting (v{typeof(Program).Assembly.GetName().Version})");

        // Route AI through the CryptoAI server when one is configured (the server holds the
        // vendor key; the app authenticates with its license token). Null → direct-to-vendor.
        ChatClient.ServerBaseUrl = ServerEndpoint.ResolveBaseUrl();
        ChatClient.LicenseTokenProvider = () => new LicenseService().GetToken();

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write("FATAL", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
    }
}
