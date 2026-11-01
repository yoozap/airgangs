# Per-agent coverage claims

With many agents porting at once, a single `docs/port_manifest.json` is a write
conflict waiting to happen. Each agent drops its claims here as
`docs/port_units/<agent-name>.json` — a JSON array of the same unit objects the
manifest's `units` array holds:

    [{"cs": "...", "js": "foo.js", "covers": 123, "note": "...", "verified": null}]

`tools/merge_port_units.py` folds them into the manifest, newest claim per `cs`
file winning. `tools/port_progress.py` then reports on the merged result.

## Before you file: a claim and an exclusion are MUTUALLY EXCLUSIVE

A module's budget is `wc -l src/<mod>.js` **minus** its entries in the
manifest's `excluded` array. So a line can be `covers`ed or `excluded`, never
both. Billing both ways is invisible to a casual look — each half is under the
raw file length — and it inflated three modules before the budget check was
corrected (fire.js by 180 lines, anims.js by 134, citymap.js by 107) while the
merge cheerfully printed "no module over-claims".

Check your module's headroom before filing:

    python3 - <<'EOF'
    import json, subprocess
    MOD = "parks.js"          # <- yours
    m = json.load(open("docs/port_manifest.json"))
    cov = sum(u.get("covers",0) for u in m["units"] if (u.get("js") or "") == MOD)
    exc = sum(e["excluded"] for e in m["excluded"] if e["js"] == MOD)
    n = int(subprocess.check_output(["wc","-l","src/"+MOD]).split()[0])
    print(f"{MOD}: {n} lines - {exc} excluded = {n-exc} budget; {cov} claimed; "
          f"{n-exc-cov} free")
    EOF

If that prints a negative or zero "free", filing anyway just feeds the clamp and
your number becomes fiction. Report "no headroom" instead.

### Known double-dip hazards — do NOT file an exclusion for these

- **`characters.js` `shoulderFront()` (1009-1024, 16 lines).** It IS dead (0
  real call sites; the only hit outside its own declaration is a PROSE COMMENT
  at `weapons.js:757`, and there is no dynamic `rig[...]()` dispatch anywhere,
  so nothing can reach it). It looks like an obvious dead-code exclusion — but
  `Character/RigSeatExtras.cs`'s `covers=53` **already counts those 16 lines**
  for implementing it. Excluding it as well would bill them twice. Pick one:
  either leave the credit alone (current state, nothing to do) or drop 16 off
  `RigSeatExtras.cs` AND add the exclusion in the same change. Never only the
  second half.
- **`landmarks.js` asset-substituted builders.** All 10 are live-called and
  excluded on "Unity loads the real exported .obj" grounds, not on dead-code
  grounds. Do not re-file them as dead; do not claim `covers` for them either.

### Restating an existing unit

The merge keys on `(cs, js)`. `norm_cs` sorts a comma-separated `cs` list, so
file ORDER does not matter — but a different SET of files is a different claim
and will **stack** instead of superseding. Reuse the `cs` string exactly as the
manifest has it. The merge also keeps the HIGHER of two claims for one key, so
you cannot lower a claim by filing a smaller number: say so in your report
instead.

### DECIDED 2026-10-27: crane.js's collider/devmode glue is NOT an exclusion

A ledger pass proposed excluding 57 lines of `crane.js` and, to its credit,
declined to file them and asked instead. The answer is no, for a reason worth
keeping:

- **crane.js:242-277, 351-356, 495, 557-563 (49 lines)** — the triangle-bake
  plus the `citymap._addCollidable` / `collidables.push` bridge. Proposed
  replacement: "Unity's native MeshCollider/PhysX broadphase". **Refuted:**
  `City/CollidableHash.cs` exists and is a direct port — `CollidableHash<T>`
  with `Add`/`Remove`/`Near`, citing `citymap.js:1249`/`:1287`. The port KEPT the
  concept, so there is nothing superseding it. These lines are ordinary
  unported-or-already-covered work.
- **crane.js:304-310, 563 (8 lines)** — `dev.register(crane, 'warehouse_crane',
  {onMove})`. `Editor/DevPlacement.cs` and `Tools/DevPlacementTag.cs` do exist,
  but they are the devmode SYSTEM; a Unity port still has to make the
  registration CALL for the crane. A call site whose callee was ported is
  portable, not superseded.

**The general rule this settles:** "Unity does this differently" is not a
supersession. Check whether the port actually KEPT the concept first — one grep
for the C# counterpart. And note that `shed.js` and `planehall.js` carry zero
exclusions despite being made of the same loader/scene glue, so excluding it for
crane.js alone would make two comparable modules read differently for identical
code.

Net effect: crane.js stays at **693** lines remaining, not 636. A larger honest
remainder is the correct outcome.
