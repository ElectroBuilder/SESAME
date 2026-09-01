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

    public MiiAppearance ReadAppearance(byte[] database, int slot)
    {
        var record = RecordAt(database, slot);
        var appearance = new MiiAppearance(
            MiiText.ReadFixed(record.Slice(0x1C, 20), bigEndian: false),
            (record[4] & 0x80) != 0,
            record[0x15] & 0x0F,
            record[0],
            record[3] & 0x7F,
            record[4] & 0x7F)
        {
            Height = record[1] & 0x7F,
            MoleType = (record[1] >> 7) & 1,
            Build = record[2] & 0x7F,
            HairFlip = (record[2] >> 7) & 1,
            FaceType = (record[0x15] >> 4) & 0x0F,
            FaceColor = record[0x16] & 0x0F,
            FaceWrinkle = (record[0x16] >> 4) & 0x0F,
            FaceMakeup = record[0x17] & 0x0F,
            EyeType = record[9] & 0x3F,
            EyePosition = record[11] & 0x1F,
            EyeRotate = (record[16] >> 5) & 0x07,
            EyeAspect = (record[17] >> 5) & 0x07,
            EyeScale = (record[18] >> 5) & 0x07,
            EyeSpacing = (record[0x17] >> 4) & 0x0F,
            EyebrowType = record[12] & 0x1F,
            EyebrowColor = record[5] & 0x7F,
            EyebrowPosition = (record[25] >> 4) & 0x0F,
            EyebrowAspect = (record[15] >> 5) & 0x07,
            EyebrowScale = record[24] & 0x0F,
            EyebrowRotate = (record[24] >> 4) & 0x0F,
            EyebrowSpacing = record[25] & 0x0F,
            NoseType = record[13] & 0x1F,
            NosePosition = record[14] & 0x1F,
            NoseScale = record[26] & 0x0F,
            MouthType = record[10] & 0x3F,
            MouthColor = record[6] & 0x7F,
            MouthPosition = record[15] & 0x1F,
            MouthAspect = (record[14] >> 5) & 0x07,
            MouthScale = (record[26] >> 4) & 0x0F,
            MustacheType = (record[12] >> 5) & 0x07,
            BeardType = (record[13] >> 5) & 0x07,
            BeardColor = record[7] & 0x7F,
            MustachePosition = record[16] & 0x1F,
            MustacheScale = record[27] & 0x0F,
            GlassesType = record[20] & 0x1F,
            GlassesColor = record[8] & 0x7F,
            GlassesPosition = record[17] & 0x1F,
            GlassesScale = (record[11] >> 5) & 0x07,
            MoleX = record[18] & 0x1F,
            MoleY = record[19] & 0x1F,
            MoleScale = (record[27] >> 4) & 0x0F,
            HasAdvancedParts = true
        };
        return appearance;
    }

    public byte[] UpdateAppearance(byte[] database, int slot, MiiAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        _ = RecordAt(database, slot);
        ValidateRange(appearance.FavoriteColor, 0, 11, "Favourite colour");
        ValidateRange(appearance.HairStyle, 0, 131, "Hair style");
        ValidateRange(appearance.HairColor, 0, 99, "Hair colour");
        ValidateRange(appearance.EyeColor, 0, 99, "Eye colour");
        ValidateRange(appearance.Height, 0, 127, "Height");
        ValidateRange(appearance.Build, 0, 127, "Build");
        ValidateRange(appearance.FaceType, 0, 15, "Face shape");
        ValidateRange(appearance.FaceColor, 0, 15, "Face colour");
        ValidateRange(appearance.FaceWrinkle, 0, 15, "Face wrinkles");
        ValidateRange(appearance.FaceMakeup, 0, 15, "Face makeup");
        ValidateRange(appearance.EyeType, 0, 63, "Eye type");
        ValidateRange(appearance.EyePosition, 0, 31, "Eye position");
        ValidateRange(appearance.EyeRotate, 0, 7, "Eye rotation");
        ValidateRange(appearance.EyeAspect, 0, 7, "Eye aspect");
        ValidateRange(appearance.EyeScale, 0, 7, "Eye scale");
        ValidateRange(appearance.EyeSpacing, 0, 15, "Eye spacing");
        ValidateRange(appearance.EyebrowType, 0, 31, "Eyebrow type");
        ValidateRange(appearance.EyebrowColor, 0, 99, "Eyebrow colour");
        ValidateRange(appearance.EyebrowPosition, 0, 31, "Eyebrow position");
        ValidateRange(appearance.EyebrowAspect, 0, 7, "Eyebrow aspect");
        ValidateRange(appearance.EyebrowScale, 0, 15, "Eyebrow scale");
        ValidateRange(appearance.EyebrowRotate, 0, 15, "Eyebrow rotation");
        ValidateRange(appearance.EyebrowSpacing, 0, 15, "Eyebrow spacing");
        ValidateRange(appearance.NoseType, 0, 31, "Nose type");
        ValidateRange(appearance.NosePosition, 0, 31, "Nose position");
        ValidateRange(appearance.NoseScale, 0, 15, "Nose scale");
        ValidateRange(appearance.MouthType, 0, 63, "Mouth type");
        ValidateRange(appearance.MouthColor, 0, 99, "Mouth colour");
        ValidateRange(appearance.MouthPosition, 0, 31, "Mouth position");
        ValidateRange(appearance.MouthAspect, 0, 7, "Mouth aspect");
        ValidateRange(appearance.MouthScale, 0, 15, "Mouth scale");
        ValidateRange(appearance.MustacheType, 0, 7, "Mustache type");
        ValidateRange(appearance.MustachePosition, 0, 31, "Mustache position");
        ValidateRange(appearance.MustacheScale, 0, 15, "Mustache scale");
        ValidateRange(appearance.BeardType, 0, 7, "Beard type");
        ValidateRange(appearance.BeardColor, 0, 99, "Beard colour");
        ValidateRange(appearance.GlassesType, 0, 31, "Glasses type");
        ValidateRange(appearance.GlassesColor, 0, 99, "Glasses colour");
        ValidateRange(appearance.GlassesPosition, 0, 31, "Glasses position");
        ValidateRange(appearance.GlassesScale, 0, 7, "Glasses scale");
        ValidateRange(appearance.MoleType, 0, 1, "Mole type");
        ValidateRange(appearance.MoleX, 0, 31, "Mole X");
        ValidateRange(appearance.MoleY, 0, 31, "Mole Y");
        ValidateRange(appearance.MoleScale, 0, 15, "Mole scale");

        var result = (byte[])database.Clone();
        var editable = result.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
        MiiText.WriteFixed(editable.Slice(0x1C, 20), appearance.Name, bigEndian: false);
        editable[0] = (byte)appearance.HairStyle;
        editable[1] = (byte)SetBits(editable[1], 0, 7, (uint)appearance.Height);
        editable[1] = (byte)SetBits(editable[1], 7, 1, (uint)appearance.MoleType);
        editable[2] = (byte)SetBits(editable[2], 0, 7, (uint)appearance.Build);
        editable[2] = (byte)SetBits(editable[2], 7, 1, (uint)appearance.HairFlip);
        editable[3] = (byte)((editable[3] & 0x80) | appearance.HairColor);
        editable[4] = (byte)((appearance.IsFemale ? 0x80 : 0) | appearance.EyeColor);
        editable[0x15] = (byte)SetBits(editable[0x15], 0, 4, (uint)appearance.FavoriteColor);
        if (!appearance.HasAdvancedParts)
        {
            MiiCrc16.WriteBigEndian(editable.Slice(0x40, 2), MiiCrc16.Compute(editable.Slice(0, 0x40)));
            WriteDatabaseChecksum(result);
            return result;
        }
        editable[5] = (byte)appearance.EyebrowColor;
        editable[6] = (byte)appearance.MouthColor;
        editable[7] = (byte)appearance.BeardColor;
        editable[8] = (byte)appearance.GlassesColor;
        editable[9] = (byte)SetBits(editable[9], 0, 6, (uint)appearance.EyeType);
        editable[10] = (byte)SetBits(editable[10], 0, 6, (uint)appearance.MouthType);
        editable[11] = (byte)SetBits(editable[11], 0, 5, (uint)appearance.EyePosition);
        editable[11] = (byte)SetBits(editable[11], 5, 3, (uint)appearance.GlassesScale);
        editable[12] = (byte)SetBits(editable[12], 0, 5, (uint)appearance.EyebrowType);
        editable[12] = (byte)SetBits(editable[12], 5, 3, (uint)appearance.MustacheType);
        editable[13] = (byte)SetBits(editable[13], 0, 5, (uint)appearance.NoseType);
        editable[13] = (byte)SetBits(editable[13], 5, 3, (uint)appearance.BeardType);
        editable[14] = (byte)SetBits(editable[14], 0, 5, (uint)appearance.NosePosition);
        editable[14] = (byte)SetBits(editable[14], 5, 3, (uint)appearance.MouthAspect);
        editable[15] = (byte)SetBits(editable[15], 0, 5, (uint)appearance.MouthPosition);
        editable[15] = (byte)SetBits(editable[15], 5, 3, (uint)appearance.EyebrowAspect);
        editable[16] = (byte)SetBits(editable[16], 0, 5, (uint)appearance.MustachePosition);
        editable[16] = (byte)SetBits(editable[16], 5, 3, (uint)appearance.EyeRotate);
        editable[17] = (byte)SetBits(editable[17], 0, 5, (uint)appearance.GlassesPosition);
        editable[17] = (byte)SetBits(editable[17], 5, 3, (uint)appearance.EyeAspect);
        editable[18] = (byte)SetBits(editable[18], 0, 5, (uint)appearance.MoleX);
        editable[18] = (byte)SetBits(editable[18], 5, 3, (uint)appearance.EyeScale);
        editable[19] = (byte)SetBits(editable[19], 0, 5, (uint)appearance.MoleY);
        editable[20] = (byte)SetBits(editable[20], 0, 5, (uint)appearance.GlassesType);
        editable[0x15] = (byte)SetBits(editable[0x15], 0, 4, (uint)appearance.FavoriteColor);
        editable[0x15] = (byte)SetBits(editable[0x15], 4, 4, (uint)appearance.FaceType);
        editable[0x16] = (byte)SetBits(editable[0x16], 0, 4, (uint)appearance.FaceColor);
        editable[0x16] = (byte)SetBits(editable[0x16], 4, 4, (uint)appearance.FaceWrinkle);
        editable[0x17] = (byte)SetBits(editable[0x17], 0, 4, (uint)appearance.FaceMakeup);
        editable[0x17] = (byte)SetBits(editable[0x17], 4, 4, (uint)appearance.EyeSpacing);
        editable[0x18] = (byte)SetBits(editable[0x18], 0, 4, (uint)appearance.EyebrowScale);
        editable[0x18] = (byte)SetBits(editable[0x18], 4, 4, (uint)appearance.EyebrowRotate);
        editable[0x19] = (byte)SetBits(editable[0x19], 0, 4, (uint)appearance.EyebrowSpacing);
        editable[0x19] = (byte)SetBits(editable[0x19], 4, 4, (uint)appearance.EyebrowPosition);
        editable[0x1A] = (byte)SetBits(editable[0x1A], 0, 4, (uint)appearance.NoseScale);
        editable[0x1A] = (byte)SetBits(editable[0x1A], 4, 4, (uint)appearance.MouthScale);
        editable[0x1B] = (byte)SetBits(editable[0x1B], 0, 4, (uint)appearance.MustacheScale);
        editable[0x1B] = (byte)SetBits(editable[0x1B], 4, 4, (uint)appearance.MoleScale);
        MiiCrc16.WriteBigEndian(editable.Slice(0x40, 2), MiiCrc16.Compute(editable.Slice(0, 0x40)));
        WriteDatabaseChecksum(result);
        var edited = Validate(result);
        if (!edited.IsValid) throw new InvalidDataException("Edited Eden Mii failed validation: " + edited.Error);
        return result;
    }

    public byte[] UpdateName(byte[] database, int slot, string name)
    {
        var current = ReadAppearance(database, slot);
        return UpdateAppearance(database, slot, new MiiAppearance(name, current.IsFemale, current.FavoriteColor,
            current.HairStyle, current.HairColor, current.EyeColor)
        {
            HasAdvancedParts = current.HasAdvancedParts,
            Height = current.Height, Build = current.Build, HairFlip = current.HairFlip,
            FaceType = current.FaceType, FaceColor = current.FaceColor, FaceMakeup = current.FaceMakeup,
            FaceWrinkle = current.FaceWrinkle, EyeType = current.EyeType, EyeScale = current.EyeScale,
            EyeAspect = current.EyeAspect, EyeRotate = current.EyeRotate, EyeSpacing = current.EyeSpacing,
            EyePosition = current.EyePosition, EyebrowType = current.EyebrowType,
            EyebrowColor = current.EyebrowColor, EyebrowScale = current.EyebrowScale,
            EyebrowAspect = current.EyebrowAspect, EyebrowRotate = current.EyebrowRotate,
            EyebrowSpacing = current.EyebrowSpacing, EyebrowPosition = current.EyebrowPosition,
            NoseType = current.NoseType, NoseScale = current.NoseScale, NosePosition = current.NosePosition,
            MouthType = current.MouthType, MouthColor = current.MouthColor, MouthScale = current.MouthScale,
            MouthAspect = current.MouthAspect, MouthPosition = current.MouthPosition,
            BeardType = current.BeardType, BeardColor = current.BeardColor, MustacheType = current.MustacheType,
            MustacheScale = current.MustacheScale, MustachePosition = current.MustachePosition,
            GlassesType = current.GlassesType, GlassesColor = current.GlassesColor,
            GlassesScale = current.GlassesScale, GlassesPosition = current.GlassesPosition,
            MoleType = current.MoleType, MoleScale = current.MoleScale, MoleX = current.MoleX, MoleY = current.MoleY
        });
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
    private static uint SetBits(uint value, int offset, int count, uint replacement)
    {
        var mask = ((1u << count) - 1) << offset;
        return (value & ~mask) | ((replacement << offset) & mask);
    }
    private static ReadOnlySpan<byte> RecordAt(byte[] database, int slot)
    {
        var validation = new MiiFormatSwitch().Validate(database);
        if (!validation.IsValid) throw new InvalidDataException(validation.Error);
        if ((uint)slot >= database[CountOffset]) throw new ArgumentOutOfRangeException(nameof(slot));
        return database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
    }
    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
    }
    private static uint Bits(uint value, int offset, int count) => (value >> offset) & ((1u << count) - 1);
    private static void WriteDatabaseChecksum(Span<byte> database) =>
        MiiCrc16.WriteBigEndian(database.Slice(ChecksumOffset, 2), MiiCrc16.Compute(database.Slice(0, ChecksumOffset)));
}
