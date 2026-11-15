#!/usr/bin/env python3
"""Fails when README.md links to a repository file by a relative path.

README.md is packed into the NuGet package and shown on the package page, where a relative link resolves
against the feed, not the repository, and breaks. Link to https://github.com/baps-apps/http-resilience-net/blob/main/...
instead. In-page anchors (#section) work on the package page and are allowed.
"""

import pathlib
import re
import sys

README = pathlib.Path(__file__).resolve().parent.parent / "README.md"

# Markdown links and images: [text](target) / ![alt](target), plus HTML src/href attributes.
LINK = re.compile(r"\]\(([^)\s]+)[^)]*\)|(?:src|href)=\"([^\"]+)\"")
ALLOWED = ("http://", "https://", "mailto:", "#")


def main() -> int:
    relative = []
    in_code = False
    for number, line in enumerate(README.read_text(encoding="utf-8").splitlines(), 1):
        if line.lstrip().startswith("```"):
            in_code = not in_code
            continue
        if in_code:
            continue
        for match in LINK.finditer(line):
            target = match.group(1) or match.group(2)
            if not target.startswith(ALLOWED):
                relative.append(f"README.md:{number}: {target}")

    if not relative:
        print("ok: README.md has no relative links")
        return 0

    print(
        "error: README.md links by relative path, which breaks on the package page. Use an absolute "
        "https://github.com/baps-apps/http-resilience-net/blob/main/... link:\n       "
        + "\n       ".join(relative),
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
