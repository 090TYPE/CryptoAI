using System.Threading;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Fetches human-readable release notes (markdown) for a given app version.
/// Failure-tolerant: any error (no network, missing release, empty body) returns null.
/// </summary>
public interface IReleaseNotesService
{
    Task<string?> GetNotesAsync(string version, CancellationToken ct = default);
}
