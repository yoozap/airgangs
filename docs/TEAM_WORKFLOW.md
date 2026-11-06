# Working on TbilisiFly as a team

**Source of truth is Unity Version Control (UVCS), hosted on Unity Cloud.**
GitHub (`yoozap/airgangs`) is a frozen archive, tagged `pre-uvcs-migration`.
Do not commit to git any more. Everything below happens in UVCS.

Why: the project is 26 GB on disk but only **1.53 GB of it is real source**. The
rest is import cache, downloaded asset sources and built players — all
regenerable, none of it belongs in the cloud. Git chokes on the 1.5 GB of
churning FBX/GLB binaries; UVCS is built for exactly that, and it gives us
**file locking**, which is the thing that stops two people destroying each
other's work in `Game.unity`.

---

## 1. Which client do you install?

There are two, and picking the right one matters more than anything else here.

| You are | Client | Workspace | Why |
|---|---|---|---|
| Programmer (C#, gameplay, tools) | **Unity Version Control** (`PlasticSCM.app`) | Full — all 1.53 GB | You need the whole project to compile and run the play-mode suites |
| Artist / modeller (FBX, textures, Blender) | **Gluon** (`Gluon.app`) | **Partial** — pick only the folders you touch | You do not need `Assets/Generated` (481 MB of baked city) or `tools/` to retexture a plane |
| Level / scene designer | **Unity Version Control** | Full | Scenes reference everything; a partial workspace gives you pink materials and missing prefabs |

Both ship in the same installer. **Artists: use Gluon.** Downloading 1.53 GB
plus a 5 GB import cache to move a texture is a waste of your morning, and
Gluon's "check out, lock, check in" model is simpler than branching.

Inside Unity, version control appears under **Window → Unity Version Control**
(the `com.unity.collab-proxy` package is already in `Packages/manifest.json`, so
it installs itself on first open).

---

## 2. The daily loop (programmers and designers)

We use **branch per task**. Never work directly on `/main`.

```
1. Branch Explorer → right-click /main → "Create branch from this changeset"
   Name it after the task:  /main/parkour-landing-fix
2. Work. Unity will tell you files are checked out as you touch them.
3. Check in often, small changesets, real messages.
4. When it works: right-click /main → "Merge from this branch" → merge your
   branch into /main → check in the merge.
5. Delete the branch.
```

Before you start, and before you merge: **update your workspace** (`Ctrl/Cmd+U`,
or Workspace → Update). Merging a branch built on a three-day-old `/main` is how
you get conflicts in files you never opened.

**Close Unity before switching branches or updating.** Unity caches the asset
database; swapping thousands of files underneath a running editor corrupts
`Library/` and you will spend 20 minutes on a reimport. This is not optional on
this project — the city alone is 193k buildings.

---

## 3. Locking: the rule that keeps the peace

Some files cannot be merged. If two people edit `Game.unity` at once, one of
them loses their work — there is no "resolve conflict" that saves both.

So those files **lock on checkout**: the first person to touch one gets it
exclusively, and everyone else sees it is taken and who has it. Locking is
configured server-side (see `docs/UVCS_SETUP.md`) and covers:

- **Scenes and prefabs** — `*.unity`, `*.prefab`
  (`Game.unity` is 11.6 MB and is the **only scene that boots** — treat it as
  the single most contended file in the project)
- **Animation** — `*.controller`, `*.anim`, `*.mask`
- **Materials** — `*.mat`
- **All binary art** — `*.fbx`, `*.glb`, `*.blend`, `*.png`, `*.jpg`, `*.tga`,
  `*.psd`, `*.exr`, `*.hdr`, and audio
- **Baked data** — `*.asset` (this includes `Assets/Generated/chunk_*.asset`;
  locking them stops two people re-baking the city simultaneously)

`*.cs` does **not** lock. Code merges fine; let it.

**If a file you need is locked:** don't wait silently and don't work around it by
editing a copy. Message whoever holds it. You can see locks in the client under
Repository → Locks. An admin can force-release a lock left behind by someone who
went on holiday.

**Corollary:** check in promptly. Holding a lock on `Game.unity` for three days
blocks the whole team.

---

## 4. What never goes in

`ignore.conf` at the workspace root handles this automatically — it is checked
in, so you inherit it. It keeps out 23 GB:

| Excluded | Size | Why |
|---|---|---|
| `unity/Library/` | 5.0 GB | Unity's import cache. Rebuilt on open. Committing it is the classic rookie mistake. |
| `tools/cache/`, `tools/trees_src/`, `tools/park_src/`, `tools/gis/` | 11.5 GB | Downloaded GIS/model sources. The scripts in `tools/` re-fetch them. |
| `build/` | 4.5 GB | Built players. |
| `unity/Tbilisifly/` | 1.2 GB | A stray nested project — a player build written to the wrong folder. |
| `unity/Assets/InitTestScene*` | — | The play-mode runner drops a random-GUID scene into `Assets/` on every `-runTests` pass and doesn't clean up. Never version these. |
| `mono_crash*` | 60 MB | Editor crash dumps. |
| `node_modules/`, `dist/`, `models_raw/` | 0.6 GB | JS build (deprioritised; `src/*.js` is kept only as the port spec). |

**Never add a `.meta` file's asset without its `.meta`, or vice versa.** Unity
identifies assets by the GUID inside the `.meta`; losing one re-links every
reference in every scene to nothing. UVCS will show them as two changes — check
in both, always, in the same changeset.

---

## 5. Project-specific rules you will trip over

These are not version control rules, they are this project's rules, and new
teammates break them in the first week.

- **`Game.unity` is the only scene that boots.** `Fly`, `Hangar` and `Tbilisi`
  are test/scaffold scenes. If you add scenery and don't see it, you almost
  certainly built it into the wrong scene. This has bitten us before.
- **`docs/QUALITY_BAR.md` is the acceptance checklist.** "Parity or better,
  never worse." A grey-box scaffold is not progress and must not be shown as
  such.
- **Coordinate frames.** Unity is left-handed, the Three.js original is
  right-handed, and the FBX bridge mirrors X. Every imported plane's nose ends
  up at **-Z**. Read the frame rules in the `unity` skill / `docs/UNITY_PORT.md`
  before you touch a model's transform. This is the single biggest source of
  silently-wrong work in the port.
- **One editor at a time.** This hardware is 16 GB. Three editors plus a running
  build filled 32 GB of swap. Don't open a second editor, and don't run the game
  while a play-mode suite is running.
- **Assets are generated, not hand-made.** The city, parks, planes, statues and
  characters come out of `tools/*.py` via Blender. Fix the generator and re-bake;
  don't hand-edit a generated FBX, because the next bake will overwrite you.
- **Verify by running, not by reading.** A log line proves a builder *ran*, not
  what it *produced*. Open the editor, press play, look at the frame.

---

## 6. Common problems

**"Everything is pink / prefabs are missing."**
Partial (Gluon) workspace missing a dependency, or `Library/` is mid-reimport.
Update the full workspace and let the import finish.

**"I can't check out, it says locked."**
Someone has it. Repository → Locks shows who. Ask them.

**"I have 4,000 pending changes after opening Unity."**
`Library/` or another ignored folder got added before `ignore.conf` reached you.
Update your workspace, confirm `ignore.conf` is present at the root, and undo the
unwanted changes — don't check them in.

**"The merge destroyed my scene."**
Scenes are locked precisely so this can't happen. If it did, the lock rules
weren't applied to the branch you were on — tell the admin, and recover the
previous revision from the file's history (UVCS keeps every one).

**"My branch won't merge, hundreds of conflicts."**
Your branch is stale. Merge `/main` *into your branch* first, resolve there,
then merge back. Never resolve a hundred conflicts directly on `/main`.
