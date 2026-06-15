using System.IO;
using System.Text;
using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DpapiSessionStoreTests
{
    [Fact]
    public void WrittenBytes_SurviveReopen_Encrypted()
    {
        var path = Path.Combine(Path.GetTempPath(), "cryptoai-tg-session-test-" + Path.GetRandomFileName());
        var payload = Encoding.UTF8.GetBytes("telegram-session-bytes-1234567890");
        try
        {
            using (var store = new DpapiSessionStore(path))
            {
                store.Write(payload, 0, payload.Length);
                store.Flush();
            }

            // On-disk file is encrypted (not the raw payload).
            var onDisk = File.ReadAllBytes(path);
            Assert.NotEqual(payload, onDisk);

            // Reopening decrypts the same bytes back.
            using var reopened = new DpapiSessionStore(path);
            var buffer = new byte[payload.Length];
            reopened.Position = 0;
            var read = reopened.Read(buffer, 0, buffer.Length);
            Assert.Equal(payload.Length, read);
            Assert.Equal(payload, buffer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
