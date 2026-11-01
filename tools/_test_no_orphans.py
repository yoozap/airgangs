#!/usr/bin/env python3
"""Guard against the port's signature failure: a correct type nothing reaches.

Found ten times by accident before `docs/ORPHANS.md`'s 2026-10-27 audit found
the rest deliberately in one pass (GameLoop.OnHint with zero subscribers, the
whole Cinema/* subsystem, LandmarkTag/SpinningPartTag never attached, the
NetClient damage-persistence endpoints, PlacementEditor). Read that doc's
"Method" and "false-positive/false-negative modes" sections before trusting a
new failure this script reports — they are reproduced here only in summary.

WHAT THIS CHECKS (one pass over Assets/Scripts/**/*.cs, production code):
  - MonoBehaviour-derived types: live if AddComponent<T>()'d anywhere (same
    file counts), or if their declaring file's script guid appears in
    Assets/Scenes/Game.unity (the one scene that actually boots the game —
    see ORPHANS.md's scene-build-order finding for why not the others).
  - static classes and plain classes/structs: live if `new (Ns.)*T(`
    (self-file counts) OR a `T.Member` static-call/factory-load site exists
    anywhere in production code (self-file counts). The static-call fallback
    exists because this codebase's dominant construction idiom is a static
    `Load()`/`Build()` factory (`CityData.Load()`), not a bare `new T(` at
    the call site.
  - A hit found ONLY under Assets/Editor/** is one of TWO different things,
    and conflating them was a real misreport (City/CityMaterials.cs, 2026-10-31):
      * SCENE_BAKER — the only reachability is Assets/Editor/BuildPlayableScene.cs,
        the baker of Assets/Scenes/Game.unity (the ONE scene that boots the game,
        the same scene this script already greps for MonoBehaviour guids).
        Whatever such a type produces is SERIALISED INTO THE SHIPPED SCENE, so it
        is delivered, not dead: CityMaterials.Get is called at
        BuildPlayableScene.cs:119 and its 266 Materials (246 of them carrying a
        real texture) are objects inside Game.unity. This is the architecture the
        ALLOWLIST's own "City-side bake-only data structs" block was hand-written
        for, a dozen entries saying the same thing. Reported as SCENE_BAKER, NOT
        failed. Deliberately the narrowest possible rule: a baker of a LEGACY
        scene (BuildCityScene/BuildFlyableScene/BuildOpeningScene/
        BuildPlaneHallScene -> Tbilisi/Fly/Hangar/PlaneHall.unity, none of which
        boot) does NOT count, so a type kept alive only by dead scaffolding
        still fails.
      * PROBE_ONLY — everything else under Assets/Editor (Editor/Probes/*,
        one-off reports, dev tools): correct code, exercised by a probe, never
        reached by the game loop OR by the shipped scene. Reported, not failed
        by default (see ALLOWLIST).

WHAT THIS DOES NOT CHECK (false-negative modes — do not treat a clean run as
proof the game is fully wired):
  1. Delegate/event fields with writers and no subscriber (`GameLoop.OnHint`,
     `OnHitToast`). This is a DIFFERENT shape of orphan — a live, constructed,
     ticking object whose public hook nobody reads — and needs its own grep
     (`field\s*\+=` vs `field\s*=`), not a construction check. Not attempted
     here: a generic version is noisy, because plenty of Action/event fields
     are legitimately write-only mid-port, before their later phase wires
     them. ORPHANS.md's findings #1/#2/#7 were found by hand for this reason.
  2. JSON DTOs built only via `JsonConvert.DeserializeObject<T>()` (reflection,
     invisible to any `new T(` grep). The `T.Member` static-call fallback
     catches the call site that deserializes a DTO in most cases, but a DTO
     referenced only as a bare field/dictionary-value type with no `.`-access
     anywhere outside its own file can still misreport dead. Every current
     hit in this class (Core/NetClient.cs's response DTOs) was hand-verified
     once; a NEW one of this shape needs the same manual check or an
     allowlist entry.
  3. Transitive reachability is ONE HOP ONLY. If type A is constructed only
     inside type B's file, and B itself is never reached from the game loop,
     A reads as "live" (self-constructed) even though the whole chain is
     dead. This is exactly how the Cinema/* subsystem's ten files all
     construct each other and all looked "live" under a naive single-hop
     check — it was only found by separately checking, by hand, whether
     ANY Cinema* type name appears outside Cinema/+UI/Hud/UI/Cinema. This
     script does not re-derive that chase automatically; if Cinema gets
     wired, remove it from ALLOWLIST and this guard will go on protecting it.
  4. Scene reachability is checked against Game.unity ONLY. A MonoBehaviour
     deliberately scene-specific to Fly.unity/Tbilisi.unity/Hangar.unity
     would misreport dead. None exist today outside Fly.unity's three
     flight-test components (verified legacy scaffolding by hand); extend
     SCENES below if that changes.
  5. A type can be "live" by every check here and still never actually run,
     if its one call site is itself provably dead by some other means this
     script doesn't model (e.g. an `if (false)` branch, a `#if` out). Treat
     a FAIL from this script as "go look," and a PASS as "nothing obviously
     unreached," not as a correctness proof either direction.

Usage:
    python3 tools/_test_no_orphans.py          # exits 1 and prints new orphans
    python3 tools/_test_no_orphans.py --list   # prints the full classification, exit 0
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(ROOT, "unity", "Assets", "Scripts")
EDITOR = os.path.join(ROOT, "unity", "Assets", "Editor")
SCENES = [os.path.join(ROOT, "unity", "Assets", "Scenes", "Game.unity")]
# The Editor script that BAKES the scene above. A type reachable only from here
# is not dead — its product is serialised into the one scene that boots. See the
# SCENE_BAKER paragraph in the docstring; keep this set as narrow as the SCENES
# list it mirrors.
GAME_SCENE_BAKERS = {os.path.join(EDITOR, "BuildPlayableScene.cs")}

TYPE_PAT = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private)?\s*(static\s+)?(?:abstract\s+)?'
    r'(?:sealed\s+)?(?:partial\s+)?(class|struct|interface|enum)\s+([A-Za-z_]\w*)\s*'
    r'(?:<[^>]*>)?\s*(?::\s*([^\{]+))?\s*\{?'
)
QUAL = r'(?:[A-Za-z_]\w*\.)*'

# ---------------------------------------------------------------------------
# ALLOWLIST — types this audit individually verified are DEAD/PROBE_ONLY on
# purpose. Grouped by reason; see docs/ORPHANS.md for the per-type writeup.
# A type newly added here without a one-line reason is a red flag in review.
# ---------------------------------------------------------------------------
ALLOW = {
    # ---- the four documented dead duplicates (unity SKILL.md) + their own
    # ---- nested/sibling helper types, never to be wired without reconciling
    # ---- ownership with Core/HeldWeapon.cs / GameLoop.BoardSequence first.
    "FootWeapons", "CarriedWeapon", "WornBelt", "SocketUserData", "PocketMetrics",
    "BodySection", "FootBelt", "WornGear", "FootBoarding", "FootWeaponFrame",

    # ---- intentionally optional, wiring it would double-tick HeroLink.Tick
    # ---- (GameLoop already calls Hero.Tick(dt) + its own OnApplicationFocus/
    # ---- OnDestroy directly). See ORPHANS.md finding #9.
    "HeroLinkBehaviour",
    # 2026-10-30, surfaced when the guard stopped counting comments as liveness:
    "CollidableHash",     # City/: a literal port of citymap's _addCollidable hash. Deliberately NO
                          # instance -- a BoxCollider on a Transform replaces it (PlaneHallFill.cs:208,
                          # WarehouseCraneFill.cs:411); crane.js:1234-1246 reindexShell is excluded on
                          # exactly this ground. Gaining an instance would be a regression.
    "FootAcrobatics",     # Character/: wiring it runs into the documented FootWeapons/FootWeaponFrame
                          # two-orchestrator hazard; KEEP-not-wired pending the belt-ownership decision.
    "CrateRowBuilder",    # Cinema/CinemaRangeMath.cs: cinema is DEFERRED by the user; the class is not
                          # named Cinema* so the pattern above misses it.

    # ---- the whole Cinema subsystem: complete, probe-verified, structurally
    # ---- disconnected (ORPHANS.md finding #3). Remove when wired.
    "CinemaFlight", "CinemaLetterboxMath", "CinemaLights", "CinemaRangeMath",
    "CinemaReel", "CinemaScenes", "CinemaStage", "CinemaTypes", "CinemaDirector",
    "CinemaMenuView", "CinemaLetterboxView", "RangeStageMath", "CardPlacement",
    "CardTarget", "StageSetup",

    # ---- City-side bake-only data structs this audit hand-verified are used
    # ---- exclusively by Assets/Editor/Build*.cs scene bakers, which is this
    # ---- project's real architecture for static geometry (not damage state).
    "RoadMeta", "RoadEntry", "Junction", "NetRoad", "BridgeRail", "BridgeFascia",
    "BridgeLamp", "RealBridge", "MeshBuf",
    # CollapseState/CollapseAnim/CollapseFrame were here; they are LIVE as of
    # 2026-10-30 (Core/CombatBuildingChunk.TickCollapses steps CollapseAnim for
    # every falling building), so they come off the list and the guard protects
    # them again.
    "TunnelLuminaire", "SatelliteGrid",

    # ---- bake-only MonoBehaviours: constructed exclusively by
    # ---- Assets/Editor/Build*.cs and baked into Game.unity (verified they
    # ---- DO ship once the scene is rebaked — ORPHANS.md's staleness note).
    "MetroRenderer", "MallRenderer", "StreetFurnitureRenderer", "VegetationRenderer",
    # WindsockRenderer: same shape as the four above — AddComponent'd by
    # Editor/BuildPlayableScene.cs into the scenery-roots list, OnEnable ->
    # EnsureBuilt at runtime. It is what un-orphaned Core/Wind.WindsockDriver.
    "WindsockRenderer",
    "SceneEnvironmentSetup", "PlaneCockpitBaker", "ChunkedSurfaceBuilder",
    # SpawnFinder came OFF this list 2026-10-30: Core/SpawnResolver.cs is its
    # runtime caller now (GameLoop.ResetPlane -> PlaceOnSpawn), so the guard
    # protects it again. docs/open_items.json `spawn-in-building`.
    "TerrainGridOps", "InfraMaterials",

    # ---- audio/physics/propeller internal data types, hand-verified used
    # ---- only within their own already-reachable file.
    "PlaneAudioDef", "EngineVoice", "EngineProfile", "BiquadCoeffs", "FxVoice",
    "SampleBank", "BodyInertia", "ContactSolver", "ContactResult", "PropHint",
    "PropBasis", "WingInfo", "WorldPhysActor", "PilotPhysActor", "PlaneRecord",
    "LooseAirframeActor", "PlaneStructState", "RigSeatExtras",
    "GunCrosshair", "IssfTarget", "B27Target", "TargetRingMath",

    # ---- NetClient DTOs: deserialized by JsonConvert.DeserializeObject<T>,
    # ---- invisible to a `new T(` grep (false-negative mode #2 above). The
    # ---- endpoints THEMSELVES being unreachable is ORPHANS.md finding #6,
    # ---- which is a real, reported bug — just not one this allowlist hides;
    # ---- the DTO *shapes* are correct regardless of whether their endpoint
    # ---- is ever called.
    "OkResponse", "HealthResponse", "DamageRow", "DamagePatch", "AddressRow",
    "PublicUser",

    # ---- genuinely still-dead, still worth fixing, but NOT a one-line "the
    # ---- obvious owner already ticks every frame" fix — reported in
    # ---- ORPHANS.md rather than silently re-flagged every run. Remove from
    # ---- here the moment any of these gets wired.
    "LandmarkTag", "SpinningPartTag", "CustomModelEntry", "CustomModelInstance",
    "PlacementEditor",
}

INTERFACE_KINDS = {"interface", "enum"}


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def strip_comments(text):
    """Remove // and /* */ comments and string-literal CONTENTS, keeping newlines.

    A single left-to-right scan, not two regex passes. Two regex orderings were
    tried and each has a failure the other does not:
      * block-first: a `//` comment containing `/*` (and `Core/*`, `src/*.js`
        are all over this codebase) opens a block that eats code to the next
        `*/` -- measured at 339 real code lines swallowed, 21 of them in
        Core/CombatWorldAdapter.cs, the main wiring file, which made the guard
        latently blind exactly where it matters and misreported PlaneMarkFx as
        PROBE_ONLY. (Found by the agent whose construction line it ate.)
      * line-first: `/* see http://x */` loses its closer to the line strip and
        the block never closes.
    Scanning in order of appearance has neither. String contents are blanked too
    (a `//` or `/*` inside a literal is text, not a comment) while the quotes
    stay, so a type name inside a string cannot satisfy a construction regex
    either. Verbatim @"..." strings are handled; interpolation braces are not
    special-cased (a type name inside `{...}` of an $"" string is rare and would
    only under-report).

    Why this must be right: the reachability regexes below are plain text
    matches, and a bare filename in a doc comment -- `/// same precedent as
    RangeLaneView.cs` -- contains `RangeLaneView.` and flipped an unconstructed
    type to LIVE. A comment masking a real orphan is the dangerous direction.
    """
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j            # keep the newline itself
        elif c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            seg = text[i:] if j < 0 else text[i:j + 2]
            out.append("\n" * seg.count("\n"))
            i = n if j < 0 else j + 2
        elif c == "@" and nxt == '"':
            out.append('@"')
            i += 2
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2; continue
                if text[i] == '"':
                    break
                if text[i] == "\n": out.append("\n")
                i += 1
            out.append('"'); i += 1
        elif c == '"' or c == "'":
            q = c; out.append(q); i += 1
            while i < n and text[i] != q:
                if text[i] == "\\": i += 1
                if i < n and text[i] == "\n": out.append("\n")
                i += 1
            out.append(q); i += 1
        else:
            out.append(c); i += 1
    return "".join(out)


def walk_cs(root):
    out = []
    for dirpath, _dirs, files in os.walk(root):
        for fn in files:
            if fn.endswith(".cs"):
                out.append(os.path.join(dirpath, fn))
    return out


def extract_types(path, text):
    types = []
    depth = 0
    for line in text.splitlines():
        m = TYPE_PAT.match(line)
        if m and depth <= 1:
            kind = m.group(2)
            name = m.group(3)
            bases = (m.group(4) or "").strip()
            types.append({
                "name": name, "file": path, "kind": kind,
                "is_mono": "MonoBehaviour" in bases,
                "is_static": bool(m.group(1)),
            })
        depth += line.count("{") - line.count("}")
    return types


def guid_of(cs_path):
    meta = cs_path + ".meta"
    if not os.path.exists(meta):
        return None
    for line in open(meta, encoding="utf-8", errors="replace"):
        if line.startswith("guid:"):
            return line.split(":", 1)[1].strip()
    return None


def classify():
    prod_files = walk_cs(SCRIPTS)
    editor_files = walk_cs(EDITOR)
    # Types are extracted from the RAW text (declarations are never inside a
    # comment that matters), but every reachability match runs on CODE ONLY.
    prod_raw = {p: read(p) for p in prod_files}
    prod_text = {p: strip_comments(t) for p, t in prod_raw.items()}
    editor_text = {p: strip_comments(read(p)) for p in editor_files}
    scene_text = "".join(read(s) for s in SCENES if os.path.exists(s))

    all_types = []
    for p in prod_files:
        all_types.extend(extract_types(p, prod_raw[p]))

    results = []
    for t in all_types:
        if t["kind"] in INTERFACE_KINDS:
            continue
        name, file = t["name"], t["file"]
        ctor_pat = re.compile(r'\bnew\s+' + QUAL + re.escape(name) + r'\s*(\(|\[|<|\{)')
        addcomp_pat = re.compile(
            r'AddComponent\s*<\s*' + QUAL + re.escape(name) + r'\s*>|'
            r'AddComponent\s*\(\s*typeof\s*\(\s*' + QUAL + re.escape(name) + r'\s*\)')
        staticcall_pat = re.compile(r'\b' + re.escape(name) + r'\.')
        usage_pat = re.compile(r'\b' + re.escape(name) + r'\b')

        prod_ctor = any(ctor_pat.search(txt) for txt in prod_text.values())
        prod_addcomp = any(addcomp_pat.search(txt) for txt in prod_text.values())
        prod_staticcall = any(staticcall_pat.search(txt) for txt in prod_text.values())
        def editor_match(txt):
            return bool(ctor_pat.search(txt) or addcomp_pat.search(txt) or staticcall_pat.search(txt))
        baker_hit = any(editor_match(txt) for ep, txt in editor_text.items() if ep in GAME_SCENE_BAKERS)
        editor_hit = any(editor_match(txt) for txt in editor_text.values())
        prod_usage = any(usage_pat.search(txt) for p, txt in prod_text.items() if p != file)

        if t["is_mono"]:
            g = guid_of(file)
            scene_hit = bool(g) and g in scene_text
            if scene_hit or prod_addcomp:
                verdict = "LIVE"
            elif baker_hit:
                verdict = "SCENE_BAKER"
            elif editor_hit:
                verdict = "PROBE_ONLY"
            else:
                verdict = "DEAD"
        else:
            if prod_ctor or prod_staticcall:
                verdict = "LIVE"
            elif baker_hit:
                verdict = "SCENE_BAKER"
            elif editor_hit:
                verdict = "PROBE_ONLY"
            elif prod_usage:
                verdict = "REFERENCED_NOT_CONSTRUCTED"
            else:
                verdict = "DEAD_UNREFERENCED"

        results.append({**t, "verdict": verdict})
    return results


def main():
    results = classify()
    if "--list" in sys.argv:
        for r in sorted(results, key=lambda r: (r["verdict"], r["name"])):
            print(f'{r["verdict"]:28s} {r["name"]:28s} {os.path.relpath(r["file"], ROOT)}')
        return 0

    bad = [r for r in results if r["verdict"] in
           ("DEAD", "DEAD_UNREFERENCED", "PROBE_ONLY") and r["name"] not in ALLOW]

    bakers = [r for r in results if r["verdict"] == "SCENE_BAKER" and r["name"] not in ALLOW]
    for r in sorted(bakers, key=lambda r: r["name"]):
        print(f'  SCENE_BAKER (delivered through Game.unity)  {r["name"]:24s} '
              f'{os.path.relpath(r["file"], ROOT)}')

    if not bad:
        print(f"_test_no_orphans: OK — {len(results)} types checked, "
              f"{len(ALLOW)} allowlisted, {len(bakers)} scene-baker, 0 new orphans.")
        return 0

    print(f"_test_no_orphans: FOUND {len(bad)} NEW orphan(s) not in the allowlist:\n")
    for r in sorted(bad, key=lambda r: (r["verdict"], r["name"])):
        print(f'  {r["verdict"]:24s} {r["name"]:28s} {os.path.relpath(r["file"], ROOT)}')
    print(
        "\nEach of these is either a genuinely new dead type, or a false positive this\n"
        "script's docstring already names a mode for (reflection DTOs, bake-only data,\n"
        "transitive-chain, scene-specific). Read docs/ORPHANS.md's method section before\n"
        "assuming it's real; if it's intentional, add it to ALLOW with a one-line reason.\n"
        "This guard does NOT catch dead delegate/event subscriptions (GameLoop.OnHint's\n"
        "class of bug) — that needs a separate, narrower check; see this file's docstring."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
