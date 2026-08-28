using System.Buffers.Binary;
using System.IO;

namespace Sesame.Services.N64;

internal static class RomSpace
{
    private static readonly int[] CartBytes =
        new[] { 8, 12, 16, 24, 32, 40, 48, 64 }.Select(mb => mb * 1024 * 1024).ToArray();

    public static int Align(int value, int align) =>
        (value + align - 1) & ~(align - 1);

    public static byte[] Expand(byte[] rom, int minLength, byte fill = 0x01)
    {
        var size = NextCart(minLength);
        if (size <= rom.Length) return (byte[])rom.Clone();
        var next = new byte[size];
        Buffer.BlockCopy(rom, 0, next, 0, rom.Length);
        Array.Fill(next, fill, rom.Length, size - rom.Length);
        return next;
    }

    public static byte[] Place(byte[] rom, byte[] blob, int align, out int at)
    {
        if (blob.Length == 0) throw new InvalidDataException("No data to write into the ROM.");
        var output = (byte[])rom.Clone();
        var start = Align(TrailingFillStart(output), align);
        if (output.Length - start < blob.Length + align)
        {
            var origin = output.Length;
            output = Expand(output, origin + blob.Length + align + 16);
            start = Align(origin, align);
        }

        at = start;
        Buffer.BlockCopy(blob, 0, output, at, blob.Length);
        return output;
    }

    public static bool TryTrailingHole(byte[] rom, int size, int align, int after, int limit, out int at)
    {
        at = 0;
        if (size <= 0) return false;
        var cap = Math.Min(rom.Length, limit);
        var start = Math.Max(Align(Math.Max(TrailingFillStart(rom), after), align), after);
        if (start + size > cap) return false;
        at = start;
        return true;
    }

    public static int TrailingFillStart(byte[] rom)
    {
        if (rom.Length < 0x2000) return rom.Length;
        var fill = rom[^1];
        if (fill is not (0x00 or 0x01 or 0xFF)) return rom.Length;
        var minKeep = Math.Max(rom.Length / 2, rom.Length - 8 * 1024 * 1024);
        var i = rom.Length - 1;
        while (i > minKeep && rom[i] == fill) i--;
        var start = i + 1;
        return rom.Length - start < 0x1000 ? rom.Length : start;
    }

    public static uint Be32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    public static void WriteBe32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    public static ushort Be16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));

    public static void WriteBe16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);

    private static int NextCart(int minLength)
    {
        foreach (var size in CartBytes)
            if (size >= minLength) return size;
        throw new InvalidDataException("De ROM zou groter dan 64 MB worden. Dat ondersteunt de N64 niet.");
    }
}
