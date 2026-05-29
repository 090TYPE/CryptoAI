using System.Numerics;
using System.Text.Json;

namespace CryptoAITerminal.Gateway.DEX;

public static class SolanaKeyMaterial
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string NormalizeSecret(string rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new ArgumentException("Solana secret material is empty.", nameof(rawSecret));
        }

        var trimmed = rawSecret.Trim();
        if (trimmed.StartsWith('['))
        {
            var bytes = JsonSerializer.Deserialize<List<int>>(trimmed)
                ?? throw new InvalidOperationException("Solana secret array could not be parsed.");

            if (bytes.Count is not 32 and not 64)
            {
                throw new InvalidOperationException("Solana secret array must contain 32 or 64 bytes.");
            }

            if (bytes.Any(static item => item is < 0 or > 255))
            {
                throw new InvalidOperationException("Solana secret array contains values outside byte range.");
            }

            return string.Join(",", bytes);
        }

        var decoded = DecodeBase58(trimmed);
        if (decoded.Length is not 32 and not 64)
        {
            throw new InvalidOperationException("Solana base58 secret must decode to 32 or 64 bytes.");
        }

        return trimmed;
    }

    public static byte[] ParseNormalizedSecret(string normalizedSecret)
    {
        if (string.IsNullOrWhiteSpace(normalizedSecret))
        {
            throw new ArgumentException("Solana secret material is empty.", nameof(normalizedSecret));
        }

        if (normalizedSecret.Contains(',', StringComparison.Ordinal))
        {
            var bytes = normalizedSecret
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(byte.Parse)
                .ToArray();

            if (bytes.Length is not 32 and not 64)
            {
                throw new InvalidOperationException("Solana secret array must contain 32 or 64 bytes.");
            }

            return bytes;
        }

        var decoded = DecodeBase58(normalizedSecret);
        if (decoded.Length is not 32 and not 64)
        {
            throw new InvalidOperationException("Solana base58 secret must decode to 32 or 64 bytes.");
        }

        return decoded;
    }

    public static string EncodeBase58(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return string.Empty;
        }

        BigInteger intData = new(bytes, isUnsigned: true, isBigEndian: true);
        var result = string.Empty;
        while (intData > 0)
        {
            var remainder = (int)(intData % 58);
            intData /= 58;
            result = Base58Alphabet[remainder] + result;
        }

        foreach (var value in bytes)
        {
            if (value == 0)
            {
                result = '1' + result;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static byte[] DecodeBase58(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        if (input.Any(ch => !Base58Alphabet.Contains(ch)))
        {
            throw new InvalidOperationException("Solana base58 secret contains unsupported characters.");
        }

        BigInteger intData = 0;
        foreach (var c in input)
        {
            int digit = Base58Alphabet.IndexOf(c);
            intData = intData * 58 + digit;
        }

        var bytes = intData.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeroCount = input.TakeWhile(static c => c == '1').Count();

        if (leadingZeroCount == 0)
        {
            return bytes;
        }

        var result = new byte[leadingZeroCount + bytes.Length];
        Array.Copy(bytes, 0, result, leadingZeroCount, bytes.Length);
        return result;
    }
}
