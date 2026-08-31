using System.Buffers.Binary;

namespace Sesame.Services.Mii;

public static class MiiText
{
    public static string ReadFixed(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        if ((bytes.Length & 1) != 0)
            throw new FormatException("Mii text has an odd byte length.");

        var chars = new char[bytes.Length / 2];
        var length = 0;
        var padding = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var unit = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i * 2, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
            if (unit == 0)
            {
                padding = true;
                continue;
            }
            if (padding)
                throw new FormatException("Mii text contains non-zero data after its null terminator.");

            var value = (char)unit;
            if (char.IsSurrogate(value) || char.IsControl(value))
                throw new FormatException("Mii text contains a surrogate or control character.");
            chars[length++] = value;
        }

        if (length == 0)
            throw new FormatException("Mii text is empty.");
        return new string(chars, 0, length);
    }

    public static void WriteFixed(Span<byte> destination, string text, bool bigEndian)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((destination.Length & 1) != 0)
            throw new ArgumentException("Mii text destination has an odd byte length.", nameof(destination));
        if (text.Length is 0 || text.Length > destination.Length / 2)
            throw new ArgumentException($"Mii text must contain 1 to {destination.Length / 2} characters.", nameof(text));
        if (text.Any(c => c == '\0' || char.IsSurrogate(c) || char.IsControl(c)))
            throw new ArgumentException("Mii text cannot contain nulls, surrogates, or control characters.", nameof(text));

        destination.Clear();
        for (var i = 0; i < text.Length; i++)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(i * 2, 2), text[i]);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * 2, 2), text[i]);
        }
    }
}
