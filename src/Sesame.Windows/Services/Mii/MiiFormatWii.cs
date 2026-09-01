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

    public MiiAppearance ReadAppearance(byte[] database, int slot)
    {
        var record = RecordAt(database, slot);
        var header = U16(record, 0);
        var hair = U16(record, 0x22);
        var face = U16(record, 0x20);
        var brow = U32(record, 0x24);
        var eye = U32(record, 0x28);
        var appearance = new MiiAppearance(
            MiiText.ReadFixed(record.Slice(2, 20), bigEndian: true),
            Bits(header, 14, 1) != 0,
            (int)Bits(header, 1, 4),
            (int)Bits(hair, 9, 7),
            (int)Bits(hair, 6, 3),
            (int)Bits(eye, 16, 3))
        {
            Height = record[22],
            Build = record[23],
            HairFlip = (int)Bits(hair, 5, 1),
            FaceType = (int)Bits(face, 13, 3),
            FaceColor = (int)Bits(face, 10, 3),
            FaceMakeup = (int)Bits(face, 6, 4),
            EyebrowType = (int)Bits(brow, 27, 5),
            EyebrowColor = (int)Bits(brow, 13, 3),
            EyebrowScale = (int)Bits(brow, 9, 4),
            EyebrowPosition = (int)Bits(brow, 4, 5),
            EyebrowSpacing = (int)Bits(brow, 0, 4),
            EyeType = (int)Bits(eye, 26, 6),
            EyeScale = (int)Bits(eye, 9, 4),
            EyeColor = (int)Bits(eye, 13, 3),
            EyeRotate = (int)Bits(eye, 21, 5),
            EyeSpacing = (int)Bits(eye, 5, 4),
            EyePosition = (int)Bits(eye, 0, 5),
            NoseType = (int)Bits(U16(record, 0x2C), 12, 4),
            NoseScale = (int)Bits(U16(record, 0x2C), 8, 4),
            NosePosition = (int)Bits(U16(record, 0x2C), 3, 5),
            MouthType = (int)Bits(U16(record, 0x2E), 11, 5),
            MouthColor = (int)Bits(U16(record, 0x2E), 9, 2),
            MouthScale = (int)Bits(U16(record, 0x2E), 5, 4),
            MouthPosition = (int)Bits(U16(record, 0x2E), 0, 5),
            GlassesType = (int)Bits(U16(record, 0x30), 12, 4),
            GlassesColor = (int)Bits(U16(record, 0x30), 9, 3),
            GlassesScale = (int)Bits(U16(record, 0x30), 5, 4),
            GlassesPosition = (int)Bits(U16(record, 0x30), 0, 5),
            MustacheType = (int)Bits(U16(record, 0x32), 14, 2),
            BeardType = (int)Bits(U16(record, 0x32), 12, 2),
            BeardColor = (int)Bits(U16(record, 0x32), 9, 3),
            MustacheScale = (int)Bits(U16(record, 0x32), 5, 4),
            MustachePosition = (int)Bits(U16(record, 0x32), 0, 5),
            MoleType = (int)Bits(U16(record, 0x34), 15, 1),
            MoleScale = (int)Bits(U16(record, 0x34), 11, 4),
            MoleY = (int)Bits(U16(record, 0x34), 6, 5),
            MoleX = (int)Bits(U16(record, 0x34), 1, 5),
            HasAdvancedParts = true
        };
        return appearance;
    }

    public byte[] UpdateAppearance(byte[] database, int slot, MiiAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var record = RecordAt(database, slot); // validates the live/draft source first
        ValidateRange(appearance.FavoriteColor, 0, 11, "Favourite colour");
        ValidateRange(appearance.HairStyle, 0, 71, "Hair style");
        ValidateRange(appearance.HairColor, 0, 7, "Hair colour");
        ValidateRange(appearance.EyeColor, 0, 5, "Eye colour");
        if (appearance.HasAdvancedParts)
        {
            ValidateRange(appearance.Height, 0, 127, "Height");
            ValidateRange(appearance.Build, 0, 127, "Build");
            ValidateRange(appearance.FaceType, 0, 7, "Face shape");
            ValidateRange(appearance.FaceColor, 0, 5, "Face colour");
            ValidateRange(appearance.FaceMakeup, 0, 11, "Face makeup");
            ValidateRange(appearance.EyebrowType, 0, 23, "Eyebrow type");
            ValidateRange(appearance.EyebrowColor, 0, 7, "Eyebrow colour");
            ValidateRange(appearance.EyebrowScale, 0, 15, "Eyebrow scale");
            ValidateRange(appearance.EyebrowPosition, 0, 31, "Eyebrow position");
            ValidateRange(appearance.EyebrowSpacing, 0, 15, "Eyebrow spacing");
            ValidateRange(appearance.EyeType, 0, 63, "Eye type");
            ValidateRange(appearance.EyeScale, 0, 15, "Eye scale");
            ValidateRange(appearance.EyeRotate, 0, 31, "Eye rotation");
            ValidateRange(appearance.EyeSpacing, 0, 15, "Eye spacing");
            ValidateRange(appearance.EyePosition, 0, 31, "Eye position");
            ValidateRange(appearance.NoseType, 0, 15, "Nose type");
            ValidateRange(appearance.NoseScale, 0, 15, "Nose scale");
            ValidateRange(appearance.NosePosition, 0, 31, "Nose position");
            ValidateRange(appearance.MouthType, 0, 31, "Mouth type");
            ValidateRange(appearance.MouthColor, 0, 3, "Mouth colour");
            ValidateRange(appearance.MouthScale, 0, 15, "Mouth scale");
            ValidateRange(appearance.MouthPosition, 0, 31, "Mouth position");
            ValidateRange(appearance.GlassesType, 0, 15, "Glasses type");
            ValidateRange(appearance.GlassesColor, 0, 7, "Glasses colour");
            ValidateRange(appearance.GlassesScale, 0, 15, "Glasses scale");
            ValidateRange(appearance.GlassesPosition, 0, 31, "Glasses position");
            ValidateRange(appearance.MustacheType, 0, 3, "Mustache type");
            ValidateRange(appearance.BeardType, 0, 3, "Beard type");
            ValidateRange(appearance.BeardColor, 0, 7, "Beard colour");
            ValidateRange(appearance.MustacheScale, 0, 15, "Mustache scale");
            ValidateRange(appearance.MustachePosition, 0, 31, "Mustache position");
            ValidateRange(appearance.MoleType, 0, 1, "Mole type");
            ValidateRange(appearance.MoleScale, 0, 15, "Mole scale");
            ValidateRange(appearance.MoleX, 0, 31, "Mole X");
            ValidateRange(appearance.MoleY, 0, 31, "Mole Y");
        }

        var result = (byte[])database.Clone();
        var editable = result.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
        MiiText.WriteFixed(editable.Slice(2, 20), appearance.Name, bigEndian: true);
        var header = SetBits(U16(editable, 0), 14, 1, appearance.IsFemale ? 1u : 0u);
        header = SetBits(header, 1, 4, (uint)appearance.FavoriteColor);
        WriteU16(editable, 0, (ushort)header);
        var hair = SetBits(U16(editable, 0x22), 9, 7, (uint)appearance.HairStyle);
        hair = SetBits(hair, 6, 3, (uint)appearance.HairColor);
        WriteU16(editable, 0x22, (ushort)hair);
        var eye = SetBits(U32(editable, 0x28), 13, 3, (uint)appearance.EyeColor);
        WriteU32(editable, 0x28, eye);
        if (!appearance.HasAdvancedParts)
        {
            WriteDatabaseChecksum(result);
            return result;
        }
        editable[22] = (byte)appearance.Height;
        editable[23] = (byte)appearance.Build;
        var face = SetBits(U16(editable, 0x20), 13, 3, (uint)appearance.FaceType);
        face = SetBits(face, 10, 3, (uint)appearance.FaceColor);
        face = SetBits(face, 6, 4, (uint)appearance.FaceMakeup);
        WriteU16(editable, 0x20, (ushort)face);
        var hairAdvanced = SetBits(U16(editable, 0x22), 5, 1, (uint)appearance.HairFlip);
        WriteU16(editable, 0x22, (ushort)hairAdvanced);
        var brow = SetBits(U32(editable, 0x24), 27, 5, (uint)appearance.EyebrowType);
        brow = SetBits(brow, 13, 3, (uint)appearance.EyebrowColor);
        brow = SetBits(brow, 9, 4, (uint)appearance.EyebrowScale);
        brow = SetBits(brow, 4, 5, (uint)appearance.EyebrowPosition);
        brow = SetBits(brow, 0, 4, (uint)appearance.EyebrowSpacing);
        WriteU32(editable, 0x24, brow);
        eye = SetBits(U32(editable, 0x28), 26, 6, (uint)appearance.EyeType);
        eye = SetBits(eye, 21, 5, (uint)appearance.EyeRotate);
        eye = SetBits(eye, 9, 4, (uint)appearance.EyeScale);
        eye = SetBits(eye, 5, 4, (uint)appearance.EyeSpacing);
        eye = SetBits(eye, 0, 5, (uint)appearance.EyePosition);
        WriteU32(editable, 0x28, eye);
        var nose = SetBits(U16(editable, 0x2C), 12, 4, (uint)appearance.NoseType);
        nose = SetBits(nose, 8, 4, (uint)appearance.NoseScale);
        nose = SetBits(nose, 3, 5, (uint)appearance.NosePosition);
        WriteU16(editable, 0x2C, (ushort)nose);
        var mouth = SetBits(U16(editable, 0x2E), 11, 5, (uint)appearance.MouthType);
        mouth = SetBits(mouth, 9, 2, (uint)appearance.MouthColor);
        mouth = SetBits(mouth, 5, 4, (uint)appearance.MouthScale);
        mouth = SetBits(mouth, 0, 5, (uint)appearance.MouthPosition);
        WriteU16(editable, 0x2E, (ushort)mouth);
        var glasses = SetBits(U16(editable, 0x30), 12, 4, (uint)appearance.GlassesType);
        glasses = SetBits(glasses, 9, 3, (uint)appearance.GlassesColor);
        glasses = SetBits(glasses, 5, 4, (uint)appearance.GlassesScale);
        glasses = SetBits(glasses, 0, 5, (uint)appearance.GlassesPosition);
        WriteU16(editable, 0x30, (ushort)glasses);
        var beard = SetBits(U16(editable, 0x32), 14, 2, (uint)appearance.MustacheType);
        beard = SetBits(beard, 12, 2, (uint)appearance.BeardType);
        beard = SetBits(beard, 9, 3, (uint)appearance.BeardColor);
        beard = SetBits(beard, 5, 4, (uint)appearance.MustacheScale);
        beard = SetBits(beard, 0, 5, (uint)appearance.MustachePosition);
        WriteU16(editable, 0x32, (ushort)beard);
        var mole = SetBits(U16(editable, 0x34), 15, 1, (uint)appearance.MoleType);
        mole = SetBits(mole, 11, 4, (uint)appearance.MoleScale);
        mole = SetBits(mole, 6, 5, (uint)appearance.MoleY);
        mole = SetBits(mole, 1, 5, (uint)appearance.MoleX);
        WriteU16(editable, 0x34, (ushort)mole);
        WriteDatabaseChecksum(result);
        var edited = Validate(result);
        if (!edited.IsValid) throw new InvalidDataException("Edited Wii Mii failed validation: " + edited.Error);
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
    private static ReadOnlySpan<byte> RecordAt(byte[] database, int slot)
    {
        var validation = new MiiFormatWii().Validate(database);
        if (!validation.IsValid) throw new InvalidDataException(validation.Error);
        if ((uint)slot >= RecordCount || validation.Slots.All(x => x.Slot != slot))
            throw new ArgumentOutOfRangeException(nameof(slot), "The selected Wii Mii slot is empty or invalid.");
        return database.AsSpan(RecordsOffset + slot * RecordSize, RecordSize);
    }
    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
    }
    private static uint Bits(uint value, int offset, int count) => (value >> offset) & ((1u << count) - 1);
    private static uint SetBits(uint value, int offset, int count, uint replacement)
    {
        var mask = ((1u << count) - 1) << offset;
        return (value & ~mask) | ((replacement << offset) & mask);
    }
    private static ushort U16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16BigEndian(value.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32BigEndian(value.Slice(offset, 4));
    private static void WriteU16(Span<byte> value, int offset, ushort field) => BinaryPrimitives.WriteUInt16BigEndian(value.Slice(offset, 2), field);
    private static void WriteU32(Span<byte> value, int offset, uint field) => BinaryPrimitives.WriteUInt32BigEndian(value.Slice(offset, 4), field);
    private static void WriteDatabaseChecksum(Span<byte> database) =>
        MiiCrc16.WriteBigEndian(database.Slice(ChecksumOffset, 2), MiiCrc16.Compute(database.Slice(0, ChecksumOffset)));
}
