using System;
using System.Collections.Generic;
using System.Linq;

namespace RealmForge.Bridge.Equipment;

/// <summary>
/// Builds the SDP payload for the equip-replace message (msgId 0xA6CD).
/// The payload is a 12-byte (for normal item ids) structure read from the
/// captured pcap.  Field types are inferred from the binary pattern.
/// </summary>
public static class SdpEquipPayloadBuilder
{
    /// <summary>
    /// Build the raw SDP payload that <c>LuaNetworkManager.SendMessage</c>
    /// expects for the equip command.
    /// </summary>
    public static byte[] Build(long itemId, long heroId)
    {
        var itemVarint = EncodeVarint((ulong)itemId);
        var heroVarint = EncodeVarint((ulong)heroId);

        var payload = new List<byte>(16);
        payload.Add(0x70);          // open struct
        payload.Add(0x50);          // sub-struct / list marker
        payload.Add(0x01);
        payload.Add(0x00);
        payload.AddRange(itemVarint);
        payload.Add(0x80);          // close item sub-struct
        payload.AddRange(heroVarint);
        payload.Add(0x80);          // close main struct
        return payload.ToArray();
    }

    /// <summary>
    /// Base-128 varint (little-endian, continuation bit) as used in the
    /// game's SDP traffic.
    /// </summary>
    public static byte[] EncodeVarint(ulong value)
    {
        var bytes = new List<byte>(8);
        while (value > 0x7F)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    /// <summary>Decode a base-128 varint for validation / tests.</summary>
    public static ulong DecodeVarint(ReadOnlySpan<byte> data, out int bytesRead)
    {
        ulong value = 0;
        int shift = 0;
        bytesRead = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            value |= (ulong)(b & 0x7F) << shift;
            bytesRead++;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new OverflowException("Varint too large");
        }
        throw new InvalidOperationException("Incomplete varint");
    }
}
