#!/usr/bin/env bash
# Make the game's GLBs readable by glTFast.
#
# Every model GLB from the Three.js pipeline declares EXT_texture_webp in
# *extensionsRequired*. glTFast does not implement that extension, and because
# it is REQUIRED it refuses the whole file — importing with 0 subassets, which
# looks exactly like a missing asset. Draco is fine (com.unity.cloud.draco is
# installed); WEBP is the blocker.
#
# gltf-transform's --formats flag selects which SOURCE formats to process, and
# it defaults to "png" — so without `--formats webp` the webp textures are
# silently skipped and nothing changes. That cost an hour; don't remove it.
#
# Textures come out lossless PNG. Unity re-compresses everything to platform GPU
# formats at import, so the source format only costs repo space, not runtime.
set -uo pipefail
cd "$(dirname "$0")/.."
DEST=unity/Assets/Resources/Art
ok=0; fail=0; skip=0

convert() {  # src  dest
  local src="$1" dst="$2"
  # only files that actually declare the extension need rewriting
  if ! python3 - "$src" <<'PY'
import json, struct, sys
try:
    f = open(sys.argv[1], "rb")
    if struct.unpack("<III", f.read(12))[0] != 0x46546C67: sys.exit(1)
    c, _ = struct.unpack("<II", f.read(8))
    d = json.loads(f.read(c))
    sys.exit(0 if "EXT_texture_webp" in d.get("extensionsRequired", []) else 1)
except Exception:
    sys.exit(1)
PY
  then
    cp -f "$src" "$dst"; skip=$((skip+1)); return
  fi
  if npx --no-install gltf-transform png "$src" "$dst" --formats webp >/dev/null 2>&1; then
    ok=$((ok+1))
  else
    echo "  FAIL $src"; cp -f "$src" "$dst"; fail=$((fail+1))
  fi
}

mkdir -p $DEST/{Planes,Props,Characters,Landmarks,Statues,Parks,Houses}
for pair in "public/models/planes:Planes" "public/models/landmarks:Landmarks" \
            "public/models/statues:Statues" "public/models/parks_new:Parks" \
            "public/models/houses:Houses"; do
  srcdir="${pair%%:*}"; out="${pair#*:}"
  for f in $srcdir/*.glb; do
    [ -e "$f" ] || continue
    convert "$f" "$DEST/$out/$(basename "$f")"
  done
done
for f in public/models/*.glb; do
  [ -e "$f" ] || continue
  convert "$f" "$DEST/Props/$(basename "$f")"
done
convert public/models/personages/elephant.glb $DEST/Characters/elephant.glb

echo "converted=$ok already_clean=$skip failed=$fail"
