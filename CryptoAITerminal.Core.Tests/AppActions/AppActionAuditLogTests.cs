using System.IO;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using Xunit;

namespace CryptoAITerminal.Core.Tests.AppActions;

public class AppActionAuditLogTests
{
    [Fact]
    public void Append_then_reload_roundtrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{System.Guid.NewGuid():N}.json");
        try
        {
            var log = new AppActionAuditLog(path);
            log.Record("trade.place_market", "PLACE MARKET BUY", "proposed", null);
            log.Record("trade.place_market", "PLACE MARKET BUY", "executed", "ok");

            var reloaded = new AppActionAuditLog(path);
            Assert.Equal(2, reloaded.Recent(10).Count);
            Assert.Equal("executed", reloaded.Recent(1)[0].Outcome);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
