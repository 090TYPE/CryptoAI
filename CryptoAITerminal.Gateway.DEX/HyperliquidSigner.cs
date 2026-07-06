using System.Globalization;
using System.Numerics;
using System.Text;
using Nethereum.Util;

namespace CryptoAITerminal.Gateway.DEX;

/// <summary>
/// Byte-exact msgpack + action-hash construction for Hyperliquid L1 action signing.
///
/// Hyperliquid signs each exchange action over
/// <c>keccak256( msgpack(action) || nonce_be8 || vaultByte )</c>, where the msgpack bytes must
/// match the reference SDK exactly (map key order, smallest-int encoding). This class produces
/// those bytes and the resulting connection-id hash deterministically, so the encoder can be
/// unit-tested against the msgpack spec without touching the network.
///
/// The order map key order matches the Hyperliquid SDK order_wire: a, b, p, s, r, t.
/// </summary>
public static class HyperliquidSigner
{
    /// <summary>Encodes the msgpack bytes for a single-order action, byte-for-byte per the SDK.</summary>
    public static byte[] EncodeOrderAction(
        int assetIndex, bool isBuy, string limitPx, string size, bool reduceOnly, string tif, string grouping = "na")
    {
        var w = new MsgPackWriter();
        // Outer map: { type, orders, grouping }
        w.WriteMapHeader(3);
        w.WriteString("type");
        w.WriteString("order");
        w.WriteString("orders");
        w.WriteArrayHeader(1);
        // Order map: { a, b, p, s, r, t }
        w.WriteMapHeader(6);
        w.WriteString("a"); w.WriteUInt((uint)assetIndex);
        w.WriteString("b"); w.WriteBool(isBuy);
        w.WriteString("p"); w.WriteString(limitPx);
        w.WriteString("s"); w.WriteString(size);
        w.WriteString("r"); w.WriteBool(reduceOnly);
        w.WriteString("t");
        w.WriteMapHeader(1);
        w.WriteString("limit");
        w.WriteMapHeader(1);
        w.WriteString("tif");
        w.WriteString(tif);
        // grouping
        w.WriteString("grouping");
        w.WriteString(grouping);
        return w.ToArray();
    }

    /// <summary>
    /// Builds the connection-id hash Hyperliquid signs:
    /// keccak256( msgpack || nonce(8, big-endian) || vaultByte ).
    /// vaultByte = 0x00 with no vault, else 0x01 followed by the 20-byte vault address.
    /// </summary>
    public static byte[] BuildActionHash(byte[] msgpack, long nonce, byte[]? vaultAddress = null)
    {
        var nonceBytes = new byte[8];
        var n = (ulong)nonce;
        for (var i = 7; i >= 0; i--)
        {
            nonceBytes[i] = (byte)(n & 0xff);
            n >>= 8;
        }

        var tail = vaultAddress is { Length: 20 }
            ? Concat(new byte[] { 0x01 }, vaultAddress)
            : new byte[] { 0x00 };

        var payload = Concat(Concat(msgpack, nonceBytes), tail);
        return new Sha3Keccack().CalculateHash(payload);
    }

    /// <summary>Convenience: msgpack + hash for a single order in one call.</summary>
    public static byte[] ConnectionId(
        int assetIndex, bool isBuy, string limitPx, string size, bool reduceOnly, string tif, long nonce)
        => BuildActionHash(EncodeOrderAction(assetIndex, isBuy, limitPx, size, reduceOnly, tif), nonce);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    /// <summary>Minimal msgpack writer covering exactly the types a Hyperliquid order action uses.</summary>
    private sealed class MsgPackWriter
    {
        private readonly List<byte> _buf = new(64);

        public void WriteMapHeader(int count)
        {
            if (count <= 15) _buf.Add((byte)(0x80 | count));
            else throw new NotSupportedException("map too large for order actions");
        }

        public void WriteArrayHeader(int count)
        {
            if (count <= 15) _buf.Add((byte)(0x90 | count));
            else throw new NotSupportedException("array too large for order actions");
        }

        public void WriteString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var len = bytes.Length;
            if (len <= 31) _buf.Add((byte)(0xa0 | len));
            else if (len <= 0xff) { _buf.Add(0xd9); _buf.Add((byte)len); }
            else { _buf.Add(0xda); _buf.Add((byte)(len >> 8)); _buf.Add((byte)(len & 0xff)); }
            _buf.AddRange(bytes);
        }

        public void WriteBool(bool b) => _buf.Add(b ? (byte)0xc3 : (byte)0xc2);

        public void WriteUInt(uint v)
        {
            if (v <= 0x7f) _buf.Add((byte)v);                                   // positive fixint
            else if (v <= 0xff) { _buf.Add(0xcc); _buf.Add((byte)v); }          // uint8
            else if (v <= 0xffff) { _buf.Add(0xcd); _buf.Add((byte)(v >> 8)); _buf.Add((byte)(v & 0xff)); } // uint16
            else { _buf.Add(0xce); _buf.Add((byte)(v >> 24)); _buf.Add((byte)(v >> 16)); _buf.Add((byte)(v >> 8)); _buf.Add((byte)v); } // uint32
        }

        public byte[] ToArray() => _buf.ToArray();
    }
}
