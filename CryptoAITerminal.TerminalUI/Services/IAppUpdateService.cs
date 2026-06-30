using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Outcome of a check for a newer release.</summary>
/// <param name="IsUpdateAvailable">True when a newer version is ready to download.</param>
/// <param name="CurrentVersion">The version currently running.</param>
/// <param name="LatestVersion">The newer version, or the current one when none.</param>
public sealed record AppUpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion);

/// <summary>
/// Self-update abstraction over the packaging tool. All methods are failure-tolerant:
/// network/IO errors surface as a non-available result or a false return, never an
/// exception into the UI. Implementations that are not installed via the updater
/// (debug runs, unpacked folders) report <see cref="IsSupported"/> = false.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>False in debug / unpacked runs where self-update is impossible.</summary>
    bool IsSupported { get; }

    /// <summary>Checks the release feed. Never throws; returns not-available on error.</summary>
    Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the pending update (delta when possible), reporting 0-100 progress.
    /// Returns true when a downloaded update is staged and ready to apply.
    /// </summary>
    Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Applies the staged update and restarts the app. Only call after
    /// <see cref="DownloadAsync"/> returned true. Does not return on success.
    /// </summary>
    void ApplyAndRestart();
}
