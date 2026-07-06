using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>GMX v2 order types (matches the on-chain Order.OrderType enum).</summary>
public enum GmxOrderType
{
    MarketSwap = 0,
    LimitSwap = 1,
    MarketIncrease = 2,
    LimitIncrease = 3,
    MarketDecrease = 4,
    LimitDecrease = 5,
    StopLossDecrease = 6,
    Liquidation = 7,
}

/// <summary>
/// GMX v2 contract addresses on Arbitrum One.
///
/// ⚠️ These addresses AND the createOrder struct layout below change when GMX ships contract
/// upgrades. Verify both against the current GMX v2 deployment (https://github.com/gmx-io/gmx-synthetics
/// deployment files) before enabling real orders. They are grouped here so there is a single place
/// to update.
/// </summary>
public static class GmxArbitrum
{
    public const string ExchangeRouter = "0x900173A66dbD345006C51fA35fA3aB760FcD843b";
    public const string Router = "0x7452c558d45f8afC8c83dAe62C3f8A5BE19c71f6"; // token approvals target
    public const string OrderVault = "0x31eF83a530Fde1B38EE9A18093A333D8Bbbc40D5";
    public const string DataStore = "0xFD70de6b91282D8017aA4E741e9Ae325CAb992d8";
    public const string Reader = "0xf60becbba223EEA9495Da3f606753867eC10d139";
    public const string Usdc = "0xaf88d065e77c8cC2239327C5EDb3A432268e5831"; // native USDC (collateral)
    public const string Weth = "0x82aF49447D8a07e3bd95BD0d56f35241523fBab1"; // WNT for execution fee
}

// ── ExchangeRouter function messages ─────────────────────────────────────────

[Function("sendWnt")]
public class GmxSendWntFunction : FunctionMessage
{
    [Parameter("address", "receiver", 1)] public string Receiver { get; set; } = string.Empty;
    [Parameter("uint256", "amount", 2)] public BigInteger Amount { get; set; }
}

[Function("sendTokens")]
public class GmxSendTokensFunction : FunctionMessage
{
    [Parameter("address", "token", 1)] public string Token { get; set; } = string.Empty;
    [Parameter("address", "receiver", 2)] public string Receiver { get; set; } = string.Empty;
    [Parameter("uint256", "amount", 3)] public BigInteger Amount { get; set; }
}

public class GmxCreateOrderAddresses
{
    [Parameter("address", "receiver", 1)] public string Receiver { get; set; } = string.Empty;
    [Parameter("address", "cancellationReceiver", 2)] public string CancellationReceiver { get; set; } = string.Empty;
    [Parameter("address", "callbackContract", 3)] public string CallbackContract { get; set; } = string.Empty;
    [Parameter("address", "uiFeeReceiver", 4)] public string UiFeeReceiver { get; set; } = string.Empty;
    [Parameter("address", "market", 5)] public string Market { get; set; } = string.Empty;
    [Parameter("address", "initialCollateralToken", 6)] public string InitialCollateralToken { get; set; } = string.Empty;
    [Parameter("address[]", "swapPath", 7)] public List<string> SwapPath { get; set; } = new();
}

public class GmxCreateOrderNumbers
{
    [Parameter("uint256", "sizeDeltaUsd", 1)] public BigInteger SizeDeltaUsd { get; set; }
    [Parameter("uint256", "initialCollateralDeltaAmount", 2)] public BigInteger InitialCollateralDeltaAmount { get; set; }
    [Parameter("uint256", "triggerPrice", 3)] public BigInteger TriggerPrice { get; set; }
    [Parameter("uint256", "acceptablePrice", 4)] public BigInteger AcceptablePrice { get; set; }
    [Parameter("uint256", "executionFee", 5)] public BigInteger ExecutionFee { get; set; }
    [Parameter("uint256", "callbackGasLimit", 6)] public BigInteger CallbackGasLimit { get; set; }
    [Parameter("uint256", "minOutputAmount", 7)] public BigInteger MinOutputAmount { get; set; }
    [Parameter("uint256", "validFromTime", 8)] public BigInteger ValidFromTime { get; set; }
}

public class GmxCreateOrderParams
{
    [Parameter("tuple", "addresses", 1)] public GmxCreateOrderAddresses Addresses { get; set; } = new();
    [Parameter("tuple", "numbers", 2)] public GmxCreateOrderNumbers Numbers { get; set; } = new();
    [Parameter("uint8", "orderType", 3)] public byte OrderType { get; set; }
    [Parameter("uint8", "decreasePositionSwapType", 4)] public byte DecreasePositionSwapType { get; set; }
    [Parameter("bool", "isLong", 5)] public bool IsLong { get; set; }
    [Parameter("bool", "shouldUnwrapNativeToken", 6)] public bool ShouldUnwrapNativeToken { get; set; }
    [Parameter("bool", "autoCancel", 7)] public bool AutoCancel { get; set; }
    [Parameter("bytes32", "referralCode", 8)] public byte[] ReferralCode { get; set; } = new byte[32];
}

[Function("createOrder", "bytes32")]
public class GmxCreateOrderFunction : FunctionMessage
{
    [Parameter("tuple", "params", 1)] public GmxCreateOrderParams Params { get; set; } = new();
}

[Function("multicall", "bytes[]")]
public class GmxMulticallFunction : FunctionMessage
{
    [Parameter("bytes[]", "data", 1)] public List<byte[]> Data { get; set; } = new();
}
