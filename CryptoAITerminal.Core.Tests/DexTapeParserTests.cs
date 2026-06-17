using CryptoAITerminal.TerminalUI.Services;

namespace CryptoAITerminal.Core.Tests;

public class DexTapeParserTests
{
    // Trimmed to the fields the parser reads, in GeckoTerminal's real shape.
    private const string SampleJson = """
    {"data":[
      {"type":"trade","attributes":{
        "tx_hash":"0xaaa","tx_from_address":"0x9eb9e9b2ba5c2665a16beaa8e137410358b6beaf",
        "from_token_amount":"52410.11","to_token_amount":"28.98",
        "price_from_in_usd":"0.9973","price_to_in_usd":"1803.26",
        "block_timestamp":"2026-06-16T13:19:35Z","kind":"buy","volume_in_usd":"52269.15"}},
      {"type":"trade","attributes":{
        "tx_hash":"0xbbb","tx_from_address":"0xc699b52d4f04e34388e8391faa19cf19db76d0d4",
        "from_token_amount":"0.113","to_token_amount":"204.07",
        "price_from_in_usd":"1803.26","price_to_in_usd":"0.9973",
        "block_timestamp":"2026-06-16T13:19:27Z","kind":"sell","volume_in_usd":"203.71"}}
    ]}
    """;

    [Fact]
    public void Parse_ReadsSwapsAndTags_AsDex()
    {
        var trades = DexTapeParser.Parse(SampleJson);

        Assert.Equal(2, trades.Count);
        Assert.All(trades, t => Assert.Equal("DEX", t.Venue));
    }

    [Fact]
    public void Parse_MapsKindToSide()
    {
        var trades = DexTapeParser.Parse(SampleJson);

        Assert.Equal("BUY", trades[0].Side);
        Assert.Equal("SELL", trades[1].Side);
    }

    [Fact]
    public void Parse_CapturesWalletAndTxHash()
    {
        var trades = DexTapeParser.Parse(SampleJson);

        Assert.Equal("0x9eb9e9b2ba5c2665a16beaa8e137410358b6beaf", trades[0].Trader);
        Assert.Equal("0xaaa", trades[0].TxHash);
        Assert.Equal("0xaaa", trades[0].DedupKey); // DEX dedups by tx hash
    }

    [Fact]
    public void Parse_UsesVolumeUsdAsQuoteAndVolatilePriceAsAssetPrice()
    {
        var trades = DexTapeParser.Parse(SampleJson);

        Assert.Equal(52269.15m, trades[0].QuoteQty);
        Assert.Equal(1803.26m, trades[0].Price);            // max(0.9973, 1803.26)
        Assert.Equal(52269.15m / 1803.26m, trades[0].Quantity); // volume / asset price
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[]")]                       // array, not the {data:[…]} envelope
    [InlineData("{\"data\":\"nope\"}")]
    public void Parse_BadInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(DexTapeParser.Parse(json));
    }
}
