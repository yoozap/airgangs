# !! DO NOT CITE THE 77% FIGURE — THE METHOD IS INVALID AS A COVERAGE MEASURE

Added by the coordinator immediately after this report landed, before anything
was acted on.

**What this document actually measures is CITATION DENSITY, not coverage.** The
author flagged two known errors and could not resolve them (aero.js reading 0%
while being one of the best-verified modules in the port; crane.js reading 100%
against a same-day decision that it has 693 lines left). I tested a third case
and it settles it:

    Combat/Ballistics.cs cites exactly 7 distinct lines of ballistics.js (of 695)

— and ballistics.js was read end to end and CLOSED at 695/695 earlier the same
session, including a real port of `_draw()`'s InstancedMesh round-streaks into
`Fx/BulletTrailFx.cs` plus a handwritten `BulletStreak.shader`. Seven citations,
a fully ported file.

So "combat is 37.3% on a code basis" is not a finding about combat. It is a
finding that combat's C# carries few `// src/x.js:NNN` comments, which nothing
in this project ever required. The 8,108 "uncited code lines" are mostly ported.

**What IS solid here, and worth keeping:** the measurement that **28% of the raw
JS line budget (14,405 of 51,376 lines) is comments or blanks**. That comes
straight off the source files and does not depend on citations at all. It means
the raw-line percentage overstates how much CODE is ported — but this document's
alternative understates it far worse.

**Conclusion: neither number is trustworthy.** Raw lines overcount by counting
comments as portable work; citation-grep undercounts by assuming a comment
discipline nobody enforced. Do NOT apply the suggested `port_progress.py`
change — it would swap a known overstatement for a larger, less obvious one.

A real fix needs per-module coverage asserted by PROBES (which compare actual
numbers) rather than by either line counting or comment archaeology. That is a
bigger piece of work than this session should start.

`tools/code_lines.py` is still genuinely useful and correct for what it does —
the code/comment/blank split. `tools/code_budget.py` should be read as a
citation-density report and labelled as such.

---

# Code-basis port coverage (2026-10-27)

`tools/port_progress.py` reports **93.74%** of a 49,415-line budget (3,092
lines left). That budget is raw `wc -l`: comments and blank lines count the
same as code. This doc re-derives coverage against **code lines only**, using
the C#'s own line citations as evidence (not the manifest's `covers` claims),
and finds a materially lower, more trustworthy figure.

## Headline

> **Code-basis coverage ≈ 77% cited (mid-70s% after two known measurement
> biases cancel partially — see "Known biases" below), against port_progress's
> 93.74%. The raw-line number overstates completion by roughly 17 points.**
> Uncited code remaining: **~8,100 lines** (vs. port_progress's claimed 3,092
> raw lines left — port_progress's number is smaller than the truth even
> though it is the more inflated-looking metric, because it is a raw-line
> count against a raw-line budget that is itself 28% non-code).

The gap is concentrated in one system: **combat is 37.3% cited on a code
basis vs. 94.12% on port_progress's raw-line basis** — a 57-point gap, the
single biggest distortion in the whole budget. `character` (94.2% code-basis
vs 94.72% raw) and `ui` (90.5% vs 87.58%, code-basis actually *higher*) show
the method isn't a one-directional discount; it is measuring something raw
lines cannot see.

## Method

1. **`tools/code_lines.py`** (new, reusable) — a single-pass character
   scanner per `src/*.js` file that classifies every line as code / comment
   / blank. It tracks `//`, `/* */` (multi-line and trailing-on-code), single-
   and double-quoted strings, and template literals (backtick strings,
   including `${...}` substitutions, parsed recursively) — a GLSL shader
   living in a backtick string is CODE, not prose, even though it looks like
   text. Regex-vs-division `/` ambiguity is resolved with the standard
   shallow heuristic (previous significant char decides); verified safe
   against every regex literal in this repo (grepped by hand, see the
   module's own docstring for the one keyword-adjacent case that would break
   it, which does not occur here).

2. **`tools/code_budget.py`** (new) — builds the real coverage map:
   - Greps every `.cs` file under `unity/Assets/Scripts/**` and
     `unity/Assets/Editor/**` for `<module>.js:NNN` or `<module>.js:NNN-MMM`
     (optionally `src/`-prefixed, optionally comma-lists like
     `foot.js:795,899`), **in any context** — `//` comments, `///` doc
     comments, Tooltip/Probe-assertion strings — because the task's own
     numbers (foot.js's 416 tokens / 6,052 lines) only reproduce when
     citations outside plain `//` comments are included; restricting to
     `// src/...` alone found only 562 tokens project-wide and ~49 for
     foot.js, nowhere near the documented baseline.
   - Unions the cited line numbers per module, intersects with that module's
     CODE line set from `code_lines.py` → **cited-code**. Code lines with no
     citation anywhere → **uncited-code** (the real remaining-work queue).
   - Subtracts the manifest's own `excluded` entries (dead code / asset-
     delivered geometry) from the budget, same as port_progress.py, but only
     the CODE lines inside each excluded range (an excluded range's comment
     lines were never going to be "ported" either way). One exclusion
     (`fire.js`, 180 lines, "scattered" — no identifiable line numbers) is
     subtracted as a raw count instead, since there is nothing to intersect.
   - `cinema.js` is dropped entirely (manifest's `deferred_modules`), same as
     port_progress.py.
   - Both scripts are **read-only** toward `docs/port_manifest.json`,
     `docs/port_units/*.json`, and `tools/port_progress.py` — nothing here
     writes to any of them.

3. **Sanity checks performed by hand** (not just trusted from the script):
   - **foot.js**: an earlier audit (cited in this task) found ~105 real
     uncited code lines against port_progress's claimed 1,067 remaining.
     This method finds **154** — same order of magnitude, not the "900 means
     your method is wrong" failure mode. Inspecting the actual uncited
     ranges: ~132 of the 154 are `OnFoot`'s constructor field list (dozens of
     `this._x = new THREE.Vector3()` lines) and the file's import/const
     header — exactly the kind of boilerplate a C# port represents as plain
     field declarations with one citation at the class level, not one
     citation per field. If that class-level citation uses a narrower range
     than the actual field block, this method will under-credit it. The
     remaining ~22 lines are inside real logic (lines 3065-3230) and look
     like genuine gaps.
   - **aero.js** came back **0% cited / 146 lines "uncited"**, which is the
     "your method is wrong, debug it" signal the task warned about — so it
     was debugged. `unity/Assets/Scripts/Flight/FlightModel.cs` says
     "Port of src/aero.js" in its header and `FlightProbe.cs` / `FlyProbe.cs`
     claim a numeric match to the original to **1.03e-06** — this is almost
     certainly one of the most rigorously verified modules in the port. It
     has zero *line-numbered* citations (`aero.js:NNN`), only whole-file
     references, which this method's regex cannot credit. **Known false
     negative, not fixed algorithmically** (see "Known biases").
   - **crane.js** came back **100% cited / 0 lines uncited**, which
     contradicts `docs/port_units/README.md`'s own "DECIDED 2026-10-27" note
     that crane.js "stays at 693 lines remaining, not 636" as of *today*.
     Reading the actual citations in `WindRider.cs`, `WindTunnel.cs`, and
     `WarehouseCrane.cs`: several are file-header *scope* statements spanning
     hundreds of lines each (`crane.js:1146-1845`, `crane.js:868-1943`,
     `crane.js:1442-1846`) that collectively blanket nearly the whole 1,993-
     line file. These read as "this file is the Unity side of this chunk of
     crane.js" scope declarations, not a literal claim that every one of
     those ~700-1,000 lines was individually transliterated. **Known false
     positive** — this method cannot distinguish a scope-level header
     citation from a verified line-level one by grep alone.

## Known biases (both documented, not silently papered over)

- **Under-citation** (foot.js-style): C# field/constant declarations ported
  as a block under one citation narrower than their actual JS range read as
  "uncited" even though the functionality exists. This pushes the headline
  number *down* (makes the port look less done than it is).
- **Over-citation** (crane.js-style): wide file-header "this file covers
  JS lines X-Y" scope statements get credited as if every line in that huge
  range were individually verified. This pushes the headline number *up*
  (makes the port look more done than it is).
- These two biases partially offset in the aggregate, but not module-for-
  module, and not necessarily in equal magnitude — there was no budget in
  this task to hand-audit all 57 modules the way foot.js, aero.js, crane.js,
  and mapui.js (spot-checked clean: 2 trivial uncited lines, both
  plausible) were. Treat the 77.0% raw script output as the floor of a
  band whose ceiling is a few points higher once scope-citations like
  crane.js's are discounted and whole-file verified ports like aero.js's are
  credited. **The headline conclusion — material overcounting by the raw-line
  metric, concentrated in combat — is robust to both biases; the second
  decimal place is not.**
- Tiny modules `gltf.js` and `hotpatch.js` show 0% cited with *zero*
  whole-file or line citations anywhere in the C# (not even a scope mention).
  These may be genuinely unaddressed, or they may be structurally replaced
  by Unity's native asset pipeline / C# not needing JS-style runtime
  monkey-patching — this script does not have the context to decide which,
  and does not invent an exclusion. Flagging for a manifest-owner call.

## Per-module table (code basis, sorted by uncited-code descending)

| module | system | total | code | comment+blank | excluded(code) | budget(code) | cited | uncited | % |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|
| citymap | city | 8552 | 6829 | 1723 | 101 | 6728 | 5690 | **1038** | 84.6% |
| planedamage | combat | 1998 | 1339 | 659 | 0 | 1339 | 325 | **1014** | 24.3% |
| weapons | combat | 1287 | 768 | 519 | 0 | 768 | 140 | **628** | 18.2% |
| impactfx | combat | 928 | 745 | 183 | 0 | 745 | 235 | **510** | 31.5% |
| dents | combat | 965 | 585 | 380 | 9 | 576 | 99 | **477** | 17.2% |
| ballistics | combat | 695 | 446 | 249 | 0 | 446 | 39 | **407** | 8.7% |
| game | core | 2769 | 1873 | 896 | 0 | 1873 | 1548 | **325** | 82.6% |
| shooting | combat | 585 | 403 | 182 | 0 | 403 | 128 | **275** | 31.8% |
| devmode | core | 357 | 297 | 60 | 0 | 297 | 37 | **260** | 12.5% |
| gunanim | combat | 693 | 422 | 271 | 15 | 407 | 168 | **239** | 41.3% |
| breakwall | combat | 309 | 252 | 57 | 0 | 252 | 27 | **225** | 10.7% |
| glass | combat | 354 | 302 | 52 | 0 | 302 | 82 | **220** | 27.2% |
| breakup | combat | 409 | 317 | 92 | 0 | 317 | 100 | **217** | 31.5% |
| blast | combat | 452 | 379 | 73 | 0 | 379 | 195 | **184** | 51.5% |
| foot | character | 6458 | 4230 | 2228 | 6 | 4224 | 4070 | **154** | 96.4% |
| aero † | flight | 248 | 146 | 102 | 0 | 146 | 0 | **146** | 0.0% |
| skillbar | ui | 375 | 314 | 61 | 0 | 314 | 169 | **145** | 53.8% |
| rockets | combat | 340 | 278 | 62 | 19 | 259 | 123 | **136** | 47.5% |
| papertargets | combat | 552 | 391 | 161 | 0 | 391 | 263 | **128** | 67.3% |
| parks | city | 546 | 494 | 52 | 0 | 494 | 366 | **128** | 74.1% |
| propeller | flight | 1123 | 819 | 304 | 11 | 808 | 708 | **100** | 87.6% |
| guns | combat | 674 | 571 | 103 | 0 | 571 | 473 | **98** | 82.8% |
| characters | character | 2886 | 1855 | 1031 | 260 | 1595 | 1501 | **94** | 94.1% |
| planes | flight | 1614 | 1018 | 596 | 0 | 1018 | 934 | **84** | 91.7% |
| audiofx | core | 1100 | 768 | 332 | 35 | 733 | 650 | **83** | 88.7% |
| net | core | 233 | 165 | 68 | 0 | 165 | 96 | **69** | 58.2% |
| anims | character | 408 | 264 | 144 | 171 | 93 | 31 | **62** | 33.3% |
| airport | city | 1102 | 792 | 310 | 0 | 792 | 731 | **61** | 92.3% |
| wind | core | 111 | 73 | 38 | 0 | 73 | 14 | **59** | 19.2% |
| crash | core | 400 | 335 | 65 | 62 | 273 | 219 | **54** | 80.2% |
| sound | core | 500 | 330 | 170 | 0 | 330 | 284 | **46** | 86.1% |
| infra | city | 430 | 344 | 86 | 0 | 344 | 300 | **44** | 87.2% |
| main | core | 856 | 663 | 193 | 0 | 663 | 622 | **41** | 93.8% |
| footsteps | character | 139 | 105 | 34 | 0 | 105 | 68 | **37** | 64.8% |
| landmarks | city | 1669 | 1389 | 280 | 852 | 537 | 502 | **35** | 93.5% |
| shopfronts | city | 652 | 580 | 72 | 0 | 580 | 549 | **31** | 94.7% |
| boundary | city | 127 | 111 | 16 | 0 | 111 | 82 | **29** | 73.9% |
| shed | flight | 273 | 168 | 105 | 0 | 168 | 143 | **25** | 85.1% |
| planehall | flight | 364 | 221 | 143 | 0 | 221 | 200 | **21** | 90.5% |
| hotpatch ‡ | core | 43 | 20 | 23 | 0 | 20 | 0 | **20** | 0.0% |
| placement | core | 131 | 116 | 15 | 0 | 116 | 96 | **20** | 82.8% |
| smoke | core | 80 | 69 | 11 | 0 | 69 | 49 | **20** | 71.0% |
| metro | city | 306 | 269 | 37 | 0 | 269 | 250 | **19** | 92.9% |
| zones | city | 118 | 98 | 20 | 0 | 98 | 80 | **18** | 81.6% |
| rangelane | combat | 419 | 265 | 154 | 0 | 265 | 248 | **17** | 93.6% |
| planephys | flight | 1032 | 724 | 308 | 0 | 724 | 708 | **16** | 97.8% |
| pickups | combat | 279 | 212 | 67 | 0 | 212 | 199 | **13** | 93.9% |
| gltf ‡ | core | 25 | 11 | 14 | 0 | 11 | 0 | **11** | 0.0% |
| hud | ui | 334 | 271 | 63 | 0 | 271 | 262 | **9** | 96.7% |
| roads | city | 465 | 367 | 98 | 0 | 367 | 361 | **6** | 98.4% |
| target | ui | 387 | 259 | 128 | 0 | 259 | 256 | **3** | 98.8% |
| malls | city | 88 | 79 | 9 | 0 | 79 | 77 | **2** | 97.5% |
| mapui | ui | 778 | 681 | 97 | 4 | 677 | 675 | **2** | 99.7% |
| settings | ui | 231 | 166 | 65 | 0 | 166 | 164 | **2** | 98.8% |
| bridges | city | 360 | 302 | 58 | 0 | 302 | 301 | **1** | 99.7% |
| crane § | core | 1993 | 1102 | 891 | 0 | 1102 | 1102 | **0** | 100.0% |
| fire | core | 717 | 633 | 84 | 180 | 453 | 453 | **0** | 100.0% |
| **TOTAL** | | **51319** | **37025** | **14294** | **1725** | **35300** | **27192** | **8108** | **77.0%** |

(`cinema.js` excluded — deferred. † aero.js is a known false negative, see
above — treat as effectively ~fully cited. § crane.js is a known false
positive — treat its true uncited count as closer to the manifest's own
693-raw-line estimate than to 0. ‡ gltf.js/hotpatch.js have no citations of
any kind; disposition unknown, flagged for a manifest-owner decision.)

### By system

| system | budget(code) | cited | uncited | % code-basis | % port_progress (raw) |
|---|--:|--:|--:|--:|--:|
| combat | 7632 | 2844 | **4788** | 37.3% | 94.12% |
| city | 10701 | 9289 | 1412 | 86.8% | 96.71% |
| core | 6178 | 5170 | 1008 | 83.7% | 88.67% |
| flight | 3085 | 2693 | 392 | 87.3% | 94.98% |
| character | 6017 | 5670 | 347 | 94.2% | 94.72% |
| ui | 1687 | 1526 | 161 | 90.5% | 87.58% |

## Top-10 real remaining-work queue (uncited code lines, descending)

1. **citymap.js** — 1038 uncited (city)
2. **planedamage.js** — 1014 uncited (combat)
3. **weapons.js** — 628 uncited (combat)
4. **impactfx.js** — 510 uncited (combat)
5. **dents.js** — 477 uncited (combat)
6. **ballistics.js** — 407 uncited (combat)
7. **game.js** — 325 uncited (core)
8. **shooting.js** — 275 uncited (combat)
9. **devmode.js** — 260 uncited (core)
10. **gunanim.js** — 239 uncited (combat)

Eight of the top ten combat-adjacent modules (planedamage, weapons, impactfx,
dents, ballistics, shooting, gunanim, plus blast/breakwall/breakup/glass/
rockets/papertargets just below) account for **4,788 of the 8,108 uncited
code lines (59%)**. Combat is where the raw-line metric's 93.74% most
diverges from reality.

## Parser limits (tools/code_lines.py)

- Lexical scan, not a real parser/AST — no grammar or ASI awareness.
- Regex-vs-division `/` disambiguation uses the standard shallow heuristic
  (previous significant character). It is wrong for a regex literal placed
  immediately after a bare keyword with no operator between (e.g.
  `return /foo/.test(x)`). A full grep of this repo's `src/*.js` for that
  pattern (`\b(return|typeof|instanceof|in|of|new|delete|void|yield|case)\s*/`)
  found zero real occurrences (one false hit was inside a `//` comment), so
  the heuristic is verified safe here, but it is not a general-purpose
  JS tokenizer.
- Assumes single/double-quoted strings don't contain a raw unescaped
  newline (true per JS grammar); defensively closes the string at a
  newline if one somehow appears rather than mis-reading subsequent lines.
- Template-literal `${...}` substitutions are parsed with brace-depth
  tracking (so an object literal inside a substitution doesn't prematurely
  close it) and nested templates recurse through the same mode stack, but
  this was exercised against this repo's actual usage (GLSL strings with
  occasional simple substitutions), not fuzzed adversarially.
- 1-indexed, `\n`-only line counting; bare-`\r` line endings would misread
  (none present in this repo).

## Citation-grep limits (tools/code_budget.py)

- Citations are the evidence used; it does not re-verify that a cited line
  range is *correct* (right line, not stale after a JS edit) — only that
  the C# claims it.
- "Scope" citations (a file header claiming a huge JS range) and "line"
  citations (a specific function's exact 6-line range) are
  indistinguishable by this grep — see crane.js above. This is the single
  biggest source of uncertainty in the per-module numbers; the system-level
  and headline numbers are more trustworthy than any one module's row.
- A reference like `foot.js:795,899` is parsed as two discrete points; only
  the `NNN-MMM` form is a continuous range. Both forms are in use.

## The exact change to `tools/port_progress.py`

Not applied (per instructions — port_manifest.json / port_units are being
actively written by other agents right now). When ready to switch the
denominator from raw lines to code lines:

1. Add `from code_lines import analyze_file` (same directory, no new
   dependency) and replace `js_lines(mod)` with a function that returns
   `len(analyze_file(path).code_lines)` instead of `sum(1 for _ in open(p))`.
2. The `covered`/`verified` accumulation (from `units[].covers`) is
   currently a raw-line count per unit; it would need the same treatment —
   either (a) keep `covers` as-is and accept it now measures against a
   smaller denominator (makes every module's % jump up, which is *wrong* in
   the opposite direction since the manifest's claims were authored against
   the raw-line budget), or (b) switch the whole evidentiary basis from
   `covers` claims to this script's citation-intersection approach
   (`cited_code` here), which is the one this document recommends — it is
   the number that cannot be inflated by a comment-counting mistake, per
   the `docs/port_units/README.md` double-dip history this task was told
   about. Recommendation: **(b)**, i.e. have `port_progress.py` import
   `tools/code_budget.py`'s per-module `cited_code`/`budget_code` in place
   of `covered[s]`/`budget[s]`, once the in-flight claims settle — doing it
   mid-flight (as instructed) would corrupt the four agents' current work.
