namespace CryptoAITerminal.Gateway.DEX;

public sealed class SolanaProgramActivityMonitor : IDisposable
{
    private readonly SolanaRpcClient _rpcClient;
    private readonly SolanaLaunchTransactionParser _transactionParser;
    private readonly SolanaProgramMonitorDefinition _definition;
    private string? _lastSeenSignature;

    public SolanaProgramActivityMonitor(SolanaProgramMonitorDefinition definition)
    {
        _definition = definition;
        _rpcClient = new SolanaRpcClient(definition.RpcUrl);
        _transactionParser = new SolanaLaunchTransactionParser();
    }

    public async Task<IReadOnlyList<SolanaProgramActivitySignal>> PollAsync(CancellationToken cancellationToken = default)
    {
        var signatures = await _rpcClient.GetRecentSignaturesForAddressAsync(
            _definition.ProgramAddress,
            limit: _definition.SignatureLookback,
            cancellationToken: cancellationToken);

        if (_lastSeenSignature is null)
        {
            _lastSeenSignature = signatures.FirstOrDefault()?.Signature;
            return [];
        }

        var fresh = new List<SolanaProgramActivitySignal>();
        foreach (var signature in signatures)
        {
            if (string.Equals(signature.Signature, _lastSeenSignature, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(signature.ErrorJson) && signature.ErrorJson != "null")
            {
                continue;
            }

            var parsedTransaction = await _rpcClient.GetParsedTransactionAsync(signature.Signature, cancellationToken);
            var tokenCandidates = parsedTransaction is null
                ? []
                : _transactionParser.ExtractTokenCandidates(parsedTransaction, _definition);

            fresh.Add(new SolanaProgramActivitySignal(
                _definition.ChainId,
                _definition.SourceLabel,
                _definition.ProgramAddress,
                signature.Signature,
                signature.Slot,
                signature.BlockTimeUnixSeconds,
                tokenCandidates));
        }

        if (signatures.Count > 0)
        {
            _lastSeenSignature = signatures[0].Signature;
        }

        return fresh
            .OrderBy(signal => signal.Slot)
            .ToList();
    }

    public void Dispose()
    {
        _rpcClient.Dispose();
    }
}

public sealed record SolanaProgramMonitorDefinition(
    string ChainId,
    string SourceLabel,
    string ProgramAddress,
    string RpcUrl = "https://api.mainnet-beta.solana.com",
    int SignatureLookback = 25);

public sealed record SolanaProgramActivitySignal(
    string ChainId,
    string SourceLabel,
    string ProgramAddress,
    string Signature,
    long Slot,
    long? BlockTimeUnixSeconds,
    IReadOnlyList<string> TokenCandidates);
