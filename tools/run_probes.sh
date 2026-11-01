#!/usr/bin/env bash
# Run every Unity probe and compare it with its JS/Python counterpart.
# Pairs come from a list file, one line per probe:
#     Namespace.Class|counterpart command|optional relative tolerance|mode
# Name-matching is NOT used: it paired Unity probes against the ~400 ORIGINAL
# Three.js Playwright probes still in tools/, and diffing those produces nonsense.
#
# The tolerance defaults to 1e-5, which suits a closed-form calculation. An
# ITERATIVE solve needs more room: C# runs float32 and JS float64, so a drag and
# penetration integrator accumulates rounding every step. Ballistics' worst pair
# sits 473 float32 ulps apart (2.8e-05 relative) across 416 values — expected
# accumulation, since a wrong formula diverges by orders of magnitude, not 3e-5.
# Where a looser tolerance is used it is stated in the list with its reason.
# The Unity project lock is EXCLUSIVE, so probes run strictly one at a time.
#
# `mode` (4th field, optional):
#   (empty)  the default — numeric diff via tools/unity_port_check.py.
#   strings  a STRING check via tools/ui_string_check.py instead (UiProbe:
#            ~440 lines of Georgian text/ids/tokens, almost no standalone
#            numbers, so a numeric tolerance diff would prove nothing).
#
# Lines starting with `SPEC ` (PlaneCatalogProbe's Georgian display-sheet
# dump, e.g. `SPEC id=x_spec sheet=Extra 330 · აკრობატი · ფრთები 8 მ`) are ALWAYS
# pulled out of the numeric diff and compared by exact text diff instead, for
# every probe, not just that one: a generic numeric differ must never
# tokenize numbers sitting inside Georgian prose (the brief's own worked
# example — "330"/"8" are part of a display string's IDENTITY, not data to
# compare with a tolerance) and no other probe currently emits such lines, so
# this is a no-op everywhere else.
set -uo pipefail
cd "$(dirname "$0")/.."
U=/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity
LIST=${1:?usage: run_probes.sh <probelist.txt> <outdir>}
OUT=${2:?}; mkdir -p "$OUT"
pass=0; fail=0; nomate=0; broke=0
while IFS='|' read -r cls mate tol mode; do
  tol=${tol:-1e-5}
  [ -z "${cls:-}" ] && continue
  short="${cls##*.}"; log="$OUT/$short.log"
  "$U" -batchmode -quit -nographics -projectPath "$PWD/unity" \
       -executeMethod "$cls.Run" -logFile "$log" >/dev/null 2>&1
  rc=$?
  # Liveness count: the probe's OWN lines. The signature of probe output is a
  # KEY with no space before its `=` (`PAD id=warehouse`, `MAX_HEALTH=1000`);
  # Unity's batch-mode prose either has no `=` ("Begin MonoManager
  # ReloadAssembly") or spaces around it ("Mono path[0] = '...'"), and its
  # environment dump is a closed set of names worth blocking by prefix.
  #
  # This matters because the old pattern, "^[A-Z][A-Z0-9_]+([ =]).*", matched
  # DOTNET_HOST_PATH=... and friends — so every probe reported a reassuring
  # "4 lines" whether it had printed anything or not, and a whole-suite failure
  # read as 55 identical "BROKE (4 lines)" instead of naming the single compile
  # error behind it. Verified: 0 own lines for a probe that never ran, 35 for
  # CityGeoProbe's 35 facts, 59 for GameRestProbe's 47 + 12 extras.
  grep -ahoE "^[A-Za-z_][A-Za-z0-9_]*(=| [a-z][A-Za-z0-9_]*=).*" "$log" 2>/dev/null \
    | grep -avE "^(DOTNET|UNITY|MONO|LC_|LANG|PATH|HOME|TMPDIR|SSH|XPC|__CF)" \
    | sort -u > "$OUT/$short.cs.txt"
  n=$(wc -l < "$OUT/$short.cs.txt" | tr -d ' ')
  if [ "$rc" != "0" ] || [ "$n" = "0" ]; then
    echo "BROKE  $short (exit $rc, $n own lines)"
    # Say WHY, and say it once per cause rather than once per probe.
    grep -m1 -ohE "Assets/[^(]*\.cs\([0-9]+,[0-9]+\): error CS[0-9]+: [^[]*" "$log" 2>/dev/null | sed 's/^/         /'
    grep -m1 -E "Scripts have compiler errors|Exception|could not be found" "$log" 2>/dev/null | cut -c1-110 | sed 's/^/         /'
    broke=$((broke+1)); continue
  fi
  if [ -z "${mate:-}" ]; then
    echo "SOLO   $short ($n facts, standalone check)"; nomate=$((nomate+1)); continue
  fi
  # The counterpart's stdout is nothing BUT probe output, so match any
  # KEY=value / KEY value shape. The old pattern required an all-caps key and
  # silently dropped lines like `WheelH_gear=1.7` and `band_at_0.56=Good` — and
  # because the same pattern was applied to the C# side, matching lines vanished
  # from BOTH and were never compared. A probe that compares nothing reports no
  # mismatch, which is how a unit reads "verified" while proving nothing.
  $mate 2>/dev/null | grep -ahoE "^[A-Za-z_][A-Za-z0-9_]*([ =]).*" | sort -u > "$OUT/$short.js.txt"
  if [ ! -s "$OUT/$short.js.txt" ]; then
    echo "SOLO   $short ($n facts, counterpart produced nothing)"; nomate=$((nomate+1)); continue
  fi

  # Re-extract the C# side using the counterpart's OWN keys (see
  # tools/probe_extract.sh). Anything the JS probe emits is signal; anything
  # else in a Unity batch log is not.
  sh tools/probe_extract.sh "$log" "$OUT/$short.js.txt" | sort -u > "$OUT/$short.cs.txt"
  n=$(wc -l < "$OUT/$short.cs.txt" | tr -d ' ')
  if [ "$n" = "0" ]; then
    echo "FAIL   $short (no C# line shares a key with its counterpart - probably a key/prefix mismatch, not a value mismatch)"
    fail=$((fail+1)); continue
  fi

  if [ "${mode:-}" = "strings" ]; then
    if python3 tools/ui_string_check.py "$OUT/$short.js.txt" "$OUT/$short.cs.txt" > "$OUT/$short.cmp" 2>&1; then
      echo "PASS   $short  (string check)"
      pass=$((pass+1))
    else
      echo "FAIL   $short"; head -8 "$OUT/$short.cmp" | sed 's/^/         /'; fail=$((fail+1))
    fi
    continue
  fi

  # Pull SPEC lines (if any) out of both sides before the numeric diff, and
  # exact-diff them on their own.
  grep -v '^SPEC ' "$OUT/$short.js.txt" > "$OUT/$short.js.num.txt" || true
  grep '^SPEC '    "$OUT/$short.js.txt" > "$OUT/$short.js.spec.txt" || true
  grep -v '^SPEC ' "$OUT/$short.cs.txt" > "$OUT/$short.cs.num.txt" || true
  grep '^SPEC '    "$OUT/$short.cs.txt" > "$OUT/$short.cs.spec.txt" || true

  spec_ok=1
  if [ -s "$OUT/$short.js.spec.txt" ] || [ -s "$OUT/$short.cs.spec.txt" ]; then
    if diff "$OUT/$short.js.spec.txt" "$OUT/$short.cs.spec.txt" > "$OUT/$short.spec.diff"; then
      :
    else
      spec_ok=0
    fi
  fi

  if python3 tools/unity_port_check.py "$OUT/$short.js.num.txt" "$OUT/$short.cs.num.txt" "$tol" > "$OUT/$short.cmp" 2>&1; then
    num_ok=1
  else
    num_ok=0
  fi

  if [ "$num_ok" = 1 ] && [ "$spec_ok" = 1 ]; then
    note=""; [ "$tol" != "1e-5" ] && note=" (tol $tol)"
    specnote=""; [ -s "$OUT/$short.js.spec.txt" ] && specnote=" + $(wc -l < "$OUT/$short.js.spec.txt" | tr -d ' ') SPEC line(s) exact-matched"
    echo "PASS   $short  $(grep -oE 'worst relative difference [0-9.e+-]+' "$OUT/$short.cmp" | head -1)$note$specnote"
    pass=$((pass+1))
  else
    echo "FAIL   $short"
    [ "$num_ok" = 0 ] && head -4 "$OUT/$short.cmp" | sed 's/^/         /'
    [ "$spec_ok" = 0 ] && echo "         SPEC lines differ:" && head -8 "$OUT/$short.spec.diff" | sed 's/^/           /'
    fail=$((fail+1))
  fi
done < "$LIST"
echo; echo "pass=$pass fail=$fail standalone=$nomate broke=$broke"
