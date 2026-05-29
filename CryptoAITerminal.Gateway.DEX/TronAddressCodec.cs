using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethereum.Signer;

namespace CryptoAITerminal.Gateway.DEX;

public static class TronAddressCodec
{
    private const byte TronMainnetPrefix = 0x41;
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            _ = DecodeAddressToPayload(address);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string DeriveAddress(string privateKey)
    {
        var key = new EthECKey(NormalizePrivateKey(privateKey));
        var evmAddress = key.GetPublicAddress();
        return FromEvmHexAddress(evmAddress);
    }

    public static string ToHexAddress(string base58Address)
    {
        var payload = DecodeAddressToPayload(base58Address);
        return Convert.ToHexString(payload).ToLowerInvariant();
    }

    public static string ToEvmHexAddress(string base58Address)
    {
        var payload = DecodeAddressToPayload(base58Address);
        return "0x" + Convert.ToHexString(payload[1..]).ToLowerInvariant();
    }

    public static string FromHexAddress(string hexAddress)
    {
        var normalized = NormalizeHexAddress(hexAddress);
        if (normalized.Length != 42 || !normalized.StartsWith("41", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A Tron hex address must be 21 bytes and start with 41.", nameof(hexAddress));
        }

        var payload = Convert.FromHexString(normalized);
        return EncodePayload(payload);
    }

    public static string FromEvmHexAddress(string evmHexAddress)
    {
        var normalized = NormalizeHexAddress(evmHexAddress);
        if (normalized.Length != 40)
        {
            throw new ArgumentException("An EVM hex address must be 20 bytes.", nameof(evmHexAddress));
        }

        var payload = new byte[21];
        payload[0] = TronMainnetPrefix;
        Convert.FromHexString(normalized).CopyTo(payload, 1);
        return EncodePayload(payload);
    }

    private static byte[] DecodeAddressToPayload(string address)
    {
        var decoded = DecodeBase58(address.Trim());
        if (decoded.Length != 25)
        {
            throw new ArgumentException("A Tron address must decode to 25 bytes.");
        }

        var payload = decoded[..21];
        var checksum = decoded[21..];
        var expectedChecksum = CalculateChecksum(payload);
        if (!checksum.SequenceEqual(expectedChecksum))
        {
            throw new ArgumentException("Tron address checksum is invalid.");
        }

        if (payload[0] != TronMainnetPrefix)
        {
            throw new ArgumentException("Unsupported Tron address prefix.");
        }

        return payload;
    }

    private static string EncodePayload(byte[] payload)
    {
        if (payload.Length != 21 || payload[0] != TronMainnetPrefix)
        {
            throw new ArgumentException("A Tron payload must be 21 bytes and start with 41.", nameof(payload));
        }

        var data = new byte[25];
        payload.CopyTo(data, 0);
        CalculateChecksum(payload).CopyTo(data, 21);
        return EncodeBase58(data);
    }

    private static byte[] CalculateChecksum(byte[] payload)
    {
        using var sha256 = SHA256.Create();
        var first = sha256.ComputeHash(payload);
        var second = sha256.ComputeHash(first);
        return second[..4];
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

        _ = Convert.FromHexString(normalized);
        return normalized;
    }

    private static string NormalizeHexAddress(string hexAddress)
    {
        var normalized = hexAddress.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string EncodeBase58(byte[] data)
    {
        var unsigned = new byte[data.Length + 1];
        for (var index = 0; index < data.Length; index++)
        {
            unsigned[index] = data[data.Length - 1 - index];
        }

        var intData = new BigInteger(unsigned);
        var builder = new StringBuilder();

        while (intData > 0)
        {
            intData = BigInteger.DivRem(intData, 58, out var remainder);
            builder.Insert(0, Base58Alphabet[(int)remainder]);
        }

        foreach (var b in data)
        {
            if (b == 0)
            {
                builder.Insert(0, Base58Alphabet[0]);
            }
            else
            {
                break;
            }
        }

        return builder.Length == 0 ? Base58Alphabet[0].ToString() : builder.ToString();
    }

    private static byte[] DecodeBase58(string encoded)
    {
        BigInteger intData = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            var digit = Base58Alphabet.IndexOf(encoded[index]);
            if (digit < 0)
            {
                throw new ArgumentException("Invalid Base58 character.");
            }

            intData = (intData * 58) + digit;
        }

        var leadingZeroCount = encoded.TakeWhile(character => character == Base58Alphabet[0]).Count();
        var bytesWithoutSign = intData.ToByteArray()
            .Reverse()
            .SkipWhile(static value => value == 0)
            .ToArray();

        var result = new byte[leadingZeroCount + bytesWithoutSign.Length];
        bytesWithoutSign.CopyTo(result, leadingZeroCount);
        return result;
    }
}
