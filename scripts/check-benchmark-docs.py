#!/usr/bin/env python3
"""Fails when a benchmark number quoted in the docs disagrees with the checked-in raw report.

docs/benchmarks/README.md carries hand-written summary tables beside the raw BenchmarkDotNet reports they
claim to summarise, and says the reports are checked in "so a claim can be re-derived rather than trusted".
They drifted: every raw report said 1,032 B for the standard pipeline -- the parity claim that had already
been retracted in prose on the same page -- while the summary said 1,336 B. Re-measured, the summary was
right and the reports were stale. Nobody noticed, because nothing compared them.

Only the **Allocated** column is compared. Allocation is deterministic and machine-independent; means are
neither, so a check on timings would fail on every machine but the one that generated the table and would be
switched off within a week. Allocation is also the column that actually drifted.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SUMMARY = ROOT / "docs" / "benchmarks" / "README.md"

# The '## ' heading a table sits under -> the raw report that table summarises.
SECTIONS = {
    "Pipeline overhead": "after-pipeline-overhead.md",
    "Authority matching": "after-authority-matching.md",
    "Client creation": "after-client-creation.md",
    "Hedging": "after-hedging-overhead.md",
    "Limiter contention": "after-limiter-contention.md",
}

BYTES = re.compile(r"^([\d,.]+)\s*(B|KB)$")


def cells(line: str) -> list[str]:
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def is_separator(row: list[str]) -> bool:
    return bool(row) and set("".join(row)) <= set("-: ")


def clean(value: str) -> str:
    return value.replace("&#39;", "").replace("**", "").replace("`", "").strip()


def allocation(value: str) -> str | None:
    """Normalises an allocation cell. '**1,336 B**' and '1336 B' both become '1336 B'.

    BenchmarkDotNet writes a dash for zero, which the summary tables spell '0 B'. They are the same
    measurement and must compare equal, or the allocation-free rows -- the ones with the strongest claim
    attached to them -- would be the ones this check could not verify.
    """
    text = clean(value)
    if text in {"-", "\u2013", "\u2014"}:
        return "0 B"

    match = BYTES.match(text)
    return f"{match.group(1).replace(',', '')} {match.group(2)}" if match else None


def report_allocations(path: Path) -> dict[str, set[str]]:
    """Allocated, per method name, from a BenchmarkDotNet github-markdown report."""
    allocations: dict[str, set[str]] = {}
    header: list[str] | None = None

    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|"):
            header = None
            continue

        row = cells(line)
        if is_separator(row):
            continue

        if header is None:
            header = row
            continue

        if "Allocated" not in header or "Method" not in header:
            continue

        method = clean(row[header.index("Method")])
        value = allocation(row[header.index("Allocated")])
        if method and value:
            allocations.setdefault(method, set()).add(value)

    return allocations


def main() -> int:
    failures: list[str] = []
    checked = 0

    section: str | None = None
    header: list[str] | None = None
    cache: dict[str, dict[str, set[str]]] = {}

    for line in SUMMARY.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            title = line[3:].strip()
            section = next((s for s in SECTIONS if title.startswith(s)), None)
            header = None
            continue

        if not line.startswith("|"):
            header = None
            continue

        row = cells(line)
        if is_separator(row):
            continue

        if header is None:
            header = row
            continue

        if section is None or "Allocated" not in header:
            continue

        report_name = SECTIONS[section]
        path = SUMMARY.parent / report_name
        if not path.exists():
            failures.append(f"{section}: {report_name} is missing")
            continue

        if report_name not in cache:
            cache[report_name] = report_allocations(path)

        scenario = clean(row[0])
        quoted = allocation(row[header.index("Allocated")])
        if quoted is None:
            # A row with no allocation figure -- a spacer, or a column holding something else.
            continue

        measured = cache[report_name].get(scenario)
        if measured is None:
            failures.append(f"{section} / {scenario}: no such row in {report_name}")
            continue

        checked += 1
        if quoted not in measured:
            failures.append(
                f"{section} / {scenario}: README says {quoted}, "
                f"{report_name} says {' / '.join(sorted(measured))}"
            )

    if checked == 0:
        failures.append(
            "no summary rows matched a raw report -- the tables were renamed or removed, and this "
            "check silently stopped checking anything"
        )

    if failures:
        print("Benchmark documentation disagrees with the checked-in reports:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            "\nRegenerate the reports and the tables together:\n"
            "  dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- "
            '--filter "*" --job medium --memory',
            file=sys.stderr,
        )
        return 1

    print(f"Benchmark documentation agrees with the checked-in reports ({checked} allocation figures).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
