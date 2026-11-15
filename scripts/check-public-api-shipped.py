#!/usr/bin/env python3
"""Fails when PublicAPI.Unshipped.txt still holds API on a tagged (release) build.

PublicApiAnalyzers gates changes to the public surface on every build; this gates the release step of moving
Unshipped entries into Shipped (see docs/VERSIONING.md). Runs only on tags.
"""

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
UNSHIPPED = ROOT / "src" / "HttpResilience.NET" / "PublicAPI.Unshipped.txt"

# Both files carry this marker; it is not an API entry.
MARKERS = {"#nullable enable", "#nullable disable"}


def main() -> int:
    if not UNSHIPPED.is_file():
        print(f"error: {UNSHIPPED} not found", file=sys.stderr)
        return 1

    entries = [
        line.strip()
        for line in UNSHIPPED.read_text(encoding="utf-8").splitlines()
        if line.strip() and line.strip() not in MARKERS
    ]

    if not entries:
        print(f"ok: {UNSHIPPED.relative_to(ROOT)} holds no unshipped API")
        return 0

    print(
        f"error: {UNSHIPPED.relative_to(ROOT)} still lists {len(entries)} public member(s) as unshipped on a "
        "release build.\n"
        "       Move its contents into PublicAPI.Shipped.txt as part of cutting the release -- see "
        "docs/VERSIONING.md.\n"
        "       Otherwise every member stays 'unshipped' forever and the distinction stops meaning anything.\n",
        file=sys.stderr,
    )
    for entry in entries[:20]:
        print(f"       {entry}", file=sys.stderr)
    if len(entries) > 20:
        print(f"       ... and {len(entries) - 20} more", file=sys.stderr)

    return 1


if __name__ == "__main__":
    sys.exit(main())
