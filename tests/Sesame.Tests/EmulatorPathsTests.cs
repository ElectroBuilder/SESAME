using System.Text.Json;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Tests;

public sealed class EmulatorPathsTests
{
    [Fact]
    public void Derived_paths_follow_roms_root_and_override_roundtrips()
    {
        var paths = LibraryPaths.Current;
        var oldRoot = paths.RomsRoot;
        var oldOverrides = paths.EmulatorOverrides;
        try
        {
            paths.RomsRoot = "/mnt/card/Emulation/roms";
            paths.EmulatorOverrides = new(StringComparer.OrdinalIgnoreCase);
            Assert.Equal("/mnt/card/Emulation/roms/wii", EmulatorPaths.RomFolder("wii"));
            Assert.Equal("/mnt/card/Emulation/storage/dolphin-emu/Load/Textures",
                EmulatorPaths.TexturesRoot("dolphin"));
            EmulatorPaths.Overrides("dolphin").TexturesRoot = "/custom/dolphin/textures/";
            var json = JsonSerializer.Serialize(paths);
            var loaded = JsonSerializer.Deserialize<LibraryPaths>(json)!;
            Assert.Equal("/custom/dolphin/textures/", loaded.EmulatorOverrides["dolphin"].TexturesRoot);
        }
        finally
        {
            paths.RomsRoot = oldRoot;
            paths.EmulatorOverrides = oldOverrides;
        }
    }

    [Theory]
    [InlineData("wii", "RMCP01", "RMCP01")]
    [InlineData("gc", "GZLE01", "GZLE01")]
    [InlineData("ps1", "SCUS-94426", "SCUS-94426")]
    [InlineData("ps2", "SLUS_203.12", "SLUS-20312")]
    public void Platform_ids_are_system_specific(string system, string input, string expected)
    {
        Assert.True(PlatformId.TryCreate(system, input, out var id));
        Assert.Equal(StoreGame.FoldSystem(system), id.System);
        Assert.Equal(expected, id.Value);
    }

    [Theory]
    [InlineData("wii", "0100ABCD12345678")]
    [InlineData("ps2", "../../escape")]
    [InlineData("switch", "0100ABCD12345678")]
    [InlineData("gc", "TOO-LONG")]
    public void Invalid_or_wrong_type_ids_are_rejected(string system, string input) =>
        Assert.False(PlatformId.TryCreate(system, input, out _));

    [Theory]
    [InlineData("gc", "Zelda [GZLE01].rvz", "GZLE01")]
    [InlineData("ps2", "Game (SLUS-20312).iso", "SLUS-20312")]
    public void Trusted_library_file_metadata_can_supply_game_id(string system, string file, string expected)
    {
        Assert.True(PlatformId.TryExtractLibraryMetadata(system, file, out var id));
        Assert.Equal(expected, id.Value);
    }

    [Fact]
    public void Switch_resolver_keeps_existing_library_path_contract()
    {
        foreach (var emulator in new[] { "eden", "yuzu", "ryujinx", "citron" })
        {
            Assert.Equal(LibraryPaths.Current.SwitchMods(emulator), EmulatorPaths.ModsRoot(emulator));
            Assert.Equal(LibraryPaths.Current.SwitchSaves(emulator), EmulatorPaths.SavesRoot(emulator));
        }
    }

    [Fact]
    public void Old_library_document_without_emulator_fields_migrates_non_destructively()
    {
        var loaded = JsonSerializer.Deserialize<LibraryPaths>("""
            { "RomsRoot": "/mnt/legacy/Emulation/roms", "UseEden": true }
            """)!;
        Assert.Equal("/mnt/legacy/Emulation/roms", loaded.RomsRoot);
        Assert.NotNull(loaded.EmulatorOverrides);
        Assert.Empty(loaded.EmulatorOverrides);
    }

    [Fact]
    public void Catalog_quick_paths_n64_cache_and_scan_roots_follow_only_library_paths()
    {
        var paths = LibraryPaths.Current;
        var oldRoot = paths.RomsRoot;
        try
        {
            paths.RomsRoot = "/mnt/card/Emulation/roms";
            var catalog = new AppCatalog();
            Assert.Contains(catalog.EffectiveQuickAccess(),
                path => path.Name == "N64" && path.Path == "/mnt/card/Emulation/roms/n64");
            Assert.Equal("/mnt/card/Emulation/bios/Mupen64plus/cache",
                AppCatalog.RelocateKnownPath(catalog.InstallRoutes[".hts"]));
            Assert.Equal("/mnt/card/Emulation/bios/Mupen64plus/cache",
                AppCatalog.RelocateKnownPath(catalog.InstallRoutes[".htc"]));
            Assert.Equal(new[] { "/mnt/card/Emulation/roms" }, RomScan.ConfiguredRootsForTests(catalog));
        }
        finally { paths.RomsRoot = oldRoot; }
    }

    [Theory]
    [InlineData("wii", "dolphin")]
    [InlineData("gc", "dolphin")]
    [InlineData("ps1", "duckstation")]
    [InlineData("ps2", "pcsx2")]
    public void Disc_game_without_validated_id_does_not_claim_shared_texture_root(string system, string emulator)
    {
        var catalog = new AppCatalog();
        Assert.Null(GameLibrary.TexturePathFor("Unknown", system, null, catalog, null));
        Assert.NotNull(EmulatorPaths.TexturesRoot(emulator));
    }
}
