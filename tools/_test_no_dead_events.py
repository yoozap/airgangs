#!/usr/bin/env python3
"""Catch DEAD DELEGATES: an Action/Func callback field invoked but never subscribed.

This is the third shape of "finished code that never runs" in this port, and the
one neither `tools/_test_no_orphans.py` nor a `new X(` grep can see.  It is how
`GameLoop.OnHint` came to have 15+ invoke sites and ZERO subscribers, silently
dropping every Georgian contextual prompt AND the death-screen title; and how
`CrashSequence.Hooks.ShowWasted` came to be invoked by a safety valve commented
"never leave the player with no restart".

WHY THIS NEEDS TO RESOLVE TYPES, which two cheaper versions did not:

  * Matching only bare/`this.` assignment calls ~117 of 143 fields dead, because
    `_foot.OnHint = ...` -- member access on another object -- is the dominant
    subscription form here.
  * Matching the NAME anywhere left of `=` calls GameLoop.OnHint LIVE, because
    `GameLoop.cs:392` contains `p.Hull.OnHint = s => OnHint?.Invoke(s)` -- a
    DIFFERENT type's identically-named field.  That is the flagship bug, so a
    guard that misses it is worse than no guard: it manufactures the false
    confidence this project keeps getting burned by.

So an assignment counts for a field only when the receiver's type resolves to the
field's own declaring type.  Receivers are resolved from declarations seen
anywhere in Assets (`PlaneHullDamage Hull;`, `public T X { get`, `var x = new T(`,
`T x = ...`), which handles the `p.Hull.OnHint` case correctly by looking up
`Hull`.

LIVE means: subscribed from somewhere under Assets/Scripts.  An assignment found
ONLY under Assets/Editor is reported EDITOR-ONLY, not live -- a delegate wired by
a baker or a probe is exactly as dead at runtime as a class constructed only by a
probe (`Editor/Probes/CoreRestProbe.cs:80` asserts `OnShowWasted != null` and
PASSES while the hook leads nowhere).

Known limits, stated rather than hidden: receivers whose type cannot be resolved
are reported UNRESOLVED and not counted either way; assignment through reflection
or through a delegate passed as a parameter is invisible; `Name = null` reads as a
subscription.  Under-reporting is the chosen direction.
"""
import os, re, sys

ROOT    = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(ROOT, "unity/Assets/Scripts")
EDITOR  = os.path.join(ROOT, "unity/Assets/Editor")
# Assets/Tests (the play-mode harness, 2026-10-30) subscribes diagnostic hooks
# such as PlaneGroundRoll.Diag / PlaneWallContact.Diag that are null in the
# game on purpose. A subscriber there is EDITOR-ONLY, same as a probe.
TESTS   = os.path.join(ROOT, "unity/Assets/Tests")
BASELINE = os.path.join(ROOT, "docs/dead_events_baseline.txt")

# Deliberately unsubscribed, with the reason.  Add ONLY when not wiring it is the
# decision -- never to silence a real gap.  "FILE.cs:Field" keys.
ALLOW = {
    "FootWeapons.cs":  "deliberate KEEP-not-wired duplicate, pending the belt-ownership decision",
    "FootBelt.cs":     "deliberate KEEP-not-wired duplicate, pending the belt-ownership decision",
    "FootBoarding.cs": "deliberate KEEP-not-wired duplicate, pending the belt-ownership decision",
    "FootWeaponFrame.cs": "deliberate KEEP-not-wired duplicate, pending the belt-ownership decision",
}

DECL = re.compile(
    r"public\s+(?:static\s+)?(?:event\s+)?(?:System\.)?(?:Action|Func)(?:<[^;>]*(?:<[^;>]*>)?[^;>]*>)?\s+"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*;")
CLASS = re.compile(r"\b(?:class|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")
# `T name;` / `T name =` / `public T Name { get` -- the receiver-type sources
FIELD_OF_TYPE = re.compile(
    r"\b([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:;|=[^=]|\{\s*get)")
VAR_NEW = re.compile(r"\b(?:var|[A-Z][A-Za-z0-9_.]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[({]")


def cs_files(base):
    for d, _, fs in os.walk(base):
        for f in fs:
            if f.endswith(".cs"):
                yield os.path.join(d, f)


NEW_OF = re.compile(r"\bnew\s+([A-Za-z_][A-Za-z0-9_.]*)\s*(?:\([^;{]*\))?\s*\{?\s*$")


def scan_scopes(lines):
    """Two maps, both keyed by 1-based line number:

    classes[i]  -> innermost class/struct name
    inits[i]    -> set of types whose OBJECT INITIALIZER encloses the line

    `inits` exists because C# object-initializer syntax assigns a field with no
    receiver at all:

        var hooks = new CrashHooks { ShowWasted = () => ..., GroundAt = ... };

    A bare `Field =` was only credited when the ENCLOSING CLASS was the field's
    owner, so every `CrashHooks` field assigned this way read as dead -- a dozen
    of them, all genuinely live, which both hid real regressions among them and
    made a correct fix look unfixed. The brace that opens the initializer may be
    on the following line (`var hooks = new CrashHooks` then `{`), so the type is
    held pending until the next `{`.
    """
    classes, inits = {}, {}
    cstack, istack, depth = [], [], 0
    pend_class = pend_new = None
    for i, line in enumerate(lines, 1):
        stripped = line.lstrip()
        if not stripped.startswith(("//", "*", "///")):
            m = CLASS.search(line)
            if m:
                pend_class = m.group(1)
            n = NEW_OF.search(line.split("//")[0].rstrip())
            if n:
                pend_new = n.group(1).split(".")[-1]
        for ch in line:
            if ch == "{":
                depth += 1
                if pend_class:
                    cstack.append((depth, pend_class)); pend_class = None
                elif pend_new:
                    istack.append((depth, pend_new)); pend_new = None
            elif ch == "}":
                while cstack and cstack[-1][0] >= depth:
                    cstack.pop()
                while istack and istack[-1][0] >= depth:
                    istack.pop()
                depth -= 1
        classes[i] = cstack[-1][1] if cstack else None
        inits[i] = {t for _, t in istack}
    return classes, inits


def main():
    bodies, enclose, initof = {}, {}, {}
    for p in list(cs_files(SCRIPTS)) + list(cs_files(EDITOR)) + list(cs_files(TESTS)):
        bodies[p] = open(p, encoding="utf-8", errors="ignore").read().splitlines()
        enclose[p], initof[p] = scan_scopes(bodies[p])

    # identifier -> set of type names it may hold
    ident_type = {}
    for p, lines in bodies.items():
        for line in lines:
            if line.lstrip().startswith("//"):
                continue
            for m in VAR_NEW.finditer(line):
                ident_type.setdefault(m.group(1), set()).add(m.group(2).split(".")[-1])
            for m in FIELD_OF_TYPE.finditer(line):
                ty, nm = m.group(1).split(".")[-1], m.group(2)
                if ty not in ("return", "new", "if", "else", "public", "private"):
                    ident_type.setdefault(nm, set()).add(ty)

    decls = []  # (path, line, field, declaring class)
    for p in cs_files(SCRIPTS):
        for i, line in enumerate(bodies[p], 1):
            m = DECL.search(line)
            if m:
                decls.append((p, i, m.group(1), enclose[p].get(i)))

    dead, editor_only, unresolved = [], [], []
    for p, line, name, owner in decls:
        if os.path.basename(p) in ALLOW:
            continue
        # NOTE the lookbehind forbids only identifier chars, NOT the dot: the
        # optional recv group consumes the dot itself, and a `.`-forbidding
        # lookbehind here made every member-access subscription fail to match,
        # which called 120 of 143 fields dead.
        assign = re.compile(r"(?:(?P<recv>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?"
                            r"(?<![A-Za-z0-9_])" + re.escape(name) + r"\s*(?:\+|-)?=(?!=)")
        runtime, edit, unk = [], [], []
        for q, qlines in bodies.items():
            for j, l in enumerate(qlines, 1):
                if (q == p and j == line) or l.lstrip().startswith("//"):
                    continue
                # finditer, not search: one line can hold two assignments, and
                # `_foot.OnHint = (s) => OnHint?.Invoke(s)` must not let the
                # first of them decide the whole line.
                for m in assign.finditer(l):
                    site = f"{os.path.relpath(q, ROOT)}:{j}"
                    recv = m.group("recv")
                    if recv is None or recv == "this":
                        # bare/this. -> the declaring class, OR an object
                        # initializer of that type enclosing this line.
                        ok = (enclose[q].get(j) == owner
                              or owner in initof[q].get(j, ()))
                    elif recv in ident_type:
                        ok = owner in ident_type[recv]
                    else:
                        unk.append(site); continue
                    if ok:
                        (edit if (q.startswith(EDITOR) or q.startswith(TESTS)) else runtime).append(site)
        rel = f"{os.path.relpath(p, ROOT)}:{line}"
        label = f"{owner}.{name}"
        if runtime:
            continue
        if edit:
            editor_only.append((label, rel, edit))
        elif unk:
            unresolved.append((label, rel, unk))
        else:
            dead.append((label, rel))

    for label, rel, sites in editor_only:
        print(f"EDITOR-ONLY  {label:38s} {rel}  (only {', '.join(sites[:2])})")
    for label, rel in dead:
        print(f"DEAD         {label:38s} {rel}")
    for label, rel, sites in unresolved:
        print(f"unresolved   {label:38s} {rel}  (receiver type unknown at {sites[0]})")

    # Baseline, so this is a REGRESSION guard and not a wall of known debt.
    # Rewrite it with --bless once a batch is genuinely fixed; the fixed entries
    # must DISAPPEAR from it, never be added to it to quieten a failure.
    found = {lab for lab, _ in dead} | {lab for lab, _, _ in editor_only}
    base = set()
    if os.path.exists(BASELINE):
        base = {l.split("#")[0].strip() for l in open(BASELINE) if l.split("#")[0].strip()}
    if "--bless" in sys.argv:
        with open(BASELINE, "w") as fh:
            fh.write("# Dead delegates known as of blessing -- see docs/QUALITY_BAR.md,\n"
                     "# 'Dead delegates'.  A fix REMOVES its line; never add one to go green.\n")
            for lab in sorted(found):
                fh.write(lab + "\n")
        print(f"blessed {len(found)} entries into {os.path.relpath(BASELINE, ROOT)}")
        return 0

    new, fixed = sorted(found - base), sorted(base - found)
    print(f"\n_test_no_dead_events: {len(decls)} public Action/Func fields, "
          f"{len(ALLOW)} file(s) allowlisted, {len(dead)} dead, "
          f"{len(editor_only)} editor-only, {len(unresolved)} unresolved; "
          f"{len(base)} baselined.")
    for lab in fixed:
        print(f"  FIXED (drop from the baseline): {lab}")
    for lab in new:
        print(f"  NEW DEAD DELEGATE: {lab}")
    if new:
        print("A declared callback with no subscriber drops everything it carries, and the\n"
              "ported percentage cannot see it -- the JS lines ARE replaced.\n"
              "See docs/QUALITY_BAR.md, 'Dead delegates'.")
        return 1
    print("OK -- no new dead delegates.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
