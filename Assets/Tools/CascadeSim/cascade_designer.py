#!/usr/bin/env python3.12
"""
Cascade designer — iterates equations × layouts × letter combinations,
runs each through the sim, keeps candidates whose cascade matches the
equation's expected trace. Rejects boards with accidental primes.

Phase 10.5f-v2: the hardcoded Template A path was replaced by a generic
equation-driven runner. See cascade_equations.py for the spec catalog.

CLI:
  # Single equation:
  python3.12 cascade_designer.py --equation eq_3v_4h_4h_3h_linear \\
      --cap 500 --min-robustness 3 --output-dir /tmp/cascade_batch/

  # All implemented equations:
  python3.12 cascade_designer.py --equation all --cap 500 \\
      --per-equation-cap 100 --output-dir /tmp/cascade_batch/

  # Difficulty presets:
  python3.12 cascade_designer.py --equation all --difficulty tutorial
  python3.12 cascade_designer.py --equation all --difficulty puzzle
"""

from __future__ import annotations
import argparse
import json
import random
import sys
from pathlib import Path

from dict_loader import load_dict
from cascade_sim import (
    CascadeSim,
    Cell,
    explore_actions_on_data,
    run_sim_on_data,
    COLS,
    LEVEL_ROWS,
)
from cascade_equations import (
    EQUATIONS,
    IMPLEMENTED_IDS,
    Equation,
    Layout,
    Candidate,
)


# ── Shared utilities ────────────────────────────────────────────────────────
def _scan_board_only(level_data: dict, dict_words: frozenset[str]) -> set[str]:
    """Dict-valid words auto-primed at load (no sim run). Matches the
    engine's MatchController.PrimeStartingBoardWords behavior."""
    sim = CascadeSim(dict_words)
    for t in (level_data.get("startingBoard") or []):
        x, y = t["x"], t["y"]
        letter = (t.get("letter") or "").upper()
        variant = (t.get("variant") or "normal")
        if not letter:
            continue
        sim.board[x][y] = Cell(
            letter=letter, col=x, row=y, is_stone=(variant == "stone")
        )
    return {m.word for m in sim._scan_entire_board()}


def _expected_auto_primes(cand: Candidate) -> set[str]:
    """The set of words that SHOULD auto-prime at level load — every word
    the equation intends to pre-prime. Derived from the candidate's word
    map by role prefix ("preprime"/"pp_")."""
    expected: set[str] = set()
    for role, word in cand.words.items():
        if role == "preprime" or role.startswith("pp_"):
            expected.add(word)
    return expected


def robustness_tier(n: int) -> str:
    if n >= 20:
        return "very-robust (20+)"
    if n >= 6:
        return "robust (6-20)"
    if n >= 2:
        return "relaxed (2-5)"
    if n == 1:
        return "strict (1)"
    return "none"


def calibrate_stars(canonical_score: int) -> list[int]:
    """Star thresholds from canonical cascade total (0.55× / 0.80× / 1.0×).
    Forces strict ordering."""
    if canonical_score <= 0:
        return [1, 2, 3]
    one = max(1, round(canonical_score * 0.55))
    two = max(one + 1, round(canonical_score * 0.80))
    three = max(two + 1, canonical_score)
    return [one, two, three]


# ── Level JSON construction ─────────────────────────────────────────────────
def build_level_json(
    equation: Equation,
    layout: Layout,
    cand: Candidate,
    level_id: int,
) -> dict:
    """Wrap the equation's board output in the level-JSON shell used by
    the Unity runtime."""
    board = equation.build_board(layout, cand)
    # Build a readable display name from the words.
    word_list = list(cand.words.values())
    display = f"{equation.id.split('_', 1)[0]} · {'→'.join(word_list)}  [{layout.name}]"
    return {
        "schemaVersion": 1,
        "levelId": level_id,
        "displayName": display,
        "target": 300,                       # recalibrated post-evaluation
        "moveBudget": 5,
        "starThresholds": [300, 600, 1000],  # recalibrated post-evaluation
        "allowedMechanics": [],
        "hazards": [],
        "allowFail": True,
        "rewriteCharges": 1,
        "swapCharges": 0,
        "hudFlags": {
            "showSwapCharges": False,
            "showEditCharges": True,
            "showChainMeter": False,
        },
        "startingBoard": board,
        "bag": None,
        "seed": 0,
        "visualCues": {},
        "tutorialPrompts": [
            {"on": "start", "text": f"Edit to form {cand.words.get('trigger', '')} "
                                    f"and watch the cascade."},
        ],
        # Designer metadata (preserved in the JSON so solution_viewer can
        # show which equation/layout this level came from and drive the
        # canonical-action probe without hard-coded coords).
        "designer": {
            "equationId": equation.id,
            "layoutName": layout.name,
            "words": cand.words,
            "canonicalAction": layout.canonical_action,
        },
    }


# ── Candidate evaluation ────────────────────────────────────────────────────
def evaluate_candidate(
    equation: Equation,
    layout: Layout,
    cand: Candidate,
    level_data: dict,
    dict_words: frozenset[str],
) -> dict:
    """Full pipeline: prime scan → canonical action → robustness count.

    Returns {"pass": bool, "reason": str, "robustness": int, ...}."""
    # 1. Prime scanner — only the equation's expected preprimes may fire.
    auto_primes = _scan_board_only(level_data, dict_words)
    expected = _expected_auto_primes(cand)
    unexpected = auto_primes - expected
    missing = expected - auto_primes
    if unexpected:
        return {"pass": False,
                "reason": f"accidental primes: {sorted(unexpected)}",
                "robustness": 0}
    if missing:
        return {"pass": False,
                "reason": f"missing expected primes: {sorted(missing)}",
                "robustness": 0}

    # 2. Canonical action — must produce the expected trace.
    report = run_sim_on_data(level_data, dict_words, layout.canonical_action)
    if "error" in report:
        return {"pass": False,
                "reason": f"action error: {report['error']}",
                "robustness": 0}
    layers = report.get("layers", [])
    ok, reason = equation.validate_trace(cand, layers)
    if not ok:
        return {"pass": False, "reason": reason, "robustness": 0,
                "seenWords": [L.get("triggerWords") for L in layers]}

    # 3. Robustness — count legal actions that produce >= layer_count layers.
    explore = explore_actions_on_data(level_data, dict_words, min_layers=1)
    robustness = sum(
        1 for r in explore["results"] if r["layers"] >= equation.layer_count
    )
    return {
        "pass": True,
        "reason": "ok",
        "robustness": robustness,
        "canonicalScore": report.get("totalScore", 0),
        "finalChainDepth": report.get("finalChainDepth", 0),
        "enumerated": explore["totalEnumerated"],
    }


# ── Equation-level runner ───────────────────────────────────────────────────
def run_equation(
    equation: Equation,
    dict_words: frozenset[str],
    candidate_cap: int | None = 500,
    min_robustness: int = 1,
    max_robustness: int | None = None,
    per_layout_cap: int | None = 50,
    start_level_id: int = 1000,
    shuffle_seed: int = 42,
) -> tuple[list[dict], int, dict[str, int]]:
    """Iterate this equation's layouts × candidates, evaluate each,
    collect passing levels. Returns (passing, attempts, failed_reasons)."""
    passing: list[dict] = []
    failed: dict[str, int] = {}
    attempts = 0
    next_id = start_level_id
    rng = random.Random(shuffle_seed)

    layouts = list(equation.iter_layouts())
    rng.shuffle(layouts)

    for layout in layouts:
        layout_yielded = 0
        for cand in equation.iter_candidates(dict_words, layout, rng,
                                              cap=per_layout_cap):
            attempts += 1
            level = build_level_json(equation, layout, cand, next_id)
            result = evaluate_candidate(
                equation, layout, cand, level, dict_words
            )
            if not result["pass"]:
                key = result["reason"].split("(")[0].split(":")[0].strip()
                failed[key] = failed.get(key, 0) + 1
                continue
            if result["robustness"] < min_robustness:
                failed["below min_robustness"] = (
                    failed.get("below min_robustness", 0) + 1
                )
                continue
            if max_robustness is not None and result["robustness"] > max_robustness:
                failed["above max_robustness"] = (
                    failed.get("above max_robustness", 0) + 1
                )
                continue
            stars = calibrate_stars(result["canonicalScore"])
            level["starThresholds"] = stars
            level["target"] = stars[0]
            passing.append({
                "equationId": equation.id,
                "layoutName": layout.name,
                "words": cand.words,
                "letters": cand.letters,
                "robustness": result["robustness"],
                "tier": robustness_tier(result["robustness"]),
                "canonicalScore": result["canonicalScore"],
                "starThresholds": stars,
                "finalChainDepth": result["finalChainDepth"],
                "level": level,
            })
            next_id += 1
            layout_yielded += 1
            if candidate_cap is not None and attempts >= candidate_cap:
                break
        if candidate_cap is not None and attempts >= candidate_cap:
            break

    passing.sort(key=lambda r: (-r["robustness"], -r["canonicalScore"]))
    return passing, attempts, failed


def run_all_equations(
    equation_ids: list[str],
    dict_words: frozenset[str],
    candidate_cap_per_eq: int = 500,
    min_robustness: int = 1,
    max_robustness: int | None = None,
    per_layout_cap: int | None = 50,
    start_level_id: int = 1000,
    shuffle_seed: int = 42,
) -> dict:
    """Run a list of equations. Returns a dict keyed by equation id with
    per-equation results + combined summary."""
    combined: dict = {
        "byEquation": {},
        "totalPassed": 0,
        "totalAttempted": 0,
    }
    next_id = start_level_id
    for eq_id in equation_ids:
        if eq_id not in EQUATIONS:
            print(f"  skipping unknown equation '{eq_id}'", file=sys.stderr)
            continue
        equation = EQUATIONS[eq_id]
        passing, attempts, failed = run_equation(
            equation, dict_words,
            candidate_cap=candidate_cap_per_eq,
            min_robustness=min_robustness,
            max_robustness=max_robustness,
            per_layout_cap=per_layout_cap,
            start_level_id=next_id,
            shuffle_seed=shuffle_seed,
        )
        combined["byEquation"][eq_id] = {
            "description": equation.description,
            "layerCount": equation.layer_count,
            "attempted": attempts,
            "passed": len(passing),
            "failedBy": dict(sorted(failed.items(), key=lambda kv: -kv[1])),
            "results": passing,
        }
        combined["totalAttempted"] += attempts
        combined["totalPassed"] += len(passing)
        next_id += max(len(passing), 0)
    return combined


# ── Summary helpers ─────────────────────────────────────────────────────────
def _variety_summary(passing: list[dict]) -> dict:
    """Unique-word and unique-layout counts + top entries."""
    def _top(counter: dict[str, int], n: int = 10) -> list[tuple[str, int]]:
        return sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))[:n]

    word_hist: dict[str, dict[str, int]] = {}   # role -> Counter
    layout_hist: dict[str, int] = {}
    tier_hist: dict[str, int] = {}
    for r in passing:
        for role, word in r["words"].items():
            word_hist.setdefault(role, {})
            word_hist[role][word] = word_hist[role].get(word, 0) + 1
        layout_hist[r["layoutName"]] = layout_hist.get(r["layoutName"], 0) + 1
        tier_hist[r["tier"]] = tier_hist.get(r["tier"], 0) + 1

    variety = {
        "uniqueLayouts": len(layout_hist),
        "layoutHistogram": layout_hist,
        "tierHistogram": tier_hist,
        "byRole": {
            role: {
                "unique": len(counter),
                "top": _top(counter),
            }
            for role, counter in word_hist.items()
        },
    }
    return variety


def build_summary(combined: dict) -> dict:
    """Full cross-equation summary with variety stats."""
    summary: dict = {
        "totalAttempted": combined["totalAttempted"],
        "totalPassed": combined["totalPassed"],
        "byEquation": {},
    }
    for eq_id, payload in combined["byEquation"].items():
        summary["byEquation"][eq_id] = {
            "description": payload["description"],
            "layerCount": payload["layerCount"],
            "attempted": payload["attempted"],
            "passed": payload["passed"],
            "failedBy": payload["failedBy"],
            "variety": _variety_summary(payload["results"]),
        }
    return summary


# ── CLI ─────────────────────────────────────────────────────────────────────
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--equation", type=str, default="all",
                    help="Equation id from cascade_equations.EQUATIONS, "
                         "comma-separated list, or 'all' (default).")
    ap.add_argument("--list-equations", action="store_true",
                    help="List registered equation ids and exit.")
    ap.add_argument("--dict", type=Path, default=None)
    ap.add_argument("--cap", type=int, default=500,
                    help="Per-equation candidate budget (default 500).")
    ap.add_argument("--per-layout-cap", type=int, default=50,
                    help="Max candidates per (equation, layout). Keeps variety "
                         "across layouts within one equation.")
    ap.add_argument("--shuffle-seed", type=int, default=42)
    ap.add_argument("--min-robustness", type=int, default=1)
    ap.add_argument("--max-robustness", type=int, default=None)
    ap.add_argument("--difficulty", choices=["tutorial", "standard", "puzzle"],
                    default=None,
                    help="tutorial=min 20, puzzle=exactly 1, standard=user-set.")
    ap.add_argument("--output-dir", type=Path, default=None)
    ap.add_argument("--start-id", type=int, default=1000)
    ap.add_argument("--summary", action="store_true",
                    help="Print only the summary (no candidate listing).")
    args = ap.parse_args(argv)

    if args.list_equations:
        for eq_id, eq in EQUATIONS.items():
            print(f"{eq_id}  ({eq.layer_count}-layer)")
            print(f"    {eq.description}")
        return 0

    dict_words = load_dict(args.dict) if args.dict else load_dict()

    # Resolve equation list.
    if args.equation == "all":
        eq_ids = list(IMPLEMENTED_IDS)
    else:
        eq_ids = [s.strip() for s in args.equation.split(",") if s.strip()]
        for eid in eq_ids:
            if eid not in EQUATIONS:
                print(f"Unknown equation id: '{eid}'", file=sys.stderr)
                print(f"Known: {list(EQUATIONS.keys())}", file=sys.stderr)
                return 2

    # Difficulty preset overrides.
    min_robust = args.min_robustness
    max_robust = args.max_robustness
    if args.difficulty == "tutorial":
        min_robust = 20
        max_robust = None
    elif args.difficulty == "puzzle":
        min_robust = 1
        max_robust = 1

    combined = run_all_equations(
        eq_ids, dict_words,
        candidate_cap_per_eq=args.cap,
        min_robustness=min_robust,
        max_robustness=max_robust,
        per_layout_cap=args.per_layout_cap,
        start_level_id=args.start_id,
        shuffle_seed=args.shuffle_seed,
    )

    # Optionally dump level JSONs to disk, grouped by equation id.
    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        for eq_id, payload in combined["byEquation"].items():
            eq_dir = args.output_dir / eq_id
            eq_dir.mkdir(parents=True, exist_ok=True)
            for r in payload["results"]:
                lvl = r["level"]
                fname = eq_dir / f"level_{lvl['levelId']}.json"
                fname.write_text(json.dumps(lvl, indent=4) + "\n")

    summary = build_summary(combined)
    if args.output_dir:
        summary["writtenTo"] = str(args.output_dir)

    if args.summary:
        print(json.dumps(summary, indent=2))
    else:
        # Compact listing: one line per passing candidate.
        listing: list[dict] = []
        for eq_id, payload in combined["byEquation"].items():
            for r in payload["results"]:
                listing.append({
                    "equationId": eq_id,
                    "layoutName": r["layoutName"],
                    "levelId": r["level"]["levelId"],
                    "words": r["words"],
                    "robustness": r["robustness"],
                    "tier": r["tier"],
                    "canonicalScore": r["canonicalScore"],
                })
        print(json.dumps({**summary, "candidates": listing}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
