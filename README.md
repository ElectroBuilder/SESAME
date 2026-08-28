# SESAME

**Steam Easy Shortcut Artwork Manager Engine** — Steam Deck shortcut & artwork manager.

![SESAME](Assets/PNG_Transparent/sesame_logo_horizontal.png)

SESAME scans ROMs, Hydra games and apps on a Steam Deck, writes **its own** Steam shortcuts (Hydra and Steam ROM Manager entries stay untouched), and pulls artwork from SteamGridDB.

It runs in two ways:

1. **Native on the Steam Deck** (SteamOS) — no SSH required  
2. **From Windows over SSH** — same engine, full desktop UI

## Downloads

GitHub Actions builds:

- Windows: `SESAME.exe` (WPF)
- Linux x64 / Steam Deck: self-contained `SESAME` + `install.sh`

## Steam Deck (local)

On the Deck, in **Desktop Mode**:

```bash
chmod +x install.sh
./install.sh
```

That copies SESAME to `~/Applications/SESAME`, installs `.desktop` files, and tries `steamos-add-to-steam` when available.

| Mode | How |
| --- | --- |
| Desktop Mode | Applications menu, or `SESAME --desktop` |
| Game Mode | Add `SESAME` as a non-Steam game, launch options: `--gamemode` |

Game Mode uses large tiles and controller navigation (D-pad / stick, A confirm, B back, L/R tabs). Steam Input can also map those to keyboard keys.

**Applying shortcuts** closes Steam briefly so `shortcuts.vdf` can be written. If SESAME itself was started from Game Mode, do that step in Desktop Mode (or via SSH) — otherwise Steam would quit the app.

Grid artwork for the SESAME shortcut itself lives in `pack/steamdeck/` and `Assets/SteamDeck_Grids/`.

### Flags

```
SESAME --desktop      # windowed desktop UI
SESAME --gamemode     # fullscreen controller UI
SESAME --local        # force local filesystem (this machine)
SESAME --remote       # do not auto-connect locally; use SSH
```

## Windows (SSH)

```powershell
dotnet run --project src\Sesame.Windows\Sesame.Windows.csproj
```

Or open `SESAME.sln`.

1. **Sessies…** — Deck IP, import private key  
2. **Verbinden** — selected profile, then fallbacks, optional Wake-on-LAN  
3. On a Steam Deck running SESAME itself, pick **Deze Steam Deck** (no SSH)

## Features

- File browser with ROM / mods / texture-pack drop targets
- Game Optimizer: shortcuts, SteamGridDB artwork, category masks, collections
- Store tab for mods / texture packs / ROM hacks (you supply your own dumps)
- Optional in-game text tools (experimental)

SESAME does not ship ROMs, keys, or copyrighted game assets.

## Build from source

.NET 8 SDK.

```powershell
dotnet build SESAME.sln -c Release
dotnet publish src\Sesame.Windows\Sesame.Windows.csproj -c Release
dotnet publish src\Sesame.Deck\Sesame.Deck.csproj -c Release -r linux-x64 --self-contained
```

## Layout

| Project | Role |
| --- | --- |
| `src/Sesame.Core` | Local + SSH host, optimizer, catalog |
| `src/Sesame.Windows` | Windows WPF app |
| `src/Sesame.Deck` | Avalonia app for SteamOS |
| `Assets/` | Brand pack (icons, Steam grids) |
| `pack/steamdeck/` | Installer and Deck artwork |

## License

MIT — see [LICENSE](LICENSE).
