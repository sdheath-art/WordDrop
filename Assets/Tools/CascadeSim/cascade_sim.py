#!/usr/bin/env python3.12
"""
Cascade simulator — Python port of the Level-mode cascade subset of
Assets/Scripts/WordDrop/RulesEngine.cs.

Inputs:
  --level  Path to a level_*.json
  --action JSON blob describing the player action:
             {"type": "drop",    "col": 4, "letter": "D"}
             {"type": "rewrite", "x": 4, "y": 3, "new_letter": "D"}
             {"type": "swap",    "cell_a": [1,3], "cell_b": [4,5]}

Output: JSON trace on stdout.

Source of truth for every rule: RulesEngine.cs. Line numbers cited inline.

Scope:
  - Level mode only (the only mode this tool is used for).
  - Handles: initial board priming (PrimeStartingBoardWords),
    drop / rewrite / swap entry, ScanSeedCellsOnly + FilterSubstringWords,
    DoCheckTriggers overlap rule w/ Phase 9.7 self-trigger unlock,
    DoExplode cluster-word loop + Rule C trigger-word clear,
    Phase 9.9 stone chain propagation (Level-mode loop-until-no-new-stones),
    ApplyGravityInData, seed-cell rebuild after gravity, chainDepth accounting.
  - Intentionally skipped (Survival-only): big-moment splash, junk splash,
    7-letter row clear, heat fuse, wild/gold/cyan mechanics, fuse expiry.
  - Scoring: computes base word score via CalculateWordScore but skips
    detonation bonus / chain multiplier / cluster boost math — PM spec
    Phase 10.5b: "match words + chainDepth; scores secondary".
"""

from __future__ import annotations
import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

from dict_loader import load_dict, is_valid_word


# ── Engine constants — mirrored from RulesEngine.cs ─────────────────────────
COLS = 6                # RulesEngine.cs:37
LEVEL_ROWS = 8          # RulesEngine.cs:39
MIN_WORD_LENGTH = 3     # RulesEngine.cs:63
MAX_WORD_LENGTH = 7     # RulesEngine.cs:64
CHAIN_DEPTH_CAP = 10    # RulesEngine.cs:3324
STONE_CHAIN_SAFETY_CAP = 50  # RulesEngine.cs:4044

# RulesEngine.cs:137-141 — two directions only, in this order.
DIRECTIONS = [(1, 0), (0, -1)]  # horizontal L→R, vertical top→bottom

# LetterData.cs:36-67
LETTER_POINTS = {
    'E': 1, 'T': 1,
    'A': 2, 'H': 2, 'I': 2, 'N': 2, 'O': 2, 'S': 2,
    'D': 3, 'L': 3, 'R': 3,
    'C': 4, 'F': 4, 'M': 4, 'U': 4, 'W': 4,
    'G': 5, 'P': 5, 'Y': 5,
    'B': 6,
    'V': 7,
    'K': 8,
    'J': 9, 'X': 9,
    'Q': 10, 'Z': 10,
}

# RulesEngine.cs:1404-1410
LENGTH_MULT = {3: 1.0, 4: 1.5, 5: 2.5, 6: 4.0, 7: 6.0}

# Detonation scoring (RulesEngine.cs:98-107)
DETONATION_SCORE_MULTIPLIER = 1.5
BREAKER_BONUS = 10
CHAIN_DEPTH_SCALE_CAP = 3


def calculate_word_score(word: str) -> int:
    if not word:
        return 0
    raw = sum(LETTER_POINTS.get(c, 0) for c in word.upper())
    mult = LENGTH_MULT.get(len(word), 1.0)
    return round(raw * mult)


def triangular_chain_multiplier(chain_depth: int) -> float:
    """RulesEngine.cs:126-130. 1 + (d² + d)/2, capped at d=3 (→ 7.0x)."""
    d = max(0, min(chain_depth, CHAIN_DEPTH_SCALE_CAP))
    return 1.0 + (d * d + d) / 2.0


# ── Data types ───────────────────────────────────────────────────────────────
@dataclass
class Cell:
    letter: str         # '' for empty; use board[col][row] is None for empty
    col: int
    row: int
    is_stone: bool = False


@dataclass
class WordMatch:
    word: str
    cells: list[tuple[int, int]]  # list of (col, row)
    direction: str                # "horizontal" or "vertical"

    @property
    def cell_key(self) -> str:
        s = sorted(self.cells)
        return ";".join(f"{c},{r}" for c, r in s)

    @property
    def key(self) -> str:
        return f"{self.word}|{self.cell_key}"


@dataclass
class PrimedWord:
    id: int
    word: str
    cells: list[tuple[int, int]]
    score: int


@dataclass
class Layer:
    chain_depth_at_score: int   # rawStep — chainDepth when pending words were scored
    cluster_size: int           # len(_stepPendingWords)
    primed_words: list[str]     # pre-existing primes that triggered
    trigger_words: list[str]    # new words formed this step (equals pending words)
    cleared_cells: list[tuple[int, int]]
    stones_chain_cleared: int
    score_delta: int


# ── Board + state machine ────────────────────────────────────────────────────
class CascadeSim:
    def __init__(self, dict_words: frozenset[str]):
        self.dict = dict_words
        # board[col][row] = Cell | None
        self.board: list[list[Cell | None]] = [
            [None] * LEVEL_ROWS for _ in range(COLS)
        ]
        self.primed: list[PrimedWord] = []
        self._next_primed_id = 1
        self.scored_keys: set[str] = set()  # persists across resolution
        # Per-resolution state (mirrors _step* fields)
        self.step_chain_depth = 0
        self.step_total_score = 0
        self.step_just_primed: set[int] = set()
        self.step_scored_keys: set[str] = set()
        self.step_pending_words: list[WordMatch] = []
        self.step_trigger_words: list[WordMatch] = []
        self.step_pending_triggers: list[int] = []
        self.step_seed_cells: list[tuple[int, int]] = []
        self.layers: list[Layer] = []
        self.warnings: list[str] = []

    # ── Setup ────────────────────────────────────────────────────────────────
    def load_level(self, level_path: Path) -> dict:
        data = json.loads(Path(level_path).read_text())
        self.load_level_data(data)
        return data

    def load_level_data(self, data: dict) -> None:
        """Populate board + prime starting board. Same as load_level but
        accepts a parsed dict so the designer can iterate without temp files."""
        for t in (data.get("startingBoard") or []):
            x, y = t["x"], t["y"]
            letter = (t.get("letter") or "").upper()
            variant = (t.get("variant") or "normal")
            if not letter:
                continue
            self.board[x][y] = Cell(
                letter=letter,
                col=x,
                row=y,
                is_stone=(variant == "stone"),
            )
        self._prime_starting_board()

    def _prime_starting_board(self) -> None:
        # MatchController.cs:1058-1088 — scan whole board, prime every valid
        # word, seed scored_keys so the player's first drop doesn't re-detect.
        words = self._scan_entire_board()
        for w in words:
            pid = self._next_primed_id
            self._next_primed_id += 1
            self.primed.append(
                PrimedWord(
                    id=pid,
                    word=w.word,
                    cells=list(w.cells),
                    score=calculate_word_score(w.word),
                )
            )
            self.scored_keys.add(w.key)

    # ── Scanners ─────────────────────────────────────────────────────────────
    def _in_bounds(self, c: int, r: int) -> bool:
        return 0 <= c < COLS and 0 <= r < LEVEL_ROWS

    def _cell(self, c: int, r: int) -> Cell | None:
        if not self._in_bounds(c, r):
            return None
        return self.board[c][r]

    def _scan_entire_board(self) -> list[WordMatch]:
        """Port of RulesEngine.cs ScanEntireBoard (line 2110)."""
        results: list[WordMatch] = []
        seen: set[str] = set()

        for dir_idx, (dc, dr) in enumerate(DIRECTIONS):
            for start_col in range(COLS):
                for start_row in range(LEVEL_ROWS):
                    if self.board[start_col][start_row] is None:
                        continue
                    # max contiguous run length (stone/empty breaks)
                    max_len = 0
                    while max_len < MAX_WORD_LENGTH + 1:
                        c = start_col + dc * max_len
                        r = start_row + dr * max_len
                        cell = self._cell(c, r)
                        if cell is None or cell.is_stone:
                            break
                        max_len += 1
                    if max_len < MIN_WORD_LENGTH:
                        continue
                    max_word_len = min(max_len, MAX_WORD_LENGTH)
                    for word_len in range(MIN_WORD_LENGTH, max_word_len + 1):
                        chars = []
                        cells: list[tuple[int, int]] = []
                        for step in range(word_len):
                            c = start_col + dc * step
                            r = start_row + dr * step
                            cell = self._cell(c, r)
                            if cell is None:
                                break
                            chars.append(cell.letter)
                            cells.append((c, r))
                        candidate = "".join(chars)
                        if len(candidate) < word_len:
                            continue
                        if not is_valid_word(candidate, self.dict):
                            continue
                        m = WordMatch(
                            word=candidate,
                            cells=cells,
                            direction="horizontal" if dir_idx == 0 else "vertical",
                        )
                        # Dedup by sorted cell key (RulesEngine.cs:2188-2194).
                        if m.key in seen:
                            continue
                        seen.add(m.key)
                        results.append(m)
        return results

    def _find_new_words(self, col: int, row: int) -> list[WordMatch]:
        """Port of RulesEngine.cs FindNewWords (line 2519). Walks both
        directions through the seed cell, enumerates every length of subword."""
        results: list[WordMatch] = []
        seen: set[str] = set()
        seed_cell = self._cell(col, row)
        if seed_cell is None or seed_cell.is_stone:
            return results

        for dir_idx, (dc, dr) in enumerate(DIRECTIONS):
            # walk backward
            run_start = 0
            safety = 0
            while safety < MAX_WORD_LENGTH + 1:
                nc = col - dc * (run_start + 1)
                nr = row - dr * (run_start + 1)
                cell = self._cell(nc, nr)
                if cell is None or cell.is_stone:
                    break
                run_start += 1
                safety += 1
            # walk forward
            run_end = 0
            safety = 0
            while safety < MAX_WORD_LENGTH + 1:
                nc = col + dc * (run_end + 1)
                nr = row + dr * (run_end + 1)
                cell = self._cell(nc, nr)
                if cell is None or cell.is_stone:
                    break
                run_end += 1
                safety += 1

            run_length = run_start + 1 + run_end
            if run_length < MIN_WORD_LENGTH:
                continue
            abs_start_col = col - dc * run_start
            abs_start_row = row - dr * run_start

            run_chars: list[str] = []
            run_cells: list[tuple[int, int]] = []
            for step in range(run_length):
                nc = abs_start_col + dc * step
                nr = abs_start_row + dr * step
                cell = self._cell(nc, nr)
                if cell is None:
                    run_chars = []
                    break
                run_chars.append(cell.letter)
                run_cells.append((nc, nr))
            if not run_chars:
                continue

            max_len = min(run_length, MAX_WORD_LENGTH)
            for word_len in range(MIN_WORD_LENGTH, max_len + 1):
                for start_offset in range(run_length - word_len + 1):
                    candidate = "".join(run_chars[start_offset : start_offset + word_len])
                    cells = run_cells[start_offset : start_offset + word_len]
                    if not is_valid_word(candidate, self.dict):
                        continue
                    m = WordMatch(
                        word=candidate,
                        cells=list(cells),
                        direction="horizontal" if dir_idx == 0 else "vertical",
                    )
                    if m.key in seen:
                        continue
                    seen.add(m.key)
                    results.append(m)
        return results

    @staticmethod
    def _filter_substring_words(words: list[WordMatch]) -> list[WordMatch]:
        """Port of RulesEngine.cs FilterSubstringWords (line 2865). Drops any
        word whose cells are a subset of a longer word's cells."""
        if len(words) <= 1:
            return words
        out: list[WordMatch] = []
        for i, w in enumerate(words):
            cells_i = set(w.cells)
            subsumed = False
            for j, other in enumerate(words):
                if i == j:
                    continue
                if len(other.cells) <= len(w.cells):
                    continue
                if cells_i.issubset(set(other.cells)):
                    subsumed = True
                    break
            if not subsumed:
                out.append(w)
        return out

    def _scan_seed_cells_only(self, seed_cells: list[tuple[int, int]]) -> list[WordMatch]:
        """Port of RulesEngine.cs ScanSeedCellsOnly (line 2226)."""
        seed_set = set(seed_cells)
        results: list[WordMatch] = []
        seen: set[str] = set()
        for seed in seed_cells:
            c, r = seed
            cell = self._cell(c, r)
            if cell is None:
                continue
            for m in self._find_new_words(c, r):
                if not any(cell in seed_set for cell in m.cells):
                    continue
                if m.key in seen:
                    continue
                seen.add(m.key)
                results.append(m)
        return self._filter_substring_words(results)

    # ── Primed registry helpers ──────────────────────────────────────────────
    def _primed_containing(self, cell: tuple[int, int]) -> list[PrimedWord]:
        return [p for p in self.primed if cell in p.cells]

    def _remove_primed(self, pid: int) -> None:
        self.primed = [p for p in self.primed if p.id != pid]
        self.step_just_primed.discard(pid)

    # ── Resolution phases ────────────────────────────────────────────────────
    def _do_detect_words(self) -> bool:
        """DoDetectWords (line 3321). Returns True if new words were found."""
        if self.step_chain_depth >= CHAIN_DEPTH_CAP:
            self.warnings.append(f"chain depth cap ({CHAIN_DEPTH_CAP}) hit")
            return False
        all_words = self._scan_seed_cells_only(self.step_seed_cells)
        new_words: list[WordMatch] = []
        for w in all_words:
            k = w.key
            if k in self.scored_keys or k in self.step_scored_keys:
                continue
            new_words.append(w)
        if not new_words:
            return False
        self.step_pending_words = new_words
        return True

    def _do_score_and_prime(self) -> int:
        """DoScoreAndPrime (line 3370). Adds each new word to primed registry,
        records it in step_just_primed, seeds scored_keys. Returns score delta.

        Applies the full chain multiplier (line 3379-3392):
            effectiveChainStep = chainDepth + max(0, clusterSize-1)
            chainMult = 1 + min(effectiveChainStep, 3) * 0.5
            chainBoosted = round(baseScore * chainMult)
        pw.Score is stored as the PRE-chain base score (matches engine's use
        of it as the detonation-bonus input)."""
        cluster_size = len(self.step_pending_words)
        effective_chain_step = self.step_chain_depth + max(0, cluster_size - 1)
        if effective_chain_step > 0:
            chain_mult = 1.0 + min(effective_chain_step, CHAIN_DEPTH_SCALE_CAP) * 0.5
        else:
            chain_mult = 1.0
        score_delta = 0
        for match in self.step_pending_words:
            base = calculate_word_score(match.word)
            chain_boosted = round(base * chain_mult)
            score_delta += chain_boosted
            pid = self._next_primed_id
            self._next_primed_id += 1
            self.primed.append(
                PrimedWord(
                    id=pid,
                    word=match.word,
                    cells=list(match.cells),
                    score=base,
                )
            )
            self.step_just_primed.add(pid)
            self.step_scored_keys.add(match.key)
        self.step_total_score += score_delta
        return score_delta

    def _do_check_triggers(self) -> None:
        """DoCheckTriggers (line 3547) — overlap triggers only (adjacency off)."""
        triggered_ids: list[int] = []
        triggered_set: set[int] = set()

        # Rule A (RulesEngine.cs:3583): in Level mode at chainDepth > 0, drop
        # the just-primed filter so gravity-formed words can self-trigger via
        # their own cells in the registry.
        skip_just_primed_filter = self.step_chain_depth > 0

        for match in self.step_pending_words:
            for cell in match.cells:
                overlapping = self._primed_containing(cell)
                for pw in overlapping:
                    if not skip_just_primed_filter and pw.id in self.step_just_primed:
                        continue
                    if pw.id in triggered_set:
                        continue
                    triggered_set.add(pw.id)
                    triggered_ids.append(pw.id)
        self.step_pending_triggers = triggered_ids
        # Snapshot trigger words (the pending words that fired this step) so
        # DoExplode's Rule-C clear can see them (RulesEngine.cs:3691).
        self.step_trigger_words = list(self.step_pending_words)

    def _do_explode(self) -> tuple[list[tuple[int, int]], list[str], int]:
        """DoExplode (line 3725). Returns (cleared_cells, primed_names, stones_cleared).

        Level-mode subset:
          - explode each triggered primed word's cells (line 3758-3783)
          - chainDepth++ per cluster word (line 3813)
          - Rule C: clear trigger-word cells that weren't already cleared,
            and remove primed words that used those cells (line 3823-3863)
          - Phase 9.9 stone chain: loop-until-no-new-stones (line 4049-4079)
        Skipped (Survival-only): big-moment splash, junk splash, 7-letter row clear.
        """
        cleared: list[tuple[int, int]] = []
        primed_names: list[str] = []
        detonation_bonus = 0

        for pid in self.step_pending_triggers:
            pw = next((p for p in self.primed if p.id == pid), None)
            if pw is None:
                continue
            primed_names.append(pw.word)
            for c, r in pw.cells:
                cell = self._cell(c, r)
                if cell is not None:
                    self.board[c][r] = None
                    cleared.append((c, r))
            # Detonation bonus (line 3785-3796). Heat=0 (fresh primes), gold=1.
            raw_bonus = round(pw.score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS
            # Chain multiplier evaluated at (chainDepth + 1) so the opening
            # detonation is 2x, not 1x (RulesEngine.cs:3792).
            chain_mult = triangular_chain_multiplier(self.step_chain_depth + 1)
            detonation_bonus += round(raw_bonus * chain_mult)
            self._remove_primed(pid)
            # Line 3813 — chain depth escalates per cluster word.
            self.step_chain_depth += 1
        self.step_total_score += detonation_bonus

        # Rule C (Level mode): clear trigger-word cells that survived.
        already_cleared = set(cleared)
        for tw in self.step_trigger_words:
            for c, r in tw.cells:
                if (c, r) in already_cleared:
                    continue
                cell = self._cell(c, r)
                if cell is not None:
                    self.board[c][r] = None
                    cleared.append((c, r))
                    already_cleared.add((c, r))

        # Any primed words that used trigger-word cells get removed so they
        # don't linger without tiles (line 3847-3862).
        for tw in self.step_trigger_words:
            for c, r in tw.cells:
                for pw in list(self.primed):
                    if (c, r) in pw.cells:
                        self._remove_primed(pw.id)

        # Phase 9.9 stone chain propagation (line 4049-4079). Level mode loops
        # until no new stones clear.
        stones_cleared = 0
        checked_stones: set[tuple[int, int]] = set()
        current_sources: list[tuple[int, int]] = list(cleared)
        dxdy = [(1, 0), (-1, 0), (0, 1), (0, -1)]
        iterations = 0
        while current_sources and iterations < STONE_CHAIN_SAFETY_CAP:
            iterations += 1
            next_sources: list[tuple[int, int]] = []
            for (cx, cy) in current_sources:
                for (ddx, ddy) in dxdy:
                    sx, sy = cx + ddx, cy + ddy
                    if not self._in_bounds(sx, sy):
                        continue
                    pos = (sx, sy)
                    if pos in checked_stones:
                        continue
                    checked_stones.add(pos)
                    adj = self.board[sx][sy]
                    if adj is not None and adj.is_stone:
                        self.board[sx][sy] = None
                        cleared.append(pos)
                        stones_cleared += 1
                        next_sources.append(pos)
            current_sources = next_sources
        if iterations >= STONE_CHAIN_SAFETY_CAP:
            self.warnings.append(
                f"stone chain safety cap ({STONE_CHAIN_SAFETY_CAP}) reached"
            )

        # Purge scored keys whose cells were destroyed so the rebuilt words
        # can re-detect after gravity (line 4198).
        cleared_set = set(cleared)
        self.scored_keys = {
            k for k in self.scored_keys
            if not any(
                (int(cc.split(",")[0]), int(cc.split(",")[1])) in cleared_set
                for cc in k.split("|", 1)[1].split(";")
            )
        }
        self.step_scored_keys = {
            k for k in self.step_scored_keys
            if not any(
                (int(cc.split(",")[0]), int(cc.split(",")[1])) in cleared_set
                for cc in k.split("|", 1)[1].split(";")
            )
        }

        # Any primed word whose cells were destroyed is stale — remove it.
        for pw in list(self.primed):
            if any(c in cleared_set for c in pw.cells):
                self._remove_primed(pw.id)

        self.step_pending_triggers = []
        return cleared, primed_names, stones_cleared

    def _do_gravity(self) -> bool:
        """ApplyGravityInData (line 2050) + DoGravity (line 4220). Returns
        True if any tile moved."""
        moves: dict[tuple[int, int], tuple[int, int]] = {}
        for col in range(COLS):
            surviving: list[Cell] = []
            for row in range(LEVEL_ROWS):
                cell = self.board[col][row]
                if cell is not None:
                    surviving.append(cell)
            # Detect whether any gap exists below a survivor.
            had_gap = False
            for i, cell in enumerate(surviving):
                if cell.row != i:
                    had_gap = True
                    break
            if not had_gap and len(surviving) == LEVEL_ROWS:
                continue
            if not had_gap and len(surviving) < LEVEL_ROWS:
                # If all survivors are already bottom-packed, nothing moves.
                all_packed = all(cell.row == i for i, cell in enumerate(surviving))
                if all_packed:
                    continue
            # Repack column.
            for r in range(LEVEL_ROWS):
                self.board[col][r] = None
            for i, cell in enumerate(surviving):
                old_row = cell.row
                cell.row = i
                cell.col = col
                self.board[col][i] = cell
                if old_row != i:
                    moves[(col, old_row)] = (col, i)

        if moves:
            # Update primed word cell positions.
            for pw in self.primed:
                pw.cells = [moves.get(c, c) for c in pw.cells]

            moved_cols = {v[0] for v in moves.values()}
            seeds: list[tuple[int, int]] = []
            for col in moved_cols:
                for row in range(LEVEL_ROWS):
                    if self.board[col][row] is not None:
                        seeds.append((col, row))
            self.step_seed_cells = seeds

            # Line 4279: chainDepth++ once per gravity pass.
            self.step_chain_depth += 1
            return True
        return False

    # ── Public driver ────────────────────────────────────────────────────────
    def apply_action(self, action: dict) -> None:
        t = action.get("type")
        self._reset_step_state()
        if t == "drop":
            col = action["col"]
            letter = action["letter"].upper()
            # Lowest empty row.
            target_row = -1
            for r in range(LEVEL_ROWS):
                if self.board[col][r] is None:
                    target_row = r
                    break
            if target_row < 0:
                raise ValueError(f"column {col} full — cannot drop")
            self.board[col][target_row] = Cell(
                letter=letter, col=col, row=target_row
            )
            self.step_seed_cells = [(col, target_row)]
        elif t == "rewrite":
            x, y = action["x"], action["y"]
            new_letter = action["new_letter"].upper()
            existing = self.board[x][y]
            if existing is None:
                raise ValueError(f"cell ({x},{y}) empty — cannot rewrite")
            # BeginRewrite (line 3160) — existing cell preserves nothing of
            # the old letter; stone flag is retained since rewrite isn't used
            # on stones in Level mode, but guard here anyway.
            if existing.is_stone:
                raise ValueError(f"cell ({x},{y}) is a stone — cannot rewrite")
            self.board[x][y] = Cell(letter=new_letter, col=x, row=y)
            # Purge any scored key that referenced this cell.
            self._purge_scored_for([(x, y)])
            # Invalidate primed words that included the old letter at this cell.
            self._remove_primed_containing_cells({(x, y)})
            self.step_seed_cells = [(x, y)]
        elif t in ("swap", "edit"):
            # Board↔board letter exchange — the in-game "edit" mechanic. Both
            # cells' letters are swapped; both cells are seeded so Layer 1
            # word detection scans each of them. "swap" kept as an alias.
            ca = tuple(action["cell_a"])
            cb = tuple(action["cell_b"])
            a = self.board[ca[0]][ca[1]]
            b = self.board[cb[0]][cb[1]]
            if a is None or b is None:
                raise ValueError(f"{t} endpoint empty")
            if a.is_stone or b.is_stone:
                raise ValueError(f"{t} cannot move stones")
            # Exchange letters in place; keep IsStone (false for both here).
            a_letter, b_letter = a.letter, b.letter
            self.board[ca[0]][ca[1]] = Cell(letter=b_letter, col=ca[0], row=ca[1])
            self.board[cb[0]][cb[1]] = Cell(letter=a_letter, col=cb[0], row=cb[1])
            self._purge_scored_for([ca, cb])
            self._remove_primed_containing_cells({ca, cb})
            self.step_seed_cells = [ca, cb]
        else:
            raise ValueError(f"unknown action type: {t}")

    def _reset_step_state(self) -> None:
        self.step_chain_depth = 0
        self.step_total_score = 0
        self.step_just_primed = set()
        self.step_scored_keys = set()
        self.step_pending_words = []
        self.step_trigger_words = []
        self.step_pending_triggers = []
        self.step_seed_cells = []
        self.layers = []
        self.warnings = []

    def _purge_scored_for(self, cells: list[tuple[int, int]]) -> None:
        cs = set(cells)
        def touches(k: str) -> bool:
            _, cellpart = k.split("|", 1)
            for cc in cellpart.split(";"):
                x, y = cc.split(",")
                if (int(x), int(y)) in cs:
                    return True
            return False
        self.scored_keys = {k for k in self.scored_keys if not touches(k)}
        self.step_scored_keys = {k for k in self.step_scored_keys if not touches(k)}

    def _remove_primed_containing_cells(self, cells: set[tuple[int, int]]) -> None:
        for pw in list(self.primed):
            if any(c in cells for c in pw.cells):
                self._remove_primed(pw.id)

    def resolve(self) -> None:
        """Run the state machine until no new words detect."""
        # Iteration 0: player's action is the seed. If the seed cell(s) yield
        # no new word, the action was a no-op — mirror engine by exiting.
        iteration = 0
        while iteration < CHAIN_DEPTH_CAP + 2:
            iteration += 1
            if not self._do_detect_words():
                break
            chain_depth_at_score = self.step_chain_depth
            cluster_size = len(self.step_pending_words)
            trigger_words_names = [w.word for w in self.step_pending_words]
            score_delta = self._do_score_and_prime()
            self._do_check_triggers()
            cleared, primed_names, stones = self._do_explode()
            self.layers.append(
                Layer(
                    chain_depth_at_score=chain_depth_at_score,
                    cluster_size=cluster_size,
                    primed_words=primed_names,
                    trigger_words=trigger_words_names,
                    cleared_cells=cleared,
                    stones_chain_cleared=stones,
                    score_delta=score_delta,
                )
            )
            # If nothing actually exploded (no primed trigger and no Rule-C
            # cells), gravity is a no-op and we'd loop forever on the same
            # seed. Only gravity-moved + unscored-key situations progress.
            moved = self._do_gravity()
            if not moved and not cleared:
                break
            if not moved:
                # No tiles fell, but something cleared (e.g. trigger-word on a
                # self-contained row). Re-detect requires seed refresh.
                # Fall back to last cleared cells as seeds — rare branch.
                self.step_seed_cells = [c for c in cleared if self._cell(*c)]
                if not self.step_seed_cells:
                    break

    def to_report(self) -> dict:
        return {
            "layers": [
                {
                    "chainDepthAtScore": L.chain_depth_at_score,
                    "clusterSize": L.cluster_size,
                    "triggerWords": L.trigger_words,
                    "primedWordsTriggered": L.primed_words,
                    "clearedCells": [list(c) for c in L.cleared_cells],
                    "stonesChainCleared": L.stones_chain_cleared,
                    "scoreDelta": L.score_delta,
                }
                for L in self.layers
            ],
            "totalLayers": len(self.layers),
            "totalScore": self.step_total_score,
            "finalChainDepth": self.step_chain_depth,
            "warnings": list(self.warnings),
        }


def _run_single(level_path: Path, dict_words: frozenset[str], action: dict) -> dict:
    sim = CascadeSim(dict_words)
    sim.load_level(level_path)
    try:
        sim.apply_action(action)
    except ValueError as e:
        return {"error": str(e), "layers": []}
    sim.resolve()
    return sim.to_report()


def run_sim_on_data(level_data: dict, dict_words: frozenset[str], action: dict) -> dict:
    sim = CascadeSim(dict_words)
    sim.load_level_data(level_data)
    try:
        sim.apply_action(action)
    except ValueError as e:
        return {"error": str(e), "layers": []}
    sim.resolve()
    return sim.to_report()


def explore_actions_on_data(
    level_data: dict,
    dict_words: frozenset[str],
    hand_letters: list[str] | None = None,
    min_layers: int = 1,
) -> dict:
    """In-memory variant of explore_actions for use by cascade_designer."""
    if hand_letters is None:
        hand_letters = [chr(c) for c in range(ord("A"), ord("Z") + 1)]
    results: list[dict] = []
    total_enumerated = 0
    total_errored = 0
    zero_layer = 0
    for action in _enumerate_actions(level_data, hand_letters):
        total_enumerated += 1
        rep = run_sim_on_data(level_data, dict_words, action)
        if "error" in rep:
            total_errored += 1
            continue
        layers = rep.get("layers", [])
        if len(layers) == 0:
            zero_layer += 1
        if len(layers) < min_layers:
            continue
        words: list[str] = []
        for L in layers:
            pw = L.get("primedWordsTriggered") or []
            tw = L.get("triggerWords") or []
            if pw:
                seen = set()
                for w in pw:
                    if w not in seen:
                        seen.add(w)
                        words.append(w)
            else:
                words.extend(tw)
        results.append(
            {
                "action": action,
                "layers": len(layers),
                "words": words,
                "score": rep.get("totalScore", 0),
                "finalChainDepth": rep.get("finalChainDepth", 0),
            }
        )
    results.sort(key=lambda r: (-r["layers"], -r["score"]))
    return {
        "totalEnumerated": total_enumerated,
        "totalErrored": total_errored,
        "zeroLayerCount": zero_layer,
        "results": results,
    }


def _enumerate_actions(level_data: dict, hand_letters: list[str]):
    from itertools import combinations

    placed = level_data.get("startingBoard") or []
    occupied = {(t["x"], t["y"]): t for t in placed}
    occupied_letter_cells = [
        (t["x"], t["y"])
        for t in placed
        if (t.get("variant") or "normal") != "stone"
        and (t.get("letter") or "")
    ]

    # edit pairs (letter cells only)
    for a, b in combinations(occupied_letter_cells, 2):
        # skip swaps where letters are identical (would be a no-op)
        la = occupied[a].get("letter", "").upper()
        lb = occupied[b].get("letter", "").upper()
        if la == lb:
            continue
        yield {"type": "edit", "cell_a": list(a), "cell_b": list(b)}

    # drops — any column with an empty slot
    for col in range(COLS):
        has_empty = any(
            (col, r) not in occupied for r in range(LEVEL_ROWS)
        )
        if not has_empty:
            continue
        for letter in hand_letters:
            yield {"type": "drop", "col": col, "letter": letter}

    # rewrites — every letter cell, every hand letter (except same)
    for (x, y) in occupied_letter_cells:
        orig = occupied[(x, y)].get("letter", "").upper()
        for letter in hand_letters:
            if letter == orig:
                continue
            yield {"type": "rewrite", "x": x, "y": y, "new_letter": letter}


def explore_actions(
    level_path: Path,
    dict_words: frozenset[str] | None = None,
    hand_letters: list[str] | None = None,
    min_layers: int = 1,
) -> list[dict]:
    """Enumerate every legal action and run the sim on each. Returns a
    list of {"action", "layers", "words", "score", "finalChainDepth"} dicts,
    sorted by layer count descending, then score descending.

    hand_letters defaults to A-Z for full exploration.
    min_layers filters out actions that don't even produce 1 cascade layer.
    """
    if dict_words is None:
        dict_words = load_dict()
    if hand_letters is None:
        hand_letters = [chr(c) for c in range(ord("A"), ord("Z") + 1)]

    level_data = json.loads(Path(level_path).read_text())
    results: list[dict] = []
    total_enumerated = 0
    total_errored = 0
    zero_layer = 0
    for action in _enumerate_actions(level_data, hand_letters):
        total_enumerated += 1
        rep = _run_single(level_path, dict_words, action)
        if "error" in rep:
            total_errored += 1
            continue
        layers = rep.get("layers", [])
        if len(layers) == 0:
            zero_layer += 1
        if len(layers) < min_layers:
            continue
        words: list[str] = []
        for L in layers:
            # Prefer pre-existing primed names (Layer 1 overlap) when present;
            # otherwise use the trigger word (self-trigger via Rule A).
            pw = L.get("primedWordsTriggered") or []
            tw = L.get("triggerWords") or []
            if pw:
                # Dedup within the layer while preserving order.
                seen = set()
                for w in pw:
                    if w not in seen:
                        seen.add(w)
                        words.append(w)
            else:
                words.extend(tw)
        results.append(
            {
                "action": action,
                "layers": len(layers),
                "words": words,
                "score": rep.get("totalScore", 0),
                "finalChainDepth": rep.get("finalChainDepth", 0),
            }
        )

    results.sort(key=lambda r: (-r["layers"], -r["score"]))
    return {
        "totalEnumerated": total_enumerated,
        "totalErrored": total_errored,
        "zeroLayerCount": zero_layer,
        "results": results,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", required=True, type=Path)
    ap.add_argument("--action", type=str,
                    help='JSON, e.g. \'{"type":"rewrite","x":4,"y":3,"new_letter":"D"}\'')
    ap.add_argument("--dict", type=Path, default=None,
                    help="Override dict path (defaults to Assets/Resources/dict_core.txt)")
    ap.add_argument("--explore", action="store_true",
                    help="Enumerate every legal action and rank by layer count")
    ap.add_argument("--hand", type=str, default=None,
                    help="Comma-separated letters for drop/rewrite exploration "
                         "(default: A-Z)")
    ap.add_argument("--min-layers", type=int, default=1,
                    help="Filter out actions producing fewer than this many layers")
    ap.add_argument("--top", type=int, default=25,
                    help="Show top N results in explore mode")
    args = ap.parse_args(argv)

    dict_words = load_dict(args.dict) if args.dict else load_dict()

    if args.explore:
        hand = (
            [c.strip().upper() for c in args.hand.split(",")]
            if args.hand
            else None
        )
        out = explore_actions(
            args.level,
            dict_words=dict_words,
            hand_letters=hand,
            min_layers=args.min_layers,
        )
        results = out["results"]
        layer_counts: dict[int, int] = {}
        for r in results:
            layer_counts[r["layers"]] = layer_counts.get(r["layers"], 0) + 1
        summary = {
            "totalEnumerated": out["totalEnumerated"],
            "totalErrored": out["totalErrored"],
            "zeroLayerCount": out["zeroLayerCount"],
            "passingFilter": sum(layer_counts.values()),
            "layerCountHistogram": dict(sorted(layer_counts.items(), reverse=True)),
            "top": results[: args.top],
        }
        print(json.dumps(summary, indent=2))
        return 0

    if not args.action:
        ap.error("--action is required unless --explore is set")
    sim = CascadeSim(dict_words)
    sim.load_level(args.level)
    sim.apply_action(json.loads(args.action))
    sim.resolve()
    report = sim.to_report()
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
