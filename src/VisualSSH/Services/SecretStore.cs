using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VisualSSH.Services;

public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SESAME.secrets.v1");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("VisualSSH.secrets.v1");

    public static void Save(string name, string value)
    {
        var key = CleanName(name);
        var path = FilePath(key);
        AppDataPaths.EnsureProtected();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (string.IsNullOrEmpty(value))
        {
            Delete(key);
            return;
        }

        var protectedBytes = Protect(Encoding.UTF8.GetBytes(value));
        File.WriteAllBytes(path, protectedBytes);
        AppDataPaths.RestrictFile(path);
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch { /* verborgen is optioneel */ }
    }

    public static string Load(string name)
    {
        try
        {
            var path = FilePath(CleanName(name));
            if (!File.Exists(path)) return "";
            var raw = File.ReadAllBytes(path);
            var bytes = Unprotect(raw);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static bool Has(string name) => Load(name).Length > 0;

    public static bool Exists(string name)
    {
        try
        {
            var path = FilePath(CleanName(name));
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Delete(string name)
    {
        try
        {
            var path = FilePath(CleanName(name));
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            /* wissen is best-effort */
        }
    }

    private static byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        return AesProtect(plain);
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return ProtectedData.Unprotect(data, LegacyEntropy, DataProtectionScope.CurrentUser);
            }
        }
        return AesUnprotect(data);
    }

    private static byte[] AesProtect(byte[] plain)
    {
        var key = LoadOrCreateMasterKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);
        var packed = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, 12);
        Buffer.BlockCopy(tag, 0, packed, 12, 16);
        Buffer.BlockCopy(cipher, 0, packed, 28, cipher.Length);
        return packed;
    }

    private static byte[] AesUnprotect(byte[] data)
    {
        if (data.Length < 29)
            throw new CryptographicException("Secret te kort.");
        var key = LoadOrCreateMasterKey();
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(12, 16);
        var cipher = data.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] LoadOrCreateMasterKey()
    {
        var path = AppDataPaths.Combine("secrets", "master.key");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
        }
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        AppDataPaths.RestrictFile(path);
        return key;
    }

    private static string CleanName(string name)
    {
        var value = (name ?? "").Trim().ToLowerInvariant();
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        return string.IsNullOrEmpty(value) ? "secret" : value;
    }

    private static string FilePath(string name) =>
        AppDataPaths.Combine("secrets", name + (OperatingSystem.IsWindows() ? ".dpapi" : ".bin"));
}
