namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Decides what the DEX list auto-refresh tick should do. A specific chain uses
/// the multi-request scout, so it is never auto-rescanned on the fast timer —
/// only the cheap "All" latest feed and active search keep refreshing.
/// </summary>
public static class DexRefreshPolicy
{
    public enum AutoRefreshAction
    {
        Skip,
        ReloadLatest,
        Search
    }

    /// <param name="chainId">Resolved chain id; null/empty means the "All" latest feed.</param>
    /// <param name="searchText">Current search box text.</param>
    public static AutoRefreshAction NextAutoRefresh(string? chainId, string? searchText)
    {
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            return AutoRefreshAction.Search;
        }

        return string.IsNullOrWhiteSpace(chainId)
            ? AutoRefreshAction.ReloadLatest
            : AutoRefreshAction.Skip;
    }
}
