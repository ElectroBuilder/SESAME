#!/usr/bin/env bash
# Thin wrapper: the installer lives at the repo root.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
exec bash "$ROOT/install.sh" "$@"
