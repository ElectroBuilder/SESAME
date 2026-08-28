using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Sesame.Services.N64;

public enum BkTextKind
{
    Dialog,
    Quiz,
    Grunty,
    Raw
}

public sealed class BkTextLine : INotifyPropertyChanged
{
    private string _translation = "";

    public int AssetId { get; init; }
    public BkTextKind Kind { get; init; }
    public string Section { get; init; } = "";
    public int Index { get; init; }
    public byte Cmd { get; init; }
    public byte[] OriginalBytes { get; init; } = [];
    public string Original { get; init; } = "";
    public bool Generic { get; init; }
    public bool InPlaceBlob { get; init; }
    public int RomOffset { get; init; }
    public int SlotBytes { get; init; }
    public int InnerOffset { get; init; }
    public int InnerSlot { get; init; }
    public int TablePtr { get; init; }
    public byte Terminator { get; init; }
    public bool AllCaps { get; init; } = true;
    public string Codec { get; init; } = "ascii";
    public string Translation
    {
        get => _translation;
        set
        {
            if (_translation == value) return;
            _translation = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Fits));
            OnPropertyChanged(nameof(Changed));
            OnPropertyChanged(nameof(LengthText));
        }
    }
    public string Speaker => Generic ? "" : BkSpeakers.Name(Cmd);
    public string KindText => Codec switch
    {
        "sm64" => "Mario",
        "dk64" => "DK64",
        _ => Kind switch
        {
            BkTextKind.Quiz => "Quiz",
            BkTextKind.Grunty => "Grunty",
            BkTextKind.Raw => "ROM-tekst",
            _ => "Dialoog"
        }
    };
    public string IdText => Generic || InPlaceBlob ? RomOffset.ToString("X6") : $"{AssetId:X4}";
    public int MaxChars
    {
        get
        {
            var slot = InnerSlot > 0 ? InnerSlot : SlotBytes;
            if (Codec == "sm64")
                return TablePtr > 0 ? 1200 : Math.Max(1, slot - 1);
            if (Codec == "dk64") return Math.Max(1, InnerSlot > 0 ? InnerSlot : slot);
            if (Generic) return Math.Max(1, slot - (Terminator is 0x00 or 0xFF ? 1 : 0));
            return 254;
        }
    }
    public byte NewlineByte => Codec == "sm64" ? (byte)0xFE : Generic ? (byte)0x0A : (byte)0xFD;
    public bool Fits => EncodedNow <= MaxChars;
    public string LengthText => $"{EncodedNow}/{MaxChars}";
    public int EncodedNow => Codec == "sm64"
        ? Sm64Charset.EncodedLength(Translation)
        : BkTextCodec.EncodedLength(Translation, AllCaps, NewlineByte);
    public bool Changed => !string.Equals(Translation, Original, StringComparison.Ordinal);
    public bool UserEdited { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class BkString
{
    public byte Cmd;
    public byte[] Bytes = [];
}

internal sealed class BkParsedText
{
    public BkTextKind Kind;
    public byte[] Header = [];
    public List<BkString> Bottom = new();
    public List<BkString> Top = new();
}

public static class BkSpeakers
{
    private static readonly Dictionary<byte, string> Map = new()
    {
        [0x80] = "Banjo", [0x81] = "Kazooie", [0x82] = "Kazooie", [0x83] = "Bottles",
        [0x84] = "Mumbo", [0x85] = "Chimpy", [0x86] = "Conga", [0x87] = "Blubber",
        [0x88] = "Nipper", [0x89] = "Clanker", [0x8A] = "Snippet", [0x8B] = "Mr. Vile",
        [0x8C] = "Tiptup", [0x8D] = "Tanktup", [0x8E] = "Flibbit", [0x8F] = "Trunker",
        [0x90] = "Rubee", [0x91] = "Gobi", [0x92] = "Grabba", [0x93] = "Napper",
        [0x94] = "Jinjo", [0x95] = "Jinjo", [0x96] = "Jinjo", [0x97] = "Jinjo",
        [0x98] = "Jinjo", [0x99] = "Jinjo", [0xA0] = "Tooty", [0xA1] = "Grunty",
        [0xAA] = "Yum-Yum", [0xAB] = "Lockup", [0xAC] = "Leaky", [0xAF] = "Snacker",
        [0xB5] = "Grunty", [0xCD] = "Snippet"
    };

    public static string Name(byte cmd) =>
        Map.TryGetValue(cmd, out var name) ? name : $"0x{cmd:X2}";
}

public static class BkTextCodec
{
    internal static bool TryParse(byte[] data, out BkParsedText parsed)
    {
        parsed = new BkParsedText();
        if (data.Length < 6) return false;

        if (StartsWith(data, 0x01, 0x01, 0x02, 0x05, 0x00))
            return ParseListed(data, BkTextKind.Quiz, 5, parsed);
        if (StartsWith(data, 0x01, 0x03, 0x00, 0x05, 0x00) && data.Length > 6)
            return ParseListed(data, BkTextKind.Grunty, 5, parsed);
        if (StartsWith(data, 0x01, 0x03, 0x00))
            return ParseDialog(data, parsed);
        return false;
    }

    internal static byte[] ToBytes(BkParsedText parsed)
    {
        using var ms = new MemoryStream();
        ms.Write(parsed.Header);
        if (parsed.Kind == BkTextKind.Dialog)
        {
            ms.WriteByte((byte)parsed.Bottom.Count);
            WriteStrings(ms, parsed.Bottom);
            ms.WriteByte((byte)parsed.Top.Count);
            WriteStrings(ms, parsed.Top);
        }
        else
        {
            ms.WriteByte((byte)(parsed.Bottom.Count + parsed.Top.Count));
            WriteStrings(ms, parsed.Bottom);
            WriteStrings(ms, parsed.Top);
        }
        return ms.ToArray();
    }

    public static string ToReadable(byte[] bytes)
    {
        var end = bytes.Length;
        if (end > 0 && bytes[end - 1] == 0) end--;
        var sb = new StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            var b = bytes[i];
            if (b == 0xFD) sb.Append('\n');
            else if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else sb.Append($"\\x{b:X2}");
        }
        return sb.ToString();
    }

    public static byte[] FromReadable(string text)
    {
        var bytes = Encode(text);
        if (bytes.Length > 254) bytes = bytes[..254];
        var result = new byte[bytes.Length + 1];
        if (bytes.Length > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    public static bool TryEncode(string text, out byte[] bytes)
    {
        bytes = FromReadable(text);
        return bytes.Length > 1;
    }

    public static bool PreferAllCaps(string text)
    {
        var letters = 0;
        var upper = 0;
        foreach (var ch in text ?? "")
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (char.IsUpper(ch)) upper++;
        }
        return letters == 0 || upper * 2 >= letters;
    }

    public static string ToGameText(string text, bool allCaps = true, byte newline = 0xFD)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var bytes = Encode(text, allCaps, newline);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b == 0xFD || (b == newline && newline != 0x20)) sb.Append('\n');
            else if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return TrimGameLines(sb.ToString());
    }

    public static int EncodedLength(string? text, bool allCaps = true, byte newline = 0xFD) =>
        Encode(text ?? "", allCaps, newline).Length;

    public static byte[] Encode(string text, bool allCaps = true, byte newline = 0xFD)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var folded = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('‚', '\'')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('„', '"')
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('−', '-')
            .Replace('…', '.')
            .Replace("...", ".");
        var list = new List<byte>(folded.Length);
        for (var i = 0; i < folded.Length; i++)
        {
            if (folded[i] == '\n')
            {
                if (list.Count > 0 && list[^1] != newline) list.Add(newline);
                continue;
            }

            if (folded[i] == '\\' && i + 3 < folded.Length &&
                (folded[i + 1] is 'x' or 'X') &&
                IsHex(folded[i + 2]) && IsHex(folded[i + 3]))
            {
                var value = Convert.ToByte(folded.Substring(i + 2, 2), 16);
                if (value != 0) list.Add(value);
                i += 3;
                continue;
            }

            foreach (var ch in folded[i].ToString().Normalize(NormalizationForm.FormD))
            {
                if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                    continue;
                var mapped = ch switch
                {
                    'æ' or 'Æ' => 'E',
                    'ø' or 'Ø' => 'O',
                    'ß' => 'S',
                    'ĳ' or 'Ĳ' => 'Y',
                    '«' or '»' => '"',
                    _ => ch
                };
                if (mapped is >= ' ' and <= '~')
                {
                    var ascii = allCaps ? char.ToUpperInvariant(mapped) : mapped;
                    if (ascii is >= ' ' and <= '~')
                        list.Add((byte)ascii);
                }
            }
        }

        while (list.Count > 0 && (list[^1] == newline || list[^1] == 0xFD))
            list.RemoveAt(list.Count - 1);
        return list.ToArray();
    }

    private static string TrimGameLines(string text)
    {
        var parts = text.Split('\n')
            .Select(p => RegexReplaceSpaces(p))
            .Where(p => p.Length > 0);
        return string.Join("\n", parts);
    }

    private static string RegexReplaceSpaces(string p)
    {
        var t = p.Trim();
        if (t.Length == 0) return "";
        var sb = new StringBuilder(t.Length);
        var space = false;
        foreach (var ch in t)
        {
            if (ch == ' ')
            {
                if (space) continue;
                space = true;
                sb.Append(' ');
            }
            else
            {
                space = false;
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static bool ParseDialog(byte[] data, BkParsedText parsed)
    {
        parsed.Kind = BkTextKind.Dialog;
        parsed.Header = [0x01, 0x03, 0x00];
        var offset = 3;
        if (!ReadList(data, ref offset, parsed.Bottom)) return false;
        if (!ReadList(data, ref offset, parsed.Top)) return false;
        return parsed.Bottom.Count + parsed.Top.Count > 0;
    }

    private static bool ParseListed(byte[] data, BkTextKind kind, int headerLen, BkParsedText parsed)
    {
        parsed.Kind = kind;
        parsed.Header = data[..headerLen];
        var offset = headerLen;
        var count = data[offset++];
        var all = new List<BkString>(count);
        for (var i = 0; i < count; i++)
        {
            if (!ReadOne(data, ref offset, out var item)) return false;
            all.Add(item);
        }
        if (all.Count < 3) return false;
        parsed.Bottom = all.Take(all.Count - 3).ToList();
        parsed.Top = all.Skip(all.Count - 3).ToList();
        return true;
    }

    private static bool ReadList(byte[] data, ref int offset, List<BkString> into)
    {
        if (offset >= data.Length) return false;
        var count = data[offset++];
        for (var i = 0; i < count; i++)
        {
            if (!ReadOne(data, ref offset, out var item)) return false;
            into.Add(item);
        }
        return true;
    }

    private static bool ReadOne(byte[] data, ref int offset, out BkString item)
    {
        item = new BkString();
        if (offset + 2 > data.Length) return false;
        item.Cmd = data[offset++];
        var len = data[offset++];
        if (offset + len > data.Length) return false;
        item.Bytes = data[offset..(offset + len)];
        offset += len;
        return len > 0;
    }

    private static void WriteStrings(Stream ms, List<BkString> items)
    {
        foreach (var item in items)
        {
            var bytes = item.Bytes.Length <= 255 ? item.Bytes : item.Bytes[..255];
            ms.WriteByte(item.Cmd);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
    }

    private static bool StartsWith(byte[] data, params byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }
}
