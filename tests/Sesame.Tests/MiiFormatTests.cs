using System.Buffers.Binary;
using System.Text;
using Sesame.Services.Mii;

namespace Sesame.Tests;

public sealed class MiiFormatTests
{
    [Fact]
    public void Crc16_xmodem_matches_standard_check_vector() =>
        Assert.Equal(0x31C3, MiiCrc16.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void Fixed_text_rejects_dirty_padding_surrogates_and_controls()
    {
        var dirty = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(dirty.AsSpan(0, 2), 'A');
        BinaryPrimitives.WriteUInt16LittleEndian(dirty.AsSpan(4, 2), 'B');
        Assert.Throws<FormatException>(() => MiiText.ReadFixed(dirty, bigEndian: false));
        Assert.Throws<ArgumentException>(() => MiiText.WriteFixed(new byte[20], "A\uD800", bigEndian: false));
        Assert.Throws<ArgumentException>(() => MiiText.WriteFixed(new byte[20], "A\n", bigEndian: false));
    }

    [Fact]
    public void Wii_empty_database_validation_is_byte_exact_and_non_mutating()
    {
        var format = new MiiFormatWii();
        var database = MiiFormatWii.CreateEmptyDatabase();
        var original = (byte[])database.Clone();

        var validation = format.Validate(database);

        Assert.True(validation.IsValid, validation.Error);
        Assert.Empty(validation.Slots);
        Assert.Equal(original, database);
        Assert.False(MiiFormatWii.WriteGateVerified);
    }

    [Fact]
    public void Wii_insert_changes_only_first_slot_and_database_crc_and_preserves_opaque_bytes()
    {
        var format = new MiiFormatWii();
        var database = MiiFormatWii.CreateEmptyDatabase();
        for (var i = 7404; i < MiiFormatWii.ChecksumOffset; i++)
            database[i] = (byte)(i * 31 + 7);
        WriteCrc(database, MiiFormatWii.ChecksumOffset);
        var original = (byte[])database.Clone();
        var opaque = database.AsSpan(7404, MiiFormatWii.ChecksumOffset - 7404).ToArray();
        var record = format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = format.Insert(database, record);

        Assert.True(format.Validate(result).IsValid);
        Assert.Equal("Mike", format.Validate(result).Slots.Single().Name);
        Assert.Equal(opaque, result.AsSpan(7404, MiiFormatWii.ChecksumOffset - 7404).ToArray());
        Assert.Equal(record, result.AsSpan(MiiFormatWii.RecordsOffset, MiiFormatWii.RecordSize).ToArray());
        Assert.Equal(original, database); // Insert is copy-on-write.
        AssertOnlyChanged(database, result,
            Enumerable.Range(MiiFormatWii.RecordsOffset, MiiFormatWii.RecordSize)
                .Concat(Enumerable.Range(MiiFormatWii.ChecksumOffset, 2)).ToHashSet());
    }

    [Fact]
    public void Wii_export_is_an_exact_detached_record()
    {
        var format = new MiiFormatWii();
        var record = format.CreateBasicRecord("Sara", [9, 8, 7, 6, 5, 4, 3, 2]);
        var database = format.Insert(MiiFormatWii.CreateEmptyDatabase(), record);

        var exported = format.ExportRecord(database, 0);

        Assert.Equal(record, exported);
        exported[0] ^= 1;
        Assert.NotEqual(exported[0], database[MiiFormatWii.RecordsOffset]);
        Assert.Throws<InvalidDataException>(() => format.ExportRecord(database, 1));
    }

    [Fact]
    public void Wii_rejects_wrong_size_magic_crc_duplicate_and_delete_bit()
    {
        var format = new MiiFormatWii();
        Assert.False(format.Validate(new byte[10]).IsValid);

        var badMagic = MiiFormatWii.CreateEmptyDatabase();
        badMagic[0] = (byte)'X';
        WriteCrc(badMagic, MiiFormatWii.ChecksumOffset);
        Assert.False(format.Validate(badMagic).IsValid);

        var badCrc = MiiFormatWii.CreateEmptyDatabase();
        badCrc[^1] ^= 1;
        Assert.False(format.Validate(badCrc).IsValid);

        var record = format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]);
        var database = format.Insert(MiiFormatWii.CreateEmptyDatabase(), record);
        Assert.Throws<InvalidDataException>(() => format.Insert(database, record));

        var deleteOnSight = (byte[])record.Clone();
        deleteOnSight[24] |= 0x20;
        Assert.Throws<InvalidDataException>(() => format.Insert(MiiFormatWii.CreateEmptyDatabase(), deleteOnSight));
    }

    [Fact]
    public void Wii_rejects_out_of_range_fields_even_with_valid_database_crc()
    {
        var format = new MiiFormatWii();
        var record = format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]);
        record[1] = 0x1E; // favorite color 15, maximum is 11
        var database = MiiFormatWii.CreateEmptyDatabase();
        record.CopyTo(database, MiiFormatWii.RecordsOffset);
        WriteCrc(database, MiiFormatWii.ChecksumOffset);

        Assert.False(format.Validate(database).IsValid);

        var badHeight = format.CreateBasicRecord("Sara", [9, 8, 7, 6, 5, 4, 3, 2]);
        badHeight[22] = 128;
        Assert.Throws<InvalidDataException>(() => format.Insert(MiiFormatWii.CreateEmptyDatabase(), badHeight));
    }

    [Fact]
    public void Wii_appearance_editor_updates_only_documented_fields_and_crc()
    {
        var format = new MiiFormatWii();
        var database = format.Insert(MiiFormatWii.CreateEmptyDatabase(),
            format.CreateBasicRecord("Mike", [1, 2, 3, 4, 5, 6, 7, 8]));
        var original = (byte[])database.Clone();

        var result = format.UpdateAppearance(database, 0,
            new MiiAppearance("Miker", true, 9, 55, 6, 4));

        Assert.Equal(new MiiAppearance("Miker", true, 9, 55, 6, 4), format.ReadAppearance(result, 0));
        Assert.True(format.Validate(result).IsValid);
        AssertOnlyChanged(original, result,
            Enumerable.Range(MiiFormatWii.RecordsOffset, 22)
                .Concat(Enumerable.Range(MiiFormatWii.RecordsOffset + 0x22, 2))
                .Concat(Enumerable.Range(MiiFormatWii.RecordsOffset + 0x28, 4))
                .Concat(Enumerable.Range(MiiFormatWii.ChecksumOffset, 2)).ToHashSet());
        Assert.Throws<ArgumentOutOfRangeException>(() => format.UpdateAppearance(database, 0,
            new MiiAppearance("Miker", false, 12, 0, 0, 0)));
    }

    [Fact]
    public void Wii_advanced_face_parts_roundtrip_through_the_real_record_fields()
    {
        var format = new MiiFormatWii();
        var database = format.Insert(MiiFormatWii.CreateEmptyDatabase(),
            format.CreateBasicRecord("Parts", [1, 2, 3, 4, 5, 6, 7, 8]));
        var input = new MiiAppearance("Parts", true, 7, 21, 3, 4)
        {
            HasAdvancedParts = true, Height = 80, Build = 44, HairFlip = 1,
            FaceType = 5, FaceColor = 5, FaceMakeup = 9,
            EyebrowType = 17, EyebrowColor = 6, EyebrowScale = 8, EyebrowPosition = 12, EyebrowSpacing = 3,
            EyeType = 31, EyeScale = 9, EyeRotate = 14, EyeSpacing = 5, EyePosition = 13,
            NoseType = 8, NoseScale = 7, NosePosition = 11,
            MouthType = 19, MouthColor = 2, MouthScale = 6, MouthPosition = 15,
            GlassesType = 4, GlassesColor = 5, GlassesScale = 7, GlassesPosition = 10,
            MustacheType = 2, BeardType = 1, BeardColor = 4, MustacheScale = 8, MustachePosition = 9,
            MoleType = 1, MoleScale = 6, MoleX = 7, MoleY = 14
        };

        var result = format.UpdateAppearance(database, 0, input);
        var actual = format.ReadAppearance(result, 0);

        Assert.True(format.Validate(result).IsValid);
        Assert.Equal(input.Height, actual.Height);
        Assert.Equal(input.Build, actual.Build);
        Assert.Equal(input.FaceType, actual.FaceType);
        Assert.Equal(input.EyebrowType, actual.EyebrowType);
        Assert.Equal(input.EyeType, actual.EyeType);
        Assert.Equal(input.EyeColor, actual.EyeColor);
        Assert.Equal(input.NoseType, actual.NoseType);
        Assert.Equal(input.MouthType, actual.MouthType);
        Assert.Equal(input.GlassesType, actual.GlassesType);
        Assert.Equal(input.MoleX, actual.MoleX);
        Assert.Equal(input.MoleY, actual.MoleY);
    }

    [Fact]
    public void Switch_empty_database_validation_is_byte_exact_and_non_mutating()
    {
        var format = new MiiFormatSwitch();
        var database = MiiFormatSwitch.CreateEmptyDatabase();
        var original = (byte[])database.Clone();

        var validation = format.Validate(database);

        Assert.True(validation.IsValid, validation.Error);
        Assert.Empty(validation.Slots);
        Assert.Equal(original, database);
        Assert.False(MiiFormatSwitch.WriteGateVerified);
    }

    [Fact]
    public void Switch_insert_changes_only_first_storedata_count_and_database_crc()
    {
        var format = new MiiFormatSwitch();
        var database = MiiFormatSwitch.CreateEmptyDatabase();
        var original = (byte[])database.Clone();
        var record = format.CreateBasicRecord("Mike", Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());

        var result = format.Insert(database, record);

        var validation = format.Validate(result);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal("Mike", validation.Slots.Single().Name);
        Assert.Equal(record, format.ExportRecord(result, 0));
        Assert.Equal(original, database);
        AssertOnlyChanged(database, result,
            Enumerable.Range(MiiFormatSwitch.RecordsOffset, MiiFormatSwitch.RecordSize)
                .Append(MiiFormatSwitch.CountOffset)
                .Concat(Enumerable.Range(MiiFormatSwitch.ChecksumOffset, 2)).ToHashSet());
    }

    [Fact]
    public void Switch_rejects_wrong_size_magic_version_count_database_crc_and_record_crc()
    {
        var format = new MiiFormatSwitch();
        Assert.False(format.Validate(new byte[88]).IsValid);

        var badMagic = MiiFormatSwitch.CreateEmptyDatabase();
        badMagic[0] = (byte)'X';
        WriteCrc(badMagic, MiiFormatSwitch.ChecksumOffset);
        Assert.False(format.Validate(badMagic).IsValid);

        var badVersion = MiiFormatSwitch.CreateEmptyDatabase();
        badVersion[MiiFormatSwitch.VersionOffset] = 2;
        WriteCrc(badVersion, MiiFormatSwitch.ChecksumOffset);
        Assert.False(format.Validate(badVersion).IsValid);

        var badCount = MiiFormatSwitch.CreateEmptyDatabase();
        badCount[MiiFormatSwitch.CountOffset] = 100;
        WriteCrc(badCount, MiiFormatSwitch.ChecksumOffset);
        Assert.False(format.Validate(badCount).IsValid);

        var badDatabaseCrc = MiiFormatSwitch.CreateEmptyDatabase();
        badDatabaseCrc[^1] ^= 1;
        Assert.False(format.Validate(badDatabaseCrc).IsValid);

        var record = format.CreateBasicRecord("Sara", Enumerable.Range(20, 16).Select(i => (byte)i).ToArray());
        record[0x40] ^= 1;
        Assert.Throws<InvalidDataException>(() => format.Insert(MiiFormatSwitch.CreateEmptyDatabase(), record));
    }

    [Fact]
    public void Switch_rejects_charinfo_duplicate_uuid_and_out_of_range_coredata()
    {
        var format = new MiiFormatSwitch();
        Assert.Throws<InvalidDataException>(() => format.Insert(MiiFormatSwitch.CreateEmptyDatabase(), new byte[88]));

        var identity = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var record = format.CreateBasicRecord("Mike", identity);
        var database = format.Insert(MiiFormatSwitch.CreateEmptyDatabase(), record);
        Assert.Throws<InvalidDataException>(() => format.Insert(database, record));

        var badField = format.CreateBasicRecord("Sara", Enumerable.Range(40, 16).Select(i => (byte)i).ToArray());
        var word5 = BinaryPrimitives.ReadUInt32LittleEndian(badField.AsSpan(20, 4));
        word5 = (word5 & ~(0xFu << 8)) | (12u << 8); // favorite color max is 11
        BinaryPrimitives.WriteUInt32LittleEndian(badField.AsSpan(20, 4), word5);
        WriteCrc(badField, 0x40);
        Assert.Throws<InvalidDataException>(() => format.Insert(MiiFormatSwitch.CreateEmptyDatabase(), badField));
    }

    [Fact]
    public void Switch_appearance_editor_updates_documented_storedata_fields_and_both_crcs()
    {
        var format = new MiiFormatSwitch();
        var database = format.Insert(MiiFormatSwitch.CreateEmptyDatabase(),
            format.CreateBasicRecord("Mike", Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()));
        var original = (byte[])database.Clone();

        var result = format.UpdateAppearance(database, 0,
            new MiiAppearance("Miker", true, 9, 120, 87, 66));

        Assert.Equal(new MiiAppearance("Miker", true, 9, 120, 87, 66), format.ReadAppearance(result, 0));
        Assert.True(format.Validate(result).IsValid);
        AssertOnlyChanged(original, result,
            new[]
            {
                MiiFormatSwitch.RecordsOffset, MiiFormatSwitch.RecordsOffset + 3,
                MiiFormatSwitch.RecordsOffset + 4, MiiFormatSwitch.RecordsOffset + 0x15
            }.Concat(Enumerable.Range(MiiFormatSwitch.RecordsOffset + 0x1C, 20))
                .Concat(Enumerable.Range(MiiFormatSwitch.RecordsOffset + 0x40, 2))
                .Concat(Enumerable.Range(MiiFormatSwitch.ChecksumOffset, 2)).ToHashSet());
        Assert.Throws<ArgumentOutOfRangeException>(() => format.UpdateAppearance(database, 0,
            new MiiAppearance("Miker", false, 0, 132, 0, 0)));
    }

    [Fact]
    public void Switch_advanced_face_parts_roundtrip_through_core_data()
    {
        var format = new MiiFormatSwitch();
        var database = format.Insert(MiiFormatSwitch.CreateEmptyDatabase(),
            format.CreateBasicRecord("Parts", Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()));
        var input = new MiiAppearance("Parts", true, 9, 120, 87, 66)
        {
            HasAdvancedParts = true, Height = 80, Build = 44, HairFlip = 1,
            FaceType = 11, FaceColor = 8, FaceWrinkle = 4, FaceMakeup = 7,
            EyebrowType = 17, EyebrowColor = 88, EyebrowScale = 8, EyebrowAspect = 4,
            EyebrowRotate = 9, EyebrowSpacing = 7, EyebrowPosition = 12,
            EyeType = 31, EyeScale = 5, EyeAspect = 4, EyeRotate = 6, EyeSpacing = 9, EyePosition = 13,
            NoseType = 8, NoseScale = 7, NosePosition = 11,
            MouthType = 19, MouthColor = 2, MouthScale = 6, MouthAspect = 5, MouthPosition = 15,
            GlassesType = 4, GlassesColor = 55, GlassesScale = 7, GlassesPosition = 10,
            MustacheType = 2, BeardType = 5, BeardColor = 44, MustacheScale = 8, MustachePosition = 9,
            MoleType = 1, MoleScale = 6, MoleX = 7, MoleY = 14
        };

        var result = format.UpdateAppearance(database, 0, input);
        var actual = format.ReadAppearance(result, 0);

        Assert.True(format.Validate(result).IsValid);
        Assert.Equal(input.IsFemale, actual.IsFemale);
        Assert.Equal(input.HairColor, actual.HairColor);
        Assert.Equal(input.FaceType, actual.FaceType);
        Assert.Equal(input.EyebrowType, actual.EyebrowType);
        Assert.Equal(input.EyeType, actual.EyeType);
        Assert.Equal(input.EyeColor, actual.EyeColor);
        Assert.Equal(input.NoseType, actual.NoseType);
        Assert.Equal(input.MouthType, actual.MouthType);
        Assert.Equal(input.GlassesType, actual.GlassesType);
        Assert.Equal(input.MoleX, actual.MoleX);
        Assert.Equal(input.MoleY, actual.MoleY);
    }

    [Theory]
    [InlineData("Mike")]
    [InlineData("Sara")]
    public void Basic_templates_roundtrip_in_both_formats(string name)
    {
        var wii = new MiiFormatWii();
        var wiiIdentity = Encoding.ASCII.GetBytes(name.PadRight(8, '_'));
        var wiiDb = wii.Insert(MiiFormatWii.CreateEmptyDatabase(), wii.CreateBasicRecord(name, wiiIdentity));
        Assert.Equal(name, wii.Validate(wiiDb).Slots.Single().Name);

        var eden = new MiiFormatSwitch();
        var edenIdentity = Enumerable.Range(name == "Mike" ? 1 : 21, 16).Select(i => (byte)i).ToArray();
        var edenDb = eden.Insert(MiiFormatSwitch.CreateEmptyDatabase(), eden.CreateBasicRecord(name, edenIdentity));
        Assert.Equal(name, eden.Validate(edenDb).Slots.Single().Name);
    }

    private static void WriteCrc(Span<byte> bytes, int checksumOffset) =>
        MiiCrc16.WriteBigEndian(bytes.Slice(checksumOffset, 2), MiiCrc16.Compute(bytes.Slice(0, checksumOffset)));

    private static void AssertOnlyChanged(byte[] before, byte[] after, HashSet<int> allowed)
    {
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
            if (before[i] != after[i])
                Assert.Contains(i, allowed);
    }
}
