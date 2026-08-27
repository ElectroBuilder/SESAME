using System.Text;
using VisualSSH.Models;

namespace VisualSSH.Services.GameOptimizer;

public static class DolphinInput
{
    public const string WrapperName = "vssh-dolphin.sh";

    public static string WrapperPath =>
        DeckClient.Combine(EmulatorProbe.WrapperDir, WrapperName);

    private static readonly string[] ConfigDirs =
    [
        "/home/deck/.var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu",
        "/home/deck/.config/dolphin-emu"
    ];

    public static bool UsesDolphin(SystemProfile profile) =>
        profile.Emulators.Contains("dolphin", StringComparer.OrdinalIgnoreCase) ||
        profile.Id is "gc" or "wii";

    public static bool IsBound(string? target) =>
        (target ?? "").Replace('\\', '/').Contains("vssh-dolphin.sh", StringComparison.OrdinalIgnoreCase);

    public static bool IsBound(OptimizerGame game) =>
        IsBound(game.Target) || IsBound(game.LaunchOptions);

    public static bool NeedsRebind(OptimizerGame game, SystemProfile profile) =>
        UsesDolphin(profile) && !IsBound(game);

    public static void Ensure(DeckClient client)
    {
        InstallWrapper(client);
        foreach (var dir in ConfigDirs)
        {
            try
            {
                client.EnsureDirectory(dir);
                PatchIni(client, DeckClient.Combine(dir, "Dolphin.ini"));
                client.WriteText(DeckClient.Combine(dir, "GCPadNew.ini"),
                    GcPad("evdev/0/Microsoft X-Box 360 pad 0", evdev: true));
                client.WriteText(DeckClient.Combine(dir, "WiimoteNew.ini"),
                    WiiPad("evdev/0/Microsoft X-Box 360 pad 0", evdev: true));
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
        client.WriteText(WrapperPath, Wrapper);
        try
        {
            client.Execute("chmod +x " + DeckClient.ShQuote(WrapperPath) +
                           " ; sed -i 's/\\r$//' " + DeckClient.ShQuote(WrapperPath), 8);
        }
        catch
        {
            /* Steam kan /bin/bash als fallback gebruiken */
        }
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

    private static string Quote(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim().Trim('"');
        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static string GcPad(string device, bool evdev) =>
        evdev
            ? "[GCPad1]\n" +
              "Device = " + device + "\n" +
              "Buttons/A = EAST\n" +
              "Buttons/B = SOUTH\n" +
              "Buttons/X = NORTH\n" +
              "Buttons/Y = WEST\n" +
              "Buttons/Z = TR\n" +
              "Buttons/Start = START\n" +
              "Main Stick/Up = `Axis 1-`\n" +
              "Main Stick/Down = `Axis 1+`\n" +
              "Main Stick/Left = `Axis 0-`\n" +
              "Main Stick/Right = `Axis 0+`\n" +
              "C-Stick/Up = `Axis 4-`\n" +
              "C-Stick/Down = `Axis 4+`\n" +
              "C-Stick/Left = `Axis 3-`\n" +
              "C-Stick/Right = `Axis 3+`\n" +
              "Triggers/L = `Full Axis 2+`\n" +
              "Triggers/R = `Full Axis 5+`\n" +
              "Triggers/L-Analog = `Full Axis 2+`\n" +
              "Triggers/R-Analog = `Full Axis 5+`\n" +
              "D-Pad/Up = `Axis 7-`\n" +
              "D-Pad/Down = `Axis 7+`\n" +
              "D-Pad/Left = `Axis 6-`\n" +
              "D-Pad/Right = `Axis 6+`\n" +
              "Rumble/Motor = Strong\n"
            : "[GCPad1]\n" +
              "Device = " + device + "\n" +
              "Buttons/A = `Button E`\n" +
              "Buttons/B = `Button S`\n" +
              "Buttons/X = `Button N`\n" +
              "Buttons/Y = `Button W`\n" +
              "Buttons/Z = `Shoulder R`\n" +
              "Buttons/Start = `Button Start`\n" +
              "Main Stick/Up = `Left Y-`\n" +
              "Main Stick/Down = `Left Y+`\n" +
              "Main Stick/Left = `Left X-`\n" +
              "Main Stick/Right = `Left X+`\n" +
              "C-Stick/Up = `Right Y-`\n" +
              "C-Stick/Down = `Right Y+`\n" +
              "C-Stick/Left = `Right X-`\n" +
              "C-Stick/Right = `Right X+`\n" +
              "Triggers/L = `Trigger L`\n" +
              "Triggers/R = `Trigger R`\n" +
              "Triggers/L-Analog = `Trigger L`\n" +
              "Triggers/R-Analog = `Trigger R`\n" +
              "D-Pad/Up = `Pad N`\n" +
              "D-Pad/Down = `Pad S`\n" +
              "D-Pad/Left = `Pad W`\n" +
              "D-Pad/Right = `Pad E`\n" +
              "Rumble/Motor = Strong\n";

    private static string WiiPad(string device, bool evdev) =>
        "[Wiimote1]\n" +
        "Device = " + device + "\n" +
        "Source = 1\n" +
        (evdev
            ? "Buttons/A = SOUTH\nButtons/B = EAST\nButtons/1 = NORTH\nButtons/2 = WEST\n" +
              "Buttons/- = SELECT\nButtons/+ = START\nButtons/Home = MODE\n" +
              "D-Pad/Up = `Axis 7-`\nD-Pad/Down = `Axis 7+`\nD-Pad/Left = `Axis 6-`\nD-Pad/Right = `Axis 6+`\n" +
              "IR/Up = `Axis 4-`\nIR/Down = `Axis 4+`\nIR/Left = `Axis 3-`\nIR/Right = `Axis 3+`\n" +
              "Shake/X = TL\nShake/Y = TL\nShake/Z = TL\n" +
              "Nunchuk/Buttons/C = TR\nNunchuk/Buttons/Z = `Full Axis 5+`\n" +
              "Nunchuk/Stick/Up = `Axis 1-`\nNunchuk/Stick/Down = `Axis 1+`\n" +
              "Nunchuk/Stick/Left = `Axis 0-`\nNunchuk/Stick/Right = `Axis 0+`\n" +
              "Classic/Buttons/A = EAST\nClassic/Buttons/B = SOUTH\nClassic/Buttons/X = NORTH\nClassic/Buttons/Y = WEST\n" +
              "Classic/Buttons/ZL = TL\nClassic/Buttons/ZR = TR\nClassic/Buttons/- = SELECT\nClassic/Buttons/+ = START\n" +
              "Classic/Left Stick/Up = `Axis 1-`\nClassic/Left Stick/Down = `Axis 1+`\n" +
              "Classic/Left Stick/Left = `Axis 0-`\nClassic/Left Stick/Right = `Axis 0+`\n" +
              "Classic/Right Stick/Up = `Axis 4-`\nClassic/Right Stick/Down = `Axis 4+`\n" +
              "Classic/Right Stick/Left = `Axis 3-`\nClassic/Right Stick/Right = `Axis 3+`\n" +
              "Classic/Triggers/L = `Full Axis 2+`\nClassic/Triggers/R = `Full Axis 5+`\n" +
              "Classic/D-Pad/Up = `Axis 7-`\nClassic/D-Pad/Down = `Axis 7+`\n" +
              "Classic/D-Pad/Left = `Axis 6-`\nClassic/D-Pad/Right = `Axis 6+`\n"
            : "Buttons/A = `Button S`\nButtons/B = `Button E`\nButtons/1 = `Button N`\nButtons/2 = `Button W`\n" +
              "Buttons/- = `Button Back`\nButtons/+ = `Button Start`\nButtons/Home = `Button Guide`\n" +
              "D-Pad/Up = `Pad N`\nD-Pad/Down = `Pad S`\nD-Pad/Left = `Pad W`\nD-Pad/Right = `Pad E`\n" +
              "IR/Up = `Right Y-`\nIR/Down = `Right Y+`\nIR/Left = `Right X-`\nIR/Right = `Right X+`\n" +
              "Shake/X = `Shoulder L`\nShake/Y = `Shoulder L`\nShake/Z = `Shoulder L`\n" +
              "Nunchuk/Buttons/C = `Shoulder R`\nNunchuk/Buttons/Z = `Trigger R`\n" +
              "Nunchuk/Stick/Up = `Left Y-`\nNunchuk/Stick/Down = `Left Y+`\n" +
              "Nunchuk/Stick/Left = `Left X-`\nNunchuk/Stick/Right = `Left X+`\n" +
              "Classic/Buttons/A = `Button E`\nClassic/Buttons/B = `Button S`\n" +
              "Classic/Buttons/X = `Button N`\nClassic/Buttons/Y = `Button W`\n" +
              "Classic/Buttons/ZL = `Shoulder L`\nClassic/Buttons/ZR = `Shoulder R`\n" +
              "Classic/Buttons/- = `Button Back`\nClassic/Buttons/+ = `Button Start`\n" +
              "Classic/Left Stick/Up = `Left Y-`\nClassic/Left Stick/Down = `Left Y+`\n" +
              "Classic/Left Stick/Left = `Left X-`\nClassic/Left Stick/Right = `Left X+`\n" +
              "Classic/Right Stick/Up = `Right Y-`\nClassic/Right Stick/Down = `Right Y+`\n" +
              "Classic/Right Stick/Left = `Right X-`\nClassic/Right Stick/Right = `Right X+`\n" +
              "Classic/Triggers/L = `Trigger L`\nClassic/Triggers/R = `Trigger R`\n" +
              "Classic/D-Pad/Up = `Pad N`\nClassic/D-Pad/Down = `Pad S`\n" +
              "Classic/D-Pad/Left = `Pad W`\nClassic/D-Pad/Right = `Pad E`\n") +
        "IR/Auto-Hide = False\n" +
        "Extension = Nunchuk\n" +
        "Rumble/Motor = Strong\n" +
        "[Wiimote2]\nSource = 0\n[Wiimote3]\nSource = 0\n[Wiimote4]\nSource = 0\n" +
        "[BalanceBoard]\nSource = 0\n";

    private static string Wrapper => """
#!/bin/bash
stripq() { local s="$1"; s="${s#\"}"; s="${s%\"}"; s="${s#\'}"; s="${s%\'}"; printf '%s' "$s"; }
ROM=""
prev=""
for a in "$@"; do
  a=$(stripq "$a")
  case "$a" in
    -b|--batch|-e|--exec) prev=e; continue ;;
  esac
  if [ "$prev" = e ]; then ROM="$a"; prev=""; continue; fi
  case "$a" in
    -*) continue ;;
  esac
  ROM="$a"
done
if [ -z "$ROM" ]; then echo "VisualSSH: geen ROM" >&2; exit 1; fi
export HOME="${HOME:-/home/deck}"
export SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1
export SDL_JOYSTICK_HIDAPI_STEAM=1
python3 - <<'PY'
import os, pathlib, re

def names():
    try:
        raw = pathlib.Path('/proc/bus/input/devices').read_text(errors='ignore')
    except Exception:
        return []
    return re.findall(r'N: Name="([^"]+)"', raw)

def find(needles, pool):
    for orig in pool:
        low = orig.lower()
        if any(n in low for n in needles):
            return orig
    return None

def pick(pool):
    steam = os.environ.get('SteamAppId') or os.environ.get('SteamGameId') or os.environ.get('SteamDeck')
    n = find(('microsoft x-box 360', 'x-box 360 pad', 'xbox 360'), pool)
    if n:
        return f'evdev/0/{n}', 'evdev'
    n = find(('steam virtual gamepad',), pool)
    if n:
        return f'SDL/0/{n}', 'sdl'
    n = find(('8bitdo', '8bitdo'), pool)
    if n:
        return f'evdev/0/{n}', 'evdev'
    n = find(('xbox wireless', 'xbox one', 'xbox controller'), pool)
    if n:
        return f'evdev/0/{n}', 'evdev'
    n = find(('joy-con', 'nintendo switch combined', 'nintendo switch pro'), pool)
    if n:
        return f'evdev/0/{n}', 'evdev'
    n = find(('dualsense', 'wireless controller', 'dualshock'), pool)
    if n:
        return f'evdev/0/{n}', 'evdev'
    if steam:
        return 'evdev/0/Microsoft X-Box 360 pad 0', 'evdev'
    n = find(('steam deck',), pool)
    if n:
        return 'SteamDeck/0/SteamDeck Controller', 'sdl'
    return 'SDL/0/Steam Deck Controller', 'sdl'

def gc_ini(dev, style):
    if style == 'evdev':
        return (
            '[GCPad1]\n'
            f'Device = {dev}\n'
            'Buttons/A = EAST\nButtons/B = SOUTH\nButtons/X = NORTH\nButtons/Y = WEST\n'
            'Buttons/Z = TR\nButtons/Start = START\n'
            'Main Stick/Up = `Axis 1-`\nMain Stick/Down = `Axis 1+`\n'
            'Main Stick/Left = `Axis 0-`\nMain Stick/Right = `Axis 0+`\n'
            'C-Stick/Up = `Axis 4-`\nC-Stick/Down = `Axis 4+`\n'
            'C-Stick/Left = `Axis 3-`\nC-Stick/Right = `Axis 3+`\n'
            'Triggers/L = `Full Axis 2+`\nTriggers/R = `Full Axis 5+`\n'
            'Triggers/L-Analog = `Full Axis 2+`\nTriggers/R-Analog = `Full Axis 5+`\n'
            'D-Pad/Up = `Axis 7-`\nD-Pad/Down = `Axis 7+`\n'
            'D-Pad/Left = `Axis 6-`\nD-Pad/Right = `Axis 6+`\n'
            'Rumble/Motor = Strong\n'
        )
    return (
        '[GCPad1]\n'
        f'Device = {dev}\n'
        'Buttons/A = `Button E`\nButtons/B = `Button S`\nButtons/X = `Button N`\nButtons/Y = `Button W`\n'
        'Buttons/Z = `Shoulder R`\nButtons/Start = `Button Start`\n'
        'Main Stick/Up = `Left Y-`\nMain Stick/Down = `Left Y+`\n'
        'Main Stick/Left = `Left X-`\nMain Stick/Right = `Left X+`\n'
        'C-Stick/Up = `Right Y-`\nC-Stick/Down = `Right Y+`\n'
        'C-Stick/Left = `Right X-`\nC-Stick/Right = `Right X+`\n'
        'Triggers/L = `Trigger L`\nTriggers/R = `Trigger R`\n'
        'Triggers/L-Analog = `Trigger L`\nTriggers/R-Analog = `Trigger R`\n'
        'D-Pad/Up = `Pad N`\nD-Pad/Down = `Pad S`\nD-Pad/Left = `Pad W`\nD-Pad/Right = `Pad E`\n'
        'Rumble/Motor = Strong\n'
    )

def wii_ini(dev, style):
    if style == 'evdev':
        buttons = (
            'Buttons/A = SOUTH\nButtons/B = EAST\nButtons/1 = NORTH\nButtons/2 = WEST\n'
            'Buttons/- = SELECT\nButtons/+ = START\nButtons/Home = MODE\n'
            'D-Pad/Up = `Axis 7-`\nD-Pad/Down = `Axis 7+`\nD-Pad/Left = `Axis 6-`\nD-Pad/Right = `Axis 6+`\n'
            'IR/Up = `Axis 4-`\nIR/Down = `Axis 4+`\nIR/Left = `Axis 3-`\nIR/Right = `Axis 3+`\n'
            'Shake/X = TL\nShake/Y = TL\nShake/Z = TL\n'
            'Nunchuk/Buttons/C = TR\nNunchuk/Buttons/Z = `Full Axis 5+`\n'
            'Nunchuk/Stick/Up = `Axis 1-`\nNunchuk/Stick/Down = `Axis 1+`\n'
            'Nunchuk/Stick/Left = `Axis 0-`\nNunchuk/Stick/Right = `Axis 0+`\n'
            'Classic/Buttons/A = EAST\nClassic/Buttons/B = SOUTH\nClassic/Buttons/X = NORTH\nClassic/Buttons/Y = WEST\n'
            'Classic/Buttons/ZL = TL\nClassic/Buttons/ZR = TR\nClassic/Buttons/- = SELECT\nClassic/Buttons/+ = START\n'
            'Classic/Left Stick/Up = `Axis 1-`\nClassic/Left Stick/Down = `Axis 1+`\n'
            'Classic/Left Stick/Left = `Axis 0-`\nClassic/Left Stick/Right = `Axis 0+`\n'
            'Classic/Right Stick/Up = `Axis 4-`\nClassic/Right Stick/Down = `Axis 4+`\n'
            'Classic/Right Stick/Left = `Axis 3-`\nClassic/Right Stick/Right = `Axis 3+`\n'
            'Classic/Triggers/L = `Full Axis 2+`\nClassic/Triggers/R = `Full Axis 5+`\n'
            'Classic/D-Pad/Up = `Axis 7-`\nClassic/D-Pad/Down = `Axis 7+`\n'
            'Classic/D-Pad/Left = `Axis 6-`\nClassic/D-Pad/Right = `Axis 6+`\n'
        )
    else:
        buttons = (
            'Buttons/A = `Button S`\nButtons/B = `Button E`\nButtons/1 = `Button N`\nButtons/2 = `Button W`\n'
            'Buttons/- = `Button Back`\nButtons/+ = `Button Start`\nButtons/Home = `Button Guide`\n'
            'D-Pad/Up = `Pad N`\nD-Pad/Down = `Pad S`\nD-Pad/Left = `Pad W`\nD-Pad/Right = `Pad E`\n'
            'IR/Up = `Right Y-`\nIR/Down = `Right Y+`\nIR/Left = `Right X-`\nIR/Right = `Right X+`\n'
            'Shake/X = `Shoulder L`\nShake/Y = `Shoulder L`\nShake/Z = `Shoulder L`\n'
            'Nunchuk/Buttons/C = `Shoulder R`\nNunchuk/Buttons/Z = `Trigger R`\n'
            'Nunchuk/Stick/Up = `Left Y-`\nNunchuk/Stick/Down = `Left Y+`\n'
            'Nunchuk/Stick/Left = `Left X-`\nNunchuk/Stick/Right = `Left X+`\n'
            'Classic/Buttons/A = `Button E`\nClassic/Buttons/B = `Button S`\n'
            'Classic/Buttons/X = `Button N`\nClassic/Buttons/Y = `Button W`\n'
            'Classic/Buttons/ZL = `Shoulder L`\nClassic/Buttons/ZR = `Shoulder R`\n'
            'Classic/Buttons/- = `Button Back`\nClassic/Buttons/+ = `Button Start`\n'
            'Classic/Left Stick/Up = `Left Y-`\nClassic/Left Stick/Down = `Left Y+`\n'
            'Classic/Left Stick/Left = `Left X-`\nClassic/Left Stick/Right = `Left X+`\n'
            'Classic/Right Stick/Up = `Right Y-`\nClassic/Right Stick/Down = `Right Y+`\n'
            'Classic/Right Stick/Left = `Right X-`\nClassic/Right Stick/Right = `Right X+`\n'
            'Classic/Triggers/L = `Trigger L`\nClassic/Triggers/R = `Trigger R`\n'
            'Classic/D-Pad/Up = `Pad N`\nClassic/D-Pad/Down = `Pad S`\n'
            'Classic/D-Pad/Left = `Pad W`\nClassic/D-Pad/Right = `Pad E`\n'
        )
    return (
        '[Wiimote1]\n'
        f'Device = {dev}\n'
        'Source = 1\n' + buttons +
        'IR/Auto-Hide = False\nExtension = Nunchuk\nRumble/Motor = Strong\n'
        '[Wiimote2]\nSource = 0\n[Wiimote3]\nSource = 0\n[Wiimote4]\nSource = 0\n'
        '[BalanceBoard]\nSource = 0\n'
    )

def patch_dolphin_ini(path: pathlib.Path):
    cur = path.read_text() if path.exists() else '[Core]\n'
    def set_key(text, section, key, value):
        header = f'[{section}]'
        line = f'{key} = {value}'
        if re.search(rf'(?im)^{re.escape(key)}\s*=', text):
            return re.sub(rf'(?im)^{re.escape(key)}\s*=.*$', line, text)
        if re.search(rf'(?i)\[{re.escape(section)}\]', text):
            return re.sub(rf'(?i)\[{re.escape(section)}\]', header + '\n' + line, text, count=1)
        return text.rstrip() + f'\n{header}\n{line}\n'
    cur = set_key(cur, 'Core', 'SIDevice0', '6')
    cur = set_key(cur, 'Core', 'WiimoteSource0', '1')
    cur = set_key(cur, 'Input', 'BackgroundInput', 'True')
    path.write_text(cur)

def dirs():
    return [
        os.path.expanduser('~/.var/app/org.DolphinEmu.dolphin-emu/config/dolphin-emu'),
        os.path.expanduser('~/.config/dolphin-emu'),
        os.path.expanduser('~/Emulation/storage/dolphin-emu'),
    ]

dev, style = pick(names())
gc = gc_ini(dev, style)
wii = wii_ini(dev, style)
for d in dirs():
    p = pathlib.Path(d)
    try:
        p.mkdir(parents=True, exist_ok=True)
        (p / 'GCPadNew.ini').write_text(gc)
        (p / 'WiimoteNew.ini').write_text(wii)
        patch_dolphin_ini(p / 'Dolphin.ini')
    except Exception:
        pass
PY
GC="$HOME/Emulation/tools/launchers/gc.sh"
WII="$HOME/Emulation/tools/launchers/wii.sh"
DOL="$HOME/Emulation/tools/launchers/dolphin.sh"
DOL2="$HOME/Emulation/tools/launchers/dolphin-emu.sh"
case "$ROM" in
  */roms/wii/*|*/wii/*) set -- "$WII" "$DOL" "$DOL2" "$GC" ;;
  *) set -- "$GC" "$DOL" "$DOL2" "$WII" ;;
esac
for s in "$@"; do
  # EmuDeck-scripts verwachten alleen het ROM-pad, geen Dolphin -b -e.
  if [ -x "$s" ]; then exec "$s" "$ROM"; fi
done
exec /usr/bin/flatpak run --filesystem=host --device=all org.DolphinEmu.dolphin-emu -b -e "$ROM"
""";
}
