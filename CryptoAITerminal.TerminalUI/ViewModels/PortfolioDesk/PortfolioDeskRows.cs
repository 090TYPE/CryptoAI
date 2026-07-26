using System;
using System.Windows.Input;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels.PortfolioDesk;

// Lightweight row/cell view models for the portfolio-desk terminal. Colours are
// carried as strings so the XAML can bind them through StringToBrushConverter,
// keeping the markup a faithful 1:1 of the design mock without a bespoke brush
// property per cell.

public sealed class PfHeaderStat
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
}

public sealed class PfTypeFilter
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public string Color { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
    public string Key { get; init; } = "";
}

public sealed class PfWalletCard
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "DEX";
    public string Chain { get; init; } = "";
    public string AddrShort { get; init; } = "";
    public string Balance { get; init; } = "—";
    public string Pnl24h { get; init; } = "—";
    public string PnlColor { get; init; } = "#3d5a72";
    public string Icon { get; init; } = "◈";
    public string IconBg { get; init; } = "#07111a";
    public string IconBorder { get; init; } = "#111d29";
    public string IconColor { get; init; } = SemanticColor.Muted;
    public string Accent { get; init; } = SemanticColor.Accent;
    public double HealthPct { get; init; }
    public string HealthColor { get; init; } = SemanticColor.Positive;
    public string StatusDot { get; init; } = SemanticColor.Positive;
    public string TypeBadgeColor { get; init; } = SemanticColor.Muted;
    public string TypeBadgeBg { get; init; } = "rgba(143,163,184,.1)";
    public string RowBg { get; init; } = "transparent";
    public ICommand? SelectCommand { get; init; }
    /// <summary>Underlying saved wallet, so selecting the card can arm it as the active session.</summary>
    public SavedWalletViewModel? Source { get; init; }
}

public sealed class PfSummaryCard
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
    public string Sub { get; init; } = "";
    public string SubColor { get; init; } = SemanticColor.Positive;
    public string SubLabel { get; init; } = "";
    public string Icon { get; init; } = "◈";
}

public sealed class PfAllocBar
{
    public string Label { get; init; } = "";
    public string Amount { get; init; } = "";
    public string Pct { get; init; } = "";
    public double Width { get; init; }
    public string Color { get; init; } = SemanticColor.Accent;
}

public sealed class PfTopAsset
{
    public string Sym { get; init; } = "";
    public string Name { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Price { get; init; } = "";
    public string Value { get; init; } = "";
    public string Chg { get; init; } = "";
    public string ChgColor { get; init; } = "#3d5a72";
    public string IconBg { get; init; } = "#07111a";
    public string IconBorder { get; init; } = "#111d29";
    public string IconColor { get; init; } = SemanticColor.Muted;
}

public sealed class PfBlotterTab
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "transparent";
    public string Color { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
    public string Key { get; init; } = "";
}

public sealed class PfBlotterTotal
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
}

public sealed class PfAssetRow
{
    public string Sym { get; init; } = "";
    public string Name { get; init; } = "";
    public string Wallet { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Value { get; init; } = "";
    public string Price { get; init; } = "";
    public string Pnl { get; init; } = "—";
    public string PnlColor { get; init; } = "#3d5a72";
    public string Chg { get; init; } = "—";
    public string ChgColor { get; init; } = "#3d5a72";
    public string Tag { get; init; } = "";
    public string TagColor { get; init; } = SemanticColor.Muted;
    public string TagBg { get; init; } = "rgba(143,163,184,.1)";
    public string IconBg { get; init; } = "#07111a";
    public string IconBorder { get; init; } = "#111d29";
    public string IconColor { get; init; } = SemanticColor.Muted;
}

public sealed class PfActivityRow
{
    public string Type { get; init; } = "";
    public string TypeColor { get; init; } = SemanticColor.Muted;
    public string TypeBg { get; init; } = "rgba(143,163,184,.1)";
    public string WType { get; init; } = "";
    public string WTypeColor { get; init; } = SemanticColor.Muted;
    public string WTypeBg { get; init; } = "rgba(143,163,184,.1)";
    public string Wallet { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Amount { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = SemanticColor.Positive;
    public string StatusBg { get; init; } = "rgba(61,220,132,.1)";
    public string Fee { get; init; } = "";
    public string Time { get; init; } = "";
}

public sealed class PfMemeRow
{
    public string Emoji { get; init; } = "◈";
    public string Name { get; init; } = "";
    public string Contract { get; init; } = "";
    public string Value { get; init; } = "";
    public string Pnl { get; init; } = "";
    public string PnlColor { get; init; } = "#3d5a72";
    public string Chg24h { get; init; } = "";
    public string ChgColor { get; init; } = "#3d5a72";
    public string Mcap { get; init; } = "";
    public string Liquidity { get; init; } = "";
    public string LiqColor { get; init; } = "#5a7a94";
    public string RugDot { get; init; } = SemanticColor.Positive;
    public string RugScore { get; init; } = "";
    public string RugColor { get; init; } = SemanticColor.Positive;
    public string Signal { get; init; } = "";
    public string SignalColor { get; init; } = SemanticColor.Accent;
    public string SignalBg { get; init; } = "rgba(33,230,193,.1)";
    public string IconBg { get; init; } = "#062006";
    public string IconBorder { get; init; } = "#0d3d0d";
}

public sealed class PfStakingRow
{
    public string Sym { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Asset { get; init; } = "";
    public string Staked { get; init; } = "";
    public string Rewards { get; init; } = "";
    public string Apy { get; init; } = "";
    public string Lockup { get; init; } = "";
    public double LockPct { get; init; }
    public string IconBg { get; init; } = "#05101a";
    public string IconBorder { get; init; } = "#0d2240";
    public string IconColor { get; init; } = SemanticColor.Accent;
    public ICommand? ClaimCommand { get; init; }
    public ICommand? UnstakeCommand { get; init; }
}

public sealed class PfWalletStat
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
}

public sealed class PfRightTab
{
    public string Label { get; init; } = "";
    public string Bg { get; init; } = "transparent";
    public string Border { get; init; } = "transparent";
    public string Color { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
    public string Key { get; init; } = "";
}

public sealed class PfCheckItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string ValueColor { get; init; } = SemanticColor.Muted;
    public string Dot { get; init; } = SemanticColor.Positive;
    public string DotBg { get; init; } = "rgba(61,220,132,.1)";
    public string DotBorder { get; init; } = "#0d2a1e";
}

public sealed class PfCheckGroup
{
    public string Icon { get; init; } = "◈";
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = SemanticColor.Positive;
    public string StatusBg { get; init; } = "rgba(61,220,132,.1)";
    public System.Collections.Generic.IReadOnlyList<PfCheckItem> Items { get; init; } = Array.Empty<PfCheckItem>();
}

public sealed class PfApproval
{
    public string Protocol { get; init; } = "";
    public string Token { get; init; } = "";
    public string Amount { get; init; } = "";
    public string RiskDot { get; init; } = SemanticColor.Positive;
    public string RevokeBorder { get; init; } = "#0d2a1e";
    public string RevokeBg { get; init; } = "rgba(61,220,132,.08)";
    public string RevokeColor { get; init; } = SemanticColor.Positive;
    public ICommand? RevokeCommand { get; init; }
}

public sealed class PfToken
{
    public string Sym { get; init; } = "";
    public string Name { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Price { get; init; } = "";
    public string Value { get; init; } = "";
    public string Chg { get; init; } = "";
    public string ChgColor { get; init; } = "#3d5a72";
    public double AllocPct { get; init; }
    public string AllocColor { get; init; } = SemanticColor.Accent;
    public string IconBg { get; init; } = "#07111a";
    public string IconBorder { get; init; } = "#111d29";
    public string IconColor { get; init; } = SemanticColor.Muted;
}

public sealed class PfTxRow
{
    public string Emoji { get; init; } = "◈";
    public string IconBg { get; init; } = "rgba(61,220,132,.1)";
    public string Action { get; init; } = "";
    public string Amount { get; init; } = "";
    public string AmountColor { get; init; } = SemanticColor.Primary;
    public string Hash { get; init; } = "";
    public string Fee { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = SemanticColor.Positive;
    public string StatusBg { get; init; } = "rgba(61,220,132,.1)";
    public string Time { get; init; } = "";
}

public sealed class PfAnalyticsCard
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Primary;
    public string Desc { get; init; } = "";
}

// ── Add-wallet modal rows ────────────────────────────────────────────────────

public sealed class PfModalTag
{
    public string Label { get; init; } = "";
    public string Color { get; init; } = SemanticColor.Muted;
    public string Bg { get; init; } = "rgba(143,163,184,.1)";
}

public sealed class PfModalWalletType
{
    public string Key { get; init; } = "";
    public string Emoji { get; init; } = "◈";
    public string Label { get; init; } = "";
    public string Desc { get; init; } = "";
    public string LabelColor { get; init; } = SemanticColor.Primary;
    public string IconBg { get; init; } = "#07111a";
    public string IconBorder { get; init; } = "#111d29";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public System.Collections.Generic.IReadOnlyList<PfModalTag> Tags { get; init; } = Array.Empty<PfModalTag>();
    public ICommand? Command { get; init; }
}

public sealed class PfStepDot
{
    public double Width { get; init; } = 8;
    public string Color { get; init; } = "#152535";
}

public sealed class PfCexOption
{
    public string Label { get; init; } = "";
    public string Logo { get; init; } = "🏦";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public string LabelColor { get; init; } = "#5a7a94";
    public ICommand? Command { get; init; }
}

public sealed class PfChainOption
{
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public string Color { get; init; } = "#3d5a72";
    public ICommand? Command { get; init; }
}

public sealed class PfStakeProtocol
{
    public string Logo { get; init; } = "🔒";
    public string Label { get; init; } = "";
    public string Apy { get; init; } = "";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public string LabelColor { get; init; } = "#c8dcef";
    public ICommand? Command { get; init; }
}

public sealed class PfPerpOption
{
    public string Logo { get; init; } = "⚡";
    public string Label { get; init; } = "";
    public string Border { get; init; } = "#152535";
    public string Bg { get; init; } = "#07111a";
    public string LabelColor { get; init; } = "#5a7a94";
    public ICommand? Command { get; init; }
}

public sealed class PfModalCheck
{
    public string Icon { get; init; } = "◈";
    public string Label { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Border { get; init; } = "#0d2a1e";
    public string IconBg { get; init; } = "rgba(61,220,132,.1)";
    public string Status { get; init; } = "PASS";
    public string StatusColor { get; init; } = SemanticColor.Positive;
    public string StatusBg { get; init; } = "rgba(61,220,132,.1)";
}
