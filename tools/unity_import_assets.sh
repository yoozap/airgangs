#!/usr/bin/env bash
# Bring the Three.js build's assets into the Unity project.
#
# Two destinations, and the split matters:
#   Assets/Art, Assets/Audio      imported by Unity (glTFast reads .glb)
#   Assets/StreamingAssets        NOT imported — read at runtime with File IO,
#                                 which is what we want for 15 MB of city JSON.
#                                 Unity would otherwise try to treat it as an asset.
#
# Copies rather than symlinks: the Three.js build keeps running off public/ for
# the length of the port, and symlinks interact badly with git-lfs.
set -euo pipefail
cd "$(dirname "$0")/.."
U=unity/Assets

mkdir -p $U/StreamingAssets/tbilisi $U/Art/{Planes,Props,Characters,Landmarks,Statues,Parks,Houses} \
         $U/Art/Textures $U/Audio

# --- city + world data -> StreamingAssets (runtime File IO, no import) ---
cp -f public/tbilisi/*.json    $U/StreamingAssets/tbilisi/ 2>/dev/null || true
cp -f public/tbilisi/*.json.gz $U/StreamingAssets/tbilisi/ 2>/dev/null || true
rm -f $U/StreamingAssets/tbilisi/city.json.gz.bak
# the satellite/terrain jpgs are sampled as data, not shown as sprites
cp -f public/tbilisi/*.jpg     $U/StreamingAssets/tbilisi/ 2>/dev/null || true

# --- models ---
cp -f public/models/*.glb              $U/Art/Props/      2>/dev/null || true
# the Spitfire is an OBJ, not a GLB — it would be skipped by the glob above
cp -f public/models/*.obj              $U/Art/Props/      2>/dev/null || true
cp -f public/models/*.mtl              $U/Art/Props/      2>/dev/null || true
cp -rf public/models/planes/.           $U/Art/Planes/     2>/dev/null || true
cp -rf public/models/landmarks/.        $U/Art/Landmarks/  2>/dev/null || true
cp -rf public/models/statues/.          $U/Art/Statues/    2>/dev/null || true
cp -rf public/models/parks_new/.        $U/Art/Parks/      2>/dev/null || true
cp -rf public/models/houses/.           $U/Art/Houses/     2>/dev/null || true
# the elephant is the only personage that stays a GLB (not humanoid, Generic rig);
# banana/eggplant/toad come through tools/glb_to_fbx.py instead
cp -f public/models/personages/elephant.glb $U/Art/Characters/ 2>/dev/null || true

# --- textures + audio ---
cp -rf public/textures/. $U/Art/Textures/ 2>/dev/null || true
cp -rf public/audio/.    $U/Audio/        2>/dev/null || true

echo "--- imported ---"
for d in StreamingAssets/tbilisi Art/Planes Art/Props Art/Characters Art/Landmarks \
         Art/Statues Art/Parks Art/Houses Art/Textures Audio; do
  printf "%-28s %4d files  %6s\n" "$d" \
    "$(find $U/$d -type f 2>/dev/null | wc -l | tr -d ' ')" \
    "$(du -sh $U/$d 2>/dev/null | cut -f1)"
done
