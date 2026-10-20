#!/usr/bin/env python3
"""WordDrop balance report — per-level outcomes from saved play sessions (move-limit runs only).

Reads ~/Library/Application Support/Spencer Heath/WordDrop/analytics/session_*.json and summarises every
level that was played with the move limit on (stage_clear with move_limit=1, and stage_fail).

    python3 tools/balance_report.py              # all move-limit sessions
    python3 tools/balance_report.py --since 20261020

Display level = internal stage - 10 (tutorial levels occupy 1-10). Built 2026-10-20 for tuning
LevelRules.MovesFor / targets from Spencer's own repeated play.
"""
import glob, json, os, sys, statistics as st
from collections import defaultdict

DIR = os.path.expanduser("~/Library/Application Support/Spencer Heath/WordDrop/analytics")
since = None
if "--since" in sys.argv: since = sys.argv[sys.argv.index("--since") + 1]

rows = defaultdict(lambda: {"clear": [], "fail": []})
sessions = 0
for path in sorted(glob.glob(os.path.join(DIR, "session_*.json"))):
    stamp = os.path.basename(path).split("_")[2]
    if since and stamp < since: continue
    try: events = json.load(open(path))
    except Exception: continue
    used = False
    for e in events:
        ev = e.get("event")
        if ev == "stage_clear" and e.get("move_limit") == 1:
            rows[e["stage"] - 10]["clear"].append(e); used = True
        elif ev == "stage_fail":
            rows[e["stage"] - 10]["fail"].append(e); used = True
    sessions += used

if not rows:
    print("No move-limit levels recorded yet. Play a few runs, then re-run this."); sys.exit()

def pct(a, b): return f"{100*a/b:.0f}%" if b else "-"
print(f"{sessions} session(s) with move-limit play\n")
print("LVL  tries  clear  fail  clear%  | moves left at clear (avg/min) | score% at fail (avg) | 4+ letter words | plants solved | overflows")
for lvl in sorted(rows):
    c, f = rows[lvl]["clear"], rows[lvl]["fail"]
    n = len(c) + len(f)
    left = [e["moves_budget"] - e["moves_used"] for e in c]
    fails = [100 * e["score"] / e["target"] for e in f if e.get("target")]
    allw = c + f
    w3 = sum(e.get("words_3", 0) for e in allw); w4 = sum(e.get("words_4", 0) + e.get("words_5", 0) + e.get("words_6p", 0) for e in allw)
    pm = sum(e.get("plants_made", 0) for e in allw); ps = sum(e.get("plants_solved", 0) for e in allw)
    ov = sum(e.get("overflows", 0) for e in allw)
    print(f"{lvl:>3}  {n:>5}  {len(c):>5}  {len(f):>4}  {pct(len(c), n):>6}  | "
          f"{(f'{st.mean(left):.1f} / {min(left)}' if left else '-'):>22}        | "
          f"{(f'{st.mean(fails):.0f}%' if fails else '-'):>10}           | "
          f"{pct(w4, w3 + w4):>8}        | {ps}/{pm:<10}  | {ov}")
print("\nReading it: a level that is clear% 100 with lots of moves left is too easy; clear% low with fail"
      " scores near 90%+ is tight-but-fair; fails far below 80% mean the target or moves are off.")
