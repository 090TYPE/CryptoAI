using System.Net.Http;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Types;
using Solana.Unity.Wallet;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class SolanaRpcClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly IRpcClient _sdkClient;

    public SolanaRpcClient(string rpcUrl = "https://api.mainnet-beta.solana.com", HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(rpcUrl);
        _sdkClient = ClientFactory.GetClient(rpcUrl, null, _httpClient, null);
    }

    public async Task<decimal> GetNativeBalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!IsValidAddress(address))
        {
            throw new ArgumentException("Invalid Solana address.", nameof(address));
        }

        var response = await _sdkClient.GetBalanceAsync(address, Commitment.Confirmed);
        if (!response.WasSuccessful || response.Result?.Value is null)
        {
            throw new InvalidOperationException($"Solana SDK balance request failed: {response.Reason}");
        }

        return response.Result.Value / 1_000_000_000m;
    }

    public async Task<int> GetTokenDecimalsAsync(string mintAddress, CancellationToken cancellationToken = default)
    {
        if (!IsValidAddress(mintAddress))
        {
            throw new ArgumentException("Invalid Solana mint address.", nameof(mintAddress));
        }

        var response = await _sdkClient.GetTokenSupplyAsync(mintAddress, Commitment.Confirmed);
        if (!response.WasSuccessful || response.Result?.Value is null)
        {
            throw new InvalidOperationException($"Solana SDK token supply request failed: {response.Reason}");
        }

        return response.Result.Value.Decimals;
    }

    public async Task<decimal> GetTokenBalanceAsync(string ownerAddress, string mintAddress, CancellationToken cancellationToken = default)
    {
        if (!IsValidAddress(ownerAddress))
        {
            throw new ArgumentException("Invalid Solana owner address.", nameof(ownerAddress));
        }

        if (!IsValidAddress(mintAddress))
        {
            throw new ArgumentException("Invalid Solana mint address.", nameof(mintAddress));
        }

        var response = await _sdkClient.GetTokenBalanceByOwnerAsync(ownerAddress, mintAddress, Commitment.Confirmed);
        if (!response.WasSuccessful)
        {
            throw new InvalidOperationException($"Solana SDK token balance request failed: {response.Reason}");
        }

        if (response.Result?.Value is null)
        {
            return 0m;
        }

        if (decimal.TryParse(response.Result.Value.UiAmountString, out var parsedUiAmount))
        {
            return parsedUiAmount;
        }

        return response.Result.Value.AmountDecimal;
    }

    public async Task<string> GetLatestBlockhashAsync(CancellationToken cancellationToken = default)
    {
        var response = await _sdkClient.GetLatestBlockHashAsync(Commitment.Confirmed);
        var blockhash = response.Result?.Value?.Blockhash;
        if (!response.WasSuccessful)
        {
            throw new InvalidOperationException($"Solana SDK latest blockhash request failed: {response.Reason}");
        }

        if (string.IsNullOrWhiteSpace(blockhash))
        {
            throw new InvalidOperationException("Solana RPC returned an empty blockhash payload.");
        }

        return blockhash;
    }

    public async Task<SolanaSimulationResult> SimulateRawTransactionAsync(string base64Transaction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(base64Transaction))
        {
            throw new ArgumentException("Transaction payload is empty.", nameof(base64Transaction));
        }

        var transactionBytes = Convert.FromBase64String(base64Transaction);
        var response = await _sdkClient.SimulateTransactionAsync(transactionBytes, true, Commitment.Confirmed, false, new List<string>());
        var logs = response.Result?.Value?.Logs?.ToList() ?? [];
        var error = response.Result?.Value?.Error?.ToString();
        var logText = logs.Count == 0
            ? string.Empty
            : $" Logs: {string.Join(" | ", logs.Take(4))}";
        return new SolanaSimulationResult(
            response.WasSuccessful && string.IsNullOrWhiteSpace(error),
            response.WasSuccessful && string.IsNullOrWhiteSpace(error)
                ? "Simulation completed without a reported RPC error."
                : $"Simulation failed: {error ?? response.Reason}.{logText}",
            logs);
    }

    public async Task<SolanaSubmitResult> SendRawTransactionAsync(string base64Transaction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(base64Transaction))
        {
            throw new ArgumentException("Transaction payload is empty.", nameof(base64Transaction));
        }

        var response = await _sdkClient.SendTransactionAsync(base64Transaction, false, Commitment.Confirmed);
        if (!response.WasSuccessful || string.IsNullOrWhiteSpace(response.Result))
        {
            return new SolanaSubmitResult(false, string.Empty, $"Solana SDK did not accept the transaction: {response.Reason}");
        }

        return new SolanaSubmitResult(true, response.Result, "Transaction submitted to Solana RPC.");
    }

    /// <summary>
    /// Sends transaction through Jito block engine for MEV protection.
    /// Falls back to standard RPC if Jito submission fails.
    /// </summary>
    public async Task<SolanaSubmitResult> SendRawTransactionWithJitoAsync(
        string base64Transaction,
        long tipLamports = 10_000,
        CancellationToken cancellationToken = default)
    {
        using var jito = new JitoTipManager();
        var bundleResult = await jito.SendBundleAsync(base64Transaction, tipLamports, cancellationToken);

        if (bundleResult.Success)
        {
            return new SolanaSubmitResult(true, bundleResult.BundleId,
                $"Transaction submitted via Jito bundle. {bundleResult.StatusLabel}");
        }

        // Fallback to standard RPC
        var fallback = await SendRawTransactionAsync(base64Transaction, cancellationToken);
        return fallback with
        {
            Narrative = $"Jito failed ({bundleResult.Error}), fell back to standard RPC. {fallback.Narrative}"
        };
    }

    public async Task<SolanaSignatureConfirmationResult> WaitForSignatureConfirmationAsync(
        string signature,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException("Transaction signature is empty.", nameof(signature));
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        var lastStatus = "unknown";

        while (DateTimeOffset.UtcNow < deadline)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "getSignatureStatuses",
                ["params"] = new JsonArray
                {
                    new JsonArray(signature),
                    new JsonObject
                    {
                        ["searchTransactionHistory"] = true
                    }
                }
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(string.Empty, content, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            var document = JsonNode.Parse(raw)?.AsObject()
                ?? throw new InvalidOperationException("Solana RPC signature status response was empty.");
            var statusNode = document["result"]?["value"]?.AsArray()?[0];
            if (statusNode is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1250), cancellationToken);
                continue;
            }

            var confirmationStatus = statusNode["confirmationStatus"]?.GetValue<string>() ?? "processed";
            lastStatus = confirmationStatus;
            var errorNode = statusNode["err"];
            var errorText = errorNode is null || errorNode.ToJsonString() == "null"
                ? null
                : errorNode.ToJsonString();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                return new SolanaSignatureConfirmationResult(
                    false,
                    string.Equals(confirmationStatus, "finalized", StringComparison.OrdinalIgnoreCase),
                    confirmationStatus,
                    $"Solana RPC reported a transaction error for {signature[..Math.Min(10, signature.Length)]}: {errorText}",
                    errorText);
            }

            if (string.Equals(confirmationStatus, "confirmed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(confirmationStatus, "finalized", StringComparison.OrdinalIgnoreCase))
            {
                var finalized = string.Equals(confirmationStatus, "finalized", StringComparison.OrdinalIgnoreCase);
                return new SolanaSignatureConfirmationResult(
                    true,
                    finalized,
                    confirmationStatus,
                    $"Signature {signature[..Math.Min(10, signature.Length)]} reached {confirmationStatus} status on Solana RPC.",
                    null);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1250), cancellationToken);
        }

        return new SolanaSignatureConfirmationResult(
            false,
            string.Equals(lastStatus, "finalized", StringComparison.OrdinalIgnoreCase),
            lastStatus,
            $"Timed out waiting for signature confirmation. Last observed Solana status: {lastStatus}.",
            null);
    }

    public async Task<IReadOnlyList<SolanaSignatureInfo>> GetRecentSignaturesForAddressAsync(
        string address,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAddress(address))
        {
            throw new ArgumentException("Invalid Solana address.", nameof(address));
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "getSignaturesForAddress",
            ["params"] = new JsonArray
            {
                address,
                new JsonObject
                {
                    ["limit"] = Math.Clamp(limit, 1, 100),
                    ["commitment"] = "confirmed"
                }
            }
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(string.Empty, content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = JsonNode.Parse(raw)?.AsObject()
            ?? throw new InvalidOperationException("Solana RPC signature list response was empty.");
        var resultArray = document["result"]?.AsArray()
            ?? throw new InvalidOperationException("Solana RPC signature list payload was empty.");

        var signatures = new List<SolanaSignatureInfo>(resultArray.Count);
        foreach (var item in resultArray)
        {
            if (item is not JsonObject signatureObject)
            {
                continue;
            }

            var signature = signatureObject["signature"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(signature))
            {
                continue;
            }

            signatures.Add(new SolanaSignatureInfo(
                signature,
                signatureObject["slot"]?.GetValue<long>() ?? 0,
                signatureObject["blockTime"]?.GetValue<long?>(),
                signatureObject["err"]?.ToJsonString()));
        }

        return signatures;
    }

    public async Task<SolanaParsedTransaction?> GetParsedTransactionAsync(
        string signature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException("Transaction signature is empty.", nameof(signature));
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "getTransaction",
            ["params"] = new JsonArray
            {
                signature,
                new JsonObject
                {
                    ["commitment"] = "confirmed",
                    ["encoding"] = "jsonParsed",
                    ["maxSupportedTransactionVersion"] = 0
                }
            }
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(string.Empty, content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = JsonNode.Parse(raw)?.AsObject()
            ?? throw new InvalidOperationException("Solana RPC parsed transaction response was empty.");
        var result = document["result"]?.AsObject();
        if (result is null)
        {
            return null;
        }

        var accountKeys = ParseAccountKeys(result["transaction"]?["message"]?["accountKeys"]);
        var logMessages = ParseStringArray(result["meta"]?["logMessages"]);
        var initializedMints = ParseInitializedMints(
            result["transaction"]?["message"]?["instructions"],
            result["meta"]?["innerInstructions"]);
        var postTokenBalances = ParseTokenBalances(result["meta"]?["postTokenBalances"]);

        return new SolanaParsedTransaction(
            signature,
            result["slot"]?.GetValue<long>() ?? 0,
            result["blockTime"]?.GetValue<long?>(),
            result["meta"]?["fee"]?.GetValue<long>() ?? 0,
            accountKeys,
            logMessages,
            initializedMints,
            postTokenBalances);
    }

    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        return PublicKey.IsValid(address, false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static IReadOnlyList<string> ParseAccountKeys(JsonNode? accountKeysNode)
    {
        if (accountKeysNode is not JsonArray array)
        {
            return [];
        }

        var keys = new List<string>(array.Count);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonValue value when value.TryGetValue<string>(out var rawKey) && !string.IsNullOrWhiteSpace(rawKey):
                    keys.Add(rawKey);
                    break;
                case JsonObject keyObject:
                    var pubkey = keyObject["pubkey"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(pubkey))
                    {
                        keys.Add(pubkey);
                    }

                    break;
            }
        }

        return keys;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(static item => item?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();
    }

    private static IReadOnlyList<string> ParseInitializedMints(JsonNode? instructionsNode, JsonNode? innerInstructionsNode)
    {
        var mints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractInitializedMintsFromInstructionArray(instructionsNode, mints);

        if (innerInstructionsNode is JsonArray innerArray)
        {
            foreach (var item in innerArray.OfType<JsonObject>())
            {
                ExtractInitializedMintsFromInstructionArray(item["instructions"], mints);
            }
        }

        return mints.ToList();
    }

    private static void ExtractInitializedMintsFromInstructionArray(JsonNode? instructionsNode, HashSet<string> mints)
    {
        if (instructionsNode is not JsonArray instructionsArray)
        {
            return;
        }

        foreach (var instruction in instructionsArray.OfType<JsonObject>())
        {
            var parsed = instruction["parsed"]?.AsObject();
            var parsedType = parsed?["type"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(parsedType) ||
                !parsedType.Contains("initializeMint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mint = parsed?["info"]?["mint"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(mint))
            {
                mints.Add(mint);
            }
        }
    }

    private static IReadOnlyList<SolanaParsedTokenBalance> ParseTokenBalances(JsonNode? balancesNode)
    {
        if (balancesNode is not JsonArray balancesArray)
        {
            return [];
        }

        var balances = new List<SolanaParsedTokenBalance>(balancesArray.Count);
        foreach (var item in balancesArray.OfType<JsonObject>())
        {
            var mint = item["mint"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(mint))
            {
                continue;
            }

            var uiAmountNode = item["uiTokenAmount"];
            decimal uiAmount = 0m;
            var uiAmountString = uiAmountNode?["uiAmountString"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(uiAmountString))
            {
                decimal.TryParse(uiAmountString, NumberStyles.Any, CultureInfo.InvariantCulture, out uiAmount);
            }
            else
            {
                var uiAmountValue = uiAmountNode?["uiAmount"]?.GetValue<decimal?>();
                uiAmount = uiAmountValue ?? 0m;
            }

            balances.Add(new SolanaParsedTokenBalance(
                mint,
                item["owner"]?.GetValue<string>(),
                item["programId"]?.GetValue<string>(),
                uiAmount));
        }

        return balances;
    }

    private sealed class SolanaRpcResponse<TResult>
    {
        public TResult? Result { get; set; }
    }

    private sealed class SolanaBalanceResult
    {
        public long Value { get; set; }
    }

    private sealed class SolanaTokenSupplyResult
    {
        public SolanaTokenSupplyValue? Value { get; set; }
    }

    private sealed class SolanaTokenSupplyValue
    {
        public int Decimals { get; set; }
    }

    private sealed class SolanaTokenAccountsResult
    {
        public List<SolanaTokenAccountItem> Value { get; set; } = [];
    }

    private sealed class SolanaTokenAccountItem
    {
        public SolanaTokenAccountDataEnvelope? Account { get; set; }
    }

    private sealed class SolanaTokenAccountDataEnvelope
    {
        public SolanaParsedDataContainer? Data { get; set; }
    }

    private sealed class SolanaParsedDataContainer
    {
        public SolanaParsedAccountInfo? Parsed { get; set; }
    }

    private sealed class SolanaParsedAccountInfo
    {
        public SolanaTokenInfo? Info { get; set; }
    }

    private sealed class SolanaTokenInfo
    {
        public SolanaTokenAmountInfo? TokenAmount { get; set; }
    }

    private sealed class SolanaTokenAmountInfo
    {
        public decimal? UiAmount { get; set; }
        public string? UiAmountString { get; set; }
        public int Decimals { get; set; }
    }
}

public sealed record SolanaSignatureInfo(
    string Signature,
    long Slot,
    long? BlockTimeUnixSeconds,
    string? ErrorJson);
