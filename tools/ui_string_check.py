#!/usr/bin/env python3
"""String-identity check for the UI port (UiProbe.cs vs tools/_probe_ui.mjs).

unity_port_check.py's numeric differ is meaningless here: the two probes
together print ~440 lines and almost none of them carry a standalone NUMBER
(id=/name=/text= are all identifiers or Georgian prose). A tolerance diff
over that is comparing nothing. This checker instead does what actually
proves the port: every Georgian string in the JS/CSS source must appear,
BYTE FOR BYTE, somewhere in the Unity-side UXML/USS/C# output, and nothing
written there in English has crept in by mistake.

Why "appears as a substring" rather than "two files line up exactly":
_probe_ui.mjs's GEORGIAN_RUN regex only matches the Georgian block
(U+10A0-U+10FF) plus a small set of adjoining punctuation — it does NOT
include ASCII letters, so a mixed string like index.html's
  "თბილისი // GRID-41.72N"
gets cut at the "G" and extracted as just "თბილისი //". The Unity side
(UiProbe.cs's TextAttr regex, reading the raw UXML text) has no such
restriction and will carry the FULL string. A whole-line/whole-string equals
check would false-fail on every one of these; checking that the JS-extracted
fragment occurs VERBATIM inside the Unity text is the real, still-strict
assertion the port needs: no transliteration, no re-typing, no dropped
diacritics.

The English-leak check needs the same care. The Settings screen legitimately
prints Latin text for KEYBOARD KEYS — "SPACE", "Shift", "Ctrl", "W A S D",
"Esc" — copied from index.html's own <b> key-cap tags, which the Georgian-only
regex also never captures. Hand-listing "TANK/DPS plus every key name" as
exceptions would be a maintenance trap. The general, principled rule used
here instead: ANY Unity-side string containing a Latin letter must occur
byte-for-byte in the ORIGINAL SOURCE (index.html / the ported *.js / style.css)
— true for "🛡 TANK"/"⚔ DPS" (target.js:213) exactly as it is true for
"SPACE" (index.html's key-cap <b> tags) and "◄ SETTINGS //" (index.html:282).
No exception list; the source is the only exception list there is. A string
that is NOT found there is real leaked/invented English.

Usage:
  tools/ui_string_check.py js.txt cs.txt
    js.txt / cs.txt: the already-sorted KEY=value dumps run_probes.sh builds
    from `node tools/_probe_ui.mjs` and UiProbe.cs's log (same convention as
    unity_port_check.py's two arguments).
"""
import re
import sys
from pathlib import Path

MIN_CHECKS = 4

ROOT = Path(__file__).resolve().parent.parent
SOURCE_FILES = [
    "index.html",
    "src/hud.js",
    "src/skillbar.js",
    "src/settings.js",
    "src/target.js",
    "src/mapui.js",
    "src/style.css",
]

STR_RE = re.compile(r"^STR file=(\S+) i=(\d+) text=(.*)$")
TOKEN_RE = re.compile(r"^TOKEN file=(\S+) name=(\S+) value=(.*)$")
HAS_LATIN = re.compile(r"[A-Za-z]")


def load_lines(path):
    out = []
    for raw in open(path, encoding="utf-8"):
        s = raw.rstrip("\n")
        if s.strip():
            out.append(s)
    return out


def parse(lines):
    strs, tokens = [], []
    for s in lines:
        m = STR_RE.match(s)
        if m:
            strs.append((m.group(1), m.group(3)))
            continue
        m = TOKEN_RE.match(s)
        if m:
            tokens.append((m.group(1), m.group(2), m.group(3)))
    return strs, tokens


def main():
    if len(sys.argv) < 3:
        print("usage: ui_string_check.py js.txt cs.txt")
        return 2
    js_lines, cs_lines = load_lines(sys.argv[1]), load_lines(sys.argv[2])
    if not js_lines or not cs_lines:
        print(f"FAIL - one side is empty (js={len(js_lines)} lines, cs={len(cs_lines)} lines).")
        return 2

    js_strs, js_tokens = parse(js_lines)
    cs_strs, cs_tokens = parse(cs_lines)

    raw_source = ""
    for rel in SOURCE_FILES:
        p = ROOT / rel
        if p.exists():
            raw_source += p.read_text(encoding="utf-8")
        else:
            print(f"FAIL - source file missing: {rel}")
            return 2

    cs_texts = [t for _, t in cs_strs]
    cs_blob = "\n".join(cs_texts)  # substring search across all cs strings at once

    # ---- 1. every JS Georgian fragment must appear verbatim in the Unity side ----
    missing = []
    js_unique = sorted({t for _, t in js_strs if t})
    for t in js_unique:
        if t not in cs_blob:
            missing.append(t)

    # ---- 2. no invented/leaked English on the Unity side -------------------------
    leaks = []
    # UXML encodes an embedded line break as the literal entity "&#10;"
    # (SettingsView's multi-line notes are one text="..." attribute spanning
    # what index.html writes as an actual newline between two tags/lines) —
    # split on it before the containment check, or a perfectly sourced note
    # never matches because the source has a real newline where the UXML has
    # five literal characters "&#10;".
    cs_unique = sorted({t for t in cs_texts if t})
    for t in cs_unique:
        if not HAS_LATIN.search(t):
            continue
        pieces = t.split("&#10;") if "&#10;" in t else [t]
        if any(p not in raw_source for p in pieces):
            leaks.append(t)

    # ---- 3. :root CSS custom properties must match name-for-name, value-for-value
    # A font token's CSS value is a whole fallback STACK ('"JetBrains Mono",
    # ui-monospace, "SF Mono", Menlo, monospace') while Unity has no such
    # concept — UI Toolkit resolves one FontAsset, so CyberTheme.cs (the only
    # place --mono/--display/--stm exist on the Unity side; there is no USS
    # custom property for them, see UiProbe.cs's own comment) carries just the
    # PRIMARY family. Compare the first stack entry on both sides rather than
    # the whole value, or a legitimately-ported font constant would always
    # read as a "mismatch". A plain colour/size token (no comma) is unaffected
    # — primary_family(x) is a no-op when x has no comma to split on.
    def primary_family(v):
        return v.split(",")[0].strip().strip('"').strip("'")

    js_tok_map = {name: val for _, name, val in js_tokens}
    cs_tok_map = {name: val for _, name, val in cs_tokens}
    token_missing = [n for n in js_tok_map if n not in cs_tok_map]
    token_mismatch = [
        (n, js_tok_map[n], cs_tok_map[n])
        for n in js_tok_map
        if n in cs_tok_map and primary_family(js_tok_map[n]) != primary_family(cs_tok_map[n])
    ]

    checked = len(js_unique) + len(js_tok_map)
    print(f"checked {len(js_unique)} Georgian strings + {len(js_tok_map)} CSS tokens "
          f"against {len(cs_unique)} Unity strings + {len(cs_tok_map)} Unity tokens")

    if checked < MIN_CHECKS:
        print(f"FAIL - only {checked} check(s) built (need at least {MIN_CHECKS}). "
              "One side likely produced nothing.")
        return 2

    ok = True
    if missing:
        ok = False
        print(f"FAIL - {len(missing)} JS/CSS string(s) not found anywhere in the Unity output:")
        for t in missing[:12]:
            print(f"  MISSING: {t!r}")
    if leaks:
        ok = False
        print(f"FAIL - {len(leaks)} Unity string(s) contain Latin text not present "
              "in index.html/src/*.js/style.css (leaked/invented English):")
        for t in leaks[:12]:
            print(f"  LEAKED: {t!r}")
    if token_missing:
        ok = False
        print(f"FAIL - {len(token_missing)} CSS custom propert(y/ies) missing on the Unity side:")
        for n in token_missing[:12]:
            print(f"  MISSING TOKEN: {n}")
    if token_mismatch:
        ok = False
        print(f"FAIL - {len(token_mismatch)} CSS custom propert(y/ies) with a different value:")
        for n, jv, cv in token_mismatch[:12]:
            print(f"  {n}: css={jv!r} unity={cv!r}")

    if not ok:
        return 1

    print(f"PASS - every Georgian string is carried, every CSS token matches, "
          f"and no un-sourced English string was found ({checked} checks)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
