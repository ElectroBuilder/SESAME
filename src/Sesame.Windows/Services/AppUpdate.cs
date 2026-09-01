using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using Sesame;

namespace Sesame.Services;

public sealed class AppRelease
{
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Notes { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool IsNewer { get; set; }
}

public static class AppUpdate
{
    public const string Repo = "ElectroBuilder/SESAME";
    private static readonly HttpClient Http = Create();

    // Windows-only resources such as the native FFL helper can register a
    // last-chance cleanup callback without making the shared updater depend on
    // the Windows UI project.
    public static Action? BeforeRestart { get; set; }

    // Kept for callers that display the expected download name. Releases may
    // use either this legacy name or the versioned name emitted by CI.
    public static string AssetFileName =>
        OperatingSystem.IsWindows() ? "sesame-windows.zip" : "sesame-linux-x64.tar.gz";

    private static string VersionedAssetFileName(string version) =>
        OperatingSystem.IsWindows()
            ? $"SESAME-{version}-windows-x64.zip"
            : $"SESAME-{version}-linux-x64.tar.gz";

    public static async Task<AppRelease?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/" + Repo + "/releases?per_page=15");
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var releases = new List<(Version Remote, string Version, string Tag, string Notes, string Name, string Url)>();
        foreach (var root in doc.RootElement.EnumerateArray())
        {
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v', 'V');
            if (!Version.TryParse(version, out var remote)) continue;
            var asset = FindAsset(root, VersionedAssetFileName(version), AssetFileName);
            if (asset is null) continue;
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            releases.Add((remote, version, tag, notes.Trim(), asset.Value.name, asset.Value.url));
        }

        var latest = releases.OrderByDescending(x => x.Remote).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latest.Version)) return null;
        var newer = Version.TryParse(AppVersion.Current, out var local) && latest.Remote > local;
        return new AppRelease
        {
            Version = latest.Version,
            Tag = latest.Tag,
            Notes = latest.Notes,
            AssetName = latest.Name,
            DownloadUrl = latest.Url,
            IsNewer = newer
        };
    }

    public static async Task ApplyAsync(AppRelease release, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            throw new InvalidOperationException("No download URL for this release.");

        var dest = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cache = AppDataPaths.CacheDir("updates");
        var pack = Path.Combine(cache, release.AssetName);
        var unpack = Path.Combine(cache, "unpack");
        progress?.Report("Downloading SESAME " + release.Version + "…");
        Directory.CreateDirectory(cache);
        await DownloadAsync(release.DownloadUrl, pack, ct);
        if (Directory.Exists(unpack))
            Directory.Delete(unpack, recursive: true);
        Directory.CreateDirectory(unpack);
        progress?.Report("Unpacking…");
        Extract(pack, unpack);
        var payload = PayloadRoot(unpack);
        if (!PayloadReady(payload))
            throw new InvalidOperationException("The update archive did not contain SESAME.");
        progress?.Report("Restarting…");
        BeforeRestart?.Invoke();
        LaunchSwap(payload, dest);
    }

    private static (string name, string url)? FindAsset(JsonElement root, params string[] fileNames)
    {
        if (!root.TryGetProperty("assets", out var assets)) return null;
        var candidates = fileNames.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        // Prefer the versioned asset, but accept the legacy name for older releases.
        foreach (var wanted in candidates)
        {
            foreach (var item in assets.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                var url = item.GetProperty("browser_download_url").GetString() ?? "";
                if (url.Length > 0) return (name, url);
            }
        }
        return null;
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, ct);
    }

    private static void Extract(string archive, string dest)
    {
        using var opened = ArchiveFactory.OpenArchive(archive);
        foreach (var entry in opened.Entries)
        {
            if (entry.IsDirectory) continue;
            var name = (entry.Key ?? "").Replace('\\', '/');
            if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal)) continue;
            entry.WriteToDirectory(dest, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }

    private static string PayloadRoot(string dir)
    {
        var exe = OperatingSystem.IsWindows() ? "SESAME.exe" : "SESAME";
        if (File.Exists(Path.Combine(dir, exe))) return dir;
        var found = Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).FirstOrDefault();
        return found is null ? dir : Path.GetDirectoryName(found)!;
    }

    private static bool PayloadReady(string dir) =>
        File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "SESAME.exe" : "SESAME"));

    private static void LaunchSwap(string unpack, string dest)
    {
        var exe = OperatingSystem.IsWindows()
            ? Path.Combine(dest, "SESAME.exe")
            : Path.Combine(dest, "SESAME");
        var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote));
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(Path.GetTempPath(), "sesame-update.cmd");
            File.WriteAllText(cmd, $"""
                @echo off
                ping 127.0.0.1 -n 3 >nul
                taskkill /F /IM ffl_testing_2.exe /T >nul 2>&1
                robocopy "{unpack}" "{dest}" /E /IS /IT /NFL /NDL /NJH /NJS /nc /ns /np >nul
                start "" "{exe}" {args}
                """);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = dest
            });
        }
        else
        {
            var sh = Path.Combine(Path.GetTempPath(), "sesame-update.sh");
            File.WriteAllText(sh, $"""
                #!/bin/bash
                sleep 2
                cp -a "{unpack}"/. "{dest}"/
                chmod +x "{exe}" 2>/dev/null || true
                exec "{exe}" {args}
                """);
            Process.Start(new ProcessStartInfo("/bin/bash", Quote(sh))
            {
                UseShellExecute = false,
                WorkingDirectory = dest
            });
        }

        Environment.Exit(0);
    }

    private static string Quote(string value) =>
        "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SESAME", AppVersion.Current));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
