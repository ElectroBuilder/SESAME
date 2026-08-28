using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Sesame.Models;

namespace Sesame.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

    public void Load(IEnumerable<ConnectionProfile> defaults)
    {
        AppDataPaths.EnsureProtected();
        Profiles.Clear();
        var path = FilePath();
        if (File.Exists(path))
        {
            AppDataPaths.RestrictFile(path);
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, JsonOptions);
            if (loaded is { Count: > 0 })
            {
                var migrated = false;
                foreach (var p in loaded)
                {
                    if (SshSecrets.TryMigrateKeyPath(p.Id, p.KeyPath))
                        migrated = true;
                    p.KeyPath = null;
                    Profiles.Add(p);
                }
                if (migrated)
                    Save();
                return;
            }
        }

        foreach (var p in defaults)
        {
            var clone = p.Clone();
            SshSecrets.TryMigrateKeyPath(clone.Id, clone.KeyPath);
            clone.KeyPath = null;
            Profiles.Add(clone);
        }
        Save();
    }

    public void Save()
    {
        AppDataPaths.EnsureProtected();
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var p in Profiles.Where(p => !p.IsLocal))
            p.KeyPath = null;
        File.WriteAllText(path, JsonSerializer.Serialize(Profiles.Where(p => !p.IsLocal).ToList(), JsonOptions));
        AppDataPaths.RestrictFile(path);
    }

    public ConnectionProfile AddNew()
    {
        var profile = new ConnectionProfile
        {
            Name = NextName(),
            Host = "",
            Port = 22,
            User = "deck"
        };
        Profiles.Add(profile);
        return profile;
    }

    public void Delete(ConnectionProfile profile)
    {
        SshSecrets.DeleteAll(profile.Id);
        Profiles.Remove(profile);
        Save();
    }

    public void Upsert(ConnectionProfile edited)
    {
        var existing = Profiles.FirstOrDefault(p => p.Id == edited.Id);
        if (existing is null)
            Profiles.Add(edited.Clone());
        else
            existing.CopyFrom(edited);
        Save();
    }

    private string NextName()
    {
        var n = 1;
        while (Profiles.Any(p => p.Name == $"Nieuwe sessie {n}"))
            n++;
        return $"Nieuwe sessie {n}";
    }

    private static string FilePath() => AppDataPaths.Combine("sessions.json");
}
