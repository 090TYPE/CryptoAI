using System.Reactive.Linq;
using CryptoAITerminal.TerminalUI.ViewModels;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// Covers navigation and validation of the onboarding wizard. Tests deliberately
/// avoid the successful-save path so they never write to the real credentials file.
/// </summary>
public class OnboardingViewModelTests
{
    private static OnboardingViewModel New() => new();

    [Fact]
    public void Open_StartsOnChooseStep_AndIsVisible()
    {
        var vm = New();
        vm.Open();

        Assert.True(vm.IsVisible);
        Assert.True(vm.IsChooseStep);
        Assert.False(vm.IsKeysStep);
        Assert.False(vm.IsDoneStep);
    }

    [Fact]
    public async Task ChooseExchange_AdvancesToKeysStep()
    {
        var vm = New();
        vm.Open();
        await vm.ChooseExchangeCommand.Execute("Bybit");

        Assert.True(vm.IsKeysStep);
        Assert.Equal("Bybit", vm.SelectedExchange);
        Assert.Equal("Connect Bybit", vm.KeysStepTitle);
        Assert.False(vm.NeedsPassphrase);
    }

    [Theory]
    [InlineData("OKX", true)]
    [InlineData("KuCoin", true)]
    [InlineData("Binance", false)]
    [InlineData("Bybit", false)]
    public async Task NeedsPassphrase_DependsOnExchange(string exchange, bool expected)
    {
        var vm = New();
        vm.Open();
        await vm.ChooseExchangeCommand.Execute(exchange);

        Assert.Equal(expected, vm.NeedsPassphrase);
    }

    [Fact]
    public async Task SaveKeys_WithEmptyInputs_StaysOnKeysStepWithError()
    {
        var vm = New();
        vm.Open();
        await vm.ChooseExchangeCommand.Execute("Bybit");
        await vm.SaveKeysCommand.Execute();

        Assert.True(vm.IsKeysStep);
        Assert.False(vm.IsDoneStep);
        Assert.Contains("key", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveKeys_OkxWithoutPassphrase_RequestsPassphrase()
    {
        var vm = New();
        vm.Open();
        await vm.ChooseExchangeCommand.Execute("OKX");
        vm.KeyInput = "k";
        vm.SecretInput = "s";
        await vm.SaveKeysCommand.Execute();

        Assert.True(vm.IsKeysStep);
        Assert.Contains("passphrase", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Back_ReturnsToChooseStep()
    {
        var vm = New();
        vm.Open();
        await vm.ChooseExchangeCommand.Execute("OKX");
        await vm.BackCommand.Execute();

        Assert.True(vm.IsChooseStep);
    }

    [Fact]
    public async Task Skip_ClosesWizard()
    {
        var vm = New();
        vm.Open();
        await vm.SkipCommand.Execute();

        Assert.False(vm.IsVisible);
    }
}
