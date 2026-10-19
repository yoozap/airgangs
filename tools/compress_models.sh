#!/usr/bin/env bash
# Shrink every shipped GLB: Draco for geometry, WebP for any leftover PNG/JPEG.
#
# Geometry is ~79% of the 235 MB of models here, so Draco is the lever that
# matters -- the largest plane went 11.7 MB -> 2.6 MB on its own.
#
# The originals are moved to models_raw/ at the project root (NOT under public/,
# so Vite never serves them). There is no git in this project, so that copy is
# the only way back.
#
# REQUIRES the game to attach a DRACOLoader (src/gltf.js). Without it a
# compressed GLB fails SILENTLY -- the model just never appears.
set -u
cd "$(dirname "$0")/.."
GT=./node_modules/.bin/gltf-transform
RAW=models_raw
mkdir -p "$RAW"

total_before=0; total_after=0; done=0; skipped=0
while IFS= read -r f; do
  rel="${f#public/models/}"
  before=$(stat -f%z "$f")
  tmp="$(mktemp -t glbc).glb"
  # --join/--flatten OFF: these are KITS. `optimize` happily merges five
  # transform-applied nodes into one mesh, and citymap.js finds its variants
  # BY NAME -- panel_block.glb came back as a single "block_2" and the other
  # four were gone. --palette OFF: it bakes untextured materials into one
  # atlas and the glass loses its own roughness. --prune-attributes OFF:
  # it strips UVs from primitives whose material has no texture, and then
  # mergeGeometries() refuses to merge them with the ones that kept theirs.
  if ! "$GT" optimize "$f" "$tmp" --compress draco --texture-compress webp \
        --texture-size 1024 --simplify false \
        --join false --flatten false --palette false --prune-attributes false \
        >/dev/null 2>&1; then
    rm -f "$tmp"; skipped=$((skipped+1)); echo "SKIP  $rel"; continue
  fi
  after=$(stat -f%z "$tmp" 2>/dev/null || echo 0)
  # only keep it if it actually got smaller and is not truncated
  if [ "$after" -lt 1024 ] || [ "$after" -ge "$before" ]; then
    rm -f "$tmp"; skipped=$((skipped+1)); echo "SKIP  $rel (no gain)"; continue
  fi
  mkdir -p "$RAW/$(dirname "$rel")"
  [ -f "$RAW/$rel" ] || cp "$f" "$RAW/$rel"
  mv "$tmp" "$f"
  total_before=$((total_before+before)); total_after=$((total_after+after)); done=$((done+1))
done < <(find public/models -name '*.glb' | sort)

echo
echo "compressed $done files, skipped $skipped"
echo "$(echo "scale=1; $total_before/1048576" | bc) MB -> $(echo "scale=1; $total_after/1048576" | bc) MB"
