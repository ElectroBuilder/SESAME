using System.IO.Compression;
using Sesame.Models;
using Sesame.Services;

namespace Sesame.Tests;

public sealed class StoreRoutingTests
{
    [Fact]
    public void Explicit_selected_system_wins_over_conflicting_hit_metadata()
    {
        var catalog = new AppCatalog();
        var selected = new StoreGame { Name = "Selected Wii game", System = "WII" };
        var hit = new PackHit { Title = "Wrong metadata", Platform = "PS2" };
        Assert.Equal("WII", PackStore.ResolveSystem(hit, catalog, selected));
    }

    [Fact]
    public void Unknown_dolphin_id_is_staged_and_disc_save_is_unsupported()
    {
        var texture = new PackHit { Title = "HD Pack", Kind = "Texture pack", Platform = "WII" };
        var staged = DiscPackRouting.Plan(texture, "WII", null);
        Assert.Equal(PackActivationState.Staged, staged.State);
        Assert.Contains("/_incoming/HD Pack", staged.Destination);
        var save = new PackHit { Title = "Save", Kind = "Save", Platform = "GC" };
        Assert.Equal(PackActivationState.Unsupported, DiscPackRouting.Plan(save, "GC", null).State);
    }

    [Fact]
    public void Known_dolphin_id_with_texture_payload_is_active()
    {
        Assert.True(PlatformId.TryCreate("gc", "GZLE01", out var id));
        var root = NewTemp();
        try
        {
            var payload = Path.Combine(root, "GZLE01");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "texture.png"), "x");
            var hit = new PackHit { Title = "HD Pack", Kind = "Texture pack", Platform = "GC" };
            var route = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "GC", id), root, hit.Title);
            Assert.Equal(PackActivationState.Active, route.State);
            Assert.Equal(payload, route.PreparedPayload);
            Assert.EndsWith("/Load/Textures/GZLE01", route.Destination);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dolphin_archive_id_cannot_override_selected_game_id()
    {
        Assert.True(PlatformId.TryCreate("gc", "GZLE01", out var selected));
        var root = NewTemp();
        try
        {
            var wrong = Path.Combine(root, "GM8E01");
            Directory.CreateDirectory(wrong);
            File.WriteAllText(Path.Combine(wrong, "texture.png"), "x");
            var hit = new PackHit { Title = "HD Pack", Kind = "Texture pack", Platform = "GC" };
            var route = DiscPackRouting.ValidatePreparedLayout(
                DiscPackRouting.Plan(hit, "GC", selected), root, hit.Title);
            Assert.Equal(PackActivationState.Staged, route.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pcsx2_accepts_serial_replacements_without_crc_suffix()
    {
        Assert.True(PlatformId.TryCreate("ps2", "SLUS-20312", out var id));
        var root = NewTemp();
        try
        {
            var replacements = Path.Combine(root, "SLUS-20312", "replacements");
            Directory.CreateDirectory(replacements);
            File.WriteAllText(Path.Combine(replacements, "tex.png"), "x");
            var hit = new PackHit { Title = "Textures", Kind = "Texture pack", Platform = "PS2" };
            var route = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS2", id), root, hit.Title);
            Assert.Equal(PackActivationState.Active, route.State);
            Assert.Equal(Path.Combine(root, "SLUS-20312"), route.PreparedPayload);
            Assert.EndsWith("/storage/pcsx2/textures/SLUS-20312", route.Destination);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pcsx2_crc_suffix_or_raw_images_are_staged()
    {
        Assert.True(PlatformId.TryCreate("ps2", "SLUS-20312", out var id));
        foreach (var folder in new[] { "SLUS-20312_2A84A1E2/replacements", "raw" })
        {
            var root = NewTemp();
            try
            {
                var payload = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(payload);
                File.WriteAllText(Path.Combine(payload, "tex.png"), "x");
                var hit = new PackHit { Title = "Textures", Kind = "Texture pack", Platform = "PS2" };
                var route = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS2", id), root, hit.Title);
                Assert.Equal(PackActivationState.Staged, route.State);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void Pcsx2_unknown_id_is_staged_and_archive_serial_never_selects_target()
    {
        var hit = new PackHit { Title = "Textures", Kind = "Texture pack", Platform = "PS2" };
        var route = DiscPackRouting.Plan(hit, "PS2", null);
        Assert.Equal(PackActivationState.Staged, route.State);
        Assert.Contains("/_incoming/Textures", route.Destination);
    }

    [Theory]
    [InlineData("ps1", "SCUS-94426", "SLUS-00001")]
    [InlineData("ps2", "SLUS-20312", "SLES-50000")]
    public void Conflicting_archive_serial_is_staged(string system, string selected, string archived)
    {
        Assert.True(PlatformId.TryCreate(system, selected, out var id));
        var root = NewTemp();
        try
        {
            var replacements = Path.Combine(root, archived, "replacements");
            Directory.CreateDirectory(replacements);
            File.WriteAllText(Path.Combine(replacements, "tex.png"), "x");
            var hit = new PackHit { Title = "Textures", Kind = "Texture pack", Platform = system };
            var route = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, system, id), root, hit.Title);
            Assert.Equal(PackActivationState.Staged, route.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Raw_cheat_uploads_only_itself_and_mixed_invalid_names_are_staged()
    {
        var root = NewTemp();
        try
        {
            var raw = Path.Combine(root, "1234ABCD.pnach");
            File.WriteAllText(raw, "patch=1");
            var hit = new PackHit { Title = "Cheat", Kind = "Mod", Platform = "PS2" };
            var active = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS2", null), raw, hit.Title);
            Assert.Equal(PackActivationState.Active, active.State);
            Assert.Equal(raw, active.PreparedPayload);

            File.WriteAllText(Path.Combine(root, "not-a-crc.pnach"), "patch=1");
            var staged = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS2", null), root, hit.Title);
            Assert.Equal(PackActivationState.Staged, staged.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Raw_duckstation_cheat_uploads_only_the_valid_serial_file()
    {
        var root = NewTemp();
        try
        {
            var raw = Path.Combine(root, "SCUS-94426.cht");
            File.WriteAllText(raw, "cheat");
            var hit = new PackHit { Title = "Cheat", Kind = "Mod", Platform = "PS1" };
            var active = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS1", null), raw, hit.Title);
            Assert.Equal(PackActivationState.Active, active.State);
            Assert.Equal(raw, active.PreparedPayload);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Exact_file_plans_do_not_double_nest_disc_layouts()
    {
        var root = NewTemp();
        try
        {
            var dolphin = Path.Combine(root, "GZLE01");
            Directory.CreateDirectory(dolphin);
            File.WriteAllText(Path.Combine(dolphin, "tex.png"), "x");
            var dolphinPlan = PackOwnershipPlanner.Build([
                new PackPayloadSource(dolphin, "/dolphin/Load/Textures/GZLE01", true)]);
            Assert.Equal("/dolphin/Load/Textures/GZLE01/tex.png", Assert.Single(dolphinPlan).RemotePath);

            var serial = Path.Combine(root, "SLUS-20312");
            var replacements = Path.Combine(serial, "replacements");
            Directory.CreateDirectory(replacements);
            File.WriteAllText(Path.Combine(replacements, "tex.png"), "x");
            var ps2Plan = PackOwnershipPlanner.Build([
                new PackPayloadSource(serial, "/pcsx2/textures/SLUS-20312", true)]);
            Assert.Equal("/pcsx2/textures/SLUS-20312/replacements/tex.png",
                Assert.Single(ps2Plan).RemotePath);

            var duckPlan = PackOwnershipPlanner.Build([
                new PackPayloadSource(serial, "/duckstation/textures/SLUS-20312", true)]);
            Assert.Equal("/duckstation/textures/SLUS-20312/replacements/tex.png",
                Assert.Single(duckPlan).RemotePath);

            var graphic = Path.Combine(root, "graphic");
            Directory.CreateDirectory(Path.Combine(graphic, "assets"));
            File.WriteAllText(Path.Combine(graphic, "metadata.json"), "{}");
            File.WriteAllText(Path.Combine(graphic, "assets", "code.bin"), "x");
            var graphicPlan = PackOwnershipPlanner.Build([
                new PackPayloadSource(graphic, "/dolphin/Load/GraphicMods/My Pack", true)]);
            Assert.Contains(graphicPlan,
                file => file.RemotePath == "/dolphin/Load/GraphicMods/My Pack/metadata.json");
            Assert.Contains(graphicPlan,
                file => file.RemotePath == "/dolphin/Load/GraphicMods/My Pack/assets/code.bin");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Exact_file_plan_rejects_two_payloads_mapping_to_one_remote_file()
    {
        var root = NewTemp();
        try
        {
            var one = Path.Combine(root, "one", "same.bin");
            var two = Path.Combine(root, "two", "same.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(one)!);
            Directory.CreateDirectory(Path.GetDirectoryName(two)!);
            File.WriteAllText(one, "1");
            File.WriteAllText(two, "2");
            Assert.Throws<InvalidDataException>(() => PackOwnershipPlanner.Build([
                new PackPayloadSource(one, "/shared", false),
                new PackPayloadSource(two, "/shared", false)]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Runtime_archive_byte_limit_counts_streamed_bytes()
    {
        using var input = new MemoryStream(new byte[9]);
        using var output = new MemoryStream();
        long copied = 0;
        Assert.Throws<InvalidDataException>(() => PackStore.CopyWithExpandedLimit(input, output, ref copied, 8));
        Assert.True(copied <= 8);
    }

    [Fact]
    public void Cache_roundtrip_preserves_validated_id_system_over_conflicting_platform()
    {
        Assert.True(PlatformId.TryCreate("ps2", "SLUS-20312", out var id));
        var cached = StoreResultCache.ToCached(new PackHit { Platform = "WII", GameId = id });
        Assert.Equal("ps2", cached.GameIdSystem);
        var hit = StoreResultCache.ToHit(cached);
        Assert.Equal(id, hit.GameId);
        Assert.Equal("WII", hit.Platform);
    }

    [Fact]
    public void Ownership_inference_only_allows_legacy_isolated_switch_folder()
    {
        var safe = new ModRecord
        {
            System = "SWITCH", TitleId = "0100123412340000",
            RemotePath = "/storage/eden/load/0100123412340000/My Mod"
        };
        var shared = new ModRecord
        {
            System = "PS2", RemotePath = "/storage/pcsx2/textures"
        };
        Assert.Equal(PackOwnershipKind.IsolatedDirectory, ModLibrary.EffectiveOwnership(safe));
        Assert.Equal(PackOwnershipKind.Unknown, ModLibrary.EffectiveOwnership(shared));
    }

    [Fact]
    public void Duckstation_requires_serial_replacements_and_rejects_raw_images()
    {
        Assert.True(PlatformId.TryCreate("ps1", "SCUS-94426", out var id));
        var root = NewTemp();
        try
        {
            var replacements = Path.Combine(root, "SCUS-94426", "replacements");
            Directory.CreateDirectory(replacements);
            File.WriteAllText(Path.Combine(replacements, "tex.png"), "x");
            var hit = new PackHit { Title = "Textures", Kind = "Texture pack", Platform = "PS1" };
            var route = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS1", id), root, hit.Title);
            Assert.Equal(PackActivationState.Active, route.State);
            Assert.Equal(Path.Combine(root, "SCUS-94426"), route.PreparedPayload);
            Directory.Delete(Path.Combine(root, "SCUS-94426"), true);
            File.WriteAllText(Path.Combine(root, "raw.png"), "x");
            var raw = DiscPackRouting.ValidatePreparedLayout(DiscPackRouting.Plan(hit, "PS1", id), root, hit.Title);
            Assert.Equal(PackActivationState.Staged, raw.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Archive_path_traversal_is_rejected()
    {
        var root = NewTemp();
        var archive = Path.Combine(root, "hostile.zip");
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../escape.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("nope");
            }
            Assert.Throws<InvalidDataException>(() => PackStore.PrepareUploadFolder(archive));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Archive_symbolic_link_is_rejected()
    {
        var root = NewTemp();
        var archive = Path.Combine(root, "symlink.zip");
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("link");
                entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write("../escape");
            }
            Assert.Throws<InvalidDataException>(() => PackStore.PrepareUploadFolder(archive));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Archive_absolute_path_and_duplicate_target_are_rejected()
    {
        var root = NewTemp();
        try
        {
            var absolute = Path.Combine(root, "absolute.zip");
            using (var zip = ZipFile.Open(absolute, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("/escape.txt").Open());
                writer.Write("nope");
            }
            Assert.Throws<InvalidDataException>(() => PackStore.PrepareUploadFolder(absolute));

            var duplicate = Path.Combine(root, "duplicate.zip");
            using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
            {
                using (var first = new StreamWriter(zip.CreateEntry("same.txt").Open())) first.Write("one");
                using (var second = new StreamWriter(zip.CreateEntry("same.txt").Open())) second.Write("two");
            }
            Assert.ThrowsAny<IOException>(() => PackStore.PrepareUploadFolder(duplicate));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Extracted_tree_postcheck_rejects_reparse_or_symbolic_link_when_supported()
    {
        var root = NewTemp();
        var outside = NewTemp();
        try
        {
            var link = Path.Combine(root, "outside-link");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // Windows without Developer Mode cannot create the fixture.
            }
            Assert.Throws<InvalidDataException>(() => PackStore.ValidateExtractedTreeForTests(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public void Ambiguous_disc_extensions_have_no_catalog_route()
    {
        var catalog = new AppCatalog();
        foreach (var extension in new[] { ".iso", ".bin", ".img", ".chd", ".rvz", ".ciso" })
            Assert.False(catalog.InstallRoutes.ContainsKey(extension));
        Assert.True(catalog.InstallRoutes.ContainsKey(".wbfs"));
        Assert.True(catalog.InstallRoutes.ContainsKey(".gcm"));
        Assert.True(catalog.InstallRoutes.ContainsKey(".z64"));
    }

    private static string NewTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), "sesame-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
