namespace CryptoAITerminal.Gateway.DEX;

public sealed record SolanaDiagnosticStep(
    string Title,
    bool Success,
    string Narrative);

public sealed record SolanaExecutionDiagnostics(
    bool Success,
    string Summary,
    IReadOnlyList<SolanaDiagnosticStep> Steps);
