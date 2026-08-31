using System.Buffers.Binary;

namespace Sesame.Services.Mii;

public static class MiiCrc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static ushort ReadBigEndian(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16BigEndian(data);

    public static void WriteBigEndian(Span<byte> destination, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(destination, value);
}
