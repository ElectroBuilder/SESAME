using System.IO;
using System.Reflection;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

/// <summary>
/// One Game Mode shortcut for SESAME itself, always pointing at the installed
/// binary, with grid artwork. install.sh used to call steamos-add-to-steam on
/// every update, which stacked extra copies without covers.
/// </summary>
public static class SteamSelfShortcut
{
    public const string AppName = "SESAME";
    public const string ShortcutPath = "SESAME-APP";

    public static void Ensure(DeckClient client)
    {
        var exe = LauncherPath(client);
        if (string.IsNullOrEmpty(exe) || !client.Exists(exe)) return;

        var configs = SteamShortcuts.FindUserConfigs(client);
        if (configs.Count == 0) return;

        var shortcuts = SteamShortcuts.LoadAll(client, configs);
        shortcuts.RemoveAll(SteamShortcuts.IsSesameLauncher);

        var item = new SteamShortcut
        {
            AppId = SteamCrc.ShortcutId(SteamCrc.Quote(exe), AppName),
            AppName = AppName,
            Exe = SteamCrc.Quote(exe),
            StartDir = SteamCrc.Quote((Path.GetDirectoryName(exe) ?? "/").Replace('\\', '/') + "/"),
            LaunchOptions = "--gamemode",
            ShortcutPath = ShortcutPath,
            AllowDesktopConfig = 1,
            Tags = { "Apps" }
        };
        shortcuts.Add(item);

        foreach (var config in configs)
        {
            SteamShortcuts.Save(client, config, shortcuts);
            WriteArtwork(client, DeckClient.Combine(config, "grid"), item.AppId);
        }
    }

    public static void TryEnsure(DeckClient client)
    {
        try
        {
            if (SteamSession.IsSteamRunning(client))
            {
                WriteArtworkForExisting(client);
                return;
            }

            Ensure(client);
        }
        catch
        {
            /* Game Mode shortcut is optional until the next optimize */
        }
    }

    private static void WriteArtworkForExisting(DeckClient client)
    {
        var configs = SteamShortcuts.FindUserConfigs(client);
        if (configs.Count == 0) return;
        var shortcuts = SteamShortcuts.LoadAll(client, configs);
        foreach (var item in shortcuts.Where(SteamShortcuts.IsSesameLauncher))
        {
            if (item.AppId == 0) continue;
            foreach (var config in configs)
                WriteArtwork(client, DeckClient.Combine(config, "grid"), item.AppId);
        }
    }

    private static void WriteArtwork(DeckClient client, string gridDir, uint appId)
    {
        client.EnsureDirectory(gridDir);
        var icon = LoadIcon();
        if (icon is not { Length: > 0 }) return;
        var portrait = CoverMask.Portrait(icon, SystemCatalog.App);
        var landscape = CoverMask.Landscape(icon, SystemCatalog.App);
        var id = appId.ToString();
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + "p.png"), portrait);
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + "_p.png"), portrait);
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + ".png"), landscape);
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + "_hero.png"), landscape);
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + "_icon.png"), portrait);
        WriteIfPresent(client, DeckClient.Combine(gridDir, id + "_logo.png"), icon);
    }

    private static void WriteIfPresent(DeckClient client, string path, byte[]? data)
    {
        if (data is not { Length: > 0 }) return;
        client.WriteBytes(path, data);
    }

    public static string LauncherPath(DeckClient client)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var process = Environment.ProcessPath?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(process) &&
                    File.Exists(process) &&
                    Path.GetFileName(process).Equals("SESAME", StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                /* fall through to install dir */
            }
        }

        var dest = DeckClient.Combine(DeckClient.Combine(DeckClient.Combine(client.Home, "Applications"), "SESAME"), "SESAME");
        return dest;
    }

    private static byte[]? LoadIcon()
    {
        foreach (var name in new[]
                 {
                     "Sesame.Assets.sesame-icon-lg.png",
                     "Sesame.Assets.sesame-icon.png"
                 })
        {
            using var stream = typeof(SteamSelfShortcut).Assembly.GetManifestResourceStream(name)
                               ?? Assembly.GetEntryAssembly()?.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            if (ms.Length > 0) return ms.ToArray();
        }

        foreach (var file in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Assets", "sesame.png"),
                     Path.Combine(AppContext.BaseDirectory, "Assets", "sesame-icon-lg.png"),
                     Path.Combine(AppContext.BaseDirectory, "sesame.png")
                 })
        {
            if (File.Exists(file))
                return File.ReadAllBytes(file);
        }

        return null;
    }
}
