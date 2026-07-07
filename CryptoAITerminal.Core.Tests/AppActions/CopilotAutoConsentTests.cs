using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using CryptoAITerminal.TerminalUI.ViewModels;
using ReactiveUI;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class CopilotAutoConsentTests
{
    private static CopilotViewModel NewVm() => new(new CopilotAgentService.CopilotDataSource());

    [Fact]
    public void TurningAutoOn_withoutConsent_staysOff_andShowsConsent()
    {
        var vm = NewVm();

        vm.IsAutoMode = true;

        Assert.False(vm.IsAutoMode);
        Assert.True(vm.ShowAutoConsent);
    }

    [Fact]
    public async Task ConfirmingConsent_enablesAuto_andHidesStrip()
    {
        var vm = NewVm();
        vm.IsAutoMode = true; // triggers consent

        await vm.ConfirmAutoModeCommand.Execute();

        Assert.True(vm.IsAutoMode);
        Assert.False(vm.ShowAutoConsent);
    }

    [Fact]
    public async Task Cancelling_leavesAutoOff_andHidesStrip()
    {
        var vm = NewVm();
        vm.IsAutoMode = true;

        await vm.CancelAutoModeCommand.Execute();

        Assert.False(vm.IsAutoMode);
        Assert.False(vm.ShowAutoConsent);
    }

    [Fact]
    public void TurningAutoOff_afterConsent_requiresConsentAgain()
    {
        var vm = NewVm();
        vm.IsAutoMode = true;
        vm.ConfirmAutoModeCommand.Execute().Subscribe();
        Assert.True(vm.IsAutoMode);

        vm.IsAutoMode = false;
        Assert.False(vm.IsAutoMode);

        // consent was reset — turning back on must ask again
        vm.IsAutoMode = true;
        Assert.False(vm.IsAutoMode);
        Assert.True(vm.ShowAutoConsent);
    }
}
