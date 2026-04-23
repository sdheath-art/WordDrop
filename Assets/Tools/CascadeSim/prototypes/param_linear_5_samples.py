#!/usr/bin/env python3.12
"""
Phase 10.5f-v2 parameterization prototype — 5 linear-equation samples
that vary beyond letter substitution.

Dimensions varied:
  Sample 1 — baseline (col1_row0, 8-stone block, 0 fillers, 3+4+4+3)
  Sample 2 — shifted position (col0_row2, same math, different visual block)
  Sample 3 — scattered rocks (col1_row0, 6 stones instead of 8)
  Sample 4 — column-L rocks + 2 filler letters (7 stones, corner fillers)
  Sample 5 — 4-letter preprime (BRAG→GNAW→CAST, 2-layer cascade)

Not yet varied: trigger length (future extension), preprime_len=5
(infeasible with 3-layer cascade without raising the whole layout).

Output: /tmp/param_5_samples.txt — all 5 rendered side-by-side via
solution_viewer for PM eyeball review.
"""

import copy
import glob
import io
import json
import contextlib
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(HERE))

from dict_loader import load_dict
from cascade_sim import run_sim_on_data
from solution_viewer import view


def load_baseline_layouts(batch_dir: Path) -> tuple[dict, dict]:
    """Pick a col1_row0 level (baseline) and a col0_row2 level (shifted
    up) from a pre-generated linear batch. These two carry very different
    visual positions even though the math is identical."""
    baseline = None
    shifted = None
    for p in sorted(glob.glob(str(batch_dir / "level_*.json"))):
        d = json.loads(Path(p).read_text())
        layout = d.get("designer", {}).get("layoutName", "")
        if baseline is None and layout == "col1_row0":
            baseline = d
        elif shifted is None and layout == "col0_row2":
            shifted = d
        if baseline and shifted:
            break
    if not (baseline and shifted):
        raise RuntimeError(
            f"batch at {batch_dir} missing required layouts; run cascade_designer first"
        )
    return baseline, shifted


def build_sample5() -> dict:
    """Hand-constructed 4-letter preprime cascade. Preprime BRAG vertical
    at col 1 rows 5..2; trigger GNAW horizontal at row 2 cols 1..4;
    6 stones; L2 CAST forms at y=0 after gravity.

    Note: BRAG has sub-word RAG (also dict-valid) so RAG also auto-primes.
    That's a known-gotcha of preprime_len=4 — most 4-letter dict words
    have dict-valid 3-letter substrings. Full parameterization would
    filter preprime pool to words whose sub-trigrams are NOT dict words.
    For the prototype the RAG-also-primes behavior still produces a valid
    2-layer cascade (GNAW triggers both BRAG and RAG simultaneously)."""
    return {
        "schemaVersion": 1,
        "levelId": 1005,
        "displayName": "Param 5 · 4-letter preprime (BRAG→GNAW→CAST)",
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
        "startingBoard": [
            {"x": 1, "y": 5, "letter": "B", "variant": "normal"},
            {"x": 1, "y": 4, "letter": "R", "variant": "normal"},
            {"x": 1, "y": 3, "letter": "A", "variant": "normal"},
            {"x": 1, "y": 2, "letter": "G", "variant": "normal"},
            {"x": 2, "y": 2, "letter": "N", "variant": "normal"},
            {"x": 3, "y": 2, "letter": "A", "variant": "normal"},
            {"x": 4, "y": 2, "letter": "R", "variant": "normal"},
            {"x": 1, "y": 0, "letter": "W", "variant": "normal"},
            {"x": 2, "y": 0, "letter": "C", "variant": "normal"},
            {"x": 3, "y": 4, "letter": "A", "variant": "normal"},
            {"x": 4, "y": 4, "letter": "S", "variant": "normal"},
            {"x": 5, "y": 0, "letter": "T", "variant": "normal"},
            {"x": 1, "y": 1, "letter": "X", "variant": "stone"},
            {"x": 2, "y": 1, "letter": "X", "variant": "stone"},
            {"x": 3, "y": 1, "letter": "X", "variant": "stone"},
            {"x": 4, "y": 1, "letter": "X", "variant": "stone"},
            {"x": 3, "y": 0, "letter": "X", "variant": "stone"},
            {"x": 4, "y": 0, "letter": "X", "variant": "stone"},
        ],
        "bag": None,
        "seed": 0,
        "visualCues": {},
        "tutorialPrompts": [
            {"on": "start", "text": "Edit to form GNAW at row 2."}
        ],
        "designer": {
            "equationId": "eq_3v_4h_4h_3h_linear",
            "layoutName": "PARAM5_preprime_len4",
            "words": {"preprime": "BRAG", "trigger": "GNAW", "layer2": "CAST"},
            "canonicalAction": {"type": "edit", "cell_a": [4, 2], "cell_b": [1, 0]},
            "parameterizationNote": "preprime_len=4, 6 stones, 2-layer cascade",
        },
    }


def mutate_remove_stones(level: dict, keep_stones: set[tuple[int, int]]) -> dict:
    """Return a copy of `level` with stones pruned to the given keep_stones set."""
    out = copy.deepcopy(level)
    out["startingBoard"] = [
        t for t in out["startingBoard"]
        if not (t.get("variant") == "stone"
                and (t["x"], t["y"]) not in keep_stones)
    ]
    return out


def mutate_add_fillers(level: dict, fillers: list[tuple[int, int, str]]) -> dict:
    """Append filler letter tiles at the given (col, row, letter) positions."""
    out = copy.deepcopy(level)
    for (c, r, letter) in fillers:
        out["startingBoard"].append(
            {"x": c, "y": r, "letter": letter.upper(), "variant": "normal"}
        )
    return out


def main() -> int:
    batch_dir = Path("/tmp/linear_batch/eq_3v_4h_4h_3h_linear")
    dict_words = load_dict()
    baseline, shifted = load_baseline_layouts(batch_dir)

    sample1 = baseline
    sample2 = shifted
    # Sample 3: drop the 2 row-0 stones.
    sample3 = mutate_remove_stones(
        baseline,
        keep_stones={(1, 2), (2, 2), (3, 2), (4, 2), (3, 1), (4, 1)},
    )
    sample3["displayName"] = "Param 3 · scattered (6 stones, no bottom row)"
    sample3["designer"]["parameterizationNote"] = "rock_count=6 (removed (3,0),(4,0))"
    # Sample 4: column-L rock shape + 2 corner fillers.
    sample4 = mutate_remove_stones(
        baseline,
        keep_stones={(1, 2), (2, 2), (3, 2), (3, 1), (4, 1), (3, 0), (4, 0)},
    )
    sample4 = mutate_add_fillers(sample4, [(5, 7, "X"), (0, 7, "Z")])
    sample4["displayName"] = "Param 4 · column-L (7 stones, 2 fillers)"
    sample4["designer"]["parameterizationNote"] = (
        "rock=column_L (7 stones); filler=2 at corners"
    )
    # Sample 5: hand-constructed 4-letter preprime.
    sample5 = build_sample5()

    samples = [
        ("Sample 1: Baseline block (L210 reference)", sample1),
        ("Sample 2: Shifted up (col0_row2)", sample2),
        ("Sample 3: Scattered 6 stones (no bottom row)", sample3),
        ("Sample 4: Column-L shape + 2 fillers", sample4),
        ("Sample 5: 4-letter preprime BRAG (word-length variation)", sample5),
    ]

    print("=== Validation ===")
    for name, data in samples:
        action = data["designer"]["canonicalAction"]
        rep = run_sim_on_data(data, dict_words, action)
        layers = rep.get("layers") or []
        if "error" in rep:
            print(f"  FAIL {name}: {rep['error']}")
            return 1
        words = [L["triggerWords"][0] if L["triggerWords"] else "?" for L in layers]
        print(f"  {name}: {len(layers)}L  {' → '.join(words)}  score={rep['totalScore']}")

    out_path = Path("/tmp/param_5_samples.txt")
    with open(out_path, "w") as f:
        f.write("PHASE 10.5f-v2 — PARAMETERIZATION PROTOTYPE\n")
        f.write("Five linear-style samples, each varying on dimensions beyond letter choice.\n")
        f.write("=" * 68 + "\n\n")
        for name, data in samples:
            f.write(f"{name}\n")
            tmp = Path(f"/tmp/__param_sample_{data['levelId']}.json")
            tmp.write_text(json.dumps(data, indent=4) + "\n")
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                view(tmp, action=None, dict_words=dict_words, explore=False)
            f.write(buf.getvalue())
            f.write("\n\n")
    print(f"\nSaved: {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
