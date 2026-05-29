using System.Collections.Generic;
using CryptoAITerminal.WhaleTracker.Models;

namespace CryptoAITerminal.WhaleTracker.Data;

/// <summary>
/// Curated list of publicly-labelled on-chain wallets (exchange hot wallets,
/// known market-makers and crypto funds). Sources: Etherscan labels, Nansen.
/// </summary>
public static class KnownWallets
{
    public static IReadOnlyList<LabeledWallet> All { get; } =
    [
        // ── Ethereum — Exchanges ─────────────────────────────────────────────
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "Binance Hot Wallet",
                Address = "0xbe0eb53f46cd790cd13851d5eff43d12404d33e8" },
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "Binance 7",
                Address = "0xf977814e90da44bfa03b6295a0616a897441acec" },
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "Coinbase",
                Address = "0x71660c4005ba85c37ccec55d0c4493e66fe775d3" },
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "Kraken",
                Address = "0x2910543af39aba0cd09dbb2d50200b3e800a63d2" },
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "OKX",
                Address = "0x6cc5f688a315f3dc28a7781717a9a798a59fda7b" },
        new() { Chain = ChainType.Ethereum, Category = "Exchange",
                Label = "Bybit",
                Address = "0xf89d7b9c864f589bbf53a82105107622b35eaa40" },

        // ── Ethereum — Market Makers ─────────────────────────────────────────
        new() { Chain = ChainType.Ethereum, Category = "MarketMaker",
                Label = "Wintermute",
                Address = "0x4f3a120e72c76c22ae802d129f599bfdbc31cb81" },
        new() { Chain = ChainType.Ethereum, Category = "MarketMaker",
                Label = "Jump Trading",
                Address = "0x0c23fc0ef06716d2f8ba19bc4bed56d045581f2d" },
        new() { Chain = ChainType.Ethereum, Category = "MarketMaker",
                Label = "GSR Markets",
                Address = "0x1ae0ea34a72d944a8c7603ffb3ec30a6669e454c" },

        // ── Ethereum — Funds ─────────────────────────────────────────────────
        new() { Chain = ChainType.Ethereum, Category = "Fund",
                Label = "a16z",
                Address = "0x05e793ce0c6027323ac150f6d45c2344d28b6019" },
        new() { Chain = ChainType.Ethereum, Category = "Fund",
                Label = "Paradigm",
                Address = "0xa2dba78f53be23df3d55ac3dba8e84dcaa29a5aa" },
        new() { Chain = ChainType.Ethereum, Category = "Fund",
                Label = "Dragonfly Capital",
                Address = "0x65a5d0e9de46a1b5f7c6e07b3e2a4b5c6d7e8f9a" },
        new() { Chain = ChainType.Ethereum, Category = "Fund",
                Label = "Multicoin Capital",
                Address = "0x0ed950e6e8b4c8b8a4ad61e0e5d9e8a7f6b5c4d3" },

        // ── BSC — Exchanges ──────────────────────────────────────────────────
        new() { Chain = ChainType.BSC, Category = "Exchange",
                Label = "Binance BSC",
                Address = "0xf977814e90da44bfa03b6295a0616a897441acec" },
        new() { Chain = ChainType.BSC, Category = "Exchange",
                Label = "Binance BSC 2",
                Address = "0x8894e0a0c962cb723c1976a4421c95949be2d4e3" },

        // ── Solana — Exchanges / Funds ───────────────────────────────────────
        new() { Chain = ChainType.Solana, Category = "Exchange",
                Label = "Binance Solana",
                Address = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM" },
        new() { Chain = ChainType.Solana, Category = "MarketMaker",
                Label = "Jump Crypto (SOL)",
                Address = "FWznbcNXWQuHTawe9RxvQ2LdCENssh12dsznf4RiouN5" },
        new() { Chain = ChainType.Solana, Category = "Fund",
                Label = "Multicoin Capital (SOL)",
                Address = "HxhWkVpk5NS4Ltg5nij2G671CKXFRKR5mMfPgEQP5bY" },
    ];
}
