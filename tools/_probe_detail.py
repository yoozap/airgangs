#!/usr/bin/env python3
"""Ground-truth aggregator for the four "closing the last named gaps" units:
LANDMARKS signage (Marriott/Radisson RED brand textures), AIRPORT detail
(warehouse interior: tables/ammo crates/pistols), the PLANE HALL fill (shed
fill + the 35-plane park), and the TARGET selection ring.

Runs each unit's own standalone JS/Node probe as a subprocess and prints its
output under a per-unit banner. Each probe is also independently runnable
(see the `node tools/_probe_*.mjs` line in its own header) — this script adds
nothing numeric of its own, it only collects.

  python3 tools/_probe_detail.py
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# (unit label, script path relative to ROOT). Missing files are reported, not
# fatal — the two sibling-agent units (airport detail, shed/plane-hall fill)
# are appended here once their probes land; this list is intentionally
# editable rather than hardcoded to "all four must exist".
PROBES = [
    ("landmarks_signage", "tools/_probe_landmarksignage.mjs"),
    ("target_ring", "tools/_probe_targetring.mjs"),
    ("airport_detail", "tools/_probe_airportdetail.mjs"),
    ("shed_fill", "tools/_probe_shedfill.mjs"),
    ("planehall_fill", "tools/_probe_planehallfill.mjs"),
]


def run_one(label, rel_path):
    path = ROOT / rel_path
    print(f"\n==== {label} ({rel_path}) ====")
    if not path.exists():
        print(f"MISSING — {rel_path} does not exist yet (that unit may still be in progress)")
        return False
    try:
        result = subprocess.run(
            ["node", str(path)], cwd=ROOT, capture_output=True, text=True, timeout=60
        )
    except Exception as e:  # pragma: no cover - diagnostic path
        print(f"ERROR running {rel_path}: {e}")
        return False
    sys.stdout.write(result.stdout)
    if result.returncode != 0:
        print(f"NONZERO EXIT {result.returncode}")
        sys.stdout.write(result.stderr)
        return False
    return True


def main():
    ok = {}
    for label, rel_path in PROBES:
        ok[label] = run_one(label, rel_path)
    print("\n==== summary ====")
    for label, _ in PROBES:
        print(f"SUMMARY_{label} ran={ok[label]}")


if __name__ == "__main__":
    main()
