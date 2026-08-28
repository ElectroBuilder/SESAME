using System.IO;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public sealed class SystemProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public int Fps { get; init; } = 60;
    public int Refresh { get; init; } = 60;
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public IReadOnlyList<string> Emulators { get; init; } = [];
    public IReadOnlyList<string> Cores { get; init; } = [];
    public string LibretroThumbs { get; init; } = "";
}

public static class SystemCatalog
{
    public static readonly SystemProfile Hydra = P("hydra", "Hydra", "Hydra", 60,
        ["hydra"], [], ["hydra"], []);
    public static readonly SystemProfile App = P("app", "Apps", "Apps", 60,
        ["apps", "app"], [], ["app"], []);

    public static IReadOnlyList<SystemProfile> Extra { get; } = [Hydra, App];

    public static IReadOnlyList<SystemProfile> All { get; } =
    [
        P("nes", "NES", "Nintendo Entertainment System", 60,
            ["nes", "famicom", "fds"],
            [".nes", ".unf", ".unif", ".fds", ".zip", ".7z"],
            ["retroarch"],
            ["nestopia_libretro.so", "fceumm_libretro.so", "mesen_libretro.so"],
            "Nintendo - Nintendo Entertainment System"),
        P("snes", "SNES", "Super Nintendo", 60,
            ["snes", "sfc", "sneshd"],
            [".sfc", ".smc", ".fig", ".swc", ".zip", ".7z"],
            ["retroarch"],
            ["snes9x_libretro.so", "bsnes_libretro.so", "mesen-s_libretro.so"],
            "Nintendo - Super Nintendo Entertainment System"),
        P("n64", "Nintendo 64", "Nintendo 64", 60,
            ["n64", "nintendo64"],
            [".z64", ".n64", ".v64", ".zip", ".7z"],
            ["retroarch"],
            ["mupen64plus_next_libretro.so", "parallel_n64_libretro.so"],
            "Nintendo - Nintendo 64"),
        P("gc", "GameCube", "Nintendo GameCube", 60,
            ["gc", "gamecube", "ngc"],
            [".iso", ".gcm", ".rvz", ".ciso", ".gcz", ".wia"],
            ["dolphin"],
            [],
            "Nintendo - GameCube"),
        P("wii", "Wii", "Nintendo Wii", 60,
            ["wii"],
            [".iso", ".wbfs", ".rvz", ".gcz", ".wia", ".dol", ".wad"],
            ["dolphin"],
            [],
            "Nintendo - Wii"),
        P("wiiu", "Wii U", "Nintendo Wii U", 30,
            ["wiiu"],
            [".wua", ".wud", ".wux", ".rpx", ".iso"],
            ["cemu"],
            []),
        P("switch", "Nintendo Switch", "Nintendo Switch", 60,
            ["switch", "nsw", "yuzu", "eden"],
            [".nsp", ".xci", ".nca"],
            ["eden", "yuzu", "ryujinx", "citron"],
            []),
        P("gb", "Game Boy", "Game Boy", 60,
            ["gb", "gameboy"],
            [".gb", ".zip", ".7z"],
            ["retroarch"],
            ["gambatte_libretro.so", "sameboy_libretro.so", "mgba_libretro.so"],
            "Nintendo - Game Boy"),
        P("gbc", "Game Boy Color", "Game Boy Color", 60,
            ["gbc", "gbcolor"],
            [".gbc", ".zip", ".7z"],
            ["retroarch"],
            ["gambatte_libretro.so", "sameboy_libretro.so", "mgba_libretro.so"],
            "Nintendo - Game Boy Color"),
        P("gba", "Game Boy Advance", "Game Boy Advance", 60,
            ["gba", "agb"],
            [".gba", ".agb", ".zip", ".7z"],
            ["retroarch"],
            ["mgba_libretro.so", "vba_next_libretro.so", "vbam_libretro.so"],
            "Nintendo - Game Boy Advance"),
        P("nds", "Nintendo DS", "Nintendo DS", 60,
            ["nds", "ds"],
            [".nds", ".zip", ".7z"],
            ["retroarch", "drastic"],
            ["melonds_libretro.so", "desmume_libretro.so"],
            "Nintendo - Nintendo DS"),
        P("3ds", "Nintendo 3DS", "Nintendo 3DS", 30,
            ["3ds", "n3ds", "citra"],
            [".3ds", ".cia", ".cxi", ".3dsx", ".app"],
            ["azahar", "lime3ds", "citra"],
            [],
            "Nintendo - Nintendo 3DS"),
        P("genesis", "Genesis", "Sega Genesis", 60,
            ["genesis", "megadrive", "md", "gen"],
            [".md", ".gen", ".smd", ".bin", ".zip", ".7z"],
            ["retroarch"],
            ["genesis_plus_gx_libretro.so", "picodrive_libretro.so"],
            "Sega - Mega Drive - Genesis"),
        P("sms", "Master System", "Sega Master System", 60,
            ["mastersystem", "sms", "ms"],
            [".sms", ".zip", ".7z"],
            ["retroarch"],
            ["genesis_plus_gx_libretro.so"],
            "Sega - Master System - Mark III"),
        P("saturn", "Saturn", "Sega Saturn", 60,
            ["saturn", "segasaturn"],
            [".cue", ".chd", ".iso", ".m3u"],
            ["retroarch"],
            ["mednafen_saturn_libretro.so", "beetle_saturn_libretro.so"],
            "Sega - Saturn"),
        P("dc", "Dreamcast", "Sega Dreamcast", 60,
            ["dreamcast", "dc"],
            [".gdi", ".cdi", ".chd", ".cue", ".m3u"],
            ["flycast", "retroarch"],
            ["flycast_libretro.so"],
            "Sega - Dreamcast"),
        P("ps1", "PlayStation", "Sony PlayStation", 60,
            ["psx", "ps1", "playstation", "ps"],
            [".cue", ".chd", ".m3u", ".pbp", ".iso", ".img", ".bin", ".ccd"],
            ["duckstation", "retroarch"],
            ["pcsx_rearmed_libretro.so", "swanstation_libretro.so", "mednafen_psx_hw_libretro.so"],
            "Sony - PlayStation"),
        P("ps2", "PlayStation 2", "Sony PlayStation 2", 60,
            ["ps2", "playstation2"],
            [".iso", ".chd", ".m3u", ".gz", ".cso", ".zso", ".bin"],
            ["pcsx2"],
            [],
            "Sony - PlayStation 2"),
        P("psp", "PSP", "Sony PSP", 60,
            ["psp"],
            [".iso", ".cso", ".pbp", ".chd"],
            ["ppsspp", "retroarch"],
            ["ppsspp_libretro.so"],
            "Sony - PlayStation Portable"),
        P("vita", "PlayStation Vita", "PlayStation Vita", 30,
            ["psvita", "vita"],
            [".vpk"],
            ["vita3k"],
            []),
        P("arcade", "Arcade", "Arcade", 60,
            ["arcade", "mame", "fbneo", "fba"],
            [".zip", ".7z"],
            ["retroarch", "mame"],
            ["fbneo_libretro.so", "mame_libretro.so", "mame2003_plus_libretro.so"],
            "MAME"),
        P("xbox", "Xbox", "Original Xbox", 60,
            ["xbox"],
            [".iso", ".xiso"],
            ["xemu"],
            [])
    ];

    private static readonly Dictionary<string, SystemProfile> ByFolder =
        All.SelectMany(p => p.Folders.Select(f => (f, p)))
            .ToDictionary(x => x.f, x => x.p, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SystemProfile> ByExt =
        All.SelectMany(p => p.Extensions.Select(e => (e, p)))
            .GroupBy(x => x.e, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().p, StringComparer.OrdinalIgnoreCase);

    public static SystemProfile? FromFolder(string folder)
    {
        var key = folder.Trim().TrimEnd('/', '\\');
        var name = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
        if (ByFolder.TryGetValue(name, out var hit)) return hit;
        var extra = Extra.FirstOrDefault(p =>
            p.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            p.Folders.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (extra is not null) return extra;
        var slug = StoreGame.FoldSystem(name);
        return All.FirstOrDefault(p => p.Id == slug || p.Folders.Any(f =>
            string.Equals(StoreGame.FoldSystem(f), slug, StringComparison.OrdinalIgnoreCase)));
    }

    public static SystemProfile? FromExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length == 0 ? null : ByExt.GetValueOrDefault(ext);
    }

    public static SystemProfile Resolve(string folder, string fileName) =>
        FromFolder(folder) ?? FromExtension(fileName) ?? Unknown(folder);

    public static SystemProfile Unknown(string folder)
    {
        var name = (folder ?? "").Trim().TrimEnd('/', '\\');
        if (name.Contains('/')) name = name[(name.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(name)) name = "roms";
        var id = StoreGame.FoldSystem(name);
        if (string.IsNullOrEmpty(id)) id = name.ToLowerInvariant();
        return new SystemProfile
        {
            Id = id,
            Name = name.Length <= 5 ? name.ToUpperInvariant() : char.ToUpper(name[0]) + name[1..],
            Category = name,
            Folders = [name.ToLowerInvariant()],
            Extensions = [],
            Emulators = [id, "retroarch"],
            Cores = []
        };
    }

    private static SystemProfile P(string id, string name, string category, int fps,
        string[] folders, string[] extensions, string[] emulators, string[] cores,
        string thumbs = "") => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Fps = fps,
        Refresh = fps >= 60 ? 60 : fps,
        Folders = folders,
        Extensions = extensions,
        Emulators = emulators,
        Cores = cores,
        LibretroThumbs = thumbs
    };
}
