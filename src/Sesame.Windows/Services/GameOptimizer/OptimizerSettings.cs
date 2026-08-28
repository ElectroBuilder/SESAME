using System.IO;
using System.Text.Json;
using Sesame.Services;

namespace Sesame.Services.GameOptimizer;

public enum SteamTabScheme
{
    Platform,
    Brand,
    Emulation
}

public static class OptimizerSettings
{
    public const string SteamGridDbSecretName = "steamgriddb";

    public static string SteamGridDbKey { get; private set; } = "";
    public static bool OverwriteShortcuts { get; set; } = true;
    public static bool OverwriteArtwork { get; set; } = true;
    public static bool UseMasks { get; set; } = true;
    public static SteamTabScheme SteamTabScheme { get; set; } = SteamTabScheme.Platform;
    public static List<string> SteamTabIds { get; private set; } = new();

    private static readonly Dictionary<string, bool> MaskPlatforms = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasSteamGridDb => SteamGridDbKey.Length >= 16;

    public static bool MasksByDefault(string systemId) =>
        !systemId.Equals("hydra", StringComparison.OrdinalIgnoreCase) &&
        !systemId.Equals("app", StringComparison.OrdinalIgnoreCase);

    public static bool PlatformMask(string? systemId)
    {
        var id = (systemId ?? "").Trim();
        if (id.Length == 0) return true;
        return MaskPlatforms.TryGetValue(id, out var on) ? on : MasksByDefault(id);
    }

    public static bool UseMaskFor(string? systemId) => UseMasks && PlatformMask(systemId);

    public static void SetPlatformMask(string systemId, bool enabled, bool persist = true)
    {
        var id = (systemId ?? "").Trim();
        if (id.Length == 0) return;
        MaskPlatforms[id] = enabled;
        if (persist)
            Save();
    }

    public static IReadOnlyList<MaskPlatformOption> MaskOptions()
    {
        var rows = new List<MaskPlatformOption>
        {
            new("hydra", "Hydra", PlatformMask("hydra")),
            new("app", "Apps", PlatformMask("app"))
        };
        rows.AddRange(SystemCatalog.All.Select(p => new MaskPlatformOption(p.Id, p.Name, PlatformMask(p.Id))));
        return rows;
    }

    public static void ResetMaskDefaults()
    {
        MaskPlatforms.Clear();
        MaskPlatforms["hydra"] = false;
        MaskPlatforms["app"] = false;
        foreach (var profile in SystemCatalog.All)
            MaskPlatforms[profile.Id] = true;
        Save();
    }

    public static void Load()
    {
        SteamGridDbKey = SecretStore.Load(SteamGridDbSecretName);
        try
        {
            var path = FilePath();
            if (!File.Exists(path))
            {
                MaskPlatforms["hydra"] = false;
                MaskPlatforms["app"] = false;
                return;
            }
            AppDataPaths.RestrictFile(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var migrated = false;
            if (root.TryGetProperty("steamGridDbKey", out var key))
            {
                var plaintext = CleanKey(key.GetString());
                if (plaintext.Length >= 16 && SteamGridDbKey.Length < 16)
                {
                    SaveKey(plaintext);
                    migrated = true;
                }
                else if (plaintext.Length > 0)
                    migrated = true;
            }
            if (root.TryGetProperty("overwriteShortcuts", out var ow))
                OverwriteShortcuts = ow.GetBoolean();
            if (root.TryGetProperty("overwriteArtwork", out var art))
                OverwriteArtwork = art.GetBoolean();
            if (root.TryGetProperty("useMasks", out var masks))
                UseMasks = masks.GetBoolean();
            if (root.TryGetProperty("steamTabScheme", out var scheme))
                SteamTabScheme = ParseScheme(scheme.GetString());
            if (root.TryGetProperty("steamTabIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                SteamTabIds = ids.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            MaskPlatforms.Clear();
            if (root.TryGetProperty("maskPlatforms", out var mp) && mp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in mp.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        MaskPlatforms[prop.Name] = prop.Value.GetBoolean();
                }
            }
            else
            {
                MaskPlatforms["hydra"] = false;
                MaskPlatforms["app"] = false;
            }
            if (!MaskPlatforms.ContainsKey("hydra"))
                MaskPlatforms["hydra"] = false;
            if (!MaskPlatforms.ContainsKey("app"))
                MaskPlatforms["app"] = false;
            if (migrated)
                Save();
        }
        catch
        {
            if (SteamGridDbKey.Length < 16)
                SteamGridDbKey = "";
            MaskPlatforms["hydra"] = false;
            MaskPlatforms["app"] = false;
        }
    }

    public static void Save()
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var masks = new Dictionary<string, bool>(MaskPlatforms, StringComparer.OrdinalIgnoreCase)
        {
            ["hydra"] = PlatformMask("hydra"),
            ["app"] = PlatformMask("app")
        };
        foreach (var profile in SystemCatalog.All)
            masks[profile.Id] = PlatformMask(profile.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            overwriteShortcuts = OverwriteShortcuts,
            overwriteArtwork = OverwriteArtwork,
            useMasks = UseMasks,
            maskPlatforms = masks,
            steamTabScheme = SchemeKey(SteamTabScheme),
            steamTabIds = SteamTabIds
        }));
        AppDataPaths.RestrictFile(path);
    }

    public static void RememberSteamTabs(IEnumerable<string> ids)
    {
        SteamTabIds = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save();
    }

    public static void SaveKey(string key)
    {
        SteamGridDbKey = CleanKey(key);
        if (SteamGridDbKey.Length == 0)
            SecretStore.Delete(SteamGridDbSecretName);
        else
            SecretStore.Save(SteamGridDbSecretName, SteamGridDbKey);
        Save();
    }

    public static void ClearKey()
    {
        SteamGridDbKey = "";
        SecretStore.Delete(SteamGridDbSecretName);
        Save();
    }

    public static string CleanKey(string? key)
    {
        var value = (key ?? "").Trim().Trim('"').Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            value = value[7..].Trim();
        return value;
    }

    public static SteamTabScheme ParseScheme(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "brand" or "merk" or "familie" => SteamTabScheme.Brand,
            "emulation" or "een" or "one" => SteamTabScheme.Emulation,
            _ => SteamTabScheme.Platform
        };

    public static string SchemeKey(SteamTabScheme scheme) => scheme switch
    {
        SteamTabScheme.Brand => "brand",
        SteamTabScheme.Emulation => "emulation",
        _ => "platform"
    };

    private static string FilePath() => AppDataPaths.Combine("optimizer.json");
}

public sealed class MaskPlatformOption : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled;

    public MaskPlatformOption(string id, string name, bool enabled)
    {
        Id = id;
        Name = name;
        _enabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OptimizerSettings.SetPlatformMask(Id, value);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
