using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Sesame.Services;

public static class SshSecrets
{
    public const int MaxKeyBytes = 256 * 1024;

    public static string KeyName(string profileId) => "ssh-key-" + profileId;
    public static string PassphraseName(string profileId) => "ssh-pass-" + profileId;
    public static string PasswordName(string profileId) => "ssh-password-" + profileId;

    public static bool HasKey(string profileId) => SecretStore.Exists(KeyName(profileId));
    public static bool HasPassphrase(string profileId) => SecretStore.Exists(PassphraseName(profileId));
    public static bool HasPassword(string profileId) => SecretStore.Exists(PasswordName(profileId));

    /// <returns>True if the key needs a passphrase.</returns>
    public static bool ImportFromFile(string profileId, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Key file not found.");
        if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "That is a public key (.pub). Import the private key next to it — the file without .pub, for example steam_deck (not steam_deck.pub).");

        var info = new FileInfo(path);
        if (info.Length is 0 or > MaxKeyBytes)
            throw new InvalidOperationException("This file does not look like an SSH private key.");

        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd().Trim();
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Windows blocked reading that file. Copy the private key to your Desktop and import it from there.");
        }
        if (text.Length == 0)
            throw new InvalidOperationException("The key file is empty.");
        if (!LooksLikePrivateKey(text))
            throw new InvalidOperationException(
                "No SSH private key found in this file. Choose the private key (often named steam_deck, id_rsa or id_ed25519), not the .pub file.");

        var needsPass = LooksEncrypted(text);
        if (!needsPass)
        {
            try
            {
                CreateKeyFile(text, "").Dispose();
            }
            catch (Exception ex) when (NeedsPassphrase(ex))
            {
                needsPass = true;
            }
            catch
            {
                throw new InvalidOperationException(
                    "The key could not be read. Use an OpenSSH or PuTTY private key (not a .pub file).");
            }
        }

        try
        {
            SecretStore.Save(KeyName(profileId), text);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Windows blocked saving the key in %APPDATA%\\SESAME\\secrets. Close other SESAME windows and try Import again.");
        }
        return needsPass;
    }

    public static bool TryMigrateKeyPath(string profileId, string? keyPath)
    {
        var changed = !string.IsNullOrWhiteSpace(keyPath);
        if (HasKey(profileId))
            return changed;

        var resolved = string.IsNullOrWhiteSpace(keyPath)
            ? ""
            : Environment.ExpandEnvironmentVariables(keyPath);
        if (resolved.Length == 0 || !File.Exists(resolved))
            return changed;

        try
        {
            ImportFromFile(profileId, resolved);
            return true;
        }
        catch
        {
            return changed;
        }
    }

    public static void SavePassphrase(string profileId, string? passphrase)
    {
        var value = passphrase ?? "";
        if (value.Length == 0)
            SecretStore.Delete(PassphraseName(profileId));
        else
            SecretStore.Save(PassphraseName(profileId), value);
    }

    public static void SavePassword(string profileId, string? password)
    {
        var value = password ?? "";
        if (value.Length == 0)
            SecretStore.Delete(PasswordName(profileId));
        else
            SecretStore.Save(PasswordName(profileId), value);
    }

    public static void DeleteKey(string profileId)
    {
        SecretStore.Delete(KeyName(profileId));
        SecretStore.Delete(PassphraseName(profileId));
    }

    public static void DeleteAll(string profileId)
    {
        SecretStore.Delete(KeyName(profileId));
        SecretStore.Delete(PassphraseName(profileId));
        SecretStore.Delete(PasswordName(profileId));
    }

    public static PrivateKeyFile? OpenKey(string profileId)
    {
        var pem = SecretStore.Load(KeyName(profileId));
        if (pem.Length == 0) return null;
        var pass = SecretStore.Load(PassphraseName(profileId));
        return CreateKeyFile(pem, pass);
    }

    public static PrivateKeyFile CreateKeyFile(string pem, string? passphrase)
    {
        var bytes = Encoding.UTF8.GetBytes(pem);
        var ms = new MemoryStream(bytes, writable: false);
        try
        {
            return string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(ms)
                : new PrivateKeyFile(ms, passphrase);
        }
        catch
        {
            ms.Dispose();
            throw;
        }
    }

    public static bool LooksEncrypted(string text) =>
        text.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Encryption:", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikePrivateKey(string text) =>
        text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)
        || text.Contains("PuTTY-User-Key-File", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsPassphrase(Exception ex)
    {
        if (ex is SshPassPhraseNullOrEmptyException) return true;
        var msg = ex.Message ?? "";
        return msg.Contains("pass", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("MAC", StringComparison.OrdinalIgnoreCase);
    }
}
