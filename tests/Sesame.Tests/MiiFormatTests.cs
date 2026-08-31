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
