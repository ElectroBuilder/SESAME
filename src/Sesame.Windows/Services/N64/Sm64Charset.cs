using System.Globalization;
using System.Text;

namespace Sesame.Services.N64;

public static class Sm64Charset
{
    private static readonly Dictionary<byte, char> ToChar = new()
    {
        [0x00] = '0', [0x01] = '1', [0x02] = '2', [0x03] = '3', [0x04] = '4',
        [0x05] = '5', [0x06] = '6', [0x07] = '7', [0x08] = '8', [0x09] = '9',
        [0x0A] = 'A', [0x0B] = 'B', [0x0C] = 'C', [0x0D] = 'D', [0x0E] = 'E',
        [0x0F] = 'F', [0x10] = 'G', [0x11] = 'H', [0x12] = 'I', [0x13] = 'J',
        [0x14] = 'K', [0x15] = 'L', [0x16] = 'M', [0x17] = 'N', [0x18] = 'O',
        [0x19] = 'P', [0x1A] = 'Q', [0x1B] = 'R', [0x1C] = 'S', [0x1D] = 'T',
        [0x1E] = 'U', [0x1F] = 'V', [0x20] = 'W', [0x21] = 'X', [0x22] = 'Y',
        [0x23] = 'Z',
        [0x24] = 'a', [0x25] = 'b', [0x26] = 'c', [0x27] = 'd', [0x28] = 'e',
        [0x29] = 'f', [0x2A] = 'g', [0x2B] = 'h', [0x2C] = 'i', [0x2D] = 'j',
        [0x2E] = 'k', [0x2F] = 'l', [0x30] = 'm', [0x31] = 'n', [0x32] = 'o',
        [0x33] = 'p', [0x34] = 'q', [0x35] = 'r', [0x36] = 's', [0x37] = 't',
        [0x38] = 'u', [0x39] = 'v', [0x3A] = 'w', [0x3B] = 'x', [0x3C] = 'y',
        [0x3D] = 'z',
        [0x3E] = '\'', [0x3F] = '.',
        [0x6F] = ',',
        [0x9E] = ' ', [0x9F] = '-',
        [0xE1] = '(', [0xE2] = ')', [0xE3] = ')', [0xE4] = '+', [0xE5] = '&', [0xE6] = ':',
        [0xF2] = '!', [0xF3] = '%', [0xF4] = '?', [0xF7] = '~', [0xF8] = '.',
        [0xFB] = 'x', [0xFC] = '*', [0xFD] = '*',
        [0xFE] = '\n'
    };

    private static readonly Dictionary<char, byte> ToByte = ToChar
        .Where(kv => kv.Key != 0xF8)
        .GroupBy(kv => kv.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    public static bool IsTextByte(byte b) =>
        b is (>= 0x00 and <= 0x3F) or (>= 0x50 and <= 0x58)
            or 0x6F or 0x9E or 0x9F or 0xD0 or 0xD1 or 0xD2
            or (>= 0xE1 and <= 0xE6)
            or (>= 0xF2 and <= 0xFE);

    public static string? TryDecode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b == 0xFF) break;
            if (ToChar.TryGetValue(b, out var ch))
            {
                sb.Append(ch);
                continue;
            }
            switch (b)
            {
                case 0xD0: sb.Append('/'); break;
                case 0xD1: sb.Append("the"); break;
                case 0xD2: sb.Append("you"); break;
                case 0x50: sb.Append('^'); break;
                case 0x51: sb.Append('v'); break;
                case 0x52: sb.Append('<'); break;
                case 0x53: sb.Append('>'); break;
                case 0x54: sb.Append("[A]"); break;
                case 0x55: sb.Append("[B]"); break;
                case 0x56: sb.Append("[C]"); break;
                case 0x57: sb.Append("[Z]"); break;
                case 0x58: sb.Append("[R]"); break;
                case 0xF5: sb.Append('"'); break;
                case 0xF6: sb.Append('"'); break;
                case 0xF9: sb.Append('$'); break;
                case 0xFA: sb.Append('*'); break;
                default: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }

    public static byte[] Encode(string text)
    {
        var list = new List<byte>(text.Length);
        var src = text.Normalize(NormalizationForm.FormD);
        for (var i = 0; i < src.Length; i++)
        {
            if (char.GetUnicodeCategory(src[i]) == UnicodeCategory.NonSpacingMark)
                continue;
            if (TryTag(src, i, out var tagLen, out var tagByte))
            {
                list.Add(tagByte);
                i += tagLen - 1;
                continue;
            }
            var ch = src[i] switch
            {
                '\r' => '\n',
                '—' or '–' => '-',
                '’' or '‘' => '\'',
                '『' or '』' => '"',
                _ => src[i]
            };
            if (ch == '\n')
            {
                if (list.Count > 0 && list[^1] != 0xFE) list.Add(0xFE);
                continue;
            }
            if (ToByte.TryGetValue(ch, out var b))
                list.Add(b);
            else if (ToByte.TryGetValue(char.ToLowerInvariant(ch), out b))
                list.Add(b);
            else if (ToByte.TryGetValue(char.ToUpperInvariant(ch), out b))
                list.Add(b);
        }
        while (list.Count > 0 && list[^1] == 0xFE) list.RemoveAt(list.Count - 1);
        return list.ToArray();
    }

    private static bool TryTag(string text, int i, out int len, out byte value)
    {
        len = 0;
        value = 0;
        if (i >= text.Length || text[i] != '[') return false;
        if (Match(text, i, "[A]", out len)) { value = 0x54; return true; }
        if (Match(text, i, "[B]", out len)) { value = 0x55; return true; }
        if (Match(text, i, "[C]", out len)) { value = 0x56; return true; }
        if (Match(text, i, "[Z]", out len)) { value = 0x57; return true; }
        if (Match(text, i, "[R]", out len)) { value = 0x58; return true; }
        return false;
    }

    private static bool Match(string text, int i, string tag, out int len)
    {
        len = tag.Length;
        return i + tag.Length <= text.Length &&
               text.AsSpan(i, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase);
    }

    public static int EncodedLength(string? text) => Encode(text ?? "").Length;
}
