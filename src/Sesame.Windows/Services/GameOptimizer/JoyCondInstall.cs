using System.Text;

namespace Sesame.Services.GameOptimizer;

/// <summary>Deploy and run the Steam Deck joycond + cemuhook installer for Dolphin.</summary>
public static class JoyCondInstall
{
    public const string RiskSummary =
        "This installs system software on your Steam Deck for Dolphin Wii Joy-Con motion " +
        "(joycond + joycond-cemuhook). It is not required for Eden or other Switch emulators.\n\n" +
        "Risks and side effects:\n" +
        "• Temporarily disables the SteamOS read-only root filesystem\n" +
        "• Installs build tools (gcc, cmake, …) and extracts Arch headers into /usr/include\n" +
        "• Installs a system-wide joycond daemon and udev rules (may change how Steam sees Joy-Cons)\n" +
        "• Needs your Deck sudo password once\n" +
        "• Can take several minutes and needs internet on the Deck\n\n" +
        "After install: pair Joy-Cons, press L+R to Combined (one player), turn Steam Input Off on the Wii shortcut, " +
        "Optimize Wii games, and launch via SESAME.";

    public static string ScriptText() => Load("Sesame.sesame-install-joycond.sh");

    public static string ScriptPath => DolphinInput.InstallJoyCondPath;

    public static string LogPath =>
        DeckClient.Combine("/home/deck/.local/share/sesame", "install-joycond.log");

    public static string StatusPath =>
        DeckClient.Combine("/home/deck/.local/share/sesame", "joycon-dsu.status");

    public static void Deploy(DeckClient client)
    {
        client.EnsureDirectory("/home/deck/.local/share/sesame");
        var script = ScriptText();
        client.WriteText(ScriptPath, script);
        client.WriteText(DeckClient.Combine(EmulatorProbe.WrapperDir, "install-joycond.sh"), script);
        client.Execute(
            "chmod +x " + DeckClient.ShQuote(ScriptPath) +
            " " + DeckClient.ShQuote(DeckClient.Combine(EmulatorProbe.WrapperDir, "install-joycond.sh")) +
            " ; sed -i 's/\\r$//' " + DeckClient.ShQuote(ScriptPath) +
            " " + DeckClient.ShQuote(DeckClient.Combine(EmulatorProbe.WrapperDir, "install-joycond.sh")),
            30);
    }

    public static JoyCondStatus Query(DeckClient client)
    {
        try
        {
            var active = client.Execute("systemctl is-active joycond 2>/dev/null || true", 10)
                .Trim();
            var status = "";
            try
            {
                if (client.Exists(StatusPath))
                    status = Encoding.UTF8.GetString(client.ReadBytes(StatusPath)).Trim();
            }
            catch
            {
                /* optional */
            }

            var cemuhook = client.Execute(
                    "python3 -c 'import joycond_cemuhook' >/dev/null 2>&1 && echo yes || " +
                    "(command -v joycond-cemuhook >/dev/null && echo yes || echo no)", 15)
                .Trim();

            return new JoyCondStatus(
                string.Equals(active, "active", StringComparison.OrdinalIgnoreCase),
                active,
                status,
                string.Equals(cemuhook, "yes", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return new JoyCondStatus(false, "error", ex.Message, false);
        }
    }

    /// <summary>Deploy script and run it with sudo via SUDO_ASKPASS (password not stored).</summary>
    public static string Run(DeckClient client, string sudoPassword, IProgress<string>? progress = null)
    {
        if (string.IsNullOrEmpty(sudoPassword))
            throw new InvalidOperationException("Deck sudo password is required.");

        progress?.Report("Deploying installer…");
        Deploy(client);

        progress?.Report("Running installer on the Deck (this can take several minutes)…");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sudoPassword));
        // Askpass embeds the password (base64); sudo -A works over SSH without a TTY.
        // Do not put the secret in the environment (sudo env_reset).
        var remote =
            "set -euo pipefail; " +
            "ASK=$(mktemp /tmp/sesame-askpass.XXXXXX); " +
            "printf '%s\\n' '#!/bin/sh' 'echo " + DeckClient.ShQuote(b64) + " | base64 -d' >\"$ASK\"; " +
            "chmod 700 \"$ASK\"; " +
            "export SUDO_ASKPASS=\"$ASK\"; " +
            "sudo -A -v; " +
            "bash " + DeckClient.ShQuote(ScriptPath) + "; " +
            "rc=$?; " +
            "rm -f \"$ASK\"; " +
            "exit $rc";

        string output;
        try
        {
            output = client.Execute(remote, timeoutSeconds: 900);
        }
        catch (Exception ex)
        {
            progress?.Report("Install failed: " + ex.Message);
            throw;
        }

        progress?.Report("Checking status…");
        var st = Query(client);
        if (!st.JoyCondActive)
            throw new InvalidOperationException(
                "Installer finished but joycond is not active.\n\n" + Tail(output, 40));

        progress?.Report(st.CemuhookOk
            ? "joycond active · cemuhook OK"
            : "joycond active · cemuhook missing (check log on Deck)");
        return output;
    }

    public static string ReadLogTail(DeckClient client, int lines = 60)
    {
        try
        {
            return client.Execute(
                "tail -n " + lines + " " + DeckClient.ShQuote(LogPath) + " 2>/dev/null || true",
                20);
        }
        catch
        {
            return "";
        }
    }

    private static string Tail(string text, int lines)
    {
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);
        if (parts.Length <= lines) return text;
        return string.Join("\n", parts.AsSpan(parts.Length - lines).ToArray());
    }

    private static string Load(string resource)
    {
        var asm = typeof(JoyCondInstall).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? asm.GetManifestResourceStream(resource.Replace("Sesame.", "VisualSSH.", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(resource + " missing from build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}

public readonly record struct JoyCondStatus(
    bool JoyCondActive,
    string ActiveRaw,
    string StatusFile,
    bool CemuhookOk);
