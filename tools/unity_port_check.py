#!/usr/bin/env python3
"""Compare a Unity probe's KEY=value output against its JS/Python counterpart.

C# runs float32 and JS float64, so an exact diff ALWAYS reports noise in the 6th
decimal. This compares numerically with a relative tolerance instead, which makes
the check a real regression gate: it passes on precision, fails on a port bug.

HISTORY, because two earlier versions of this file reported false confidence:

  1. The first keyed rows on `(id, "rho0" in kv)` — a hack written for the aero
     probe, where each plane prints exactly two lines. Any line without an `id`,
     or reusing one, silently COLLIDED and overwrote the previous row. The real
     coverage was a fraction of what it claimed: "PlaneCatalogProbe exact" had
     compared 3 values, not ~700; PropellerProbe 23, not 57.
  2. It then reported "PASS" having compared ZERO values, so a mispaired probe
     looked like a verified port.

Both are fixed here, and the approach is deliberately dumb so it cannot drift:
both sides are compared LINE BY LINE in sorted order. A line's "skeleton" (its
tokens with every number blanked) must match its opposite number, and then the
numbers on that line are compared pairwise. No keying, no dictionaries, nothing
to collide. Callers sort both files, so identical content lines up exactly, and a
line present on only one side shows up as a skeleton mismatch rather than being
quietly dropped.

  tools/unity_port_check.py js.txt cs.txt [rel_tol]
"""
import re, sys

NUM = re.compile(r"-?\d+\.?\d*(?:[eE][-+]?\d+)?")
MIN_VALUES = 4


def lines(path):
    out = []
    for raw in open(path):
        s = raw.strip()
        if s:
            out.append(s)
    return sorted(out)


# A VALUE is a number standing on its own — after '=' or whitespace, and not
# glued to letters. A number inside an identifier (jetAB_f420, an225, f15, 13R)
# is part of that line's IDENTITY and must never be blanked.
VALUE = re.compile(r"(?<![A-Za-z0-9_.])-?\d+\.?\d*(?:[eE][-+]?\d+)?(?![A-Za-z0-9_])")


def skeleton(s):
    """The line with every standalone NUMBER blanked, identifiers left intact.

    The first version blanked every number anywhere, which destroyed the identity
    of any line whose id contains digits: `id=jetAB_f420` and `id=jetAB_f90`
    both collapsed to `jetAB_f#`, so two different sample frames were paired and
    their RPMs "disagreed" by 0.88 — a pairing artifact, not a port bug. Same for
    `id=an225`/`id=f15`/`id=f117` in the plane-hall pack test.
    """
    return VALUE.sub("#", s)


def numbers(s):
    """Only the standalone values — identifier digits are identity, not data."""
    return VALUE.findall(s)


def main():
    if len(sys.argv) < 3:
        print("usage: unity_port_check.py js.txt cs.txt [rel_tol]")
        return 2
    a, b = lines(sys.argv[1]), lines(sys.argv[2])
    tol = float(sys.argv[3]) if len(sys.argv) > 3 else 1e-5

    if not a or not b:
        print(f"FAIL - one side is empty (js={len(a)} lines, cs={len(b)} lines).")
        return 2

    bad, checked, worst, worst_at = [], 0, 0.0, ""

    # Pair by skeleton so a line missing on one side is reported, not skipped.
    from collections import defaultdict
    bucket = defaultdict(list)
    for s in b:
        bucket[skeleton(s)].append(s)

    # ONE-SIDED FACTS. A probe legitimately asserts things only one side can
    # know: "does src/*.js still contain this function", "how many GLSL lines
    # are in the JS", "how many bare-arg call sites does this const have". The
    # C# cannot mirror those by construction, so they can never pair — and
    # before this, every such line made an otherwise-passing probe report FAIL.
    # Four probes were failing on exactly this today while their numeric
    # comparisons were clean.
    #
    # The convention: prefix the line with JSONLY (or CSONLY) and it is counted
    # and reported, never paired. Keeping unmatched lines a FAIL otherwise is
    # the point — an unmatched line is usually a C# line that should exist and
    # does not, which is precisely what this tool is for.
    def one_sided(line):
        t = line.split()
        return bool(t) and t[0] in ("JSONLY", "CSONLY", "NOTE")

    one_sided_js = sum(1 for s in a if one_sided(s))
    one_sided_cs = sum(1 for s in b if one_sided(s))
    a = [s for s in a if not one_sided(s)]
    b = [s for s in b if not one_sided(s)]
    bucket.clear()
    for s in b:
        bucket[skeleton(s)].append(s)

    unmatched_js = 0
    for s in a:
        k = skeleton(s)
        if not bucket[k]:
            unmatched_js += 1
            if len(bad) < 12:
                bad.append(f"no C# line matching: {s[:100]}")
            continue
        t = bucket[k].pop(0)
        for x, y in zip(numbers(s), numbers(t)):
            try:
                fa, fb = float(x), float(y)
            except ValueError:
                continue
            checked += 1
            denom = max(abs(fa), abs(fb), 1e-9)
            rel = abs(fa - fb) / denom
            if rel > worst:
                worst, worst_at = rel, s.split()[0] + " " + k[:60]
            if rel > tol and len(bad) < 12:
                bad.append(f"{s.split()[0]}: js={fa} cs={fb} rel={rel:.3e}  in {k[:70]}")

    leftover = sum(len(v) for v in bucket.values())

    print(f"compared {checked} values across {len(a)} js / {len(b)} cs lines, tol={tol:g}")
    if one_sided_js or one_sided_cs:
        print(f"one-sided facts not paired (by design): {one_sided_js} js, {one_sided_cs} cs")
    print(f"worst relative difference {worst:.3e} at {worst_at}")
    if unmatched_js or leftover:
        print(f"unmatched lines: {unmatched_js} js-only, {leftover} cs-only")

    if checked < MIN_VALUES:
        print(f"FAIL - only {checked} value(s) compared (need at least {MIN_VALUES}).")
        print("  Nothing meaningful was verified. Likely causes: the two sides print")
        print("  different KEY=value shapes, one side produced no output, or the probe")
        print("  was paired with the wrong counterpart.")
        return 2

    if bad:
        print(f"FAIL ({len(bad)} shown)")
        for x in bad:
            print("  " + x)
        return 1

    # Unmatched lines are a WARNING, not a failure. The grep that feeds this
    # picks up stray editor-log lines that happen to start with capitals, and a
    # probe legitimately printing extra diagnostics on one side should not read as
    # a port bug. What must fail is a numeric mismatch, or comparing almost
    # nothing — those are the two things that produced false confidence before.
    if unmatched_js or leftover:
        print(f"WARN - {unmatched_js} js-only and {leftover} cs-only lines were not paired; "
              "every number that COULD be paired agrees.")

    print(f"PASS - {checked} values match to float32 precision")
    return 0


if __name__ == "__main__":
    sys.exit(main())
