using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class TelegramLoginFlowTests
{
    [Theory]
    [InlineData("+1 (234) 567-89-00", "+12345678900")]
    [InlineData("12345678900", "+12345678900")]
    [InlineData("  +44 7700 900123 ", "+447700900123")]
    public void NormalizePhone_StripsFormatting_AndEnsuresPlus(string input, string expected)
    {
        Assert.Equal(expected, TelegramLoginFlow.NormalizePhone(input));
    }

    [Theory]
    [InlineData(null, TelegramConnectionState.Connected)]
    [InlineData("verification_code", TelegramConnectionState.AwaitingCode)]
    [InlineData("password", TelegramConnectionState.AwaitingPassword)]
    [InlineData("name", TelegramConnectionState.Error)]
    public void MapStep_MapsWTelegramLoginResult_ToState(string? step, TelegramConnectionState expected)
    {
        Assert.Equal(expected, TelegramLoginFlow.MapStep(step));
    }
}
