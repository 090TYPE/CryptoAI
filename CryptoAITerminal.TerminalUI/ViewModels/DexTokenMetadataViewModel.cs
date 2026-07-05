using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CryptoAITerminal.Core.Models;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

/// <summary>
/// Full profile of the selected DEX token — contract, community links, logo + header
/// image, description, holders and security signals. Downloads the images to bitmaps
/// and exposes clickable social links; also renders an <see cref="AiContextText"/>
/// block so the copilot answers questions about the coin.
/// </summary>
public sealed class DexTokenMetadataViewModel : ReactiveObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private DexTokenMetadata? _meta;
    private string _contractAddress = string.Empty;
    private string _chainId = string.Empty;
    private Bitmap? _logo;
    private Bitmap? _banner;
    private bool _isLoading;
    private int _seq;

    public DexTokenMetadataViewModel()
    {
        OpenWebsiteCommand = ReactiveCommand.Create(() => Open(_meta?.Website), outputScheduler: App.UiScheduler);
        OpenTwitterCommand = ReactiveCommand.Create(() => Open(_meta?.Twitter), outputScheduler: App.UiScheduler);
        OpenTelegramCommand = ReactiveCommand.Create(() => Open(_meta?.Telegram), outputScheduler: App.UiScheduler);
        OpenDiscordCommand = ReactiveCommand.Create(() => Open(_meta?.Discord), outputScheduler: App.UiScheduler);
        OpenExplorerCommand = ReactiveCommand.Create(() => Open(ExplorerUrl), outputScheduler: App.UiScheduler);
        OpenCoingeckoCommand = ReactiveCommand.Create(() => Open(CoingeckoUrl), outputScheduler: App.UiScheduler);
    }

    public ReactiveCommand<Unit, Unit> OpenWebsiteCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTwitterCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTelegramCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDiscordCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenExplorerCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCoingeckoCommand { get; }

    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    public string Name => _meta?.Name is { Length: > 0 } n ? n : "—";
    public string Symbol => _meta?.Symbol ?? string.Empty;
    public string ContractAddress => _contractAddress;
    public string ContractShort => _contractAddress.Length > 14
        ? $"{_contractAddress[..8]}…{_contractAddress[^6..]}"
        : _contractAddress;

    public string Description => _meta?.Description is { Length: > 0 } d ? d : "No description provided by the token's profile.";
    public bool HasDescription => !string.IsNullOrWhiteSpace(_meta?.Description);

    public bool HasWebsite => !string.IsNullOrWhiteSpace(_meta?.Website);
    public bool HasTwitter => !string.IsNullOrWhiteSpace(_meta?.Twitter);
    public bool HasTelegram => !string.IsNullOrWhiteSpace(_meta?.Telegram);
    public bool HasDiscord => !string.IsNullOrWhiteSpace(_meta?.Discord);

    public string HoldersLabel => _meta is { Holders: > 0 } ? $"{_meta.Holders:N0}" : "—";
    public string GtScoreLabel => _meta is { GtScore: > 0 } ? $"{_meta.GtScore:0}/100" : "—";
    public string CategoriesLabel => _meta?.Categories ?? string.Empty;
    public bool HasCategories => !string.IsNullOrWhiteSpace(_meta?.Categories);
    public bool IsVerified => _meta?.GtVerified == true;

    public string HoneypotLabel => _meta?.IsHoneypot switch { true => "HONEYPOT", false => "NOT A HONEYPOT", _ => "UNKNOWN" };
    public string HoneypotBrush => _meta?.IsHoneypot switch { true => "#ff6b6b", false => "#3ddc84", _ => "#8fa3b8" };

    public bool HasDeveloperHolding => _meta is { DeveloperHoldingPercentage: > 0m };
    public string DeveloperHoldingLabel => _meta is { DeveloperHoldingPercentage: > 0m } m ? $"{m.DeveloperHoldingPercentage:0.##}%" : "—";
    public string DeveloperHoldingBrush => (_meta?.DeveloperHoldingPercentage ?? 0m) >= 10m ? "#ff6b6b"
        : (_meta?.DeveloperHoldingPercentage ?? 0m) >= 3m ? "#f4b860" : "#3ddc84";

    public bool HasSecurity => HasAuth(_meta?.MintAuthority) || HasAuth(_meta?.FreezeAuthority);
    public string MintAuthorityLabel => AuthLabel(_meta?.MintAuthority);
    public string MintAuthorityBrush => AuthBrush(_meta?.MintAuthority);
    public string FreezeAuthorityLabel => AuthLabel(_meta?.FreezeAuthority);
    public string FreezeAuthorityBrush => AuthBrush(_meta?.FreezeAuthority);

    public bool HasCoingecko => !string.IsNullOrWhiteSpace(_meta?.CoingeckoId);
    public string CoingeckoUrl => HasCoingecko ? $"https://www.coingecko.com/en/coins/{_meta!.CoingeckoId}" : string.Empty;

    private static bool HasAuth(string? v) => !string.IsNullOrWhiteSpace(v);
    private static string AuthLabel(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "yes" or "true" => "active",
        "no" or "false" => "renounced",
        "" => "n/a",
        _ => v!,
    };
    private static string AuthBrush(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "no" or "false" or "" => "#3ddc84",
        "yes" or "true" => "#ff6b6b",
        _ => "#f4b860",
    };

    public Bitmap? Logo { get => _logo; private set { this.RaiseAndSetIfChanged(ref _logo, value); this.RaisePropertyChanged(nameof(HasLogo)); } }
    public bool HasLogo => _logo is not null;
    public Bitmap? Banner { get => _banner; private set { this.RaiseAndSetIfChanged(ref _banner, value); this.RaisePropertyChanged(nameof(HasBanner)); } }
    public bool HasBanner => _banner is not null;

    public string ExplorerUrl => _contractAddress.Length == 0 ? string.Empty : _chainId.ToLowerInvariant() switch
    {
        "ethereum" or "eth" => $"https://etherscan.io/token/{_contractAddress}",
        "bsc" => $"https://bscscan.com/token/{_contractAddress}",
        "base" => $"https://basescan.org/token/{_contractAddress}",
        "arbitrum" => $"https://arbiscan.io/token/{_contractAddress}",
        "polygon" => $"https://polygonscan.com/token/{_contractAddress}",
        "solana" => $"https://solscan.io/token/{_contractAddress}",
        _ => $"https://dexscreener.com/{_chainId}/{_contractAddress}",
    };

    /// <summary>Full token profile as plain text for the AI/copilot context.</summary>
    public string AiContextText
    {
        get
        {
            if (_meta is null && _contractAddress.Length == 0)
            {
                return string.Empty;
            }

            var lines = new System.Collections.Generic.List<string>
            {
                $"Selected DEX token: {Name} ({Symbol}) on {_chainId}",
                $"Contract: {_contractAddress}",
            };
            if (HasDescription) lines.Add($"About: {_meta!.Description}");
            if (_meta is { Holders: > 0 }) lines.Add($"Holders: {_meta.Holders:N0}");
            if (_meta is { GtScore: > 0 }) lines.Add($"GT score: {_meta.GtScore:0}/100{(IsVerified ? " (verified)" : "")}");
            if (_meta?.IsHoneypot is not null) lines.Add($"Honeypot check: {HoneypotLabel}");
            if (HasDeveloperHolding) lines.Add($"Developer holdings: {DeveloperHoldingLabel}");
            if (HasAuth(_meta?.MintAuthority)) lines.Add($"Mint authority: {MintAuthorityLabel}");
            if (HasAuth(_meta?.FreezeAuthority)) lines.Add($"Freeze authority: {FreezeAuthorityLabel}");
            if (HasCategories) lines.Add($"Categories: {_meta!.Categories}");
            if (HasWebsite) lines.Add($"Website: {_meta!.Website}");
            if (HasTwitter) lines.Add($"Twitter: {_meta!.Twitter}");
            if (HasTelegram) lines.Add($"Telegram: {_meta!.Telegram}");
            if (HasDiscord) lines.Add($"Discord: {_meta!.Discord}");
            return string.Join("\n", lines);
        }
    }

    public void Clear()
    {
        _seq++;
        _meta = null;
        _contractAddress = string.Empty;
        _chainId = string.Empty;
        Logo = null;
        Banner = null;
        RaiseAll();
    }

    /// <summary>Apply freshly fetched metadata and load its images.</summary>
    public async Task ApplyAsync(DexTokenMetadata? meta, string chainId, string address)
    {
        var seq = ++_seq;
        _meta = meta;
        _chainId = chainId ?? string.Empty;
        _contractAddress = string.IsNullOrWhiteSpace(meta?.Address) ? (address ?? string.Empty) : meta!.Address;
        Logo = null;
        Banner = null;
        RaiseAll();

        if (meta is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var logo = await DownloadAsync(meta.ImageUrl);
            if (seq == _seq) Logo = logo;

            var banner = await DownloadAsync(meta.BannerImageUrl);
            if (seq == _seq) Banner = banner;
        }
        finally
        {
            if (seq == _seq) IsLoading = false;
        }
    }

    private static async Task<Bitmap?> DownloadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore — nothing we can do if the shell can't open it
        }
    }

    private void RaiseAll()
    {
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(Symbol));
        this.RaisePropertyChanged(nameof(ContractAddress));
        this.RaisePropertyChanged(nameof(ContractShort));
        this.RaisePropertyChanged(nameof(Description));
        this.RaisePropertyChanged(nameof(HasDescription));
        this.RaisePropertyChanged(nameof(HasWebsite));
        this.RaisePropertyChanged(nameof(HasTwitter));
        this.RaisePropertyChanged(nameof(HasTelegram));
        this.RaisePropertyChanged(nameof(HasDiscord));
        this.RaisePropertyChanged(nameof(HoldersLabel));
        this.RaisePropertyChanged(nameof(GtScoreLabel));
        this.RaisePropertyChanged(nameof(CategoriesLabel));
        this.RaisePropertyChanged(nameof(HasCategories));
        this.RaisePropertyChanged(nameof(IsVerified));
        this.RaisePropertyChanged(nameof(HoneypotLabel));
        this.RaisePropertyChanged(nameof(HoneypotBrush));
        this.RaisePropertyChanged(nameof(HasDeveloperHolding));
        this.RaisePropertyChanged(nameof(DeveloperHoldingLabel));
        this.RaisePropertyChanged(nameof(DeveloperHoldingBrush));
        this.RaisePropertyChanged(nameof(HasSecurity));
        this.RaisePropertyChanged(nameof(MintAuthorityLabel));
        this.RaisePropertyChanged(nameof(MintAuthorityBrush));
        this.RaisePropertyChanged(nameof(FreezeAuthorityLabel));
        this.RaisePropertyChanged(nameof(FreezeAuthorityBrush));
        this.RaisePropertyChanged(nameof(HasCoingecko));
        this.RaisePropertyChanged(nameof(CoingeckoUrl));
        this.RaisePropertyChanged(nameof(ExplorerUrl));
        this.RaisePropertyChanged(nameof(AiContextText));
    }
}
