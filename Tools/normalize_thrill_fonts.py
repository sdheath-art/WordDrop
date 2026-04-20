#!/usr/bin/env python3

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUT_DIR = REPO_ROOT / "Assets" / "Thrill Fonts" / "TTF Fonts"
DEFAULT_OUTPUT_DIR = DEFAULT_INPUT_DIR / "TMP Safe"
DEFAULT_FONTS = ("Bouncy", "Boulder", "Clarity")

CHARSETS = {
    "ascii": "U+0020-007E",
    "extended-ascii": "U+0020-007E,U+00A0-00FF",
}


def resolve_pyftsubset() -> str:
    for candidate in ("pyftsubset", "/opt/homebrew/bin/pyftsubset"):
        resolved = shutil.which(candidate) if "/" not in candidate else candidate
        if resolved and Path(resolved).exists():
            return resolved
    raise FileNotFoundError("Unable to find pyftsubset. Install fonttools or add pyftsubset to PATH.")


def normalize_font(pyftsubset: str, source: Path, output: Path, unicodes: str) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)

    command = [
        pyftsubset,
        str(source),
        f"--output-file={output}",
        f"--unicodes={unicodes}",
        "--glyph-names",
        "--notdef-glyph",
        "--notdef-outline",
        "--recommended-glyphs",
        "--name-IDs=*",
        "--name-legacy",
        "--drop-tables+=GSUB,GPOS,GDEF,FFTM,cvt,fpgm,prep",
    ]

    subprocess.run(command, check=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Create TMP-safe copies of the Thrill Fonts TTFs by stripping OpenType layout, "
            "hinting tables, and unused glyphs."
        )
    )
    parser.add_argument(
        "--input-dir",
        type=Path,
        default=DEFAULT_INPUT_DIR,
        help=f"Directory containing the source TTFs. Default: {DEFAULT_INPUT_DIR}",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=DEFAULT_OUTPUT_DIR,
        help=f"Directory for normalized TTFs. Default: {DEFAULT_OUTPUT_DIR}",
    )
    parser.add_argument(
        "--charset",
        choices=sorted(CHARSETS),
        default="ascii",
        help="Predefined Unicode range to keep. Default: ascii",
    )
    parser.add_argument(
        "--unicodes",
        help="Override the Unicode range passed to pyftsubset, for example: U+0020-007E,U+00A0-00FF",
    )
    parser.add_argument(
        "fonts",
        nargs="*",
        default=list(DEFAULT_FONTS),
        help="Font basenames without extension. Default: Bouncy Boulder Clarity",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    pyftsubset = resolve_pyftsubset()
    unicodes = args.unicodes or CHARSETS[args.charset]

    missing = []
    produced = []

    for font_name in args.fonts:
        source = args.input_dir / f"{font_name}.ttf"
        if not source.exists():
            missing.append(str(source))
            continue

        output = args.output_dir / f"{font_name} TMP.ttf"
        normalize_font(pyftsubset, source, output, unicodes)
        produced.append(output)

    if missing:
        for item in missing:
            print(f"Missing source font: {item}", file=sys.stderr)

    if not produced:
        print("No fonts were normalized.", file=sys.stderr)
        return 1

    print(f"Unicode range: {unicodes}")
    for path in produced:
        print(path.relative_to(REPO_ROOT))

    return 0 if not missing else 2


if __name__ == "__main__":
    raise SystemExit(main())
