using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sesame.Services.Mii;

public sealed class MiiFormatSwitch : IMiiFormat
{
    public const int Size = 0x1A98;
    public const int RecordSize = 0x44;
    public const int RecordCount = 100;
    public const int RecordsOffset = 4;
    public const int VersionOffset = RecordsOffset + RecordSize * RecordCount;
    public const int CountOffset = VersionOffset + 1;
    public const int ChecksumOffset = CountOffset + 1;

    // This must remain false until a separately acknowledged real-Eden validation run succeeds.
    public const bool WriteGateVerified = false;

    public MiiTargetKind Kind => MiiTargetKind.Eden;
    public int DatabaseSize => Size;
    public string ExportExtension => ".miigx";

    public static byte[] CreateEmptyDatabase()
    {
        var database = new byte[Size];
        "NFDB"u8.CopyTo(database);
        database[VersionOffset] = 1;
        WriteDatabaseChecksum(database);
        return database;
    }

    public MiiValidation Validate(byte[] database)
    {
        if (database is null || database.Length != Size)
            return MiiValidation.Invalid($"Eden Mii database must be exactly {Size} bytes; 88-byte CharInfo/NFIF files are not supported.");
        if (!database.AsSpan(0, 4).SequenceEqual("NFDB"u8))
            return MiiValidation.Invalid("Eden Mii database has an invalid NFDB header.");
        if (database[VersionOffset] != 1)
            return MiiValidation.Invalid("Eden Mii database version is unsupported.");
        var count = database[CountOffset];
        if (count >= RecordCount)
            return MiiValidation.Invalid("Eden Mii database count must be less than 100.");
        if (MiiCrc16.ReadBigEndian(database.AsSpan(ChecksumOffset, 2)) !=
            MiiCrc16.Compute(database.AsSpan(0, ChecksumOffset)))
            return MiiValidation.Invalid("Eden Mii database checksum is invalid.");

        var slots = new List<MiiSlot>(count);
        for (var slot = 0; slot < count; slot++)
        {
            var record = database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
            if (!TryValidateRecord(record, out var name, out var error))
                return MiiValidation.Invalid($"Eden Mii slot {slot} is invalid: {error}");
            slots.Add(new MiiSlot(slot, name, Convert.ToHexString(record.Slice(0x30, 16))));
        }
        return MiiValidation.Valid(slots);
    }

    public byte[] Insert(byte[] database, byte[] record)
    {
        var validation = Validate(database);
        if (!validation.IsValid)
            throw new InvalidDataException(validation.Error);
        if (record is null || record.Length != RecordSize)
            throw new InvalidDataException($"An Eden StoreData record must be exactly {RecordSize} bytes; CharInfo/NFIF is not accepted.");
        if (!TryValidateRecord(record, out _, out var error))
            throw new InvalidDataException($"Eden StoreData record is invalid: {error}");

        var count = database[CountOffset];
        if (count + 1 >= RecordCount)
            throw new InvalidDataException("The Eden Mii database cannot accept another record while keeping count below 100.");
        var id = record.AsSpan(0x30, 16);
        for (var slot = 0; slot < count; slot++)
        {
            var existing = database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
            if (existing.Slice(0x30, 16).SequenceEqual(id))
                throw new InvalidDataException("An Eden Mii with this UUID already exists.");
        }

        var result = (byte[])database.Clone();
        record.CopyTo(result, RecordsOffset + count * RecordSize);
        result[CountOffset] = (byte)(count + 1);
        WriteDatabaseChecksum(result);
        return result;
    }

    public byte[] ExportRecord(byte[] database, int slot)
    {
        var validation = Validate(database);
        if (!validation.IsValid)
            throw new InvalidDataException(validation.Error);
        if ((uint)slot >= database[CountOffset])
            throw new ArgumentOutOfRangeException(nameof(slot));
        return database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize).ToArray();
    }

    public byte[] CreateBasicRecord(string name, byte[]? identity = null)
    {
        var uuid = identity is null ? RandomNumberGenerator.GetBytes(16) : (byte[])identity.Clone();
        if (uuid.Length != 16)
            throw new ArgumentException("An Eden identity must be a 16-byte UUID.", nameof(identity));
        if (uuid.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("An Eden UUID cannot be all zero.", nameof(identity));
        if (identity is null)
        {
            uuid[6] = (byte)((uuid[6] & 0x0F) | 0x40);
            uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        }

        var record = new byte[RecordSize];
        MiiText.WriteFixed(record.AsSpan(0x1C, 20), name, bigEndian: false);
        uuid.CopyTo(record, 0x30);
        MiiCrc16.WriteBigEndian(record.AsSpan(0x40, 2), MiiCrc16.Compute(record.AsSpan(0, 0x40)));
        // Eden stores a zero device-id in this database variant, so its device CRC is zero.
        MiiCrc16.WriteBigEndian(record.AsSpan(0x42, 2), 0);
        return record;
    }

    private static bool TryValidateRecord(ReadOnlySpan<byte> record, out string name, out string error)
    {
        name = "";
        error = "";
        if (record.Length != RecordSize)
            return Fail("record has the wrong size", out error);
        if (record.Slice(0x30, 16).IndexOfAnyExcept((byte)0) < 0)
            return Fail("UUID is all zero", out error);
        if (MiiCrc16.ReadBigEndian(record.Slice(0x40, 2)) != MiiCrc16.Compute(record.Slice(0, 0x40)))
            return Fail("StoreData checksum is invalid", out error);
        if (MiiCrc16.ReadBigEndian(record.Slice(0x42, 2)) != 0)
            return Fail("device checksum is not zero for Eden's zero device-id", out error);
        try
        {
            name = MiiText.ReadFixed(record.Slice(0x1C, 20), bigEndian: false);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var w0 = U32(record, 0);
        if (Bits(w0, 0, 8) > 131 || Bits(w0, 8, 7) > 127 || Bits(w0, 15, 1) > 1 ||
            Bits(w0, 16, 7) > 127 || Bits(w0, 23, 1) > 1 || Bits(w0, 24, 7) > 99 || Bits(w0, 31, 1) > 1)
            return Fail("CoreData word 0 fields are out of range", out error);
        var w1 = U32(record, 4);
        if (Bits(w1, 0, 7) > 99 || Bits(w1, 7, 1) > 1 || Bits(w1, 8, 7) > 99 ||
            Bits(w1, 16, 7) > 99 || Bits(w1, 24, 7) > 99)
            return Fail("CoreData word 1 fields are out of range", out error);
        var w2 = U32(record, 8);
        if (Bits(w2, 0, 7) > 99 || Bits(w2, 8, 6) > 59 || Bits(w2, 14, 2) > 3 ||
            Bits(w2, 16, 6) > 35 || Bits(w2, 22, 2) > 3 || Bits(w2, 24, 5) > 18 || Bits(w2, 29, 3) > 7)
            return Fail("CoreData word 2 fields are out of range", out error);
        var w3 = U32(record, 12);
        if (Bits(w3, 0, 5) > 23 || Bits(w3, 5, 3) > 5 || Bits(w3, 8, 5) > 17 ||
            Bits(w3, 13, 3) > 5 || Bits(w3, 16, 5) > 18 || Bits(w3, 21, 3) > 6 ||
            Bits(w3, 24, 5) > 18 || Bits(w3, 29, 3) > 6)
            return Fail("CoreData word 3 fields are out of range", out error);
        var w4 = U32(record, 16);
        if (Bits(w4, 0, 5) > 16 || Bits(w4, 5, 3) > 7 || Bits(w4, 8, 5) > 20 ||
            Bits(w4, 13, 3) > 6 || Bits(w4, 16, 5) > 16 || Bits(w4, 21, 3) > 7 || Bits(w4, 24, 5) > 30)
            return Fail("CoreData word 4 fields are out of range", out error);
        var w5 = U32(record, 20);
        if (Bits(w5, 0, 5) > 19 || Bits(w5, 8, 4) > 11 || Bits(w5, 12, 4) > 11 ||
            Bits(w5, 16, 4) > 9 || Bits(w5, 20, 4) > 11 || Bits(w5, 24, 4) > 11 || Bits(w5, 28, 4) > 12)
            return Fail("CoreData word 5 fields are out of range", out error);
        var w6 = U32(record, 24);
        if (Bits(w6, 0, 4) > 8 || Bits(w6, 4, 4) > 11 || Bits(w6, 8, 4) > 12 || Bits(w6, 12, 4) > 15 ||
            Bits(w6, 16, 4) > 8 || Bits(w6, 20, 4) > 8 || Bits(w6, 24, 4) > 8 || Bits(w6, 28, 4) > 8)
            return Fail("CoreData word 6 fields are out of range", out error);
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(offset, 4));
    private static uint Bits(uint value, int offset, int count) => (value >> offset) & ((1u << count) - 1);
    private static void WriteDatabaseChecksum(Span<byte> database) =>
        MiiCrc16.WriteBigEndian(database.Slice(ChecksumOffset, 2), MiiCrc16.Compute(database.Slice(0, ChecksumOffset)));
}
