using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace CryptoAITerminal.Gateway.DEX;

public sealed class EvmWalletClient
{
    private readonly Web3 _web3;

    public EvmWalletClient(string rpcUrl)
    {
        _web3 = new Web3(rpcUrl);
    }

    public async Task<decimal> GetNativeBalanceAsync(string address)
    {
        var balance = await _web3.Eth.GetBalance.SendRequestAsync(address);
        return Web3.Convert.FromWei(balance);
    }

    public static string DeriveAddress(string privateKey)
    {
        return new Account(privateKey).Address;
    }

    public static bool IsValidAddress(string? address)
    {
        return !string.IsNullOrWhiteSpace(address) &&
               AddressUtil.Current.IsValidEthereumAddressHexFormat(address);
    }
}
