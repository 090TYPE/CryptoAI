using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// <see cref="IAppUpdateService"/> backed by Velopack reading GitHub Releases.
/// Construction is cheap and never touches the network. <see cref="IsSupported"/>
/// is false unless the app was installed via the Velopack Setup.exe.
/// </summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService()
    {
        try
        {
            // GithubSource 1.2.0: (string repoUrl, string? accessToken, bool prerelease, IFileDownloader? downloader)
            var source = new GithubSource(AppInfo.RepoUrl, null, false, null);
            _manager = new UpdateManager(source);
        }
        catch (Exception)
        {
            // Velopack locator not initialised (debug / unpacked run, or test host).
            // Keep _manager null — IsSupported will return false, all operations are no-ops.
            _manager = null;
        }
    }

    public bool IsSupported => _manager?.IsInstalled == true;

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        // ct is part of the interface contract; Velopack 1.2.0's CheckForUpdatesAsync() has no token overload.
        var current = AppInfo.Version;
        if (!IsSupported)
            return new AppUpdateInfo(false, current, current);

        try
        {
            _pending = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
                return new AppUpdateInfo(false, current, current);

            var latest = _pending.TargetFullRelease.Version.ToString();
            return new AppUpdateInfo(true, current, latest);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Non-fatal: stay on the current version, surface nothing to the UI.
            return new AppUpdateInfo(false, current, current);
        }
    }

    public async Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!IsSupported || _pending is null) return false;
        try
        {
            // DownloadUpdatesAsync 1.2.0: (UpdateInfo updates, Action<int> progress, CancellationToken ct)
            await _manager!.DownloadUpdatesAsync(
                _pending, p => progress?.Report(p), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return false;
        }
    }

    public void ApplyAndRestart()
    {
        if (!IsSupported || _pending is null) return;
        try
        {
            // ApplyUpdatesAndRestart 1.2.0: (VelopackAsset toApply, string[]? restartArgs = null)
            // Does not return on success (process is replaced); caller handles the no-return-on-failure case.
            _manager!.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        }
        catch
        {
            // Non-fatal: stay on the current version. The caller resets the UI.
        }
    }
}
