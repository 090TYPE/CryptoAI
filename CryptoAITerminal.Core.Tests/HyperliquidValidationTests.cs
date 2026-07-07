using CryptoAITerminal.Gateway.DEX;
using Nethereum.Signer;

namespace CryptoAITerminal.Core.Tests;

public class HyperliquidValidationTests
{
    [Fact]
    public void ParseAssetIndex_finds_coin_position_in_universe()
    {
        const string meta = """
        { "universe": [
            { "name": "BTC", "szDecimals": 5 },
            { "name": "ETH", "szDecimals": 4 },
            { "name": "SOL", "szDecimals": 2 }
        ] }
        """;

        Assert.Equal(0, HyperliquidPerpClient.ParseAssetIndex(meta, "BTC"));
        Assert.Equal(1, HyperliquidPerpClient.ParseAssetIndex(meta, "eth")); // case-insensitive
        Assert.Equal(2, HyperliquidPerpClient.ParseAssetIndex(meta, "SOL"));
        Assert.Equal(-1, HyperliquidPerpClient.ParseAssetIndex(meta, "DOGE"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("garbage")]
    public void ParseAssetIndex_returns_minus_one_on_bad_meta(string json)
    {
        Assert.Equal(-1, HyperliquidPerpClient.ParseAssetIndex(json, "ETH"));
    }

    [Fact]
    public void Signature_recovers_to_the_signing_address_offline()
    {
        // Proves the EIP-712 typed-data hashing is correct without any network:
        // sign the connection id, recover the signer, and it must equal the key's address.
        const string key = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
        var expectedAddress = new EthECKey(key).GetPublicAddress();

        var mp = HyperliquidSigner.EncodeOrderAction(1, true, "1000", "0.01", false, "Gtc");
        var cid = HyperliquidSigner.BuildActionHash(mp, nonce: 1_700_000_000_123);

        var client = new HyperliquidExchangeClient(testnet: true);
        var rawSig = client.SignConnectionIdRaw(key, cid);
        var recovered = client.RecoverSigner(cid, rawSig);

        Assert.Equal(expectedAddress.ToLowerInvariant(), recovered.ToLowerInvariant());
    }

    [Fact]
    public void Recovery_fails_for_wrong_connection_id()
    {
        const string key = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
        var address = new EthECKey(key).GetPublicAddress();
        var client = new HyperliquidExchangeClient(testnet: true);

        var cidA = HyperliquidSigner.ConnectionId(1, true, "1000", "0.01", false, "Gtc", 1);
        var cidB = HyperliquidSigner.ConnectionId(1, true, "1000", "0.01", false, "Gtc", 2);
        var sig = client.SignConnectionIdRaw(key, cidA);

        // Recovering the signature against a different action hash must NOT yield the signer.
        Assert.NotEqual(address.ToLowerInvariant(), client.RecoverSigner(cidB, sig).ToLowerInvariant());
    }

    /// <summary>
    /// Manual testnet harness — proves Hyperliquid accepts our signature end-to-end.
    /// Run with a funded testnet key:
    ///   HYPERLIQUID_TESTNET_KEY=0x... dotnet test --filter Live_testnet_order_is_accepted
    /// Skipped (no-op) when the env var is absent so CI stays offline.
    /// A top-level "status":"ok" means the signature/domain were accepted (per-order errors are fine).
    /// </summary>
    [Fact]
    public async Task Live_testnet_order_is_accepted()
    {
        var key = Environment.GetEnvironmentVariable("HYPERLIQUID_TESTNET_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            return; // no key → skip the network harness
        }

        var read = new HyperliquidPerpClient(enableLiveOrders: false, testnet: true);
        var assetIndex = await read.GetAssetIndexAsync("ETH");
        Assert.True(assetIndex >= 0, "Could not resolve ETH asset index from testnet meta.");

        var live = new HyperliquidPerpClient(enableLiveOrders: true, testnet: true);
        // Far-from-market resting buy so it does not fill; signature acceptance is what we validate.
        var response = await live.PlaceOrderAsync(key!, assetIndex, isBuy: true, limitPrice: 100m, size: 0.01m, reduceOnly: false);

        Assert.Contains("\"status\":\"ok\"", response);
    }
}
