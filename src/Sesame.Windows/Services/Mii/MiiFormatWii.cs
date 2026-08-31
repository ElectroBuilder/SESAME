using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sesame.Services.Mii;

public sealed class MiiFormatWii : IMiiFormat
{
    public const int Size = 127456;
    public const int RecordSize = 74;
    public const int RecordCount = 100;
    public const int RecordsOffset = 4;
    public const int ChecksumOffset = Size - 2;

    // Synthetic fixtures prove parser/mutator behavior, not safety against real NAND variants.
    public const bool WriteGateVerified = false;

    public MiiTargetKind Kind => MiiTargetKind.Wii;
    public int DatabaseSize => Size;
    public string ExportExtension => ".mii";

    public static byte[] CreateEmptyDatabase()
    {
        var database = new byte[Size];
        "RNOD"u8.CopyTo(database);
        WriteDatabaseChecksum(database);
        return database;
    }

    public MiiValidation Validate(byte[] database)
    {
        if (database is null || database.Length != Size)
            return MiiValidation.Invalid($"Wii Mii database must be exactly {Size} bytes.");
        if (!database.AsSpan(0, 4).SequenceEqual("RNOD"u8))
            return MiiValidation.Invalid("Wii Mii database has an invalid RNOD header.");
        if (MiiCrc16.ReadBigEndian(database.AsSpan(ChecksumOffset, 2)) !=
            MiiCrc16.Compute(database.AsSpan(0, ChecksumOffset)))
            return MiiValidation.Invalid("Wii Mii database checksum is invalid.");

        var slots = new List<MiiSlot>();
        for (var slot = 0; slot < RecordCount; slot++)
        {
            var record = database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
            if (IsEmpty(record))
                continue;
            if (!TryValidateRecord(record, out var name, out var error))
                return MiiValidation.Invalid($"Wii Mii slot {slot} is invalid: {error}");
            slots.Add(new MiiSlot(slot, name, Convert.ToHexString(record.Slice(24, 4))));
        }
        return MiiValidation.Valid(slots);
    }

    public byte[] Insert(byte[] database, byte[] record)
    {
        var validation = Validate(database);
        if (!validation.IsValid)
            throw new InvalidDataException(validation.Error);
        if (record is null || record.Length != RecordSize)
            throw new InvalidDataException($"A Wii Mii record must be exactly {RecordSize} bytes.");
        if (!TryValidateRecord(record, out _, out var error))
            throw new InvalidDataException($"Wii Mii record is invalid: {error}");

        var id = record.AsSpan(24, 4);
        var emptySlot = -1;
        for (var slot = 0; slot < RecordCount; slot++)
        {
            var existing = database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
            if (IsEmpty(existing))
            {
                if (emptySlot < 0) emptySlot = slot;
                continue;
            }
            if (existing.Slice(24, 4).SequenceEqual(id))
                throw new InvalidDataException("A Wii Mii with this Mii ID already exists.");
        }
        if (emptySlot < 0)
            throw new InvalidDataException("The Wii Mii database is full.");

        var result = (byte[])database.Clone();
        record.CopyTo(result, RecordsOffset + emptySlot * RecordSize);
        WriteDatabaseChecksum(result);
        return result;
    }

    public byte[] ExportRecord(byte[] database, int slot)
    {
        var validation = Validate(database);
        if (!validation.IsValid)
            throw new InvalidDataException(validation.Error);
        if ((uint)slot >= RecordCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var record = database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
        if (IsEmpty(record))
            throw new InvalidDataException("The selected Wii Mii slot is empty.");
        return record.ToArray();
    }

    public byte[] UpdateName(byte[] database, int slot, string name)
    {
        var validation = Validate(database);
        if (!validation.IsValid) throw new InvalidDataException(validation.Error);
        if ((uint)slot >= RecordCount || validation.Slots.All(x => x.Slot != slot))
            throw new ArgumentOutOfRangeException(nameof(slot), "The selected Wii Mii slot is empty or invalid.");
        var result = (byte[])database.Clone();
        var nameBytes = result.AsSpan(RecordsOffset + slot * RecordSize + 2, 20);
        MiiText.WriteFixed(nameBytes, name, bigEndian: true);
        WriteDatabaseChecksum(result);
        var edited = Validate(result);
        if (!edited.IsValid) throw new InvalidDataException("Edited Wii Mii failed validation: " + edited.Error);
        return result;
    }

    public byte[] CreateBasicRecord(string name, byte[]? identity = null)
    {
        var ids = identity is null ? RandomNumberGenerator.GetBytes(8) : (byte[])identity.Clone();
        if (ids.Length != 8)
            throw new ArgumentException("A Wii identity must contain a 4-byte Mii ID and 4-byte system ID.", nameof(identity));
        ids[0] &= 0xDF; // Never set the delete-on-sight bit.
        if (ids.AsSpan(0, 4).IndexOfAnyExcept((byte)0) < 0)
            ids[3] = 1;

        var record = new byte[RecordSize];
        MiiText.WriteFixed(record.AsSpan(2, 20), name, bigEndian: true);
        record[22] = 64;
        record[23] = 64;
        ids.CopyTo(record, 24);
        WriteU32(record, 0x24, (4u << 9) | (10u << 4) | 2u);
        WriteU32(record, 0x28, (12u << 16) | (4u << 9) | (2u << 5));
        WriteU16(record, 0x2C, (ushort)((4 << 8) | (9 << 3)));
        WriteU16(record, 0x2E, (ushort)((4 << 5) | 13));
        WriteU16(record, 0x30, (ushort)((4 << 5) | 10));
        WriteU16(record, 0x32, (ushort)((4 << 5) | 10));
        WriteU16(record, 0x34, (ushort)((4 << 11) | (20 << 6) | (2 << 1)));
        return record;
    }

    private static bool TryValidateRecord(ReadOnlySpan<byte> record, out string name, out string error)
    {
        name = "";
        error = "";
        if (record.Length != RecordSize || IsEmpty(record))
        {
            error = "record is empty or has the wrong size";
            return false;
        }
        try
        {
            name = MiiText.ReadFixed(record.Slice(2, 20), bigEndian: true);
            if (!record.Slice(54, 20).IsEmpty && record.Slice(54, 20).IndexOfAnyExcept((byte)0) >= 0)
                _ = MiiText.ReadFixed(record.Slice(54, 20), bigEndian: true);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if ((record[24] & 0x20) != 0)
        {
            error = "Mii ID has the delete-on-sight bit set";
            return false;
        }
        if (record[22] > 127 || record[23] > 127)
            return Fail("height or build is out of range", out error);
        var header = U16(record, 0);
        if (Bits(header, 15, 1) != 0 || Bits(header, 10, 4) > 12 || Bits(header, 5, 5) > 31 || Bits(header, 1, 4) > 11)
            return Fail("header fields are out of range", out error);
        var face = U16(record, 0x20);
        if (Bits(face, 10, 3) > 5 || Bits(face, 6, 4) > 11)
            return Fail("face fields are out of range", out error);
        var hair = U16(record, 0x22);
        if (Bits(hair, 9, 7) > 71)
            return Fail("hair type is out of range", out error);
        var brow = U32(record, 0x24);
        if (Bits(brow, 27, 5) > 23 || Bits(brow, 22, 4) > 11 || Bits(brow, 9, 4) > 8 ||
            Bits(brow, 4, 5) is < 3 or > 18 || Bits(brow, 0, 4) > 12)
            return Fail("eyebrow fields are out of range", out error);
        var eye = U32(record, 0x28);
        if (Bits(eye, 26, 6) > 47 || Bits(eye, 16, 5) > 18 || Bits(eye, 13, 3) > 5 || Bits(eye, 5, 4) > 12)
            return Fail("eye fields are out of range", out error);
        var nose = U16(record, 0x2C);
        if (Bits(nose, 12, 4) > 11 || Bits(nose, 8, 4) > 8 || Bits(nose, 3, 5) > 18)
            return Fail("nose fields are out of range", out error);
        var mouth = U16(record, 0x2E);
        if (Bits(mouth, 11, 5) > 23 || Bits(mouth, 9, 2) > 2 || Bits(mouth, 5, 4) > 8 || Bits(mouth, 0, 5) > 18)
            return Fail("mouth fields are out of range", out error);
        var glasses = U16(record, 0x30);
        if (Bits(glasses, 12, 4) > 8 || Bits(glasses, 9, 3) > 5 || Bits(glasses, 8, 1) != 0 || Bits(glasses, 0, 5) > 20)
            return Fail("glasses fields are out of range", out error);
        var beard = U16(record, 0x32);
        if (Bits(beard, 5, 4) > 8 || Bits(beard, 0, 5) > 16)
            return Fail("beard fields are out of range", out error);
        var mole = U16(record, 0x34);
        if (Bits(mole, 11, 4) > 8 || Bits(mole, 6, 5) > 30 || Bits(mole, 1, 5) > 16)
            return Fail("mole fields are out of range", out error);
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool IsEmpty(ReadOnlySpan<byte> value) => value.IndexOfAnyExcept((byte)0) < 0;
    private static uint Bits(uint value, int offset, int count) => (value >> offset) & ((1u << count) - 1);
    private static ushort U16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16BigEndian(value.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32BigEndian(value.Slice(offset, 4));
    private static void WriteU16(Span<byte> value, int offset, ushort field) => BinaryPrimitives.WriteUInt16BigEndian(value.Slice(offset, 2), field);
    private static void WriteU32(Span<byte> value, int offset, uint field) => BinaryPrimitives.WriteUInt32BigEndian(value.Slice(offset, 4), field);
    private static void WriteDatabaseChecksum(Span<byte> database) =>
        MiiCrc16.WriteBigEndian(database.Slice(ChecksumOffset, 2), MiiCrc16.Compute(database.Slice(0, ChecksumOffset)));
}
