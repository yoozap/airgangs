#!/usr/bin/env python3
"""Remove a vestigial glTF extension declaration from a GLB.

Some models (parks_new/parklamp.glb) declare EXT_texture_webp as REQUIRED while
carrying no images at all — 4 textures pointing at nothing. gltf-transform
crashes on them ("Cannot read properties of null"), and glTFast refuses them
because the required extension is unimplemented. There is nothing to convert,
so the declaration is simply dropped.

  tools/unity_strip_glb_ext.py <in.glb> <out.glb> [EXT_name]
"""
import json, struct, sys

GLB_MAGIC, JSON_CHUNK, BIN_CHUNK = 0x46546C67, 0x4E4F534A, 0x004E4942

def read_glb(path):
    with open(path, "rb") as f:
        magic, ver, _ = struct.unpack("<III", f.read(12))
        if magic != GLB_MAGIC:
            raise ValueError("not a GLB")
        chunks = []
        while True:
            hdr = f.read(8)
            if len(hdr) < 8:
                break
            clen, ctype = struct.unpack("<II", hdr)
            chunks.append((ctype, f.read(clen)))
    return ver, chunks

def write_glb(path, ver, chunks):
    body = b""
    for ctype, data in chunks:
        pad = (4 - len(data) % 4) % 4
        filler = (b" " if ctype == JSON_CHUNK else b"\x00") * pad
        data = data + filler
        body += struct.pack("<II", len(data), ctype) + data
    with open(path, "wb") as f:
        f.write(struct.pack("<III", GLB_MAGIC, ver, 12 + len(body)))
        f.write(body)

def strip(src, dst, ext="EXT_texture_webp"):
    ver, chunks = read_glb(src)
    out, changed = [], False
    for ctype, data in chunks:
        if ctype == JSON_CHUNK:
            d = json.loads(data)
            imgs = d.get("images", [])
            webp = [i for i in imgs if i.get("mimeType") == "image/webp"]
            if webp:
                raise ValueError(
                    f"{src} has {len(webp)} webp images — convert it, don't strip")
            for key in ("extensionsUsed", "extensionsRequired"):
                if ext in d.get(key, []):
                    d[key] = [e for e in d[key] if e != ext]
                    if not d[key]:
                        del d[key]
                    changed = True
            # drop any per-texture reference to the extension as well
            for t in d.get("textures", []):
                if ext in t.get("extensions", {}):
                    del t["extensions"][ext]
                    if not t["extensions"]:
                        del t["extensions"]
                    changed = True
            data = json.dumps(d, separators=(",", ":")).encode("utf-8")
        out.append((ctype, data))
    write_glb(dst, ver, out)
    return changed

if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    ext = sys.argv[3] if len(sys.argv) > 3 else "EXT_texture_webp"
    print(f"{'stripped' if strip(src, dst, ext) else 'no change'}: {dst}")
