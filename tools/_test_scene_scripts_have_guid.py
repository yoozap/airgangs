#!/usr/bin/env python3
"""Every MonoBehaviour serialised into a scene must reference its script by GUID.

A component whose script lives in a file not named after it serialises as
`m_Script: {fileID: N}` with NO guid and is resolved only by
`m_EditorClassIdentifier` -- editor-only metadata. In the editor that reads as
several unrelated systems null at once (the skill's ONE-MonoBehaviour-PER-FILE
trap); in a PLAYER it is fatal: `Data/level0 is corrupted! [Position out of
bounds!]` and the whole scene fails to load, with nothing in the log naming
the cause. Found 2026-10-31 by the first real player build (PlaneShellTag in
Ballistics.cs, FootBoarding.PlaneCockpitRig nested in FootBoarding.cs).

Invariant: zero guid-less m_Script lines in every .unity under Assets/Scenes.
Also lists MonoBehaviours declared in files not named after them: harmless
while only AddComponent'd, fatal the moment one is saved into a scene.
"""
import glob, os, re, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
bad = 0
for scene in sorted(glob.glob(os.path.join(ROOT, "unity/Assets/Scenes/*.unity"))):
    n = 0
    for line in open(scene, encoding="utf-8", errors="ignore"):
        if re.match(r"\s*m_Script: \{fileID: -?\d+\}\s*$", line):
            n += 1
    print(f"{'FAIL' if n else 'ok  '} {os.path.relpath(scene, ROOT)} guidless_m_Script={n}")
    bad += n
# MonoBehaviours declared in files not named after them (warning only)
warn = []
for p in glob.glob(os.path.join(ROOT, "unity/Assets/Scripts/**/*.cs"), recursive=True):
    src = open(p, encoding="utf-8", errors="ignore").read()
    stem = os.path.splitext(os.path.basename(p))[0]
    for m in re.finditer(r"class\s+(\w+)\s*:\s*(?:[\w.]*\.)?MonoBehaviour\b", src):
        if m.group(1) != stem:
            warn.append(f"{m.group(1)} in {os.path.relpath(p, ROOT)}")
for w in sorted(warn):
    print(f"WARN MonoBehaviour not in its own file: {w}")
print(f"_test_scene_scripts_have_guid: {bad} guid-less scene scripts, {len(warn)} mis-filed MonoBehaviours (warn)")
sys.exit(1 if bad else 0)
