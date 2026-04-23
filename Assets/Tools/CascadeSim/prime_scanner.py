#!/usr/bin/env python3.12
"""
Prime scanner — reads a level JSON, scans its starting board for every
dict-valid word that would auto-prime at load via PrimeStartingBoardWords
(MatchController.cs:1058-1088), and reports them.

Used by cascade_designer.py to reject candidates with accidental primes,
and by PM to sanity-check a hand-authored level before shipping.

CLI:
  python3.12 prime_scanner.py --level path/to/level_NNN.json
  python3.12 prime_scanner.py --level ... --expected PAW      # pass if only PAW
  python3.12 prime_scanner.py --level ... --expected PAW,CAT  # pass if only these

Exit codes:
  0 — scan complete (use --expected to enforce pass/fail)
  1 — file/parse error
  2 — accidental primes present (only when --expected was set)
"""

from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path

from dict_loader import load_dict

# Import the scan machinery from the sim — single source of truth.
from cascade_sim import CascadeSim, Cell


def scan_level(level_path: Path, dict_words: frozenset[str]) -> list[dict]:
    """Returns a list of {word, cells, direction} dicts, one per auto-primed
    word on the starting board."""
    data = json.loads(Path(level_path).read_text())
    sim = CascadeSim(dict_words)
    # Populate the board WITHOUT calling _prime_starting_board, so we can
    # observe the raw scan results instead of registry mutations.
    for t in (data.get("startingBoard") or []):
        x, y = t["x"], t["y"]
        letter = (t.get("letter") or "").upper()
        variant = (t.get("variant") or "normal")
        if not letter:
            continue
        sim.board[x][y] = Cell(
            letter=letter,
            col=x,
            row=y,
            is_stone=(variant == "stone"),
        )
    matches = sim._scan_entire_board()
    return [
        {
            "word": m.word,
            "cells": [list(c) for c in m.cells],
            "direction": m.direction,
        }
        for m in matches
    ]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", required=True, type=Path)
    ap.add_argument("--dict", type=Path, default=None)
    ap.add_argument(
        "--expected",
        type=str,
        default=None,
        help="Comma-separated list of words that are allowed to prime. "
             "If set, exits non-zero when any other word is found.",
    )
    args = ap.parse_args(argv)

    try:
        dict_words = load_dict(args.dict) if args.dict else load_dict()
        words = scan_level(args.level, dict_words)
    except FileNotFoundError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    print(json.dumps({"primedAtLoad": words, "count": len(words)}, indent=2))

    if args.expected is not None:
        allowed = {w.strip().upper() for w in args.expected.split(",") if w.strip()}
        found = {w["word"] for w in words}
        unexpected = found - allowed
        if unexpected:
            print(
                f"\nFAIL: unexpected primes {sorted(unexpected)} "
                f"(allowed: {sorted(allowed)})",
                file=sys.stderr,
            )
            return 2
        missing = allowed - found
        if missing:
            print(
                f"\nWARN: expected primes not found: {sorted(missing)}",
                file=sys.stderr,
            )
    return 0


if __name__ == "__main__":
    sys.exit(main())
