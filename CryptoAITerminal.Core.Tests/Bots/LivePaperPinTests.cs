using CryptoAITerminal.TerminalUI.ViewModels;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Bots;

/// <summary>
/// Без действующей лицензии режим исполнения приколочен к PAPER ONLY. Проверка стояла только в
/// <c>ApplyGlobalExecutionMode</c> — то есть закрывала лишь свой путь, из панели кошелька. Снять
/// флаг мог кто угодно присваиванием свойства, и вкладка «Боты» именно так и делала
/// (<c>BotsDeskViewModel.PaperOnly</c> → <c>Wallet.GlobalPaperOnlyMode = value</c>), минуя и
/// лицензию, и двухшаговое взведение живого режима.
///
/// Само по себе это был бы обход настройки. Вместе с тем, что AI-трейдер смотрел на голый
/// <c>GlobalLiveExecutionEnabled</c> (то есть на <c>!GlobalPaperOnlyMode</c>, без лицензии), пока
/// три остальных движка ходили через <c>TryApproveLiveExecution</c>, получалась пара, дающая
/// реальные ордера на истёкшем триале.
/// </summary>
public class LivePaperPinTests
{
    [Fact]
    public void Without_a_license_the_flag_cannot_be_cleared_by_anyone()
    {
        var wallet = new WalletWorkspaceViewModel { LicenseAllowsLive = false };

        wallet.GlobalPaperOnlyMode = false;   // прямая запись — тот самый путь вкладки «Боты»

        Assert.True(wallet.GlobalPaperOnlyMode);
        Assert.False(wallet.GlobalLiveExecutionEnabled);
    }

    [Fact]
    public void With_a_license_the_flag_still_moves_freely()
    {
        var wallet = new WalletWorkspaceViewModel { LicenseAllowsLive = true };

        wallet.GlobalPaperOnlyMode = false;

        Assert.False(wallet.GlobalPaperOnlyMode);
        Assert.True(wallet.GlobalLiveExecutionEnabled);
    }

    [Fact]
    public void Turning_paper_only_back_on_is_never_blocked()
    {
        // Заслон односторонний: в безопасную сторону флаг обязан двигаться всегда.
        var wallet = new WalletWorkspaceViewModel { LicenseAllowsLive = true };
        wallet.GlobalPaperOnlyMode = false;

        wallet.LicenseAllowsLive = false;
        wallet.GlobalPaperOnlyMode = true;

        Assert.True(wallet.GlobalPaperOnlyMode);
    }

    [Fact]
    public void The_guard_reason_names_the_license_before_the_mode()
    {
        var wallet = new WalletWorkspaceViewModel { LicenseAllowsLive = false };

        Assert.False(wallet.TryApproveLiveExecution("AI trader (CEX)", out var reason));
        Assert.Contains("license", reason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI trader (CEX)", reason);
    }

    [Fact]
    public void A_licensed_but_paper_only_wallet_still_refuses_live()
    {
        var wallet = new WalletWorkspaceViewModel { LicenseAllowsLive = true, GlobalPaperOnlyMode = true };

        Assert.False(wallet.TryApproveLiveExecution("AI trader (CEX)", out var reason));
        Assert.Contains("PAPER ONLY", reason);
    }
}
