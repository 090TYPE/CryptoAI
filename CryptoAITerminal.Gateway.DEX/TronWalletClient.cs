using System.Globalization;
using System.Numerics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethereum.ABI;
using Nethereum.ABI.Model;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class TronWalletClient
{
    private const decimal SunPerTrx = 1_000_000m;
    private const string DefaultRpcUrl = "https://api.trongrid.io/";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;

    public TronWalletClient(HttpClient? httpClient = null, string? rpcUrl = null, string? apiKey = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= new Uri(string.IsNullOrWhiteSpace(rpcUrl) ? DefaultRpcUrl : rpcUrl);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoAITerminal/1.0");
        }

        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? Environment.GetEnvironmentVariable("TRONGRID_API_KEY")
            : apiKey;
        if (!string.IsNullOrWhiteSpace(effectiveApiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("TRON-PRO-API-KEY"))
        {
            _httpClient.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", effectiveApiKey);
        }
    }

    public async Task<decimal> GetNativeBalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["address"] = address,
            ["visible"] = true
        };

        var response = await PostAsync("wallet/getaccount", payload, cancellationToken);
        var balanceSun = response?["balance"]?.GetValue<long>() ?? 0L;
        return balanceSun / SunPerTrx;
    }

    public async Task<int> GetTrc20DecimalsAsync(string contractAddress, string ownerAddress, CancellationToken cancellationToken = default)
    {
        var response = await TriggerConstantContractRawAsync(
            ownerAddress,
            contractAddress,
            "decimals()",
            parameterHex: string.Empty,
            cancellationToken);

        var resultHex = response?["constant_result"]?[0]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resultHex))
        {
            throw new InvalidOperationException("Tron RPC did not return token decimals.");
        }

        return (int)BigInteger.Parse($"0{resultHex}", NumberStyles.HexNumber);
    }

    public async Task<decimal> GetTrc20BalanceAsync(string ownerAddress, string contractAddress, CancellationToken cancellationToken = default)
    {
        var encoded = EncodeAddressParameter(ownerAddress);
        var response = await TriggerConstantContractRawAsync(
            ownerAddress,
            contractAddress,
            "balanceOf(address)",
            encoded,
            cancellationToken);

        var resultHex = response?["constant_result"]?[0]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resultHex))
        {
            return 0m;
        }

        var rawBalance = BigInteger.Parse($"0{resultHex}", NumberStyles.HexNumber);
        var decimals = await GetTrc20DecimalsAsync(contractAddress, ownerAddress, cancellationToken);
        return ScaleDown(rawBalance, decimals);
    }

    public async Task<TronTransferResult> SendTrc20Async(
        string privateKey,
        string ownerAddress,
        string contractAddress,
        string recipientAddress,
        decimal amount,
        decimal feeLimitTrx = 30m,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Transfer amount must be positive.");
        }

        var decimals = await GetTrc20DecimalsAsync(contractAddress, ownerAddress, cancellationToken);
        var scaledAmount = ScaleUp(amount, decimals);
        var parameterHex = EncodeTransferParameters(recipientAddress, scaledAmount);
        var feeLimitSun = Convert.ToInt64(Math.Max(1m, feeLimitTrx) * SunPerTrx);

        var triggerPayload = new JsonObject
        {
            ["owner_address"] = ownerAddress,
            ["contract_address"] = contractAddress,
            ["function_selector"] = "transfer(address,uint256)",
            ["parameter"] = parameterHex,
            ["fee_limit"] = feeLimitSun,
            ["call_value"] = 0,
            ["visible"] = true
        };

        var response = await PostAsync("wallet/triggersmartcontract", triggerPayload, cancellationToken);
        var resultNode = response?["result"];
        if (resultNode?["result"]?.GetValue<bool>() != true)
        {
            var failureMessage = DecodeMessage(resultNode?["message"]?.GetValue<string>()) ??
                                 resultNode?["message"]?.GetValue<string>() ??
                                 "TRC20 transfer creation failed.";
            throw new InvalidOperationException(failureMessage);
        }

        var transaction = response?["transaction"]?.AsObject()
                          ?? throw new InvalidOperationException("Tron RPC did not return an unsigned transaction.");
        var rawDataHex = transaction["raw_data_hex"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("Unsigned Tron transaction is missing raw_data_hex.");
        var signatureHex = SignRawDataHex(rawDataHex, privateKey);
        transaction["signature"] = new JsonArray(signatureHex);

        var txId = transaction["txID"]?.GetValue<string>() ?? ComputeTxId(rawDataHex);
        var broadcast = await PostAsync("wallet/broadcasttransaction", transaction, cancellationToken);
        if (broadcast?["result"]?.GetValue<bool>() != true)
        {
            var failureMessage = DecodeMessage(broadcast?["message"]?.GetValue<string>()) ??
                                 broadcast?["code"]?.GetValue<string>() ??
                                 "Broadcast rejected by Tron RPC.";
            throw new InvalidOperationException(failureMessage);
        }

        var confirmation = await WaitForConfirmationAsync(txId, cancellationToken);
        return new TronTransferResult(
            txId,
            confirmation.Confirmed,
            amount,
            decimals,
            confirmation.Narrative);
    }

    public async Task<BigInteger> TriggerConstantContractAsync(
        string ownerAddress,
        string contractAddress,
        string functionSelector,
        string parameterHex,
        CancellationToken cancellationToken = default)
    {
        var response = await TriggerConstantContractRawAsync(ownerAddress, contractAddress, functionSelector, parameterHex, cancellationToken);
        return ParseSingleBigIntegerResult(response);
    }

    public async Task<IReadOnlyList<BigInteger>> TriggerConstantContractArrayAsync(
        string ownerAddress,
        string contractAddress,
        string functionSelector,
        string parameterHex,
        CancellationToken cancellationToken = default)
    {
        var response = await TriggerConstantContractRawAsync(ownerAddress, contractAddress, functionSelector, parameterHex, cancellationToken);
        var resultHex = response?["constant_result"]?[0]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resultHex))
        {
            return [];
        }

        if (resultHex.Length < 128)
        {
            return [];
        }

        var values = new List<BigInteger>();
        var lengthHex = resultHex.Substring(64, 64);
        var itemCount = (int)BigInteger.Parse($"0{lengthHex}", NumberStyles.HexNumber);
        var offset = 128;
        for (var index = 0; index < itemCount; index++)
        {
            if (offset + 64 > resultHex.Length)
            {
                break;
            }

            var itemHex = resultHex.Substring(offset, 64);
            values.Add(BigInteger.Parse($"0{itemHex}", NumberStyles.HexNumber));
            offset += 64;
        }

        return values;
    }

    public async Task<TronContractInvocationResult> ExecuteSmartContractAsync(
        string privateKey,
        string ownerAddress,
        string contractAddress,
        string functionSelector,
        string parameterHex,
        decimal feeLimitTrx = 30m,
        long callValueSun = 0L,
        CancellationToken cancellationToken = default)
    {
        var feeLimitSun = Convert.ToInt64(Math.Max(1m, feeLimitTrx) * SunPerTrx);
        var triggerPayload = new JsonObject
        {
            ["owner_address"] = ownerAddress,
            ["contract_address"] = contractAddress,
            ["function_selector"] = functionSelector,
            ["parameter"] = parameterHex,
            ["fee_limit"] = feeLimitSun,
            ["call_value"] = callValueSun,
            ["visible"] = true
        };

        var response = await PostAsync("wallet/triggersmartcontract", triggerPayload, cancellationToken);
        var resultNode = response?["result"];
        if (resultNode?["result"]?.GetValue<bool>() != true)
        {
            var failureMessage = DecodeMessage(resultNode?["message"]?.GetValue<string>()) ??
                                 resultNode?["message"]?.GetValue<string>() ??
                                 "Tron smart-contract invocation failed.";
            throw new InvalidOperationException(failureMessage);
        }

        var transaction = response?["transaction"]?.AsObject()
                          ?? throw new InvalidOperationException("Tron RPC did not return an unsigned transaction.");
        var rawDataHex = transaction["raw_data_hex"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("Unsigned Tron transaction is missing raw_data_hex.");
        var signatureHex = SignRawDataHex(rawDataHex, privateKey);
        transaction["signature"] = new JsonArray(signatureHex);

        var txId = transaction["txID"]?.GetValue<string>() ?? ComputeTxId(rawDataHex);
        var broadcast = await PostAsync("wallet/broadcasttransaction", transaction, cancellationToken);
        if (broadcast?["result"]?.GetValue<bool>() != true)
        {
            var failureMessage = DecodeMessage(broadcast?["message"]?.GetValue<string>()) ??
                                 broadcast?["code"]?.GetValue<string>() ??
                                 "Broadcast rejected by Tron RPC.";
            throw new InvalidOperationException(failureMessage);
        }

        var confirmation = await WaitForConfirmationAsync(txId, cancellationToken);
        var info = await GetTransactionInfoByIdAsync(txId, cancellationToken);
        return new TronContractInvocationResult(
            txId,
            confirmation.Confirmed,
            confirmation.Narrative,
            info?["receipt"]?["energy_usage_total"]?.GetValue<long>() ?? 0L,
            info?["fee"]?.GetValue<long>() ?? 0L);
    }

    public async Task<TronSmartContractPreviewResult> PreviewSmartContractAsync(
        string ownerAddress,
        string contractAddress,
        string functionSelector,
        string parameterHex,
        decimal feeLimitTrx = 30m,
        long callValueSun = 0L,
        CancellationToken cancellationToken = default)
    {
        var feeLimitSun = Convert.ToInt64(Math.Max(1m, feeLimitTrx) * SunPerTrx);
        var triggerPayload = new JsonObject
        {
            ["owner_address"] = ownerAddress,
            ["contract_address"] = contractAddress,
            ["function_selector"] = functionSelector,
            ["parameter"] = parameterHex,
            ["fee_limit"] = feeLimitSun,
            ["call_value"] = callValueSun,
            ["visible"] = true
        };

        var response = await PostAsync("wallet/triggersmartcontract", triggerPayload, cancellationToken);
        var resultNode = response?["result"];
        var success = resultNode?["result"]?.GetValue<bool>() == true;
        var message = DecodeMessage(resultNode?["message"]?.GetValue<string>()) ??
                      resultNode?["message"]?.GetValue<string>() ??
                      (success ? "Unsigned smart-contract preview built successfully." : "Unsigned smart-contract preview failed.");
        var txId = response?["transaction"]?["txID"]?.GetValue<string>();
        var energyUsed = response?["energy_used"]?.GetValue<long>() ??
                         response?["energy_penalty"]?.GetValue<long>() ??
                         response?["transaction"]?["raw_data"]?["fee_limit"]?.GetValue<long>() ??
                         0L;

        return new TronSmartContractPreviewResult(success, message, txId, energyUsed);
    }

    public async Task<JsonNode?> GetTransactionInfoByIdAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["value"] = transactionId
        };

        return await PostAsync("wallet/gettransactioninfobyid", payload, cancellationToken, tolerateEmptyBody: true);
    }

    public static string EncodeAddressArrayParameter(params string[] addresses)
    {
        var encoder = new ABIEncode();
        var evmHexAddresses = addresses.Select(TronAddressCodec.ToEvmHexAddress).ToArray();
        return Convert.ToHexString(encoder.GetABIEncoded(new ABIValue("address[]", evmHexAddresses))).ToLowerInvariant();
    }

    public static string EncodeAddressAndUint256Parameters(string address, BigInteger rawAmount)
    {
        var encoder = new ABIEncode();
        var evmHex = TronAddressCodec.ToEvmHexAddress(address);
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("address", evmHex),
            new ABIValue("uint256", rawAmount))).ToLowerInvariant();
    }

    public static string EncodeTwoAddressParameters(string firstAddress, string secondAddress)
    {
        var encoder = new ABIEncode();
        var firstHex = TronAddressCodec.ToEvmHexAddress(firstAddress);
        var secondHex = TronAddressCodec.ToEvmHexAddress(secondAddress);
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("address", firstHex),
            new ABIValue("address", secondHex))).ToLowerInvariant();
    }

    public static string EncodeUint256AndAddressArrayParameters(BigInteger rawAmount, params string[] addresses)
    {
        var encoder = new ABIEncode();
        var evmHexAddresses = addresses.Select(TronAddressCodec.ToEvmHexAddress).ToArray();
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("uint256", rawAmount),
            new ABIValue("address[]", evmHexAddresses))).ToLowerInvariant();
    }

    public static string EncodeSwapExactNativeForTokensParameters(BigInteger amountOutMin, string[] path, string toAddress, BigInteger deadlineUnix)
    {
        var encoder = new ABIEncode();
        var evmHexAddresses = path.Select(TronAddressCodec.ToEvmHexAddress).ToArray();
        var toHex = TronAddressCodec.ToEvmHexAddress(toAddress);
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("uint256", amountOutMin),
            new ABIValue("address[]", evmHexAddresses),
            new ABIValue("address", toHex),
            new ABIValue("uint256", deadlineUnix))).ToLowerInvariant();
    }

    public static string EncodeSwapExactTokensParameters(BigInteger amountIn, BigInteger amountOutMin, string[] path, string toAddress, BigInteger deadlineUnix)
    {
        var encoder = new ABIEncode();
        var evmHexAddresses = path.Select(TronAddressCodec.ToEvmHexAddress).ToArray();
        var toHex = TronAddressCodec.ToEvmHexAddress(toAddress);
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("uint256", amountIn),
            new ABIValue("uint256", amountOutMin),
            new ABIValue("address[]", evmHexAddresses),
            new ABIValue("address", toHex),
            new ABIValue("uint256", deadlineUnix))).ToLowerInvariant();
    }

    private async Task<JsonNode?> TriggerConstantContractRawAsync(
        string ownerAddress,
        string contractAddress,
        string functionSelector,
        string parameterHex,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["owner_address"] = ownerAddress,
            ["contract_address"] = contractAddress,
            ["function_selector"] = functionSelector,
            ["parameter"] = parameterHex,
            ["visible"] = true
        };

        return await PostAsync("wallet/triggerconstantcontract", payload, cancellationToken);
    }

    private async Task<TronConfirmationStatus> WaitForConfirmationAsync(string transactionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await GetTransactionInfoByIdAsync(transactionId, cancellationToken);
            if (response is not null && response.AsObject().Count > 0)
            {
                var receiptResult = response["receipt"]?["result"]?.GetValue<string>();
                var resMessage = DecodeMessage(response["resMessage"]?.GetValue<string>());
                var confirmed = string.Equals(receiptResult, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                return new TronConfirmationStatus(
                    confirmed,
                    confirmed
                        ? "TRC20 transfer confirmed on Tron."
                        : $"Tron receipt returned {receiptResult ?? "unknown"}{(string.IsNullOrWhiteSpace(resMessage) ? string.Empty : $": {resMessage}")}");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return new TronConfirmationStatus(false, "Broadcast accepted, but Tron confirmation did not arrive before timeout.");
    }

    private async Task<JsonNode?> PostAsync(
        string relativeUrl,
        JsonObject payload,
        CancellationToken cancellationToken,
        bool tolerateEmptyBody = false)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(relativeUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return tolerateEmptyBody ? null : throw new InvalidOperationException($"Tron RPC returned an empty payload for {relativeUrl}.");
        }

        return JsonNode.Parse(raw);
    }

    private static BigInteger ParseSingleBigIntegerResult(JsonNode? response)
    {
        var resultHex = response?["constant_result"]?[0]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(resultHex))
        {
            return BigInteger.Zero;
        }

        return BigInteger.Parse($"0{resultHex}", NumberStyles.HexNumber);
    }

    private static string EncodeAddressParameter(string address)
    {
        var encoder = new ABIEncode();
        var evmHex = TronAddressCodec.ToEvmHexAddress(address);
        return Convert.ToHexString(encoder.GetABIEncoded(new ABIValue("address", evmHex))).ToLowerInvariant();
    }

    private static string EncodeTransferParameters(string recipientAddress, BigInteger rawAmount)
    {
        var encoder = new ABIEncode();
        var evmHex = TronAddressCodec.ToEvmHexAddress(recipientAddress);
        return Convert.ToHexString(encoder.GetABIEncoded(
            new ABIValue("address", evmHex),
            new ABIValue("uint256", rawAmount))).ToLowerInvariant();
    }

    private static string SignRawDataHex(string rawDataHex, string privateKey)
    {
        var rawBytes = Convert.FromHexString(rawDataHex);
        var hash = SHA256.HashData(rawBytes);
        var key = new EthECKey(NormalizePrivateKey(privateKey));
        var signature = key.SignAndCalculateV(hash);

        var signatureBytes = new byte[65];
        signature.R.PadLeft(32).CopyTo(signatureBytes, 0);
        signature.S.PadLeft(32).CopyTo(signatureBytes, 32);
        signatureBytes[64] = signature.V.Length > 0 && signature.V[0] >= 27
            ? (byte)(signature.V[0] - 27)
            : signature.V[0];
        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    private static string ComputeTxId(string rawDataHex)
    {
        var rawBytes = Convert.FromHexString(rawDataHex);
        return Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
    }

    private static decimal ScaleDown(BigInteger value, int decimals)
    {
        if (decimals <= 0)
        {
            return (decimal)value;
        }

        var divisor = BigInteger.Pow(10, decimals);
        var integerPart = BigInteger.DivRem(value, divisor, out var remainder);
        var normalized = $"{integerPart.ToString(CultureInfo.InvariantCulture)}.{remainder.ToString().PadLeft(decimals, '0')}";
        return decimal.Parse(normalized, CultureInfo.InvariantCulture);
    }

    private static BigInteger ScaleUp(decimal value, int decimals)
    {
        var normalized = value.ToString(CultureInfo.InvariantCulture);
        if (!normalized.Contains('.'))
        {
            normalized += ".0";
        }

        var parts = normalized.Split('.', 2);
        var fractional = parts[1].PadRight(decimals, '0');
        if (fractional.Length > decimals)
        {
            fractional = fractional[..decimals];
        }

        var combined = parts[0] + fractional;
        return BigInteger.Parse(string.IsNullOrWhiteSpace(combined) ? "0" : combined, CultureInfo.InvariantCulture);
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        var normalized = privateKey.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length != 64)
        {
            throw new ArgumentException("A Tron private key must be 32 bytes (64 hex chars).", nameof(privateKey));
        }

        return normalized;
    }

    private static string? DecodeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return value;
        }
    }
}

public sealed record TronTransferResult(
    string TransactionId,
    bool Confirmed,
    decimal Amount,
    int TokenDecimals,
    string Narrative);

public sealed record TronContractInvocationResult(
    string TransactionId,
    bool Confirmed,
    string Narrative,
    long EnergyUsed,
    long FeeSun);

public sealed record TronSmartContractPreviewResult(
    bool Success,
    string Narrative,
    string? TransactionId,
    long EnergyUsed);

internal sealed record TronConfirmationStatus(bool Confirmed, string Narrative);

internal static class TronByteArrayExtensions
{
    public static byte[] PadLeft(this byte[] source, int totalLength)
    {
        if (source.Length >= totalLength)
        {
            return source[^totalLength..];
        }

        var padded = new byte[totalLength];
        Buffer.BlockCopy(source, 0, padded, totalLength - source.Length, source.Length);
        return padded;
    }
}
