using System.IO;
using System.Text.Json;

namespace VisualSSH.Services;

public static class TranslateSettings
{
    public const string DeepLSecretName = "deepl";

    public static string ApiKey { get; private set; } = "";

    public static bool HasDeepL => ApiKey.Length > 8;

    public static void Load()
    {
        ApiKey = SecretStore.Load(DeepLSecretName);
        try
        {
            var path = FilePath();
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var migrated = false;
            if (doc.RootElement.TryGetProperty("deepLKey", out var key))
            {
                var plaintext = key.GetString()?.Trim() ?? "";
                if (plaintext.Length > 8 && ApiKey.Length <= 8)
                    SaveDeepLKey(plaintext);
                migrated = plaintext.Length > 0;
            }
            if (migrated)
                TryDeletePlaintext();
        }
        catch
        {
            if (ApiKey.Length <= 8)
                ApiKey = "";
        }
    }

    public static void SaveDeepLKey(string key)
    {
        ApiKey = (key ?? "").Trim();
        if (ApiKey.Length == 0)
            SecretStore.Delete(DeepLSecretName);
        else
            SecretStore.Save(DeepLSecretName, ApiKey);
        TryDeletePlaintext();
    }

    public static void ClearKey()
    {
        ApiKey = "";
        SecretStore.Delete(DeepLSecretName);
        TryDeletePlaintext();
    }

    private static void TryDeletePlaintext()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            try
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{}");
                AppDataPaths.RestrictFile(path);
            }
            catch
            {
                /* oude plaintext weghalen is best-effort */
            }
        }
    }

    private static string FilePath() => AppDataPaths.Combine("translate.json");
}
