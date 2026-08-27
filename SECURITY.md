# Security

Please do **not** open public GitHub issues for vulnerabilities that expose SSH keys, API tokens, or remote access.

Email or a private GitHub security advisory on [ElectroBuilder/SESAME](https://github.com/ElectroBuilder/SESAME) is preferred.

SESAME stores secrets in:

- Windows: `%APPDATA%\SESAME\secrets` (DPAPI, current user)
- Linux: `~/.local/share/sesame/secrets` (AES-GCM, mode 600)

Never paste those files into bug reports.
