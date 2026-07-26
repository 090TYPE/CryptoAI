using System;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// The app's composition root. Microsoft.Extensions.DependencyInjection had been referenced by the
/// project and used nowhere: every service was reached through <c>new</c> at its point of use, so a
/// "singleton" meant whichever caller happened to construct one first, and the same file-backed
/// service was rebuilt on every call.
///
/// What belongs here: services that are genuinely one per process and can be built without knowing
/// anything about the shell. Resolution is lazy — a registration nobody asks for is never
/// constructed, so adding one cannot change startup behaviour on its own.
///
/// Where the rest goes: anything written today as <c>x ?? new X()</c> in a view model is a
/// registration in <see cref="Register"/> plus one resolve at the call site. Services that need the
/// main window, the gateway set or a signed-in session do not belong here — they have no meaning
/// before the shell exists, and pretending otherwise is how a container turns into a global.
///
/// The provider is not disposed: nothing registered here holds an unmanaged handle, and it lives
/// exactly as long as the process does.
/// </summary>
public static class AppServices
{
    private static readonly object Gate = new();
    private static IServiceProvider? _provider;

    /// <summary>Builds the container. Program.Main calls this before Avalonia starts; idempotent.</summary>
    public static void Init()
    {
        if (_provider is not null) return;
        lock (Gate)
        {
            if (_provider is not null) return;
            var services = new ServiceCollection();
            Register(services);
            _provider = services.BuildServiceProvider();
        }
    }

    /// <summary>
    /// Resolves a registered service, throwing if the type was never registered. Builds the
    /// container on first use, because the XAML previewer and the test host never run Program.Main.
    /// </summary>
    public static T Get<T>() where T : notnull
    {
        Init();
        return _provider!.GetRequiredService<T>();
    }

    private static void Register(IServiceCollection services)
    {
        // Immutable configuration and a file read per call — one instance is all the app ever
        // needed. Program, ServerFeedService and FavoritesSyncService each built their own (and
        // re-hashed the machine id doing so) just to read the same license file.
        services.AddSingleton(_ => new LicenseService());

        // Both are cheap to construct and touch no network at construction time. MainWindowViewModel
        // still builds its own — it already accepts them as optional constructor arguments, so
        // moving it over is a resolve at the one place that constructs it, not a rewrite.
        services.AddSingleton<IAppUpdateService, VelopackUpdateService>();
        services.AddSingleton<IReleaseNotesService>(_ => new ReleaseNotesService());

        // Pure decision + one marker file; no reason for more than one.
        services.AddSingleton(_ => new WhatsNewGate());
    }
}
