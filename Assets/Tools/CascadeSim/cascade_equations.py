#!/usr/bin/env python3.12
"""
Cascade equation catalog — each Equation subclass defines a distinct
cascade topology (what primes, what triggers, how gravity composes the
layers). The designer iterates over equations × layouts × letter
combinations; the sim validates each candidate.

A single equation's layout can itself vary (column shift, mirror flip,
row offset) so one equation yields many visually-distinct levels sharing
the same math. Different equations yield fundamentally different
puzzles.

Scope for Phase 10.5f-v2 initial:
  - Equation base class + layout iterator + validation hook
  - EQ_LINEAR_3V_4H_4H_3H  — current Template A math, col-shift + mirror
  - EQ_SHORT_3V_4H_3H      — 2-layer variant (no Layer 3)
  - EQ_CLUSTER_3V_3V_4H    — two preprimes side-by-side, cluster L1

Deferred to future phase:
  - eq_4v_5h_4h_3h_long (longer preprime + trigger)
  - eq_3v_4h_4h_3h_3h_deep (4-layer)
  - eq_3h_4v_4v_3v_inverted (horizontal preprime)
  - eq_drop_3v_4h_4h_3h (drop instead of edit)
  - eq_selfoverlap_4h_3h (no preprime)
  - eq_convergent_3v_3v_4h (convergent mini-cascades)
  - eq_cross_axis_3v_4h_4v_3h (alternating orientation)
"""

from __future__ import annotations
import random
from dataclasses import dataclass, field
from typing import Iterator

from dict_loader import is_valid_word
from cascade_sim import COLS, LEVEL_ROWS


# ── Core types ──────────────────────────────────────────────────────────────
@dataclass
class Layout:
    """A concrete placement of an equation's cells on the 6×8 grid. The
    equation subclass defines what each named cell holds; the layout
    concretizes the (col, row) coords."""
    name: str                                        # e.g. "col0_mirror0_row0"
    positions: dict[str, tuple[int, int]]            # role -> (col, row)
    stones: list[tuple[int, int]]                    # stone cells
    canonical_action: dict                           # player action that triggers L1
    mirror: bool = False
    preprime_col: int = -1


@dataclass
class Candidate:
    equation_id: str
    layout_name: str
    words: dict[str, str]                            # role_label -> word
    letters: dict[str, str]                          # single-letter filler roles
    expected_trace: list[dict]                       # per-layer validation target


class Equation:
    """Subclass contract:
      - id: short string identifier
      - layer_count: how many cascade layers this equation produces
      - iter_layouts(): yields Layout objects (column shifts, mirrors, etc.)
      - iter_candidates(dict_words, layout, ...): yields (letter_map, expected_trace)
        tuples for each letter combination valid on this layout
      - build_board(layout, letter_map): returns list of {x,y,letter,variant}
      - canonical_for(layout, cand): returns the action dict (defaults to
        layout.canonical_action; override when the action needs per-candidate
        letter info, e.g. drop actions need trigger[3] in the letter field)
      - get_bag(cand): returns an optional bag override dict for the level
        JSON (drop equations force trigger[3] into the hand draw)
    """
    id: str = "BASE"
    description: str = ""
    layer_count: int = 0

    def iter_layouts(self) -> Iterator[Layout]:
        raise NotImplementedError

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
    ) -> Iterator[Candidate]:
        raise NotImplementedError

    def build_board(self, layout: Layout, cand: Candidate) -> list[dict]:
        raise NotImplementedError

    def canonical_for(self, layout: Layout, cand: Candidate) -> dict:
        """Return the canonical action for this candidate on this layout.
        Default: use the layout's static canonical_action. Equations that
        need per-candidate letter info (e.g. drops) override this."""
        return layout.canonical_action

    def get_bag(self, cand: Candidate) -> dict | None:
        """Return a bag override for the level JSON, or None to use
        the default bag. Used by drop-style equations to force the
        first hand draw to be trigger[3]."""
        return None

    def validate_trace(self, cand: Candidate, sim_layers: list[dict]) -> tuple[bool, str]:
        """Default: check layer count and cluster sizes match. Subclasses
        can override for topology-specific validation."""
        if len(sim_layers) != self.layer_count:
            return (False, f"expected {self.layer_count} layers, got {len(sim_layers)}")
        for i, (expected, actual) in enumerate(zip(cand.expected_trace, sim_layers)):
            if expected.get("clusterSize") is not None:
                if actual.get("clusterSize") != expected["clusterSize"]:
                    return (False, f"Layer {i+1} cluster {actual.get('clusterSize')} "
                                   f"!= {expected['clusterSize']}")
            if expected.get("chainDepthAtScore") is not None:
                if actual.get("chainDepthAtScore") != expected["chainDepthAtScore"]:
                    return (False, f"Layer {i+1} rawStep {actual.get('chainDepthAtScore')} "
                                   f"!= {expected['chainDepthAtScore']}")
            if expected.get("triggerWord") is not None:
                tw = actual.get("triggerWords") or []
                if expected["triggerWord"] not in tw:
                    return (False, f"Layer {i+1} trigger {expected['triggerWord']} "
                                   f"not in {tw}")
            if expected.get("primedWord") is not None:
                pw = actual.get("primedWordsTriggered") or []
                if expected["primedWord"] not in pw:
                    return (False, f"Layer {i+1} primed {expected['primedWord']} "
                                   f"not in {pw}")
        return (True, "ok")


# ── Shared helpers ──────────────────────────────────────────────────────────
def _mirror_col(col: int) -> int:
    return (COLS - 1) - col


def _pick_safety_letter(layer3: str, dict_words: frozenset[str]) -> str | None:
    """Post-Layer-1 row at y=1 will read [safety, layer3[1], layer3[2]]
    (in Equation 1's linear layout). The safety letter must NOT form a
    dict word with those two letters."""
    preferred = ["R", "L", "C", "F", "M", "G", "H", "B", "P", "T"]
    alphabet = [chr(c) for c in range(ord("A"), ord("Z") + 1)]
    for letter in list(preferred) + [c for c in alphabet if c not in preferred]:
        if is_valid_word(letter + layer3[1] + layer3[2], dict_words):
            continue
        return letter
    return None


def _safe_decorative(dict_words: frozenset[str], *avoid_trigrams: str) -> str:
    """Pick a filler letter that doesn't form a dict word with any of the
    supplied 2-letter sequences (appended or prepended)."""
    for letter in ["K", "J", "Q", "X", "Z", "V", "W", "Y"]:
        safe = True
        for pair in avoid_trigrams:
            if len(pair) == 2:
                if is_valid_word(letter + pair, dict_words):
                    safe = False
                    break
                if is_valid_word(pair + letter, dict_words):
                    safe = False
                    break
        if safe:
            return letter
    return "X"  # fallback


# ─────────────────────────────────────────────────────────────────────────────
# Equation 1: Linear 3v → 4h → 4h → 3h
# (the existing Template A math, now with column-shift + mirror layout
# variety so we get many visually-distinct boards.)
# ─────────────────────────────────────────────────────────────────────────────
class EqLinear3v4h4h3h(Equation):
    id = "eq_3v_4h_4h_3h_linear"
    description = (
        "Vertical 3-letter preprime, horizontal 4-letter trigger crossing "
        "its bottom, 8 rocks in mid-board, 4-letter Layer 2 + 3-letter "
        "Layer 3 forming at the grid floor via gravity. Edit action swaps "
        "the trigger's completing letter in from a safety cell."
    )
    layer_count = 3

    # Role labels used in position dicts:
    #   preprime_0/1/2 (top to bottom)
    #   trigger_1/2 (letters 1 and 2 of the horizontal trigger; letter 0
    #                overlaps preprime_2, letter 3 is filled by the edit)
    #   safety (pre-edit filler at trigger letter-3 cell)
    #   trigger_3 (pre-edit cell holding the letter the player edits IN)
    #   decorative (neutral filler letter above the cascade formation zone)
    #   layer2_0..layer2_3 (starting positions for L2 word letters)
    #   layer3_0..layer3_2 (starting positions for L3 word letters)
    ROLES = [
        "preprime_0", "preprime_1", "preprime_2",
        "trigger_1", "trigger_2", "safety", "trigger_3",
        "decorative",
        "layer2_0", "layer2_1", "layer2_2", "layer2_3",
        "layer3_0", "layer3_1", "layer3_2",
    ]

    def iter_layouts(self) -> Iterator[Layout]:
        # Two layout dimensions:
        #   anchor — column shift {0, 1}. Reference layout spans cols 1..5;
        #       with shift = (anchor - 1), rightmost col (layer2_3 at col 5)
        #       must stay within COLS-1 = 5, so anchor ≤ 1.
        #   row_offset — vertical shift {0, 1, 2}. Slides the whole layout
        #       up the grid. Cascade math is preserved because gravity still
        #       pulls surviving letters to y=0; only the load-time board
        #       positions change visually.
        # Mirror is intentionally omitted: horizontal trigger/L2/L3 words
        # would read right-to-left after flip (KEEP → PEEK), breaking the
        # cascade. Re-add once we pick mirror-safe word pairs.
        for anchor in [0, 1]:
            for row_offset in [0, 1, 2]:
                yield self._build_layout(anchor, row_offset)

    def _build_layout(self, anchor: int, row_offset: int) -> Layout:
        """Build position map for this anchor + row_offset combination. The
        reference (anchor=1, row_offset=0) layout matches post-shift L210:
          preprime at col 1 rows 5,4,3
          trigger  at row 3 cols 1,2,3,4
          stones   at row 2 cols 1-4, row 1 cols 3-4, row 0 cols 3-4
          L2 word  lands at row 0 cols 2-5
          L3 word  lands at row 0 cols 1-3 after L2 clears
          edit     action: (1,1) <-> (4,3)
        """
        def shift(col: int) -> int:
            return col + (anchor - 1)

        def lift(row: int) -> int:
            return row + row_offset

        pos: dict[str, tuple[int, int]] = {
            "preprime_0": (shift(1), lift(5)),
            "preprime_1": (shift(1), lift(4)),
            "preprime_2": (shift(1), lift(3)),
            "trigger_1":  (shift(2), lift(3)),
            "trigger_2":  (shift(3), lift(3)),
            "safety":     (shift(4), lift(3)),
            "trigger_3":  (shift(1), lift(1)),
            "decorative": (shift(2), lift(4)),
            "layer2_0":   (shift(2), lift(0)),
            "layer2_1":   (shift(3), lift(4)),
            "layer2_2":   (shift(4), lift(4)),
            "layer2_3":   (shift(5), lift(0)),
            "layer3_0":   (shift(1), lift(0)),
            "layer3_1":   (shift(2), lift(1)),
            "layer3_2":   (shift(3), lift(5)),
        }
        stones_ref = [
            (1, 2), (2, 2), (3, 2), (4, 2),
            (3, 1), (4, 1),
            (3, 0), (4, 0),
        ]
        stones = [(shift(c), lift(r)) for (c, r) in stones_ref]

        # Canonical edit action: the trigger_3 cell <-> the safety cell.
        ca = pos["trigger_3"]
        cb = pos["safety"]
        canonical = {"type": "edit", "cell_a": list(ca), "cell_b": list(cb)}

        name = f"col{anchor}_row{row_offset}"
        return Layout(
            name=name,
            positions=pos,
            stones=stones,
            canonical_action=canonical,
            mirror=False,
            preprime_col=shift(1),
        )

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
        pp_pool: list[str] | None = None,
        tr_pool_by_first: dict[str, list[str]] | None = None,
        l2_pool: list[str] | None = None,
        l3_pool: list[str] | None = None,
        cap: int | None = None,
    ) -> Iterator[Candidate]:
        """Shuffled (preprime, trigger) pair walk. Yields at most one
        candidate per pair so no single preprime or trigger dominates the
        output — preprime variety is preserved across layouts and caps."""
        three = sorted(w for w in dict_words if len(w) == 3)
        four = sorted(w for w in dict_words if len(w) == 4)
        pp_list = list(pp_pool if pp_pool is not None else three)
        l2_list = list(l2_pool if l2_pool is not None else four)
        l3_list = list(l3_pool if l3_pool is not None else three)

        if tr_pool_by_first is None:
            tr_pool_by_first = {}
            for t in four:
                if t in dict_words and len(t) == 4:
                    tr_pool_by_first.setdefault(t[0], []).append(t)

        pairs: list[tuple[str, str]] = []
        for pp in pp_list:
            if pp not in dict_words or len(pp) != 3:
                continue
            for tr in tr_pool_by_first.get(pp[2], []):
                pairs.append((pp, tr))
        rng.shuffle(pairs)

        yielded = 0
        for (preprime, trigger) in pairs:
            if is_valid_word(trigger[:3], dict_words):
                continue  # row 3 load-time accidental prime
            l2_local = l2_list[:]
            l3_local = l3_list[:]
            rng.shuffle(l2_local)
            rng.shuffle(l3_local)
            found = False
            for layer2 in l2_local:
                if found:
                    break
                if layer2 not in dict_words or len(layer2) != 4:
                    continue
                for layer3 in l3_local:
                    if layer3 not in dict_words or len(layer3) != 3:
                        continue
                    safety = _pick_safety_letter(layer3, dict_words)
                    if safety is None:
                        continue
                    if is_valid_word(trigger[:3] + safety, dict_words):
                        continue
                    if is_valid_word(trigger[1:3] + safety, dict_words):
                        continue
                    # Col at trigger_2 (rows for trigger_2, layer2_1, layer3_2):
                    # trigger[2], layer2[1], layer3[2] form a vertical run.
                    if is_valid_word(trigger[2] + layer2[1] + layer3[2], dict_words):
                        continue
                    decorative = _safe_decorative(dict_words)

                    letters = {
                        "preprime_0": preprime[0], "preprime_1": preprime[1],
                        "preprime_2": preprime[2],
                        "trigger_1": trigger[1], "trigger_2": trigger[2],
                        "safety": safety, "trigger_3": trigger[3],
                        "decorative": decorative,
                        "layer2_0": layer2[0], "layer2_1": layer2[1],
                        "layer2_2": layer2[2], "layer2_3": layer2[3],
                        "layer3_0": layer3[0], "layer3_1": layer3[1],
                        "layer3_2": layer3[2],
                    }
                    trace = [
                        {"clusterSize": 1, "chainDepthAtScore": 0,
                         "triggerWord": trigger, "primedWord": preprime},
                        {"clusterSize": 1, "chainDepthAtScore": 2,
                         "triggerWord": layer2},
                        {"clusterSize": 1, "chainDepthAtScore": 4,
                         "triggerWord": layer3},
                    ]
                    cand = Candidate(
                        equation_id=self.id,
                        layout_name=layout.name,
                        words={"preprime": preprime, "trigger": trigger,
                               "layer2": layer2, "layer3": layer3},
                        letters=letters,
                        expected_trace=trace,
                    )
                    yield cand
                    yielded += 1
                    found = True
                    if cap is not None and yielded >= cap:
                        return
                    break

    def build_board(self, layout: Layout, cand: Candidate) -> list[dict]:
        board: list[dict] = []
        for role, letter in cand.letters.items():
            col, row = layout.positions[role]
            board.append({"x": col, "y": row, "letter": letter.upper(),
                          "variant": "normal"})
        for (c, r) in layout.stones:
            board.append({"x": c, "y": r, "letter": "X", "variant": "stone"})
        return board


# ─────────────────────────────────────────────────────────────────────────────
# Equation 2: Short 3v → 4h → 3h  (2 layers only)
# ─────────────────────────────────────────────────────────────────────────────
class EqShort3v4h3h(EqLinear3v4h4h3h):
    """Same skeleton as Equation 1 but stops at Layer 2: only preprime +
    trigger + layer2 word (no layer3). Good for tutorials — cleaner mental
    model than the 3-layer chain.

    Achieved by omitting the layer3_0..2 placements so row 0 at the end
    of Layer 1 gravity reads [_, _, L2[0], L2[1], L2[2], L2[3]] and no
    further word forms once L2 detonates.
    """
    id = "eq_3v_4h_3h_short"
    description = (
        "Vertical 3-letter preprime + 4-letter horizontal trigger + "
        "4-letter Layer 2. No Layer 3 — the cascade ends cleanly after "
        "L2 detonates. Tutorial-friendly."
    )
    layer_count = 2

    ROLES = [
        "preprime_0", "preprime_1", "preprime_2",
        "trigger_1", "trigger_2", "safety", "trigger_3",
        "decorative",
        "layer2_0", "layer2_1", "layer2_2", "layer2_3",
    ]

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
        pp_pool: list[str] | None = None,
        tr_pool_by_first: dict[str, list[str]] | None = None,
        l2_pool: list[str] | None = None,
        l3_pool: list[str] | None = None,
        cap: int | None = None,
    ) -> Iterator[Candidate]:
        """Shuffled (preprime, trigger) pair walk. Same variety discipline
        as Equation 1 but no Layer 3 cell placements — gravity leaves row 1
        empty so no cascade word can form after Layer 2 clears."""
        three = sorted(w for w in dict_words if len(w) == 3)
        four = sorted(w for w in dict_words if len(w) == 4)
        pp_list = list(pp_pool if pp_pool is not None else three)
        l2_list = list(l2_pool if l2_pool is not None else four)

        if tr_pool_by_first is None:
            tr_pool_by_first = {}
            for t in four:
                if t in dict_words and len(t) == 4:
                    tr_pool_by_first.setdefault(t[0], []).append(t)

        pairs: list[tuple[str, str]] = []
        for pp in pp_list:
            if pp not in dict_words or len(pp) != 3:
                continue
            for tr in tr_pool_by_first.get(pp[2], []):
                pairs.append((pp, tr))
        rng.shuffle(pairs)

        yielded = 0
        for (preprime, trigger) in pairs:
            if is_valid_word(trigger[:3], dict_words):
                continue
            l2_local = l2_list[:]
            rng.shuffle(l2_local)
            found = False
            for layer2 in l2_local:
                if layer2 not in dict_words or len(layer2) != 4:
                    continue
                safety = None
                for s in ["R", "L", "C", "F", "M", "G", "H", "B", "P", "T"]:
                    if is_valid_word(trigger[:3] + s, dict_words):
                        continue
                    if is_valid_word(trigger[1:3] + s, dict_words):
                        continue
                    safety = s
                    break
                if safety is None:
                    continue
                decorative = _safe_decorative(dict_words)
                letters = {
                    "preprime_0": preprime[0], "preprime_1": preprime[1],
                    "preprime_2": preprime[2],
                    "trigger_1": trigger[1], "trigger_2": trigger[2],
                    "safety": safety, "trigger_3": trigger[3],
                    "decorative": decorative,
                    "layer2_0": layer2[0], "layer2_1": layer2[1],
                    "layer2_2": layer2[2], "layer2_3": layer2[3],
                }
                trace = [
                    {"clusterSize": 1, "chainDepthAtScore": 0,
                     "triggerWord": trigger, "primedWord": preprime},
                    {"clusterSize": 1, "chainDepthAtScore": 2,
                     "triggerWord": layer2},
                ]
                cand = Candidate(
                    equation_id=self.id,
                    layout_name=layout.name,
                    words={"preprime": preprime, "trigger": trigger,
                           "layer2": layer2},
                    letters=letters,
                    expected_trace=trace,
                )
                yield cand
                yielded += 1
                found = True
                if cap is not None and yielded >= cap:
                    return
                break


# ─────────────────────────────────────────────────────────────────────────────
# Equation 3 (catalog position 4 in PM's list): Cluster 3v + 3v + 4h
# Two vertical preprimes at adjacent columns; horizontal trigger crosses
# both so Layer 1 detonates a cluster of two pre-existing primed words.
# Different visual topology from Equation 1.
# ─────────────────────────────────────────────────────────────────────────────
class EqCluster3v3v4h(Equation):
    id = "eq_3v_3v_4h_cluster"
    description = (
        "Two adjacent vertical preprimes + horizontal 4-letter trigger "
        "crossing both. Layer 1 detonates as a cluster of two primed "
        "words. Layer 2 forms from gravity drop."
    )
    layer_count = 2

    def iter_layouts(self) -> Iterator[Layout]:
        # Two preprimes at cols (anchor, anchor+2); trigger spans anchor..anchor+3.
        # Rightmost cell is at anchor+3 which must be <= COLS-1=5, so anchor ≤ 2.
        # row_offset slides the whole layout up; cascade math survives because
        # gravity still pulls to y=0. Mirror omitted (horizontal trigger
        # reversal issue — see EqLinear comment).
        for anchor in [0, 1, 2]:
            for row_offset in [0, 1, 2]:
                yield self._build_layout(anchor, row_offset)

    def _build_layout(self, anchor: int, row_offset: int) -> Layout:
        """Two preprimes at (anchor, 5..3) and (anchor+2, 5..3). Trigger at
        row 3 cols [anchor..anchor+3] overlaps both preprimes at their
        bottom letter (positions 0 and 2 of the trigger word).

        The "middle" trigger letter at anchor+1 sits between the preprimes;
        cells (anchor+1, 5) and (anchor+1, 4) stay empty for the preprime
        adjacency gap.

        Layer 2: 4-letter word forms at row 0 after Layer 1 clears.
        """
        def shift(col: int) -> int:
            return col + anchor

        def lift(row: int) -> int:
            return row + row_offset

        pos = {
            "pp_a_0": (shift(0), lift(5)),
            "pp_a_1": (shift(0), lift(4)),
            "pp_a_2": (shift(0), lift(3)),
            "pp_b_0": (shift(2), lift(5)),
            "pp_b_1": (shift(2), lift(4)),
            "pp_b_2": (shift(2), lift(3)),
            # Trigger: positions 0 (=pp_a_2), 1 (middle), 2 (=pp_b_2), 3 (edit target).
            "trigger_1": (shift(1), lift(3)),
            "safety":    (shift(3), lift(3)),
            "trigger_3": (shift(0), lift(1)),
            # Layer 2 letters placed so gravity drops them to y=0 after Layer 1.
            "layer2_0": (shift(0), lift(0)),
            "layer2_1": (shift(1), lift(4)),
            "layer2_2": (shift(2), lift(0)),
            "layer2_3": (shift(3), lift(0)),
        }
        stones_ref = [
            (0, 2), (1, 2), (2, 2), (3, 2),
        ]
        stones = [(shift(c), lift(r)) for (c, r) in stones_ref]

        canonical = {
            "type": "edit",
            "cell_a": list(pos["trigger_3"]),
            "cell_b": list(pos["safety"]),
        }
        name = f"anchor{anchor}_row{row_offset}"
        return Layout(
            name=name, positions=pos, stones=stones,
            canonical_action=canonical, mirror=False,
            preprime_col=shift(0),
        )

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
        pp_pool: list[str] | None = None,
        l2_pool: list[str] | None = None,
        cap: int | None = None,
        **_kwargs,
    ) -> Iterator[Candidate]:
        """Shuffled (pp_a, pp_b, trigger) triple walk. Trigger must satisfy
        trigger[0]=pp_a[2] and trigger[2]=pp_b[2] so it crosses both
        primed columns. One candidate per triple; L2 is re-shuffled per
        triple so no single L2 word dominates."""
        three = sorted(w for w in dict_words if len(w) == 3)
        four = sorted(w for w in dict_words if len(w) == 4)
        pp_list = list(pp_pool if pp_pool is not None else three)
        l2_list = list(l2_pool if l2_pool is not None else four)

        # Bucket 4-letter words by (first, third) so each pp-pair can
        # enumerate its valid triggers cheaply.
        trig_by_pattern: dict[tuple[str, str], list[str]] = {}
        for t in four:
            if t in dict_words and len(t) == 4:
                trig_by_pattern.setdefault((t[0], t[2]), []).append(t)

        triples: list[tuple[str, str, str]] = []
        for pp_a in pp_list:
            if pp_a not in dict_words or len(pp_a) != 3:
                continue
            for pp_b in pp_list:
                if pp_b == pp_a:
                    continue
                if pp_b not in dict_words or len(pp_b) != 3:
                    continue
                for tr in trig_by_pattern.get((pp_a[2], pp_b[2]), []):
                    triples.append((pp_a, pp_b, tr))
        rng.shuffle(triples)

        yielded = 0
        for (pp_a, pp_b, trigger) in triples:
            # Don't accidentally prime 3-letter subwords on row 3.
            if is_valid_word(trigger[:3], dict_words):
                continue
            if is_valid_word(trigger[1:], dict_words):
                continue
            # Safety filler must not form a word with trigger[1..2].
            safety = None
            for s in ["R", "L", "C", "F", "M", "G", "H", "B"]:
                if is_valid_word(trigger[1:3] + s, dict_words):
                    continue
                if is_valid_word(trigger[:3] + s, dict_words):
                    continue
                safety = s
                break
            if safety is None:
                continue

            l2_local = l2_list[:]
            rng.shuffle(l2_local)
            for layer2 in l2_local:
                if layer2 not in dict_words or len(layer2) != 4:
                    continue
                letters = {
                    "pp_a_0": pp_a[0], "pp_a_1": pp_a[1], "pp_a_2": pp_a[2],
                    "pp_b_0": pp_b[0], "pp_b_1": pp_b[1], "pp_b_2": pp_b[2],
                    "trigger_1": trigger[1], "safety": safety,
                    "trigger_3": trigger[3],
                    "layer2_0": layer2[0], "layer2_1": layer2[1],
                    "layer2_2": layer2[2], "layer2_3": layer2[3],
                }
                trace = [
                    # Layer 1: player forms `trigger` (cluster=1 new word)
                    # which overlaps BOTH primed pp_a and pp_b — both
                    # detonate. chainDepth increments per primed word:
                    # 0 → 1 (pp_a) → 2 (pp_b). Gravity bumps to 3, so
                    # Layer 2's word scores at rawStep=3.
                    {"clusterSize": 1, "chainDepthAtScore": 0,
                     "triggerWord": trigger},
                    {"clusterSize": 1, "chainDepthAtScore": 3,
                     "triggerWord": layer2},
                ]
                cand = Candidate(
                    equation_id=self.id,
                    layout_name=layout.name,
                    words={"pp_a": pp_a, "pp_b": pp_b,
                           "trigger": trigger, "layer2": layer2},
                    letters=letters,
                    expected_trace=trace,
                )
                yield cand
                yielded += 1
                if cap is not None and yielded >= cap:
                    return
                break  # one yield per triple

    def build_board(self, layout: Layout, cand: Candidate) -> list[dict]:
        board: list[dict] = []
        for role, letter in cand.letters.items():
            col, row = layout.positions[role]
            board.append({"x": col, "y": row, "letter": letter.upper(),
                          "variant": "normal"})
        for (c, r) in layout.stones:
            board.append({"x": c, "y": r, "letter": "X", "variant": "stone"})
        return board


# ─────────────────────────────────────────────────────────────────────────────
# Equation 4: Inverted — 3h preprime + 4v trigger + 3v layer2 (2-layer)
#
# Scope note: PM's catalog lists eq_3h_4v_4v_3v_inverted as a 3-layer
# equation, but a 4-letter vertical L2 + 3-letter vertical L3 is very hard
# to stage — vertical L2 consumes a whole column, and after it detonates
# there are no letters ABOVE to fall into a new column for L3 (col tops
# are already cleared by Layer 1). Shipping the 2-layer variant first
# so PM can confirm the "horizontal preprime at top + vertical cascade"
# shape is what they wanted; 3-layer extension is a follow-up.
# ─────────────────────────────────────────────────────────────────────────────
class EqInverted3h4v3v(Equation):
    id = "eq_3h_4v_3v_inverted"
    description = (
        "Horizontal 3-letter preprime at the top row + 4-letter vertical "
        "trigger extending DOWN through the preprime's leftmost cell + "
        "3-letter vertical Layer 2 forming in a separate column after "
        "gravity. 6 rocks between the trigger column and the L2 column "
        "chain-clear during Layer 1. Cascade axis is vertical — visually "
        "distinct from L210's horizontal bottom-cascade shape."
    )
    layer_count = 2

    def iter_layouts(self) -> Iterator[Layout]:
        # anchor: preprime leftmost col ∈ {0, 1, 2} (trigger extends down
        #   at this col; stones occupy (anchor+1, anchor+2); L2 lives at
        #   anchor+3).
        # preprime_row ∈ {5, 6, 7} — how high on the grid the preprime
        #   sits. Trigger extends 3 rows down from preprime_row, so
        #   trigger bottom is preprime_row - 3 (must be ≥ 0, so
        #   preprime_row ≥ 3 — our range satisfies).
        for anchor in [0, 1, 2]:
            for preprime_row in [5, 6, 7]:
                yield self._build_layout(anchor, preprime_row)

    def _build_layout(self, anchor: int, preprime_row: int) -> Layout:
        trigger_col = anchor
        trigger_bottom = preprime_row - 3
        stone_cols = [anchor + 1, anchor + 2]
        l2_col = anchor + 3

        # Safety cell — far from trigger column to avoid horizontal
        # collision with the preprime word at preprime_row.
        if anchor <= 1:
            safety_col = COLS - 1
        else:
            safety_col = 0

        pos = {
            "preprime_0": (anchor, preprime_row),
            "preprime_1": (anchor + 1, preprime_row),
            "preprime_2": (anchor + 2, preprime_row),
            "trigger_1":  (trigger_col, preprime_row - 1),
            "trigger_2":  (trigger_col, preprime_row - 2),
            # "safety" role cell IS IN the trigger word (post-edit holds
            # trigger[3]); pre-edit it holds the safety filler letter.
            "safety":     (trigger_col, trigger_bottom),
            # "trigger_3" role cell is FAR AWAY; pre-edit holds trigger[3],
            # post-edit holds the safety filler.
            "trigger_3":  (safety_col, preprime_row),
            # L2 letters at col l2_col, rows 4, 2, 0 (with gaps). After
            # gravity they compact to rows 2, 1, 0 forming the vertical
            # 3-letter word reading top-down.
            "layer2_0":   (l2_col, 4),
            "layer2_1":   (l2_col, 2),
            "layer2_2":   (l2_col, 0),
        }
        stones: list[tuple[int, int]] = []
        for sc in stone_cols:
            for sr in range(trigger_bottom, preprime_row):
                stones.append((sc, sr))

        canonical = {
            "type": "edit",
            "cell_a": list(pos["safety"]),
            "cell_b": list(pos["trigger_3"]),
        }
        name = f"col{anchor}_pprow{preprime_row}"
        return Layout(
            name=name, positions=pos, stones=stones,
            canonical_action=canonical, mirror=False,
            preprime_col=anchor,
        )

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
        pp_pool: list[str] | None = None,
        tr_pool_by_first: dict[str, list[str]] | None = None,
        l2_pool: list[str] | None = None,
        cap: int | None = None,
        **_kwargs,
    ) -> Iterator[Candidate]:
        """Shuffled (preprime, trigger) pair walk.
        Constraints:
          - trigger[0] == preprime[0] (trigger extends DOWN from preprime's
            leftmost cell; they share (anchor, preprime_row))
          - preprime is horizontal; trigger is vertical 4-letter
          - layer2 is 3-letter vertical (forms at col l2_col after gravity)
          - no accidental primes at load (checked via substring filters)
        """
        three = sorted(w for w in dict_words if len(w) == 3)
        four = sorted(w for w in dict_words if len(w) == 4)
        pp_list = list(pp_pool if pp_pool is not None else three)
        l2_list = list(l2_pool if l2_pool is not None else three)

        if tr_pool_by_first is None:
            tr_pool_by_first = {}
            for t in four:
                if t in dict_words and len(t) == 4:
                    tr_pool_by_first.setdefault(t[0], []).append(t)

        # Pairs: trigger[0] == preprime[0].
        pairs: list[tuple[str, str]] = []
        for pp in pp_list:
            if pp not in dict_words or len(pp) != 3:
                continue
            for tr in tr_pool_by_first.get(pp[0], []):
                pairs.append((pp, tr))
        rng.shuffle(pairs)

        yielded = 0
        for (preprime, trigger) in pairs:
            # Row preprime_row: preprime + gap + trigger_3 letter. The
            # preprime is the only word we want to prime. No substring
            # collision to worry about since the safety cell is separated
            # by at least one gap from the preprime.
            # Col trigger_col: preprime[0], trigger[1], trigger[2], safety
            # (pre-edit). Substrings to reject:
            #   trigger[:3]  — 3-letter top-3 of trigger column
            #   trigger[1:3] + safety  — 3-letter bottom-3 of trigger col
            if is_valid_word(trigger[:3], dict_words):
                continue
            # Pick safety letter.
            safety = None
            for s in ["R", "L", "C", "F", "M", "G", "H", "B", "P", "T"]:
                if is_valid_word(trigger[1:3] + s, dict_words):
                    continue
                if is_valid_word(trigger[:3] + s, dict_words):
                    continue
                safety = s
                break
            if safety is None:
                continue

            l2_local = l2_list[:]
            rng.shuffle(l2_local)
            for layer2 in l2_local:
                if layer2 not in dict_words or len(layer2) != 3:
                    continue
                # Col l2_col at load: layer2[0] @ row 4, layer2[1] @ row 2,
                # layer2[2] @ row 0 (with gaps at rows 1, 3). Vertical
                # scan at load from any of those rows hits empty cells →
                # length 1. No accidental load-time word. But we do need
                # the post-gravity word to be exactly `layer2`:
                # top-down from row 2 after gravity: layer2[0], [1], [2].
                # That's the assignment we encoded, so no extra check.
                letters = {
                    "preprime_0": preprime[0], "preprime_1": preprime[1],
                    "preprime_2": preprime[2],
                    "trigger_1": trigger[1], "trigger_2": trigger[2],
                    "safety":    safety,
                    "trigger_3": trigger[3],
                    "layer2_0": layer2[0], "layer2_1": layer2[1],
                    "layer2_2": layer2[2],
                }
                trace = [
                    {"clusterSize": 1, "chainDepthAtScore": 0,
                     "triggerWord": trigger, "primedWord": preprime},
                    {"clusterSize": 1, "chainDepthAtScore": 2,
                     "triggerWord": layer2},
                ]
                cand = Candidate(
                    equation_id=self.id,
                    layout_name=layout.name,
                    words={"preprime": preprime, "trigger": trigger,
                           "layer2": layer2},
                    letters=letters,
                    expected_trace=trace,
                )
                yield cand
                yielded += 1
                if cap is not None and yielded >= cap:
                    return
                break  # one yield per (preprime, trigger)

    def build_board(self, layout: Layout, cand: Candidate) -> list[dict]:
        board: list[dict] = []
        for role, letter in cand.letters.items():
            col, row = layout.positions[role]
            board.append({"x": col, "y": row, "letter": letter.upper(),
                          "variant": "normal"})
        for (c, r) in layout.stones:
            board.append({"x": c, "y": r, "letter": "X", "variant": "stone"})
        return board


# ─────────────────────────────────────────────────────────────────────────────
# Equation 5: Drop — same cascade math as linear but the player's action is
# a DROP from hand into the rightmost trigger column (col anchor+3), not
# an edit. Visual difference from linear:
#   - No pre-edit filler at the "safety" cell (it's EMPTY at load).
#   - No pre-edit letter at the "trigger_3" cell (it's EMPTY at load).
#   - Level bag is forced to supply trigger[3] as the first hand draw.
#   - UI shows drop target instead of edit charges.
#
# At play time: stones at col (anchor+3) rows 0-2 form a stack; the
# player's hand letter (trigger[3]) drops onto that stack and lands at
# the trigger row, completing the trigger word.
# ─────────────────────────────────────────────────────────────────────────────
class EqDrop3v4h4h3h(EqLinear3v4h4h3h):
    id = "eq_drop_3v_4h_4h_3h"
    description = (
        "Same 3-layer cascade math as the linear equation, but the "
        "player's action is a DROP from hand into the rightmost trigger "
        "column. The target cell is empty at load; stones below form a "
        "landing stack. Bag override forces first hand draw to be the "
        "trigger's completing letter."
    )
    layer_count = 3

    def iter_layouts(self) -> Iterator[Layout]:
        # Same anchor range as linear; row_offset must keep the drop
        # column's stones above row 0 (they already do at row_offset ∈
        # {0, 1, 2}) and keep the trigger row ≤ LEVEL_ROWS-3 so the
        # drop target cell is reachable from above.
        for anchor in [0, 1]:
            for row_offset in [0, 1, 2]:
                yield self._build_layout(anchor, row_offset)

    def _build_layout(self, anchor: int, row_offset: int) -> Layout:
        def shift(col: int) -> int:
            return col + (anchor - 1)

        def lift(row: int) -> int:
            return row + row_offset

        # Position map intentionally omits "safety" and "trigger_3" roles
        # from the linear equation — those cells are EMPTY at load. The
        # player's drop fills the former safety position.
        pos: dict[str, tuple[int, int]] = {
            "preprime_0": (shift(1), lift(5)),
            "preprime_1": (shift(1), lift(4)),
            "preprime_2": (shift(1), lift(3)),
            "trigger_1":  (shift(2), lift(3)),
            "trigger_2":  (shift(3), lift(3)),
            "decorative": (shift(2), lift(4)),
            "layer2_0":   (shift(2), lift(0)),
            "layer2_1":   (shift(3), lift(4)),
            "layer2_2":   (shift(4), lift(4)),
            "layer2_3":   (shift(5), lift(0)),
            "layer3_0":   (shift(1), lift(0)),
            "layer3_1":   (shift(2), lift(1)),
            "layer3_2":   (shift(3), lift(5)),
        }
        stones_ref = [
            (1, 2), (2, 2), (3, 2), (4, 2),
            (3, 1), (4, 1),
            (3, 0), (4, 0),
        ]
        stones = [(shift(c), lift(r)) for (c, r) in stones_ref]
        # For row_offset > 0, the lifted stone stack in the drop column
        # (col shift(4)) leaves the rows below empty — a drop would fall
        # past the stones and land at y=0 instead of the trigger row.
        # Plug those lower rows with extra stones so the landing target
        # is always lift(3). Extras chain-clear with the rest in Phase 9.9.
        for extra_row in range(row_offset):
            stones.append((shift(4), extra_row))

        # Canonical action template — trigger[3] letter is filled by
        # canonical_for() once we have a candidate.
        canonical_template = {
            "type": "drop",
            "col": shift(4),
            "letter": None,
        }
        name = f"col{anchor}_row{row_offset}"
        return Layout(
            name=name, positions=pos, stones=stones,
            canonical_action=canonical_template, mirror=False,
            preprime_col=shift(1),
        )

    def canonical_for(self, layout: Layout, cand: Candidate) -> dict:
        trigger = cand.words["trigger"]
        return {
            "type": "drop",
            "col": layout.canonical_action["col"],
            "letter": trigger[3],
        }

    def get_bag(self, cand: Candidate) -> dict | None:
        return {"letterOverrides": [{"letter": cand.words["trigger"][3],
                                      "count": 40}]}

    def iter_candidates(
        self,
        dict_words: frozenset[str],
        layout: Layout,
        rng: random.Random,
        pp_pool: list[str] | None = None,
        tr_pool_by_first: dict[str, list[str]] | None = None,
        l2_pool: list[str] | None = None,
        l3_pool: list[str] | None = None,
        cap: int | None = None,
    ) -> Iterator[Candidate]:
        """Same pair-walk as linear, but:
          - No safety letter to pick (target cell is empty pre-drop).
          - No "trigger_3" pre-edit cell constraint (also empty pre-drop).
          - Row 3 load-time check: trigger[:3] must not be dict-valid
            (preprime[2], trigger[1], trigger[2] with empty col 4).
          - Col 3 load-time check: trigger[2] + layer2[1] + layer3[2]
            must not form a dict word.
        """
        three = sorted(w for w in dict_words if len(w) == 3)
        four = sorted(w for w in dict_words if len(w) == 4)
        pp_list = list(pp_pool if pp_pool is not None else three)
        l2_list = list(l2_pool if l2_pool is not None else four)
        l3_list = list(l3_pool if l3_pool is not None else three)

        if tr_pool_by_first is None:
            tr_pool_by_first = {}
            for t in four:
                if t in dict_words and len(t) == 4:
                    tr_pool_by_first.setdefault(t[0], []).append(t)

        pairs: list[tuple[str, str]] = []
        for pp in pp_list:
            if pp not in dict_words or len(pp) != 3:
                continue
            for tr in tr_pool_by_first.get(pp[2], []):
                pairs.append((pp, tr))
        rng.shuffle(pairs)

        yielded = 0
        for (preprime, trigger) in pairs:
            if is_valid_word(trigger[:3], dict_words):
                continue  # row 3 at load would accidentally prime
            l2_local = l2_list[:]
            l3_local = l3_list[:]
            rng.shuffle(l2_local)
            rng.shuffle(l3_local)
            found = False
            for layer2 in l2_local:
                if found:
                    break
                if layer2 not in dict_words or len(layer2) != 4:
                    continue
                for layer3 in l3_local:
                    if layer3 not in dict_words or len(layer3) != 3:
                        continue
                    # Col at trigger_2 vertical run: trigger[2], layer2[1], layer3[2].
                    if is_valid_word(trigger[2] + layer2[1] + layer3[2], dict_words):
                        continue
                    decorative = _safe_decorative(dict_words)
                    letters = {
                        "preprime_0": preprime[0], "preprime_1": preprime[1],
                        "preprime_2": preprime[2],
                        "trigger_1": trigger[1], "trigger_2": trigger[2],
                        "decorative": decorative,
                        "layer2_0": layer2[0], "layer2_1": layer2[1],
                        "layer2_2": layer2[2], "layer2_3": layer2[3],
                        "layer3_0": layer3[0], "layer3_1": layer3[1],
                        "layer3_2": layer3[2],
                    }
                    trace = [
                        {"clusterSize": 1, "chainDepthAtScore": 0,
                         "triggerWord": trigger, "primedWord": preprime},
                        {"clusterSize": 1, "chainDepthAtScore": 2,
                         "triggerWord": layer2},
                        {"clusterSize": 1, "chainDepthAtScore": 4,
                         "triggerWord": layer3},
                    ]
                    cand = Candidate(
                        equation_id=self.id,
                        layout_name=layout.name,
                        words={"preprime": preprime, "trigger": trigger,
                               "layer2": layer2, "layer3": layer3},
                        letters=letters,
                        expected_trace=trace,
                    )
                    yield cand
                    yielded += 1
                    found = True
                    if cap is not None and yielded >= cap:
                        return
                    break


# ── Registry ────────────────────────────────────────────────────────────────
EQUATIONS: dict[str, Equation] = {
    "eq_3v_4h_4h_3h_linear":  EqLinear3v4h4h3h(),
    "eq_3v_4h_3h_short":      EqShort3v4h3h(),
    "eq_3v_3v_4h_cluster":    EqCluster3v3v4h(),
    "eq_3h_4v_3v_inverted":   EqInverted3h4v3v(),
    "eq_drop_3v_4h_4h_3h":    EqDrop3v4h4h3h(),
}


IMPLEMENTED_IDS = list(EQUATIONS.keys())

# Deferred — PM catalog items not yet implemented:
#   eq_4v_5h_4h_3h_long, eq_3v_4h_4h_3h_3h_deep,
#   eq_drop_3v_4h_4h_3h, eq_selfoverlap_4h_3h, eq_convergent_3v_3v_4h,
#   eq_cross_axis_3v_4h_4v_3h.
# eq_3h_4v_4v_3v_inverted (3-layer variant of inverted) deferred —
# vertical 4v L2 + 3v L3 is structurally very hard; shipping the
# 2-layer variant (eq_3h_4v_3v_inverted) first.
