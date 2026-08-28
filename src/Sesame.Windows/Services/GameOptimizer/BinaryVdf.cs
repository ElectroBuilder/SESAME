using System.IO;
using System.Text;

namespace Sesame.Services.GameOptimizer;

public sealed class VdfNode
{
    public List<(string Key, object Value)> Entries { get; } = new();

    public VdfNode? Child(string key) =>
        Entries.FirstOrDefault(e => KeyEq(e.Key, key)).Value as VdfNode;

    public string? GetString(string key)
    {
        var value = Find(key);
        return value as string;
    }

    public int GetInt(string key, int fallback = 0) =>
        Find(key) switch
        {
            int n => n,
            uint u => unchecked((int)u),
            long l => unchecked((int)l),
            ulong ul => unchecked((int)ul),
            _ => fallback
        };

    public void Set(string key, object value)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!KeyEq(Entries[i].Key, key)) continue;
            Entries[i] = (Entries[i].Key, value);
            return;
        }
        Entries.Add((key, value));
    }

    public IEnumerable<VdfNode> Maps() =>
        Entries.Select(e => e.Value).OfType<VdfNode>();

    private object? Find(string key) =>
        Entries.FirstOrDefault(e => KeyEq(e.Key, key)).Value;

    private static bool KeyEq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public readonly record struct VdfRaw(byte Type, byte[] Data);

public static class BinaryVdf
{
    public static VdfNode Read(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        var root = new VdfNode();
        if (ms.Length == 0) return root;
        var type = br.ReadByte();
        if (type != 0x00) throw new InvalidDataException("shortcuts.vdf does not start with a map.");
        var name = ReadCString(br);
        var node = ReadMap(br);
        root.Set(name, node);
        return root;
    }

    public static byte[] Write(VdfNode root)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        if (root.Entries.Count == 0)
        {
            bw.Write((byte)0x00);
            WriteCString(bw, "shortcuts");
            bw.Write((byte)0x08);
            bw.Write((byte)0x08);
            return ms.ToArray();
        }

        foreach (var (key, value) in root.Entries)
        {
            WriteValue(bw, key, value);
        }
        bw.Write((byte)0x08);
        return ms.ToArray();
    }

    private static VdfNode ReadMap(BinaryReader br)
    {
        var node = new VdfNode();
        while (true)
        {
            if (br.BaseStream.Position >= br.BaseStream.Length) break;
            var type = br.ReadByte();
            if (type == 0x08) break;
            var key = ReadCString(br);
            object value = type switch
            {
                0x00 => ReadMap(br),
                0x01 => ReadCString(br),
                0x02 => br.ReadInt32(),
                0x03 => new VdfRaw(0x03, br.ReadBytes(4)),
                0x05 => new VdfRaw(0x05, ReadWStringBytes(br)),
                0x07 => br.ReadUInt64(),
                _ => throw new InvalidDataException($"Onbekend VDF-type {type} bij '{key}'.")
            };
            node.Entries.Add((key, value));
        }
        return node;
    }

    private static void WriteValue(BinaryWriter bw, string key, object value)
    {
        switch (value)
        {
            case VdfNode child:
                bw.Write((byte)0x00);
                WriteCString(bw, key);
                WriteMap(bw, child);
                break;
            case string text:
                bw.Write((byte)0x01);
                WriteCString(bw, key);
                WriteCString(bw, text);
                break;
            case int number:
                bw.Write((byte)0x02);
                WriteCString(bw, key);
                bw.Write(number);
                break;
            case uint unumber:
                bw.Write((byte)0x02);
                WriteCString(bw, key);
                bw.Write(unumber);
                break;
            case ulong ulongNumber:
                bw.Write((byte)0x07);
                WriteCString(bw, key);
                bw.Write(ulongNumber);
                break;
            case VdfRaw raw:
                bw.Write(raw.Type);
                WriteCString(bw, key);
                bw.Write(raw.Data);
                break;
        }
    }

    private static void WriteMap(BinaryWriter bw, VdfNode node)
    {
        foreach (var (key, value) in node.Entries)
            WriteValue(bw, key, value);
        bw.Write((byte)0x08);
    }

    private static byte[] ReadWStringBytes(BinaryReader br)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var a = br.ReadByte();
            var b = br.ReadByte();
            bytes.Add(a);
            bytes.Add(b);
            if (a == 0 && b == 0) break;
        }
        return bytes.ToArray();
    }

    private static string ReadCString(BinaryReader br)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteCString(BinaryWriter bw, string value)
    {
        if (!string.IsNullOrEmpty(value))
            bw.Write(Encoding.UTF8.GetBytes(value));
        bw.Write((byte)0);
    }
}
