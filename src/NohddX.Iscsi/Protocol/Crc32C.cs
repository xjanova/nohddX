namespace NohddX.Iscsi.Protocol;

/// <summary>
/// CRC-32C (Castagnoli) — the polynomial used by iSCSI header/data digests
/// per RFC 3720 §10.2.4 and RFC 3385. NOT the same as the standard CRC-32
/// shipped in <c>System.IO.Hashing.Crc32</c>; that one uses the IEEE 802.3
/// polynomial 0xEDB88320 (reflected). Castagnoli uses 0x82F63B78 (reflected)
/// which is significantly stronger for the message lengths iSCSI sees.
///
/// Implementation is the standard byte-at-a-time table; fast enough for the
/// per-PDU rates we care about. Hot paths in storage stacks would normally
/// use the hardware SSE 4.2 CRC32 instruction (which IS Castagnoli), but
/// that's not exposed by BCL today; reach for an unsafe intrinsic if this
/// shows up in a profile.
/// </summary>
public static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint Polynomial = 0x82F63B78u; // CRC-32C reflected
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Compute the CRC-32C of <paramref name="data"/>. Initial value 0xFFFFFFFF,
    /// final XOR with 0xFFFFFFFF — matches RFC 3385 §3 reference algorithm.
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFFu];
        return ~crc;
    }
}
