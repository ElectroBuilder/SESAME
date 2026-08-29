#!/usr/bin/env python3
import re
import sys
from pathlib import Path

ver = sys.argv[1] if len(sys.argv) > 1 else ""
text = Path("CHANGELOG.md").read_text(encoding="utf-8")
if not ver:
    print(f"SESAME")
    sys.exit(0)
pattern = rf"^## {re.escape(ver)}\b.*?(?=^## |\Z)"
match = re.search(pattern, text, flags=re.M | re.S)
print((match.group(0).strip() if match else f"SESAME {ver}").strip())
