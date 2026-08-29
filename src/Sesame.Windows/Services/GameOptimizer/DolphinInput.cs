using System.Text;
using Sesame.Models;

namespace Sesame.Services.GameOptimizer;

public static class DolphinInput
{
    public const string WrapperName = "sesame-dolphin.sh";
    public const string LegacyWrapperName = "vssh-dolphin.sh";
    public const string CfgName = "sesame-dolphin-cfg.py";
    public const string ProfileName = "SESAME-gyro";

    public const string DutchGyroHint =
        "Wii / Joy-Con: pair Joy-Cons to the Steam Deck over Bluetooth, then Optimize Wii games again. " +
        "SESAME maps Right Joy-Con → Wiimote (gyro) and Left Joy-Con → Nunchuk — same idea as BetterJoyForDolphin, but native on Linux. " +
        "Extra Joy-Cons become Wiimote 2–4 (sideways) for Wii Sports. " +
        "In Steam: set Steam Input for each Joy-Con to Off so Dolphin sees gyro. " +
        "Without Joy-Cons, SESAME uses the Deck IMU and mouse IR. " +
        "8BitDo Ultimate 2.4 GHz (XInput) has no gyro — use Switch/Bluetooth mode or the Deck gyro.";

    public static string WrapperPath =>
        DeckClient.Combine(EmulatorProbe.WrapperDir, WrapperName);

    public static string CfgPath =>
        DeckClient.Combine(EmulatorProbe.WrapperDir, CfgName);

    private static readonly string[] ConfigDirs =
    [
        "/home/deck/.var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu",
        "/home/deck/.config/dolphin-emu"
    ];

    public static bool UsesDolphin(SystemProfile profile) =>
        profile.Emulators.Contains("dolphin", StringComparer.OrdinalIgnoreCase) ||
        profile.Id is "gc" or "wii";

    public static bool UsesDolphin(OptimizerGame game) =>
        game.SystemId is "gc" or "wii" ||
        (game.EmulatorName?.Contains("Dolphin", StringComparison.OrdinalIgnoreCase) ?? false) ||
        IsBound(game);

    public static bool IsBound(string? target)
    {
        var hay = (target ?? "").Replace('\\', '/');
        return hay.Contains(WrapperName, StringComparison.OrdinalIgnoreCase) ||
               hay.Contains(LegacyWrapperName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBound(OptimizerGame game) =>
        IsBound(game.Target) || IsBound(game.LaunchOptions);

    public static bool NeedsRebind(OptimizerGame game, SystemProfile profile)
    {
        if (!UsesDolphin(profile)) return false;
        var hay = ((game.Target ?? "") + " " + (game.LaunchOptions ?? "")).Replace('\\', '/');
        return !hay.Contains(WrapperName, StringComparison.OrdinalIgnoreCase);
    }

    public static void Ensure(DeckClient client)
    {
        InstallWrapper(client);
        try
        {
            client.Execute(
                "flatpak override --user --device=all --filesystem=host org.DolphinEmu.dolphin-emu 2>/dev/null || true",
                15);
        }
        catch
        {
            /* override is extra: EmuDeck zet filesystem=host al */
        }

        foreach (var dir in ConfigDirs)
        {
            try
            {
                client.EnsureDirectory(dir);
                client.EnsureDirectory(DeckClient.Combine(dir, "Profiles/Wiimote"));
                client.EnsureDirectory(DeckClient.Combine(dir, "Profiles/GCPad"));
                PatchIni(client, DeckClient.Combine(dir, "Dolphin.ini"));
                client.WriteText(DeckClient.Combine(dir, "Profiles/Wiimote/" + ProfileName + ".ini"),
                    WiiProfile("SDL/0/Steam Virtual Gamepad"));
                client.WriteText(DeckClient.Combine(dir, "Profiles/Wiimote/SESAME-joycon-nunchuk.ini"),
                    JoyConNunchukProfile());
                client.WriteText(DeckClient.Combine(dir, "Profiles/Wiimote/SESAME-joycon-remote.ini"),
                    JoyConRemoteProfile());
                client.WriteText(DeckClient.Combine(dir, "Profiles/GCPad/" + ProfileName + ".ini"),
                    GcProfile("SDL/0/Steam Virtual Gamepad"));
                MergeWiimote(client, DeckClient.Combine(dir, "WiimoteNew.ini"));
                EnsureGcPad(client, DeckClient.Combine(dir, "GCPadNew.ini"));
                EnsureDsu(client, DeckClient.Combine(dir, "DSUClient.ini"));
            }
            catch
            {
                /* andere config-map is optioneel */
            }
        }
    }

    public static void Bind(OptimizerGame game)
    {
        var rom = (game.RomPath ?? "").Replace('\\', '/').Trim().Trim('"');
        game.Target = Quote(WrapperPath);
        game.StartDir = Quote(EmulatorProbe.WrapperDir.TrimEnd('/') + "/");
        game.LaunchOptions = Quote(rom);
        game.IsRetroArch = false;
        game.EmulatorName = "Dolphin";
    }

    private static void InstallWrapper(DeckClient client)
    {
        client.EnsureDirectory(EmulatorProbe.WrapperDir);
        client.WriteText(WrapperPath, LoadScript("Sesame.sesame-dolphin.sh"));
        client.WriteText(CfgPath, LoadScript("Sesame.sesame-dolphin-cfg.py"));
        try
        {
            client.Execute(
                "chmod +x " + DeckClient.ShQuote(WrapperPath) +
                " ; sed -i 's/\\r$//' " + DeckClient.ShQuote(WrapperPath) +
                " " + DeckClient.ShQuote(CfgPath), 8);
        }
        catch
        {
            /* Steam kan /bin/bash als fallback gebruiken */
        }
    }

    private static string LoadScript(string resource)
    {
        var asm = typeof(DolphinInput).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? asm.GetManifestResourceStream(resource.Replace("Sesame.", "VisualSSH.", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(resource + " ontbreekt in de build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ReadUtf8(DeckClient client, string path)
    {
        try
        {
            if (!client.Exists(path)) return "";
            return Encoding.UTF8.GetString(client.ReadBytes(path));
        }
        catch
        {
            return "";
        }
    }

    private static void PatchIni(DeckClient client, string path)
    {
        var text = ReadUtf8(client, path);
        if (string.IsNullOrWhiteSpace(text))
            text = "[Core]\nSIDevice0 = 6\nWiimoteSource0 = 1\n[Input]\nBackgroundInput = True\n";
        text = SetIni(text, "Core", "SIDevice0", "6");
        text = SetIni(text, "Core", "WiimoteSource0", "1");
        text = SetIni(text, "Input", "BackgroundInput", "True");
        client.WriteText(path, text);
    }

    private static void MergeWiimote(DeckClient client, string path)
    {
        var text = ReadUtf8(client, path);
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("[Wiimote1]", StringComparison.OrdinalIgnoreCase))
        {
            client.WriteText(path, WiiPad("SDL/0/Steam Virtual Gamepad"));
            return;
        }

        text = SetIni(text, "Wiimote1", "Source", "1");
        if (string.IsNullOrWhiteSpace(GetIni(text, "Wiimote1", "Device")))
            text = SetIni(text, "Wiimote1", "Device", "SDL/0/Steam Virtual Gamepad");

        foreach (var (key, value) in ImuKeys())
        {
            var existing = GetIni(text, "Wiimote1", key);
            if (existing.Contains("SteamDeck/0/Steam Deck", StringComparison.Ordinal) &&
                existing.Contains("DSUClient", StringComparison.Ordinal))
                continue;
            text = SetIni(text, "Wiimote1", key, value);
        }

        var irUp = GetIni(text, "Wiimote1", "IR/Up");
        if (!irUp.Contains("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (key, value) in IrKeys())
                text = SetIni(text, "Wiimote1", key, value);
        }
        else
            text = SetIni(text, "Wiimote1", "IR/Auto-Hide", "False");

        text = SetIni(text, "Wiimote1", "IMUIR/Enabled", "True");

        client.WriteText(path, text);
    }

    private static void EnsureGcPad(DeckClient client, string path)
    {
        var text = ReadUtf8(client, path);
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("[GCPad1]", StringComparison.OrdinalIgnoreCase))
            client.WriteText(path, GcPad("SDL/0/Steam Virtual Gamepad"));
    }

    private static void EnsureDsu(DeckClient client, string path)
    {
        var text = ReadUtf8(client, path);
        if (string.IsNullOrWhiteSpace(text))
            text = "[Server]\n";
        text = SetIni(text, "Server", "Enabled", "True");
        if (string.IsNullOrWhiteSpace(GetIni(text, "Server", "Server1Name")))
        {
            text = SetIni(text, "Server", "Server1Name", "BetterJoy");
            text = SetIni(text, "Server", "Server1IP", "127.0.0.1");
            text = SetIni(text, "Server", "Server1Port", "26760");
        }
        client.WriteText(path, text);
    }

    private static string JoyConNunchukProfile()
    {
        const string right = "SDL/0/Nintendo Switch Right Joy-Con";
        const string left = "SDL/0/Nintendo Switch Left Joy-Con";
        return
            "[Profile]\n" +
            "Device = " + right + "\n" +
            "Source = 1\n" +
            "Buttons/A = `Button A`|`Button S`|SOUTH|EAST\n" +
            "Buttons/B = `Button B`|`Button ZR`|`Trigger R`|EAST\n" +
            "Buttons/1 = `Button X`|`Button N`|NORTH\n" +
            "Buttons/2 = `Button Y`|`Button W`|WEST\n" +
            "Buttons/- = `Button Minus`|`Button Capture`|SELECT\n" +
            "Buttons/+ = `Button Plus`|START\n" +
            "Buttons/Home = `Button Home`|`Button Guide`|MODE\n" +
            "D-Pad/Up = `Pad N`|`Hat 0 N`|`Left Y-`\n" +
            "D-Pad/Down = `Pad S`|`Hat 0 S`|`Left Y+`\n" +
            "D-Pad/Left = `Pad W`|`Hat 0 W`|`Left X-`\n" +
            "D-Pad/Right = `Pad E`|`Hat 0 E`|`Left X+`\n" +
            "Shake/X = `Button SL`|`Button SR`|TL\n" +
            "Shake/Y = `Button SL`|`Button SR`|TL\n" +
            "Shake/Z = `Button SL`|`Button SR`|TL\n" +
            "Extension = Nunchuk\n" +
            "Nunchuk/Buttons/C = `" + left + ":Button L`|`" + left + ":Button SL`|TL\n" +
            "Nunchuk/Buttons/Z = `" + left + ":Button ZL`|`" + left + ":Trigger L`|`Full Axis 2+`\n" +
            "Nunchuk/Stick/Up = `" + left + ":Left Y-`|`" + left + ":Axis 1-`\n" +
            "Nunchuk/Stick/Down = `" + left + ":Left Y+`|`" + left + ":Axis 1+`\n" +
            "Nunchuk/Stick/Left = `" + left + ":Left X-`|`" + left + ":Axis 0-`\n" +
            "Nunchuk/Stick/Right = `" + left + ":Left X+`|`" + left + ":Axis 0+`\n" +
            JoyConImu(right) +
            "IMUIR/Enabled = True\n" +
            "IR/Up = `Cursor Y-`\nIR/Down = `Cursor Y+`\nIR/Left = `Cursor X-`\nIR/Right = `Cursor X+`\n" +
            "IR/Auto-Hide = False\n" +
            "Rumble/Motor = Strong\n";
    }

    private static string JoyConRemoteProfile()
    {
        const string pad = "SDL/0/Nintendo Switch Right Joy-Con";
        return
            "[Profile]\n" +
            "Device = " + pad + "\n" +
            "Source = 1\n" +
            "Extension = None\n" +
            "Options/Sideways Wiimote = True\n" +
            "Buttons/A = `Button A`|`Button S`|SOUTH|EAST\n" +
            "Buttons/B = `Button B`|`Button ZR`|EAST\n" +
            "Buttons/1 = `Button X`|NORTH\n" +
            "Buttons/2 = `Button Y`|WEST\n" +
            "Buttons/- = `Button Minus`|SELECT\n" +
            "Buttons/+ = `Button Plus`|START\n" +
            "Buttons/Home = `Button Home`|MODE\n" +
            "D-Pad/Up = `Pad N`|`Left Y-`\n" +
            "D-Pad/Down = `Pad S`|`Left Y+`\n" +
            "D-Pad/Left = `Pad W`|`Left X-`\n" +
            "D-Pad/Right = `Pad E`|`Left X+`\n" +
            "Shake/X = `Button SL`|`Button SR`\n" +
            "Shake/Y = `Button SL`|`Button SR`\n" +
            "Shake/Z = `Button SL`|`Button SR`\n" +
            JoyConImu(pad) +
            "IMUIR/Enabled = True\n" +
            "Rumble/Motor = Strong\n";
    }

    private static string JoyConImu(string device)
    {
        var sb = new StringBuilder();
        foreach (var (key, axis) in new (string, string)[]
                 {
                     ("IMUAccelerometer/Up", "Accel Up"),
                     ("IMUAccelerometer/Down", "Accel Down"),
                     ("IMUAccelerometer/Left", "Accel Left"),
                     ("IMUAccelerometer/Right", "Accel Right"),
                     ("IMUAccelerometer/Forward", "Accel Forward"),
                     ("IMUAccelerometer/Backward", "Accel Backward"),
                     ("IMUGyroscope/Pitch Up", "Gyro Pitch Up"),
                     ("IMUGyroscope/Pitch Down", "Gyro Pitch Down"),
                     ("IMUGyroscope/Roll Left", "Gyro Roll Left"),
                     ("IMUGyroscope/Roll Right", "Gyro Roll Right"),
                     ("IMUGyroscope/Yaw Left", "Gyro Yaw Left"),
                     ("IMUGyroscope/Yaw Right", "Gyro Yaw Right"),
                 })
        {
            sb.Append(key).Append(" = `").Append(device).Append(':').Append(axis)
                .Append("`|`DSUClient/0/BetterJoy:").Append(axis).Append("`|`").Append(axis).Append("`\n");
        }
        return sb.ToString();
    }

    private static string SetIni(string text, string section, string key, string value)
    {
        var header = "[" + section + "]";
        var keyRx = new System.Text.RegularExpressions.Regex(
            @"^(" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=).*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var sectionRx = new System.Text.RegularExpressions.Regex(
            @"\[" + System.Text.RegularExpressions.Regex.Escape(section) + @"\](?<body>[\s\S]*?)(?=\n\[|\z)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!text.Contains(header, StringComparison.OrdinalIgnoreCase))
            return text.TrimEnd() + "\n" + header + "\n" + key + " = " + value + "\n";
        var hit = sectionRx.Match(text);
        if (!hit.Success)
            return text.TrimEnd() + "\n" + header + "\n" + key + " = " + value + "\n";
        var body = hit.Groups["body"].Value;
        if (keyRx.IsMatch(body))
        {
            var updated = keyRx.Replace(body, key + " = " + value, 1);
            return text[..hit.Groups["body"].Index] + updated +
                   text[(hit.Groups["body"].Index + body.Length)..];
        }
        var insertAt = hit.Groups["body"].Index;
        return text[..insertAt] + "\n" + key + " = " + value + body +
               text[(insertAt + body.Length)..];
    }

    private static string GetIni(string text, string section, string key)
    {
        var sectionRx = new System.Text.RegularExpressions.Regex(
            @"\[" + System.Text.RegularExpressions.Regex.Escape(section) + @"\](?<body>[\s\S]*?)(?=\n\[|\z)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var hit = sectionRx.Match(text);
        if (!hit.Success) return "";
        var keyRx = new System.Text.RegularExpressions.Regex(
            @"^" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=\s*(.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var m = keyRx.Match(hit.Groups["body"].Value);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string Quote(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim().Trim('"');
        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static IEnumerable<(string Key, string Value)> ImuKeys()
    {
        yield return ("IMUAccelerometer/Up", Imu("Accel Up"));
        yield return ("IMUAccelerometer/Down", Imu("Accel Down"));
        yield return ("IMUAccelerometer/Left", Imu("Accel Left"));
        yield return ("IMUAccelerometer/Right", Imu("Accel Right"));
        yield return ("IMUAccelerometer/Forward", Imu("Accel Forward"));
        yield return ("IMUAccelerometer/Backward", Imu("Accel Backward"));
        yield return ("IMUGyroscope/Pitch Up", Imu("Gyro Pitch Up"));
        yield return ("IMUGyroscope/Pitch Down", Imu("Gyro Pitch Down"));
        yield return ("IMUGyroscope/Roll Left", Imu("Gyro Roll Left"));
        yield return ("IMUGyroscope/Roll Right", Imu("Gyro Roll Right"));
        yield return ("IMUGyroscope/Yaw Left", Imu("Gyro Yaw Left"));
        yield return ("IMUGyroscope/Yaw Right", Imu("Gyro Yaw Right"));
    }

    private static string Imu(string name) =>
        "`SteamDeck/0/Steam Deck:" + name + "`|`DSUClient/0/steamdeckgyro:" + name + "`|`" + name + "`";

    private static IEnumerable<(string Key, string Value)> IrKeys()
    {
        yield return ("IR/Up", "`Cursor Y-`|`XInput2/0/Virtual core pointer:Cursor Y-`|`Right Y-`");
        yield return ("IR/Down", "`Cursor Y+`|`XInput2/0/Virtual core pointer:Cursor Y+`|`Right Y+`");
        yield return ("IR/Left", "`Cursor X-`|`XInput2/0/Virtual core pointer:Cursor X-`|`Right X-`");
        yield return ("IR/Right", "`Cursor X+`|`XInput2/0/Virtual core pointer:Cursor X+`|`Right X+`");
        yield return ("IR/Auto-Hide", "False");
    }

    private static string GcProfile(string device) =>
        "[Profile]\n" + GcPad(device).Replace("[GCPad1]\n", "", StringComparison.Ordinal);

    private static string WiiProfile(string device)
    {
        var pad = WiiPad(device);
        var extra = pad.IndexOf("[Wiimote2]", StringComparison.Ordinal);
        var body = extra > 0 ? pad[..extra] : pad;
        return "[Profile]\n" + body.Replace("[Wiimote1]\n", "", StringComparison.Ordinal);
    }

    private static string GcPad(string device) =>
        "[GCPad1]\n" +
        "Device = " + device + "\n" +
        "Buttons/A = `Button E`|EAST\n" +
        "Buttons/B = `Button S`|SOUTH\n" +
        "Buttons/X = `Button N`|NORTH\n" +
        "Buttons/Y = `Button W`|WEST\n" +
        "Buttons/Z = `Shoulder R`|TR\n" +
        "Buttons/Start = `Button Start`|START\n" +
        "Main Stick/Up = `Left Y-`|`Axis 1-`\n" +
        "Main Stick/Down = `Left Y+`|`Axis 1+`\n" +
        "Main Stick/Left = `Left X-`|`Axis 0-`\n" +
        "Main Stick/Right = `Left X+`|`Axis 0+`\n" +
        "C-Stick/Up = `Right Y-`|`Axis 4-`\n" +
        "C-Stick/Down = `Right Y+`|`Axis 4+`\n" +
        "C-Stick/Left = `Right X-`|`Axis 3-`\n" +
        "C-Stick/Right = `Right X+`|`Axis 3+`\n" +
        "Triggers/L = `Trigger L`|`Full Axis 2+`\n" +
        "Triggers/R = `Trigger R`|`Full Axis 5+`\n" +
        "Triggers/L-Analog = `Trigger L`|`Full Axis 2+`\n" +
        "Triggers/R-Analog = `Trigger R`|`Full Axis 5+`\n" +
        "D-Pad/Up = `Pad N`|`Axis 7-`\n" +
        "D-Pad/Down = `Pad S`|`Axis 7+`\n" +
        "D-Pad/Left = `Pad W`|`Axis 6-`\n" +
        "D-Pad/Right = `Pad E`|`Axis 6+`\n" +
        "Rumble/Motor = Strong\n";

    private static string WiiPad(string device)
    {
        var imu = new StringBuilder();
        foreach (var (key, value) in ImuKeys())
            imu.Append(key).Append(" = ").Append(value).Append('\n');
        foreach (var (key, value) in IrKeys())
            imu.Append(key).Append(" = ").Append(value).Append('\n');

        return
            "[Wiimote1]\n" +
            "Device = " + device + "\n" +
            "Source = 1\n" +
            "Buttons/A = `Button S`|SOUTH\n" +
            "Buttons/B = `Button E`|EAST\n" +
            "Buttons/1 = `Button N`|NORTH\n" +
            "Buttons/2 = `Button W`|WEST\n" +
            "Buttons/- = `Button Back`|SELECT\n" +
            "Buttons/+ = `Button Start`|START\n" +
            "Buttons/Home = `Button Guide`|MODE\n" +
            "D-Pad/Up = `Pad N`|`Axis 7-`\n" +
            "D-Pad/Down = `Pad S`|`Axis 7+`\n" +
            "D-Pad/Left = `Pad W`|`Axis 6-`\n" +
            "D-Pad/Right = `Pad E`|`Axis 6+`\n" +
            "Shake/X = `Shoulder L`|TL\n" +
            "Shake/Y = `Shoulder L`|TL\n" +
            "Shake/Z = `Shoulder L`|TL\n" +
            "Nunchuk/Buttons/C = `Shoulder R`|TR\n" +
            "Nunchuk/Buttons/Z = `Trigger R`|`Full Axis 5+`\n" +
            "Nunchuk/Stick/Up = `Left Y-`|`Axis 1-`\n" +
            "Nunchuk/Stick/Down = `Left Y+`|`Axis 1+`\n" +
            "Nunchuk/Stick/Left = `Left X-`|`Axis 0-`\n" +
            "Nunchuk/Stick/Right = `Left X+`|`Axis 0+`\n" +
            "Classic/Buttons/A = `Button E`|EAST\n" +
            "Classic/Buttons/B = `Button S`|SOUTH\n" +
            "Classic/Buttons/X = `Button N`|NORTH\n" +
            "Classic/Buttons/Y = `Button W`|WEST\n" +
            "Classic/Buttons/ZL = `Shoulder L`|TL\n" +
            "Classic/Buttons/ZR = `Shoulder R`|TR\n" +
            "Classic/Buttons/- = `Button Back`|SELECT\n" +
            "Classic/Buttons/+ = `Button Start`|START\n" +
            "Classic/Left Stick/Up = `Left Y-`|`Axis 1-`\n" +
            "Classic/Left Stick/Down = `Left Y+`|`Axis 1+`\n" +
            "Classic/Left Stick/Left = `Left X-`|`Axis 0-`\n" +
            "Classic/Left Stick/Right = `Left X+`|`Axis 0+`\n" +
            "Classic/Right Stick/Up = `Right Y-`|`Axis 4-`\n" +
            "Classic/Right Stick/Down = `Right Y+`|`Axis 4+`\n" +
            "Classic/Right Stick/Left = `Right X-`|`Axis 3-`\n" +
            "Classic/Right Stick/Right = `Right X+`|`Axis 3+`\n" +
            "Classic/Triggers/L = `Trigger L`|`Full Axis 2+`\n" +
            "Classic/Triggers/R = `Trigger R`|`Full Axis 5+`\n" +
            "Classic/D-Pad/Up = `Pad N`|`Axis 7-`\n" +
            "Classic/D-Pad/Down = `Pad S`|`Axis 7+`\n" +
            "Classic/D-Pad/Left = `Pad W`|`Axis 6-`\n" +
            "Classic/D-Pad/Right = `Pad E`|`Axis 6+`\n" +
            imu +
            "IMUIR/Enabled = True\n" +
            "Extension = Nunchuk\n" +
            "Rumble/Motor = Strong\n" +
            "[Wiimote2]\nSource = 0\n[Wiimote3]\nSource = 0\n[Wiimote4]\nSource = 0\n" +
            "[BalanceBoard]\nSource = 0\n";
    }
}
