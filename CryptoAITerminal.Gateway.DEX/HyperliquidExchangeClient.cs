using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Nethereum.ABI.EIP712;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>Split ECDSA signature in the shape Hyperliquid's /exchange endpoint expects.</summary>
public sealed record HyperliquidSignature(string R, string S, int V);

/// <summary>
/// Signs and (optionally) submits Hyperliquid exchange actions.
///
/// The EIP-712 domain is Hyperliquid's fixed <c>Exchange</c> agent domain (chainId 1337,
/// zero verifying contract); the signed message is <c>Agent{ source, connectionId }</c> where
/// source is "a" on mainnet / "b" on testnet and connectionId is the action hash from
/// <see cref="HyperliquidSigner"/>. Signing is deterministic and unit-testable; only
/// <see cref="SubmitOrderAsync"/> touches the network.
/// </summary>
public sealed class HyperliquidExchangeClient
{
    public const string MainnetUrl = "https://api.hyperliquid.xyz/exchange";
    public const string TestnetUrl = "https://api.hyperliquid-testnet.xyz/exchange";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _testnet;

    public HyperliquidExchangeClient(bool testnet = true) => _testnet = testnet;

    public string SourceTag => _testnet ? "b" : "a";
    public string EndpointUrl => _testnet ? TestnetUrl : MainnetUrl;

    /// <summary>Builds the Hyperliquid Agent EIP-712 typed-data for a given connection id.</summary>
    public static TypedData<Domain> BuildTypedData(byte[] connectionId, bool testnet)
    {
        return new TypedData<Domain>
        {
            Domain = new Domain
            {
                Name = "Exchange",
                Version = "1",
                ChainId = new BigInteger(1337),
                VerifyingContract = "0x0000000000000000000000000000000000000000",
            },
            Types = new Dictionary<string, MemberDescription[]>
            {
                ["EIP712Domain"] = new[]
                {
                    new MemberDescription { Name = "name", Type = "string" },
                    new MemberDescription { Name = "version", Type = "string" },
                    new MemberDescription { Name = "chainId", Type = "uint256" },
                    new MemberDescription { Name = "verifyingContract", Type = "address" },
                },
                ["Agent"] = new[]
                {
                    new MemberDescription { Name = "source", Type = "string" },
                    new MemberDescription { Name = "connectionId", Type = "bytes32" },
                },
            },
            PrimaryType = "Agent",
            Message = new[]
            {
                new MemberValue { TypeName = "string", Value = testnet ? "b" : "a" },
                new MemberValue { TypeName = "bytes32", Value = connectionId },
            },
        };
    }

    /// <summary>Signs the action hash and returns the r/s/v split Hyperliquid expects.</summary>
    public HyperliquidSignature SignConnectionId(string privateKey, byte[] connectionId)
    {
        var typed = BuildTypedData(connectionId, _testnet);
        var signature = new Eip712TypedDataSigner().SignTypedDataV4(typed, new EthECKey(privateKey));
        return SplitSignature(signature);
    }

    /// <summary>Splits a 65-byte hex signature into r, s and v (27/28).</summary>
    public static HyperliquidSignature SplitSignature(string signatureHex)
    {
        var hex = signatureHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? signatureHex[2..] : signatureHex;
        if (hex.Length != 130)
        {
            throw new ArgumentException($"Expected a 65-byte signature, got {hex.Length / 2} bytes.", nameof(signatureHex));
        }

        var r = "0x" + hex[..64];
        var s = "0x" + hex[64..128];
        var v = int.Parse(hex[128..130], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (v < 27) v += 27;
        return new HyperliquidSignature(r, s, v);
    }

    /// <summary>Composes the /exchange request body (action + nonce + signature + vault).</summary>
    public static string BuildRequestBody(string actionJson, long nonce, HyperliquidSignature sig)
    {
        return "{\"action\":" + actionJson +
               ",\"nonce\":" + nonce.ToString(CultureInfo.InvariantCulture) +
               ",\"signature\":{\"r\":\"" + sig.R + "\",\"s\":\"" + sig.S + "\",\"v\":" + sig.V + "}" +
               ",\"vaultAddress\":null}";
    }

    /// <summary>Signs and POSTs a single order to /exchange. Returns the raw response body.</summary>
    public async Task<string> SubmitOrderAsync(
        string privateKey, int assetIndex, bool isBuy, string limitPx, string size,
        bool reduceOnly, string tif, long nonce, CancellationToken ct = default)
    {
        var msgpack = HyperliquidSigner.EncodeOrderAction(assetIndex, isBuy, limitPx, size, reduceOnly, tif);
        var connectionId = HyperliquidSigner.BuildActionHash(msgpack, nonce);
        var sig = SignConnectionId(privateKey, connectionId);
        var actionJson = HyperliquidPerpClient.BuildOrderActionJsonExact(assetIndex, isBuy, limitPx, size, reduceOnly, tif);
        var body = BuildRequestBody(actionJson, nonce, sig);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(EndpointUrl, content, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
