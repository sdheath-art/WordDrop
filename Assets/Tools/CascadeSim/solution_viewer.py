#!/usr/bin/env python3.12
"""
Solution viewer — the curation tool for designer-generated levels.

Point it at any level JSON and see everything at a glance:
  - Starting board (6x8 grid, letters + rock markers)
  - Auto-primed words at load (from prime_scanner)
  - Canonical action + full cascade trace (layer-by-layer)
  - Expected final score (full chain + detonation math)
  - Robustness tier (# of legal actions that produce the same layer count)
  - Top alternate solution paths

Use this to decide which generated levels to ship — no need to play every
candidate, just read the report.

CLI:
  python3.12 solution_viewer.py path/to/level_NNN.json
  python3.12 solution_viewer.py level.json --action '{"type":"drop","col":3,"letter":"S"}'
  python3.12 solution_viewer.py level.json --no-explore   # skip the 632-run action scan

If --action is omitted, the viewer guesses: for levels that carry a
canonical "edit (1,1)↔(4,3)" or similar hint (Template A), it uses that;
otherwise it searches for the single highest-layer action and calls that
the "intended" path.
"""

from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path

from dict_loader import load_dict
from cascade_sim import (
    CascadeSim,
    Cell,
    COLS,
    LEVEL_ROWS,
    run_sim_on_data,
    explore_actions_on_data,
)
from prime_scanner import scan_level


# ── Rendering helpers ───────────────────────────────────────────────────────
def render_board(level_data: dict, highlights: dict[tuple[int, int], str] | None = None) -> str:
    """Return an ASCII render of the starting board. `highlights` maps
    (col,row) to a single-char marker (e.g., '*' for primed cells)."""
    highlights = highlights or {}
    # Build 2D char grid (row-major, y=top to y=0 bottom for display).
    grid: list[list[str]] = [["  ." for _ in range(COLS)] for _ in range(LEVEL_ROWS)]
    for t in level_data.get("startingBoard") or []:
        x, y = t["x"], t["y"]
        letter = (t.get("letter") or "").upper() or "?"
        variant = t.get("variant") or "normal"
        if variant == "stone":
            cell = " ##"
        else:
            cell = f" {letter} "
        if (x, y) in highlights:
            cell = cell.replace(" ", highlights[(x, y)], 1)
        grid[y][x] = cell
    # Print from y=7 (top) down to y=0 (bottom).
    lines = []
    for y in range(LEVEL_ROWS - 1, -1, -1):
        row_str = "".join(grid[y][c] for c in range(COLS))
        lines.append(f"  y={y} |{row_str} |")
    lines.append("        " + "".join(f" c{c} " for c in range(COLS)))
    return "\n".join(lines)


def render_layer(i: int, layer: dict) -> list[str]:
    """One cascade layer → 2-3 lines of text."""
    trig = ", ".join(layer.get("triggerWords") or []) or "—"
    primed = ", ".join(
        dict.fromkeys(layer.get("primedWordsTriggered") or []).keys()
    ) or "(self-triggered or trigger-clear only)"
    cleared = len(layer.get("clearedCells") or [])
    stones = layer.get("stonesChainCleared", 0)
    return [
        f"  Layer {i + 1}: words formed = [{trig}]",
        f"           primed detonated = [{primed}]",
        f"           rawStep={layer['chainDepthAtScore']}  "
        f"cluster={layer['clusterSize']}  "
        f"cellsCleared={cleared} (stones={stones})  "
        f"score+={layer['scoreDelta']}",
    ]


def guess_canonical_action(level_data: dict) -> dict | None:
    """Heuristic for Template A layouts. Returns the canonical edit
    (1,1)↔(4,3) only if both cells contain letter tiles."""
    by_pos: dict[tuple[int, int], dict] = {
        (t["x"], t["y"]): t for t in level_data.get("startingBoard") or []
    }
    a = by_pos.get((1, 1))
    b = by_pos.get((4, 3))
    if a and b and a.get("variant", "normal") != "stone" and b.get("variant", "normal") != "stone":
        return {"type": "edit", "cell_a": [1, 1], "cell_b": [4, 3]}
    return None


# ── Main report ─────────────────────────────────────────────────────────────
def view(
    level_path: Path,
    action: dict | None,
    dict_words,
    explore: bool = True,
    top_alt: int = 3,
) -> str:
    level_data = json.loads(Path(level_path).read_text())
    name = level_data.get("displayName") or f"L{level_data.get('levelId', '?')}"
    lvl_id = level_data.get("levelId", "?")

    out: list[str] = []
    out.append("=" * 68)
    out.append(f"Level {lvl_id} — {name}")
    out.append(f"File: {level_path}")
    out.append("=" * 68)
    out.append("")

    # Charges + HUD
    rewrites = level_data.get("rewriteCharges", 0)
    swaps = level_data.get("swapCharges", 0)
    moves = level_data.get("moveBudget", "?")
    stars = level_data.get("starThresholds") or []
    out.append(
        f"moveBudget={moves}  rewriteCharges={rewrites}  "
        f"swapCharges={swaps}  stars={stars}"
    )
    out.append("")

    # Board render — highlight primed cells.
    auto_primes = scan_level(level_path, dict_words)
    primed_cells: dict[tuple[int, int], str] = {}
    for pw in auto_primes:
        for (x, y) in pw["cells"]:
            primed_cells[(x, y)] = "*"
    out.append("Starting board (primed cells marked *, rocks as ##):")
    out.append(render_board(level_data, highlights=primed_cells))
    out.append("")

    # Prime scan summary.
    out.append("Auto-primed at load:")
    if not auto_primes:
        out.append("  (none)")
    else:
        for pw in auto_primes:
            cells = " ".join(f"({c},{r})" for c, r in pw["cells"])
            out.append(f"  {pw['word']}  [{pw['direction']}]  {cells}")
    out.append("")

    # Canonical action.
    resolved_action = action or guess_canonical_action(level_data)
    if resolved_action is None:
        out.append(
            "Canonical action: (not provided, and Template A heuristic didn't "
            "find matching cells at (1,1) and (4,3) — pass --action to specify)"
        )
        print("\n".join(out))
        return "\n".join(out)

    out.append(f"Canonical action: {json.dumps(resolved_action)}")
    out.append("")

    # Run the sim.
    report = run_sim_on_data(level_data, dict_words, resolved_action)
    if "error" in report:
        out.append(f"  ERROR: {report['error']}")
        print("\n".join(out))
        return "\n".join(out)

    layers = report.get("layers") or []
    out.append(f"Cascade trace ({len(layers)} layer(s)):")
    for i, L in enumerate(layers):
        out.extend(render_layer(i, L))
    out.append("")
    out.append(
        f"Canonical total score: {report['totalScore']}  "
        f"finalChainDepth: {report['finalChainDepth']}"
    )
    if report.get("warnings"):
        out.append(f"Warnings: {report['warnings']}")
    out.append("")

    # Robustness scan.
    if not explore:
        out.append("(robustness scan skipped via --no-explore)")
        print("\n".join(out))
        return "\n".join(out)

    scan = explore_actions_on_data(level_data, dict_words, min_layers=1)
    results = scan["results"]
    # Count actions that match the canonical layer count exactly.
    canon_layers = len(layers)
    same_layer_actions = [r for r in results if r["layers"] == canon_layers]
    layer_hist: dict[int, int] = {}
    for r in results:
        layer_hist[r["layers"]] = layer_hist.get(r["layers"], 0) + 1

    out.append(
        f"Robustness: {len(same_layer_actions)} of {scan['totalEnumerated']} "
        f"legal actions produce a {canon_layers}-layer cascade."
    )
    tier = _robustness_tier(len(same_layer_actions))
    out.append(f"  Tier: {tier}")
    out.append(f"  Layer-count histogram: {dict(sorted(layer_hist.items(), reverse=True))}")
    out.append("")

    # Top alternate solutions.
    alts = [
        r for r in results
        if r["layers"] >= canon_layers and r["action"] != resolved_action
    ][:top_alt]
    if alts:
        out.append(f"Top {len(alts)} alternate {canon_layers}+ layer path(s):")
        for r in alts:
            out.append(
                f"  {r['layers']}L  score={r['score']}  "
                f"words={r['words']}  action={json.dumps(r['action'])}"
            )
    else:
        out.append(f"No alternate {canon_layers}+ layer paths found.")

    print("\n".join(out))
    return "\n".join(out)


def _robustness_tier(n: int) -> str:
    if n >= 20:
        return f"very-robust (20+)   — {n} paths"
    if n >= 6:
        return f"robust (6-20)       — {n} paths"
    if n >= 2:
        return f"relaxed (2-5)       — {n} paths"
    if n == 1:
        return "strict (1)          — only the canonical works"
    return "none — action does not solve this level"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("level", type=Path)
    ap.add_argument("--action", type=str, default=None,
                    help="JSON action blob; if omitted, guesses Template A canonical.")
    ap.add_argument("--dict", type=Path, default=None)
    ap.add_argument("--no-explore", action="store_true",
                    help="Skip the robustness scan (faster).")
    ap.add_argument("--top-alt", type=int, default=3,
                    help="Number of alternate solution paths to show.")
    args = ap.parse_args(argv)

    dict_words = load_dict(args.dict) if args.dict else load_dict()
    action = json.loads(args.action) if args.action else None
    view(
        args.level, action, dict_words,
        explore=not args.no_explore, top_alt=args.top_alt,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
