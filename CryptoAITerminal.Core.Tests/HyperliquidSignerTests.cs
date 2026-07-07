using CryptoAITerminal.Gateway.DEX;

namespace CryptoAITerminal.Core.Tests;

public class HyperliquidSignerTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void EncodeOrderAction_matches_msgpack_byte_for_byte()
    {
        // a=0, b=true, p="100", s="1", r=false, tif="Gtc"
        var bytes = HyperliquidSigner.EncodeOrderAction(0, true, "100", "1", false, "Gtc");

        // Hand-computed per the msgpack spec (fixmap/fixarray/fixstr, positive fixint, true/false):
        const string expected =
            "83" +                                   // fixmap(3)
            "a474797065" + "a56f72646572" +          // "type":"order"
            "a66f7264657273" + "91" +                // "orders":fixarray(1)
            "86" +                                   //   fixmap(6)
            "a16100" +                               //   "a":0
            "a162c3" +                               //   "b":true
            "a170a3313030" +                         //   "p":"100"
            "a173a131" +                             //   "s":"1"
            "a172c2" +                               //   "r":false
            "a17481a56c696d697481a3746966a3477463" + //   "t":{"limit":{"tif":"Gtc"}}
            "a867726f7570696e67" + "a26e61";         // "grouping":"na"

        Assert.Equal(expected, Hex(bytes));
    }

    [Fact]
    public void EncodeOrderAction_encodes_large_asset_index_as_uint16()
    {
        // asset 300 → uint16 0xcd 0x01 0x2c
        var bytes = HyperliquidSigner.EncodeOrderAction(300, false, "1", "1", true, "Ioc");
        Assert.Contains("a161cd012c", Hex(bytes)); // "a":<uint16 300>
        Assert.Contains("a162c2", Hex(bytes));     // "b":false
        Assert.Contains("a172c3", Hex(bytes));     // "r":true
    }

    [Fact]
    public void BuildActionHash_is_deterministic_and_32_bytes()
    {
        var mp = HyperliquidSigner.EncodeOrderAction(0, true, "100", "1", false, "Gtc");
        var h1 = HyperliquidSigner.BuildActionHash(mp, nonce: 1_700_000_000_000);
        var h2 = HyperliquidSigner.BuildActionHash(mp, nonce: 1_700_000_000_000);

        Assert.Equal(32, h1.Length);
        Assert.Equal(Hex(h1), Hex(h2));
    }

    [Fact]
    public void BuildActionHash_changes_with_nonce()
    {
        var mp = HyperliquidSigner.EncodeOrderAction(0, true, "100", "1", false, "Gtc");
        var a = HyperliquidSigner.BuildActionHash(mp, nonce: 1);
        var b = HyperliquidSigner.BuildActionHash(mp, nonce: 2);
        Assert.NotEqual(Hex(a), Hex(b));
    }

    [Fact]
    public void BuildActionHash_vault_byte_differs_from_no_vault()
    {
        var mp = HyperliquidSigner.EncodeOrderAction(0, true, "100", "1", false, "Gtc");
        var vault = new byte[20];
        for (var i = 0; i < 20; i++) vault[i] = (byte)(i + 1);

        var noVault = HyperliquidSigner.BuildActionHash(mp, 5);
        var withVault = HyperliquidSigner.BuildActionHash(mp, 5, vault);
        Assert.NotEqual(Hex(noVault), Hex(withVault));
    }

    [Fact]
    public void SplitSignature_extracts_r_s_v()
    {
        var r = new string('1', 64);
        var s = new string('2', 64);
        var sig = HyperliquidExchangeClient.SplitSignature("0x" + r + s + "1b"); // 0x1b = 27

        Assert.Equal("0x" + r, sig.R);
        Assert.Equal("0x" + s, sig.S);
        Assert.Equal(27, sig.V);
    }

    [Fact]
    public void SplitSignature_normalises_v_from_0_1()
    {
        var body = new string('a', 128);
        var sig = HyperliquidExchangeClient.SplitSignature("0x" + body + "00");
        Assert.Equal(27, sig.V);
    }

    [Fact]
    public void BuildRequestBody_composes_action_nonce_signature_vault()
    {
        var action = HyperliquidPerpClient.BuildOrderActionJsonExact(0, true, "100", "1", false, "Gtc");
        var sig = new HyperliquidSignature("0xr", "0xs", 28);
        var body = HyperliquidExchangeClient.BuildRequestBody(action, 1700000000000, sig);

        Assert.Contains("\"action\":{\"type\":\"order\"", body);
        Assert.Contains("\"nonce\":1700000000000", body);
        Assert.Contains("\"signature\":{\"r\":\"0xr\",\"s\":\"0xs\",\"v\":28}", body);
        Assert.Contains("\"vaultAddress\":null", body);
    }

    [Fact]
    public void SignConnectionId_produces_valid_split_signature()
    {
        // Deterministic dummy key (never used on-chain) — proves the EIP-712 pipeline wires up.
        const string key = "0x4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
        var mp = HyperliquidSigner.EncodeOrderAction(0, true, "100", "1", false, "Gtc");
        var cid = HyperliquidSigner.BuildActionHash(mp, 1700000000000);

        var client = new HyperliquidExchangeClient(testnet: true);
        var sig = client.SignConnectionId(key, cid);

        Assert.StartsWith("0x", sig.R);
        Assert.StartsWith("0x", sig.S);
        Assert.Equal(66, sig.R.Length); // 0x + 64 hex
        Assert.True(sig.V is 27 or 28);
    }
}
