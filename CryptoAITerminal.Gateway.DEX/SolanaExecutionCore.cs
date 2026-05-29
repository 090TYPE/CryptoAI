using System.Text.Json.Nodes;

namespace CryptoAITerminal.Gateway.DEX;

public sealed record SolanaSwapLeg(
    string DexId,
    string InputMint,
    string OutputMint,
    string RouteHint);

public sealed record SolanaSwapQuote(
    string DexId,
    string InputMint,
    string OutputMint,
    decimal InputAmount,
    decimal ExpectedOutputAmount,
    decimal MinimumOutputAmount,
    decimal SlippagePercent,
    IReadOnlyList<SolanaSwapLeg> Legs);

public sealed record SolanaSwapExecutionPlan(
    SolanaSwapQuote Quote,
    string WalletAddress,
    string RecentBlockhash,
    string ExecutionNarrative,
    bool RequiresSignatureMaterial,
    JsonObject SourceQuotePayload);

public sealed record SolanaSimulationResult(
    bool Success,
    string Narrative,
    IReadOnlyList<string> Logs);

public sealed record SolanaSubmitResult(
    bool Success,
    string Signature,
    string Narrative);

public sealed record SolanaSignatureConfirmationResult(
    bool Confirmed,
    bool Finalized,
    string ConfirmationStatus,
    string Narrative,
    string? Error);
