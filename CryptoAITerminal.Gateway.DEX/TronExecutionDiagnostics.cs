namespace CryptoAITerminal.Gateway.DEX;

public sealed record TronDiagnosticStep(
    string Title,
    bool Success,
    string Narrative);

public sealed record TronExecutionDiagnostics(
    bool Success,
    string Summary,
    IReadOnlyList<TronDiagnosticStep> Steps);
