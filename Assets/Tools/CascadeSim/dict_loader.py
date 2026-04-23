"""Dict loader. Mirrors WordDictionary.IsValidWord against dict_core.txt."""

from __future__ import annotations
from pathlib import Path
from functools import lru_cache


DEFAULT_DICT = Path(__file__).resolve().parents[2] / "Resources" / "dict_core.txt"


@lru_cache(maxsize=4)
def load_dict(path: str | Path = DEFAULT_DICT) -> frozenset[str]:
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(f"dictionary not found at {p}")
    words = set()
    with p.open("r", encoding="utf-8") as f:
        for line in f:
            w = line.strip().upper()
            if w:
                words.add(w)
    return frozenset(words)


def is_valid_word(word: str, words: frozenset[str]) -> bool:
    if not word:
        return False
    return word.upper() in words
