#!/usr/bin/env python3
"""Resolve the platform package versions the platform-drift workflow tests against.

The test projects take their platform versions from MSBuild properties in Directory.Packages.props. This prints
the -p: arguments that move them to the newest versions on nuget.org, for one channel:

  pinned   nothing: the versions committed in Directory.Packages.props (the control run)
  current  the newest stable version within the .NET major in global.json: the graph most consumers resolve
  latest   the newest stable version of each package
  preview  the newest version of each package, prerelease included

ASP.NET Core packages target only their own .NET version, so Microsoft.AspNetCore.TestHost is held to the newest
version of the .NET major in global.json; every other package may move past it, as it can for a consumer.

In GitHub Actions it appends `args=...` to $GITHUB_OUTPUT and a table to $GITHUB_STEP_SUMMARY; elsewhere it prints
the arguments, so a run can be reproduced locally:

  dotnet test -c Release $(python3 scripts/resolve-platform-versions.py latest)
"""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.request
from pathlib import Path

FEED = "https://api.nuget.org/v3-flatcontainer/{id}/index.json"

# MSBuild property -> the package whose versions it follows.
PROPERTIES = {
    "ExtensionsTestVersion": "Microsoft.Extensions.Http",
    "HttpResilienceTestVersion": "Microsoft.Extensions.Http.Resilience",
    "PollyTestVersion": "Polly.Core",
}
# Highest major each package may move to. Polly 9 is commercially licensed; the package accepts only 8.x.
MAX_MAJOR = {
    "PollyTestVersion": 8,
}
# Packages versioned with .NET, held to the global.json major on the current channel. Polly is not.
FOLLOWS_RUNTIME_MAJOR = {"ExtensionsTestVersion", "HttpResilienceTestVersion"}
CHANNELS = ("pinned", "current", "latest", "preview")
TEST_HOST_PROPERTY = "AspNetCoreTestVersion"
TEST_HOST_PACKAGE = "Microsoft.AspNetCore.TestHost"

# Versions are passed to MSBuild on the command line, so only what NuGet versions contain is accepted.
SAFE_VERSION = re.compile(r"^[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z.-]+)?$")


def fetch_versions(package_id: str) -> list[str]:
    request = urllib.request.Request(
        FEED.format(id=package_id.lower()), headers={"User-Agent": "platform-drift-check"})
    with urllib.request.urlopen(request, timeout=60) as response:
        versions = json.load(response)["versions"]
    # Build metadata never orders versions; drop it.
    return [v.split("+", 1)[0] for v in versions]


def sort_key(version: str) -> tuple:
    """NuGet ordering: numeric release parts, then a release above any of its prereleases, then the
    prerelease labels compared numerically where both are numbers and case-insensitively otherwise."""
    release, _, prerelease = version.partition("-")
    numbers = [int(part) for part in release.split(".")]
    numbers += [0] * (4 - len(numbers))
    if not prerelease:
        return (numbers, 1, [])
    labels = [(0, int(label), "") if label.isdigit() else (1, 0, label.lower())
              for label in prerelease.split(".")]
    return (numbers, 0, labels)


def newest(versions: list[str], allow_prerelease: bool, major: int | None = None,
           max_major: int | None = None) -> str:
    candidates = [v for v in versions
                  if (allow_prerelease or "-" not in v)
                  and (major is None or sort_key(v)[0][0] == major)
                  and (max_major is None or sort_key(v)[0][0] <= max_major)]
    if not candidates:
        raise SystemExit(f"no {'' if allow_prerelease else 'stable '}version found"
                         f"{'' if major is None else f' for major {major}'}"
                         f"{'' if max_major is None else f' at or below major {max_major}'}")
    best = max(candidates, key=sort_key)
    if not SAFE_VERSION.match(best):
        raise SystemExit(f"refusing unexpected version string {best!r}")
    return best


def runtime_major(repo_root: Path) -> int:
    sdk = json.loads((repo_root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    return int(sdk.split(".", 1)[0])


def main() -> int:
    if len(sys.argv) != 2 or sys.argv[1] not in CHANNELS:
        print(f"usage: resolve-platform-versions.py {'|'.join(CHANNELS)}", file=sys.stderr)
        return 2

    channel = sys.argv[1]
    resolved: dict[str, tuple[str, str]] = {}

    if channel != "pinned":
        allow_prerelease = channel == "preview"
        major = runtime_major(Path(__file__).resolve().parent.parent)
        for prop, package in PROPERTIES.items():
            held = major if channel == "current" and prop in FOLLOWS_RUNTIME_MAJOR else None
            resolved[prop] = (package, newest(
                fetch_versions(package), allow_prerelease, held, MAX_MAJOR.get(prop)))
        resolved[TEST_HOST_PROPERTY] = (
            TEST_HOST_PACKAGE, newest(fetch_versions(TEST_HOST_PACKAGE), allow_prerelease, major))

    args = " ".join(f"-p:{prop}={version}" for prop, (_, version) in resolved.items())

    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"args={args}\n")
    else:
        print(args)

    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(f"### Platform versions: {channel}\n\n")
            if not resolved:
                handle.write("The versions committed in `Directory.Packages.props` (control run).\n")
            else:
                handle.write("| Property | Package | Version |\n| --- | --- | --- |\n")
                for prop, (package, version) in resolved.items():
                    handle.write(f"| `{prop}` | {package} | {version} |\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
