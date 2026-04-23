#!/usr/bin/env python3.12
"""
Cascade designer — enumerates level candidates for a given template,
filters out boards with accidental primes (via prime_scanner logic),
runs the sim on each surviving board, and ranks by robustness.

Template A (L210-style): vertical 3-letter pre-prime at top-left,
horizontal 4-letter trigger crossing its last letter, rock block in the
middle, 4-letter Layer-2 word forming at y=0 after Layer 1 gravity,
3-letter Layer-3 word forming at y=0 after Layer 2 gravity.

Post-shift coords (content at y=0..5, touching the grid floor):

   y=5 | _  P  _  N  _  _     <- (1,5)=preprime[0],  (3,5)=layer3[2]
   y=4 | _  A  K  E  S  _     <- (1,4)=preprime[1],  (2,4)=decorative,
                                  (3,4)=layer2[1],   (4,4)=layer2[2]
   y=3 | _  W  A  N  R  _     <- preprime[2] | trigger[1..2] | safety (pre-edit)
   y=2 | _  #  #  #  #  _     <- stones
   y=1 | _  D  E  #  #  _     <- (1,1)=trigger[3] (pre-edit), (2,1)=layer3[1], stones
   y=0 | _  P  B  #  #  T     <- (1,0)=layer3[0], (2,0)=layer2[0], stones, (5,0)=layer2[3]

Canonical player action: edit (1,1)↔(4,3). Post-edit:
   (1,1) = safety letter (the former R)
   (4,3) = trigger[3]     (the former D → completes WAND)

The trigger word forms at row 3, overlaps preprime at (1,3), detonates
PAW + WAND + 8 stones. After gravity, layer2 forms at y=0. After
Layer 2, layer3 forms at y=0.

CLI:
  python3.12 cascade_designer.py --template A --limit 20 --min-robustness 1
"""

from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path
from typing import Iterator

from dict_loader import load_dict, is_valid_word
from cascade_sim import (
    CascadeSim,
    Cell,
    explore_actions_on_data,
    run_sim_on_data,
    COLS,
    LEVEL_ROWS,
)


# ── Template A skeleton ─────────────────────────────────────────────────────
TEMPLATE_A = {
    "id": "A",
    "description": "L210-style — vertical pre-prime top-left, horizontal trigger, rock block, bottom cascade",
    "grid": (COLS, LEVEL_ROWS),
    "stones": [
        (1, 2), (2, 2), (3, 2), (4, 2),   # row 2 cols 1-4
        (3, 1), (4, 1),                    # row 1 cols 3-4
        (3, 0), (4, 0),                    # row 0 cols 3-4
    ],
    "positions": {
        "preprime_0": (1, 5),
        "preprime_1": (1, 4),
        "preprime_2": (1, 3),
        "trigger_1":  (2, 3),
        "trigger_2":  (3, 3),
        "safety":     (4, 3),   # pre-edit cell — player will swap this out
        "trigger_3":  (1, 1),   # pre-edit cell — player will swap this into (4,3)
        "decorative": (2, 4),
        "layer2_1":   (3, 4),
        "layer2_2":   (4, 4),
        "layer3_2":   (3, 5),
        "layer2_0":   (2, 0),
        "layer2_3":   (5, 0),
        "layer3_0":   (1, 0),
        "layer3_1":   (2, 1),
    },
    "edit_action": {"type": "edit", "cell_a": [1, 1], "cell_b": [4, 3]},
}


def build_template_a_level(
    preprime: str,
    trigger: str,
    layer2: str,
    layer3: str,
    *,
    decorative: str = "K",
    safety: str = "R",
    level_id: int = 910,
) -> dict:
    """Assemble a level JSON dict from four words + filler letters."""
    p = TEMPLATE_A["positions"]
    letter_map = {
        "preprime_0": preprime[0], "preprime_1": preprime[1], "preprime_2": preprime[2],
        "trigger_1":  trigger[1],  "trigger_2":  trigger[2],  "trigger_3":  trigger[3],
        "safety":     safety,      "decorative": decorative,
        "layer2_0": layer2[0], "layer2_1": layer2[1], "layer2_2": layer2[2], "layer2_3": layer2[3],
        "layer3_0": layer3[0], "layer3_1": layer3[1], "layer3_2": layer3[2],
    }
    board: list[dict] = []
    for name, letter in letter_map.items():
        col, row = p[name]
        board.append(
            {"x": col, "y": row, "letter": letter.upper(), "variant": "normal"}
        )
    for (col, row) in TEMPLATE_A["stones"]:
        board.append(
            {"x": col, "y": row, "letter": "X", "variant": "stone"}
        )
    return {
        "schemaVersion": 1,
        "levelId": level_id,
        "displayName": f"Template A · {preprime}→{layer2}→{layer3}",
        "target": 300,
        "moveBudget": 5,
        "starThresholds": [300, 600, 1000],
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
            {"on": "start", "text": f"Swap to form {trigger} on the middle row."},
        ],
    }


# ── Candidate enumeration ───────────────────────────────────────────────────
def _pick_safety_letter(
    layer3: str, dict_words: frozenset[str], preferred: list[str] | None = None
) -> str | None:
    """Pick a letter that, when placed at (1,1) post-edit, does NOT form a
    word with layer3[1] at (2,1) and layer3[2] at (3,1). Prefers letters in
    `preferred` (typically low-risk consonants).

    Returns None if no safe letter is found in the 26-letter alphabet (this
    would be very unusual; the check is here for completeness)."""
    candidates = preferred or ["R", "L", "C", "F", "M", "G", "H", "B", "P", "T"]
    # Fall back to full alphabet if none of the preferred letters fit.
    alphabet = [chr(c) for c in range(ord("A"), ord("Z") + 1)]
    for letter in list(candidates) + [c for c in alphabet if c not in candidates]:
        if is_valid_word(letter + layer3[1] + layer3[2], dict_words):
            continue
        return letter
    return None


def enumerate_template_a(
    dict_words: frozenset[str],
    preprime_pool: list[str] | None = None,
    trigger_pool: list[str] | None = None,
    layer2_pool: list[str] | None = None,
    layer3_pool: list[str] | None = None,
    decorative: str = "K",
    cap: int | None = None,
    per_preprime_cap: int | None = None,
) -> Iterator[tuple[str, str, str, str, str]]:
    """Yields (preprime, trigger, layer2, layer3, safety) tuples for every
    combination that survives the cheap pre-filters (dict validity,
    letter-junction check, safety-letter availability).

    Pools default to every dict word of the right length. `cap` limits total
    yielded candidates; `per_preprime_cap` balances variety by stopping
    enumeration within a preprime once its quota is hit so other preprimes
    get a turn.
    """
    three = sorted(w for w in dict_words if len(w) == 3)
    four = sorted(w for w in dict_words if len(w) == 4)
    pp_list = preprime_pool if preprime_pool is not None else three
    tr_list = trigger_pool if trigger_pool is not None else four
    l2_list = layer2_pool if layer2_pool is not None else four
    l3_list = layer3_pool if layer3_pool is not None else three

    # Pre-bucket triggers by first letter so each preprime's inner loop
    # doesn't scan the entire 4-letter space.
    trig_by_first: dict[str, list[str]] = {}
    for t in tr_list:
        if t not in dict_words or len(t) != 4:
            continue
        trig_by_first.setdefault(t[0], []).append(t)

    count = 0
    for preprime in pp_list:
        if preprime not in dict_words or len(preprime) != 3:
            continue
        per_pp_count = 0
        candidates = trig_by_first.get(preprime[2], [])
        for trigger in candidates:
            if is_valid_word(trigger[:3], dict_words):
                continue  # row 3 load-time accidental prime
            for layer2 in l2_list:
                if layer2 not in dict_words or len(layer2) != 4:
                    continue
                for layer3 in l3_list:
                    if layer3 not in dict_words or len(layer3) != 3:
                        continue
                    safety = _pick_safety_letter(layer3, dict_words)
                    if safety is None:
                        continue
                    # Row 3 accidental-prime checks with safety at col 4.
                    if is_valid_word(trigger[:3] + safety, dict_words):
                        continue
                    if is_valid_word(trigger[1:3] + safety, dict_words):
                        continue
                    # Col 3 load-time: trigger[2], layer2[1], layer3[2].
                    if is_valid_word(trigger[2] + layer2[1] + layer3[2], dict_words):
                        continue
                    yield (preprime, trigger, layer2, layer3, safety)
                    count += 1
                    per_pp_count += 1
                    if cap is not None and count >= cap:
                        return
                    if per_preprime_cap is not None and per_pp_count >= per_preprime_cap:
                        break
                if per_preprime_cap is not None and per_pp_count >= per_preprime_cap:
                    break
            if per_preprime_cap is not None and per_pp_count >= per_preprime_cap:
                break


# ── Full candidate evaluation ───────────────────────────────────────────────
def _scan_board_only(level_data: dict, dict_words: frozenset[str]) -> set[str]:
    """Returns the set of dict-valid words auto-primed at load (no sim run)."""
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


def evaluate_candidate(
    level_data: dict,
    dict_words: frozenset[str],
    preprime: str,
    trigger: str,
    layer2: str,
    layer3: str,
) -> dict:
    """Full pipeline: prime scan → canonical action → robustness count.

    Returns {"pass": bool, "reason": str, "robustness": int, ...}."""
    # 1. Prime scanner: only preprime may auto-prime.
    auto_primes = _scan_board_only(level_data, dict_words)
    unexpected = auto_primes - {preprime}
    if unexpected:
        return {
            "pass": False,
            "reason": f"accidental primes: {sorted(unexpected)}",
            "robustness": 0,
        }

    # 2. Canonical action — must produce the exact expected 3-layer trace.
    canonical = TEMPLATE_A["edit_action"]
    report = run_sim_on_data(level_data, dict_words, canonical)
    if "error" in report:
        return {"pass": False, "reason": f"action error: {report['error']}", "robustness": 0}
    layers = report.get("layers", [])
    if len(layers) != 3:
        return {
            "pass": False,
            "reason": f"expected 3 layers, got {len(layers)}",
            "robustness": 0,
            "words_seen": [L.get("triggerWords") for L in layers],
        }

    l1, l2, l3 = layers
    if preprime not in (l1.get("primedWordsTriggered") or []):
        return {"pass": False, "reason": f"Layer 1 missing preprime {preprime}", "robustness": 0}
    if trigger not in (l1.get("triggerWords") or []):
        return {"pass": False, "reason": f"Layer 1 missing trigger {trigger}", "robustness": 0}
    if l2.get("clusterSize") != 1 or l2.get("chainDepthAtScore") != 2:
        return {
            "pass": False,
            "reason": f"Layer 2 unclean (cluster={l2.get('clusterSize')}, "
                      f"rawStep={l2.get('chainDepthAtScore')})",
            "robustness": 0,
        }
    if layer2 not in (l2.get("triggerWords") or []):
        return {"pass": False, "reason": f"Layer 2 missing {layer2}", "robustness": 0}
    if l3.get("clusterSize") != 1 or l3.get("chainDepthAtScore") != 4:
        return {
            "pass": False,
            "reason": f"Layer 3 unclean (cluster={l3.get('clusterSize')}, "
                      f"rawStep={l3.get('chainDepthAtScore')})",
            "robustness": 0,
        }
    if layer3 not in (l3.get("triggerWords") or []):
        return {"pass": False, "reason": f"Layer 3 missing {layer3}", "robustness": 0}

    # 3. Robustness — count every legal action that produces ≥3 layers.
    explore = explore_actions_on_data(level_data, dict_words, min_layers=3)
    robustness = sum(1 for r in explore["results"] if r["layers"] >= 3)
    return {
        "pass": True,
        "reason": "ok",
        "robustness": robustness,
        "canonicalScore": report.get("totalScore", 0),
        "finalChainDepth": report.get("finalChainDepth", 0),
        "explore": {
            "totalEnumerated": explore["totalEnumerated"],
            "threeLayer": robustness,
        },
    }


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
    """Star thresholds derived from the canonical cascade total. The 3-star
    line is the canonical score itself — getting the full intended cascade
    earns all three stars. Lower tiers are 55% / 80% of canonical."""
    if canonical_score <= 0:
        return [1, 2, 3]
    one = max(1, round(canonical_score * 0.55))
    two = max(one + 1, round(canonical_score * 0.80))
    three = max(two + 1, canonical_score)
    return [one, two, three]


def run_template_a(
    dict_words: frozenset[str],
    candidate_cap: int | None = 2000,
    min_robustness: int = 1,
    start_level_id: int = 910,
    per_preprime_cap: int | None = 20,
    preprime_pool: list[str] | None = None,
    trigger_pool: list[str] | None = None,
    layer2_pool: list[str] | None = None,
    layer3_pool: list[str] | None = None,
) -> tuple[list[dict], int, dict[str, int]]:
    """Enumerate + evaluate candidates for Template A. Pools default to
    every dict word of the right length for broad exploration.

    Returns (passing_candidates, attempts, failed_reasons). Passing
    candidates are sorted by (robustness desc, score desc) and include
    calibrated star thresholds."""
    passing: list[dict] = []
    failed_reasons: dict[str, int] = {}
    attempts = 0
    next_id = start_level_id
    for (preprime, trigger, layer2, layer3, safety) in enumerate_template_a(
        dict_words,
        preprime_pool=preprime_pool,
        trigger_pool=trigger_pool,
        layer2_pool=layer2_pool,
        layer3_pool=layer3_pool,
        cap=candidate_cap,
        per_preprime_cap=per_preprime_cap,
    ):
        attempts += 1
        level = build_template_a_level(
            preprime, trigger, layer2, layer3,
            safety=safety, level_id=next_id,
        )
        result = evaluate_candidate(
            level, dict_words, preprime, trigger, layer2, layer3
        )
        if not result["pass"]:
            key = result["reason"].split("(")[0].split(":")[0].strip()
            failed_reasons[key] = failed_reasons.get(key, 0) + 1
            continue
        if result["robustness"] < min_robustness:
            failed_reasons["below min_robustness"] = (
                failed_reasons.get("below min_robustness", 0) + 1
            )
            continue
        # Calibrate star thresholds using the canonical score.
        stars = calibrate_stars(result["canonicalScore"])
        level["starThresholds"] = stars
        level["target"] = stars[0]
        passing.append(
            {
                "preprime": preprime,
                "trigger": trigger,
                "layer2": layer2,
                "layer3": layer3,
                "safety": safety,
                "robustness": result["robustness"],
                "tier": robustness_tier(result["robustness"]),
                "canonicalScore": result["canonicalScore"],
                "starThresholds": stars,
                "finalChainDepth": result["finalChainDepth"],
                "level": level,
            }
        )
        next_id += 1
    passing.sort(key=lambda r: (-r["robustness"], -r["canonicalScore"]))
    return passing, attempts, failed_reasons


# ── CLI ─────────────────────────────────────────────────────────────────────
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--template", type=str, default="A")
    ap.add_argument("--dict", type=Path, default=None)
    ap.add_argument("--cap", type=int, default=2000,
                    help="Max candidates to evaluate (default 2000)")
    ap.add_argument("--per-preprime-cap", type=int, default=20,
                    help="Max candidates per preprime word — keeps variety "
                         "across preprimes instead of exhausting one first")
    ap.add_argument("--min-robustness", type=int, default=1,
                    help="Minimum # of 3-layer actions to keep a candidate")
    ap.add_argument("--output-dir", type=Path, default=None,
                    help="Write each passing level JSON into this directory")
    ap.add_argument("--start-id", type=int, default=910,
                    help="First levelId to assign (default 910)")
    ap.add_argument("--summary", action="store_true",
                    help="Skip the full candidate dump; only print the summary")
    args = ap.parse_args(argv)

    dict_words = load_dict(args.dict) if args.dict else load_dict()
    if args.template != "A":
        print(f"Only Template A is implemented. Got '{args.template}'.", file=sys.stderr)
        return 2

    passing, attempts, failed_reasons = run_template_a(
        dict_words,
        candidate_cap=args.cap,
        per_preprime_cap=args.per_preprime_cap,
        min_robustness=args.min_robustness,
        start_level_id=args.start_id,
    )

    # Histogram by tier.
    tier_hist: dict[str, int] = {}
    for r in passing:
        tier_hist[r["tier"]] = tier_hist.get(r["tier"], 0) + 1

    summary = {
        "template": args.template,
        "attempted": attempts,
        "passed": len(passing),
        "failedBy": dict(sorted(failed_reasons.items(), key=lambda kv: -kv[1])),
        "tierHistogram": tier_hist,
    }

    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        for r in passing:
            lvl = r["level"]
            fname = args.output_dir / f"level_{lvl['levelId']}.json"
            fname.write_text(json.dumps(lvl, indent=4) + "\n")
        summary["writtenTo"] = str(args.output_dir)

    if args.summary:
        print(json.dumps(summary, indent=2))
    else:
        # Print summary + compact listing of passing candidates.
        print(json.dumps({
            **summary,
            "candidates": [
                {
                    "levelId": r["level"]["levelId"],
                    "words": [r["preprime"], r["trigger"], r["layer2"], r["layer3"]],
                    "safety": r["safety"],
                    "robustness": r["robustness"],
                    "tier": r["tier"],
                    "canonicalScore": r["canonicalScore"],
                }
                for r in passing
            ],
        }, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
