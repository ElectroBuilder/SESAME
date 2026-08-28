# Contributing to SESAME

Thanks for helping. SESAME is **Steam Easy Shortcut Artwork Manager Engine**.

## Dev setup

- .NET 8 SDK
- Windows: Visual Studio 2022 or `dotnet build SESAME.sln`
- Linux / Steam Deck: `dotnet publish src/Sesame.Deck/Sesame.Deck.csproj -c Release -r linux-x64 --self-contained`

## Layout

- `src/Sesame.Core` — shared services (local filesystem + SSH)
- `src/Sesame.Windows` — Windows WPF desktop (full UI)
- `src/Sesame.Deck` — Avalonia UI for SteamOS (desktop + game mode)

## Rules

- Do not commit secrets, SSH keys, ROM dumps, or `%APPDATA%/SESAME` user data.
- Keep Steam shortcut writes scoped to SESAME-owned entries; leave Hydra/SRM items alone.
- Prefer small PRs with a test note (Windows remote SSH and/or Deck local).

## License

By contributing you agree that your work is licensed under the MIT License.
