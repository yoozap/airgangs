# Unity Cloud / UVCS setup — one-time, admin only

Day-to-day usage is in [`TEAM_WORKFLOW.md`](TEAM_WORKFLOW.md). This file is the
migration and the server-side configuration: do it once, then hand the team the
other document.

## Why UVCS and not git

Measured on this machine, 2026-11-06:

| | |
|---|---|
| Working tree | 26 GB |
| **Actual source (what syncs)** | **1.53 GB / 10,395 files** |
| Excluded by `ignore.conf` | 23 GB (import cache, downloaded GIS sources, built players) |
| Git pack before migration | 701 MB — git was choking on churning FBX/GLB |

UVCS cloud free tier, as of the March 2026 pricing change: **unlimited seats,
25 GB storage, 100 GB egress/month.** 1.53 GB leaves ample room for revisions.

It also gives us **exclusive checkout (locking)**, which git does not do well,
and which is the only real defence for an 11.6 MB `Game.unity`.

---

## Step 1 — Install the client

```sh
sudo installer -pkg <path>/unity-vcs-11.0.16.10616-mac.pkg -target /
```

Installs `/Applications/PlasticSCM.app` (full client), `/Applications/Gluon.app`
(artist partial-workspace client), the Unity VCS Tray, and symlinks `cm`,
`gluon` etc. into `/usr/local/bin`. The macOS build is x64 and runs under
Rosetta on Apple Silicon.

Verify:

```sh
cm version
```

## Step 2 — Create the org and sign in

1. Go to **cloud.unity.com** → create or pick an organisation.
2. **DevOps → Version Control → Repositories → Create repository**, name it
   `tbilisifly`.
3. Sign the local client in against that org:

```sh
plasticgui --configure      # GUI wizard: choose "Unity Cloud", sign in with your Unity ID
cm status                   # should no longer complain about credentials
```

## Step 3 — Import the git history

GitSync preserves **full history** — all changesets, comments, branches, tags.

```sh
cm sync tbilisifly git https://github.com/yoozap/airgangs.git
```

GitHub requires a **personal access token**, not a password.

Then create a workspace over the existing folder and update it.

⚠️ **Once imported, stop using git.** GitSync forbids history rewriting: no
`git rebase`, and `git merge --no-ff` only. Since GitHub is frozen as an archive
this is moot — just don't commit there again.

## Step 4 — Lock rules (the important one)

**Unity Dashboard → DevOps → Settings → Lock rules → Repository lock rules →
Configure**, and paste:

```
rep:tbilisifly br:/main
*.unity
*.prefab
*.asset
*.mat
*.anim
*.controller
*.mask
*.physicMaterial
*.fbx
*.glb
*.blend
*.png
*.jpg
*.jpeg
*.tga
*.psd
*.exr
*.hdr
*.wav
*.mp3
*.ogg
*.gz
```

Notes:

- `*.cs` is deliberately absent — code merges fine, locking it would cripple the
  team.
- These patterns do **not** match `.meta` files (`*.unity` ≠ `Game.unity.meta`),
  which is correct: metas are text and must stay mergeable.
- `*.asset` covers `Assets/Generated/chunk_*.asset`. Locking them is intentional:
  it stops two people re-baking the city at once.

## Step 5 — Smart merge for scenes and prefabs

Locking prevents most scene conflicts, but branch merges still need it. In the
client: **Preferences → Merge tools → Add**, for each of `.unity`, `.prefab`,
`.asset`, `.mat`, `.anim`, `.controller`:

```
/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/Helpers/UnityYAMLMerge merge -p "@basefile" "@sourcefile" "@destinationfile" "@output"
```

Or edit `~/.plastic4/client.conf` directly. `m_SerializationMode: 2` (Force Text)
is already set in `ProjectSettings/EditorSettings.asset`, which is the
precondition for any of this working.

## Step 6 — Invite the team

**Unity Dashboard → Organisation → Members.** Seats are free. Point everyone at
[`TEAM_WORKFLOW.md`](TEAM_WORKFLOW.md) and make sure artists install **Gluon**,
not the full client.

---

## Already done in the repo

- `ignore.conf` at the workspace root — 51 rules, verified against the real tree
  (1.53 GB in, 23 GB out). Checked in, so the team inherits it.
- `com.unity.collab-proxy` 2.14.0 added to `unity/Packages/manifest.json`
  (requires Unity 6000.0; this project is on 6000.6.7f1).
- `.gitignore` extended to stop versioning `unity/Assets/InitTestScene*` and the
  stray `unity/Tbilisifly/` build output.

## Optional local cleanup

Not needed for the migration — both are already excluded from version control —
but they reclaim **1.36 GB**:

```sh
rm -rf unity/Tbilisifly            # 1.3 GB stray nested project (player build + its Library)
rm -f  unity/mono_crash*           # 60 MB of editor crash dumps
```

## Build Automation (later)

Unity Build Automation reads directly from a UVCS repo, so once this migration is
done CI is a few clicks. Free tier: 200 min Windows + 100 min Mac compute per
month, first two concurrent machines free. Worth setting up only once `Game.unity`
builds clean headlessly.
