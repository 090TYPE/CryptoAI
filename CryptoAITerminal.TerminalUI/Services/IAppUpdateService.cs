using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>Outcome of a check for a newer release.</summary>
/// <param name="IsUpdateAvailable">True when a newer version is ready to download.</param>
/// <param name="CurrentVersion">The version currently running.</param>
/// <param name="LatestVersion">The newer version, or the current one when none.</param>
/// <param name="FailureReason">
/// Why the check could not complete, or null when it did. Without it "the feed is unreachable"
/// is indistinguishable from "you are up to date", and a user can sit on a known-broken build
/// for months believing it is the latest one.
/// </param>
public sealed record AppUpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string? FailureReason = null)
{
    /// <summary>True when the check itself failed — not the same as "no update available".</summary>
    public bool IsFailed => FailureReason is not null;

    public static AppUpdateInfo Failed(string current, string reason) =>
        new(false, current, current, reason);
}

/// <summary>
/// Self-update abstraction over the packaging tool. All methods are failure-tolerant:
/// network/IO errors surface as <see cref="AppUpdateInfo.FailureReason"/> or a false return,
/// never an exception into the UI. Implementations that are not installed via the updater
/// (debug runs, unpacked folders) report <see cref="IsSupported"/> = false.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>False in debug / unpacked runs where self-update is impossible.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Checks the release feed. Never throws; a failure comes back as
    /// <see cref="AppUpdateInfo.Failed"/> with the reason, not as a silent "up to date".
    /// </summary>
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
