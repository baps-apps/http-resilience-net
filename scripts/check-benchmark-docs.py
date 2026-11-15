#!/usr/bin/env python3
"""Fails when a benchmark number quoted in the docs disagrees with the raw reports.

Compares the Allocated column of the summary tables in docs/benchmarks/README.md against the raw
BenchmarkDotNet reports beside it. Only allocation is compared: means depend on the machine.

  check-benchmark-docs.py                  against the checked-in reports, exactly
  check-benchmark-docs.py --against DIR    against a fresh run's results directory (BenchmarkDotNet's
                                           --artifacts DIR, then DIR/results), so a regression shows

Rows in VARIABLE depend on thread scheduling, so a fresh run may differ from the docs within their tolerance.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SUMMARY = ROOT / "docs" / "benchmarks" / "README.md"

# The '## ' heading a table sits under -> the raw report that table summarises.
SECTIONS = {
    "Pipeline overhead": "pipeline-overhead.md",
    "Authority matching": "authority-matching.md",
    "Client creation": "client-creation.md",
    "Hedging": "hedging-overhead.md",
    "Limiter contention": "limiter-contention.md",
}

# Benchmark class behind each raw report, for a fresh run's '<namespace>.<class>-report-github.md'.
CLASSES = {
    "pipeline-overhead.md": "PipelineOverheadBenchmarks",
    "authority-matching.md": "AuthorityMatchingBenchmarks",
    "client-creation.md": "ClientCreationBenchmarks",
    "hedging-overhead.md": "HedgingOverheadBenchmarks",
    "limiter-contention.md": "LimiterContentionBenchmarks",
}

# (section, scenario) -> relative tolerance against a fresh run. How much of a hedged attempt is built before
# the first one completes depends on scheduling: four runs spanned 46.4-48.3 KB and 8.97-9.41 KB.
VARIABLE = {
    ("Hedging", "hedged GET (attempt is started)"): 0.10,
    ("Hedging", "hedged POST (attempt is suppressed)"): 0.10,
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


def to_bytes(value: str) -> float:
    number, unit = value.split()
    return float(number) * (1024 if unit == "KB" else 1)


def within(quoted: str, measured: set[str], tolerance: float) -> bool:
    expected = to_bytes(quoted)
    return any(abs(to_bytes(m) - expected) <= expected * tolerance for m in measured)


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


def report_path(report_name: str, fresh: Path | None) -> Path:
    if fresh is None:
        return SUMMARY.parent / report_name
    matches = sorted(fresh.glob(f"*.{CLASSES[report_name]}-report-github.md"))
    return matches[0] if matches else fresh / f"{CLASSES[report_name]}-report-github.md"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--against", type=Path, metavar="DIR",
                        help="a fresh run's BenchmarkDotNet results directory")
    fresh: Path | None = parser.parse_args().against
    if fresh is not None and not fresh.is_dir():
        print(f"error: {fresh} is not a directory", file=sys.stderr)
        return 2

    failures: list[str] = []
    not_run: list[str] = []
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
        path = report_path(report_name, fresh)
        if not path.exists():
            # A fresh run may cover some benchmarks (--filter); a checked-in report must exist.
            if fresh is None:
                failures.append(f"{section}: {path.name} is missing")
            elif section not in not_run:
                not_run.append(section)
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
        tolerance = VARIABLE.get((section, scenario)) if fresh is not None else None
        agrees = within(quoted, measured, tolerance) if tolerance is not None else quoted in measured
        if not agrees:
            allowance = f" (tolerance {tolerance:.0%})" if tolerance is not None else ""
            failures.append(
                f"{section} / {scenario}: README says {quoted}, "
                f"{path.name} says {' / '.join(sorted(measured))}{allowance}"
            )

    if checked == 0 and fresh is not None:
        failures.append(
            f"no benchmark report in {fresh}: pass the run's results directory (--artifacts DIR, then DIR/results)"
        )
    elif checked == 0:
        failures.append(
            "no summary rows matched a raw report -- the tables were renamed or removed, and this "
            "check silently stopped checking anything"
        )

    source = f"the run in {fresh}" if fresh is not None else "the checked-in reports"
    if failures:
        print(f"Benchmark documentation disagrees with {source}:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        print(
            "\nA fresh run that disagrees is a regression, or a change to explain and record. Regenerate the "
            "reports and the tables together:\n"
            "  dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- "
            '--filter "*" --job medium --memory',
            file=sys.stderr,
        )
        return 1

    print(f"Benchmark documentation agrees with {source} ({checked} allocation figures).")
    if not_run:
        print(f"Not in that run, so not compared: {', '.join(not_run)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
