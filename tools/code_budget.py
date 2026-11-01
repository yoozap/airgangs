#!/usr/bin/env python3
"""Port coverage measured against CODE lines, not raw `wc -l`.

tools/port_progress.py's percentage is taken against raw line counts, which
weights a module's blank lines and comments the same as its code — and this
codebase is documented unusually heavily (roughly 1 comment line for every 3
code lines). This script re-derives two things raw lines cannot show:

  1. The real code/comment/blank split per module (via tools/code_lines.py).
  2. Which of those CODE lines the C# port actually CITES, found by grepping
     unity/Assets/Scripts/**/*.cs and unity/Assets/Editor/**/*.cs for a
     reference to "<module>.js:NNN" or "<module>.js:NNN-MMM" (optionally
     prefixed "src/", and allowing a comma-separated list of such tokens in
     one citation, e.g. "foot.js:795,899" or "foot.js:649-680"). This is
     evidence the C# itself asserts, not a number copied from
     docs/port_manifest.json's `covers` claims — the manifest's own claims
     can (and did, see docs/port_units/README.md) double-count, go stale, or
     restate over each other.

This script is READ-ONLY with respect to docs/port_manifest.json,
docs/port_units/*.json and tools/port_progress.py — it never writes them.
It only prints a report (optionally as JSON) that a human (or
docs/CODE_BUDGET.md) can cite.

## Method

- `excluded` lines come from the manifest's own `excluded` array (dead code /
  asset-delivered geometry) — same source port_progress.py uses — and are
  subtracted from a module's CODE budget before computing a percentage,
  exactly like port_progress.py does for its raw-line budget. We exclude
  only the CODE lines inside each excluded range (a comment inside a dead
  function was never going to be "ported" either way, so it is irrelevant
  to a code-basis count, but we still intersect with the real range rather
  than subtracting the manifest's raw `excluded` count, which is a raw-line
  count and would double count any comment lines in that range).
- `cinema.js` is in the manifest's `deferred_modules` and is dropped
  entirely, matching port_progress.py.
- Citation line numbers that fall on a COMMENT or BLANK line in the JS
  source (a citation drifted after an edit, or cites a doc-comment) are
  counted as cited ranges but cannot mark a code line as cited if there is
  no code line there — they simply contribute nothing, which is correct:
  you cannot port a blank line.

## Known limits of the citation grep (see also tools/code_lines.py's own
limits section for the JS-side parser)

- A reference like "foot.js:795,899" is parsed as two independent points,
  not a range; "foot.js:795-899" is a full inclusive range. Both forms are
  used in this codebase.
- The regex requires the exact module basename immediately followed by
  `.js:` and a digit, with a non-identifier character (or start-of-file)
  immediately before it, so it will not match a substring of a longer
  identifier. It will match inside both `//` comments, `///` XML-doc
  comments, and C# string literals (e.g. Tooltip/Probe-assertion strings) —
  deliberately: a Probe or Tooltip that names the exact JS line it verifies
  is exactly the kind of evidence this script is hunting for, and the task
  that requested this script confirmed citations live in all of those
  places, not only `//` line comments.
- This finds CITED ranges, not verified-correct ranges. A citation can be
  wrong (cites the wrong line) or stale (the JS moved since). This script
  has no way to detect that; it is reporting what the C# claims to be
  ported, the same evidentiary standard the task asked for.

Usage:
    python3 tools/code_budget.py            # table, sorted by uncited desc
    python3 tools/code_budget.py --json      # machine-readable
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from code_lines import analyze_file  # noqa: E402


def _parse_range_list(body):
    """'795,899' -> {795,899}; '649-680' -> {649..680}; mixes both."""
    out = set()
    for part in body.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            a, b = int(a.strip()), int(b.strip())
            if a > b:
                a, b = b, a
            out.update(range(a, b + 1))
        else:
            out.add(int(part))
    return out


def _load_cs_text():
    chunks = []
    for base in ("Assets/Scripts", "Assets/Editor"):
        d = os.path.join(ROOT, "unity", base)
        for dirpath, _, names in os.walk(d):
            for nm in names:
                if nm.endswith(".cs"):
                    p = os.path.join(dirpath, nm)
                    with open(p, encoding="utf-8", errors="replace") as f:
                        chunks.append(f.read())
    return chunks


def citation_lines_for_module(mod, cs_texts, cache={}):
    """Union of all JS line numbers of `mod` cited anywhere in the C#."""
    if mod in cache:
        return cache[mod]
    pat = re.compile(
        r"(?<![A-Za-z0-9_])(?:src/)?"
        + re.escape(mod)
        + r"\.js:\s*(\d+(?:\s*-\s*\d+)?(?:\s*,\s*\d+(?:\s*-\s*\d+)?)*)"
    )
    lines = set()
    tokens = 0
    for text in cs_texts:
        for m in pat.finditer(text):
            tokens += 1
            lines |= _parse_range_list(m.group(1))
    cache[mod] = (lines, tokens)
    return cache[mod]


def main(argv):
    man = json.load(open(os.path.join(ROOT, "docs", "port_manifest.json")))
    deferred = set(x[:-3] for x in man.get("deferred_modules", []))

    mod_to_system = {m: s for s, mods in man["systems"].items() for m in mods}
    excluded_lines = {}     # mod -> set of specific JS line numbers excluded
    excluded_unlocated = {}  # mod -> count of excluded lines with no identifiable
    # line numbers (manifest says "scattered" instead of a range). These cannot
    # be removed from the code-line SET (we don't know which lines), so they are
    # subtracted from the budget as a raw count instead — documented, not hidden.
    for x in man.get("excluded", []):
        mod = (x.get("js") or "")[:-3]
        rng = x.get("lines", "")
        if not mod:
            continue
        try:
            excluded_lines.setdefault(mod, set()).update(_parse_range_list(rng))
        except ValueError:
            excluded_unlocated[mod] = excluded_unlocated.get(mod, 0) + x.get("excluded", 0)

    cs_texts = _load_cs_text()

    rows = []
    for mod, system in sorted(mod_to_system.items()):
        if mod in deferred:
            continue
        path = os.path.join(ROOT, "src", mod + ".js")
        if not os.path.exists(path):
            continue
        r = analyze_file(path)

        excl_lines = excluded_lines.get(mod, set())
        excl_code = excl_lines & r.code_lines
        unlocated = excluded_unlocated.get(mod, 0)

        budget_code = (r.code_lines - excl_code)
        budget_code_n = max(0, len(budget_code) - unlocated)
        cited_lines, tokens = citation_lines_for_module(mod, cs_texts)
        cited_code = budget_code & cited_lines
        # unlocated exclusions reduce the budget but can't be removed from
        # the set itself, so clamp uncited at the now-smaller budget.
        uncited_code = max(0, budget_code_n - len(cited_code))

        rows.append({
            "module": mod,
            "system": system,
            "total": r.total,
            "code": r.code,
            "comment": r.comment,
            "blank": r.blank,
            "excluded_code": len(excl_code) + unlocated,
            "budget_code": budget_code_n,
            "citation_tokens": tokens,
            "cited_code": min(len(cited_code), budget_code_n),
            "uncited_code": uncited_code,
            "pct_code": (100.0 * min(len(cited_code), budget_code_n) / budget_code_n) if budget_code_n else 100.0,
        })

    rows.sort(key=lambda r: -r["uncited_code"])

    grand = {
        k: sum(r[k] for r in rows)
        for k in ("total", "code", "comment", "blank", "excluded_code",
                   "budget_code", "citation_tokens", "cited_code", "uncited_code")
    }
    grand["pct_code"] = (100.0 * grand["cited_code"] / grand["budget_code"]) if grand["budget_code"] else 0.0

    by_system = {}
    for r in rows:
        s = by_system.setdefault(r["system"], {
            "budget_code": 0, "cited_code": 0, "uncited_code": 0, "excluded_code": 0, "code": 0,
        })
        for k in ("budget_code", "cited_code", "uncited_code", "excluded_code", "code"):
            s[k] += r[k]
    for s in by_system.values():
        s["pct_code"] = (100.0 * s["cited_code"] / s["budget_code"]) if s["budget_code"] else 0.0

    out = {"modules": rows, "systems": by_system, "overall": grand}

    if "--json" in argv:
        print(json.dumps(out, indent=2))
        return 0

    print(f"{'module':<14}{'system':<10}{'total':>7}{'code':>7}{'cmt+blk':>8}"
          f"{'excl':>6}{'budget':>7}{'cited':>7}{'uncited':>8}{'pct':>7}")
    print("-" * 86)
    for r in rows:
        print(f"{r['module']:<14}{r['system']:<10}{r['total']:>7}{r['code']:>7}"
              f"{r['comment']+r['blank']:>8}{r['excluded_code']:>6}{r['budget_code']:>7}"
              f"{r['cited_code']:>7}{r['uncited_code']:>8}{r['pct_code']:>6.1f}%")
    print("-" * 86)
    g = grand
    print(f"{'TOTAL':<14}{'':<10}{g['total']:>7}{g['code']:>7}{g['comment']+g['blank']:>8}"
          f"{g['excluded_code']:>6}{g['budget_code']:>7}{g['cited_code']:>7}{g['uncited_code']:>8}"
          f"{g['pct_code']:>6.1f}%")
    print()
    print("By system:")
    for s, v in sorted(by_system.items(), key=lambda kv: -kv[1]["uncited_code"]):
        print(f"  {s:<10} budget={v['budget_code']:>6}  cited={v['cited_code']:>6}  "
              f"uncited={v['uncited_code']:>6}  {v['pct_code']:5.1f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
