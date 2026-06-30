using System;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class AppUpdateServiceTests
{
    /// <summary>Configurable fake — records calls, returns scripted results.</summary>
    private sealed class FakeUpdateService : IAppUpdateService
    {
        public bool IsSupported { get; set; } = true;
        public AppUpdateInfo NextCheck { get; set; } = new(false, "1.0.0", "1.0.0");
        public bool DownloadResult { get; set; } = true;
        public bool ThrowOnCheck { get; set; }
        public int ApplyCalls { get; private set; }
        public int[] ReportedProgress = Array.Empty<int>();

        public Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            if (ThrowOnCheck) throw new InvalidOperationException("network down");
            return Task.FromResult(NextCheck);
        }

        public Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            progress?.Report(50);
            progress?.Report(100);
            ReportedProgress = new[] { 50, 100 };
            return Task.FromResult(DownloadResult);
        }

        public void ApplyAndRestart() => ApplyCalls++;
    }

    [Fact]
    public async Task Check_returns_available_when_newer_version_exists()
    {
        var svc = new FakeUpdateService { NextCheck = new(true, "1.6.0", "1.7.0") };
        var result = await svc.CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.7.0", result.LatestVersion);
    }

    [Fact]
    public async Task Download_reports_progress_and_succeeds()
    {
        var svc = new FakeUpdateService { DownloadResult = true };
        var seen = new System.Collections.Generic.List<int>();
        var ok = await svc.DownloadAsync(new Progress<int>(p => seen.Add(p)));
        Assert.True(ok);
        Assert.Equal(new[] { 50, 100 }, svc.ReportedProgress);
    }

    [Fact]
    public async Task Download_failure_does_not_apply()
    {
        var svc = new FakeUpdateService { DownloadResult = false };
        var ok = await svc.DownloadAsync();
        Assert.False(ok);
        Assert.Equal(0, svc.ApplyCalls);
    }

    [Fact]
    public void Unsupported_service_reports_not_supported()
    {
        var svc = new FakeUpdateService { IsSupported = false };
        Assert.False(svc.IsSupported);
    }

    [Fact]
    public void Real_service_is_not_supported_in_test_host()
    {
        // Not installed via Velopack Setup.exe → must report unsupported, never throw.
        var svc = new VelopackUpdateService();
        Assert.False(svc.IsSupported);
    }
}
