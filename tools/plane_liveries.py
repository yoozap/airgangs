"""Rescue the fleet's liveries across the GLB -> FBX bridge.

THE DEFECT (measured 2026-10-30)
--------------------------------
`tools/glb_batch_fbx.py` exports with `embed_textures=True`, and the images ARE
in the FBX: `p51.fbx` carries five `Video` nodes whose `Content` blobs are valid
PNGs.  What is missing is the file EXTENSION.  A GLB from the Three.js pipeline
stores its images in buffer views with no `uri`, so Blender's glTF importer
names them `Image_0`, `PaletteBaseColor`, ... with no suffix, and Blender's FBX
exporter writes that bare name into `RelativeFilename`:

    Video 'PaletteBaseColor'  RelativeFilename 'p51.fbm/PaletteBaseColor'

Unity extracts embedded media into a sibling `<name>.fbm/` folder and then
imports the extracted files as ordinary assets.  An extensionless file has no
importer, so nothing becomes a `Texture2D` -- which is why there is not one
`.fbm` folder and not one image file under `unity/Assets/Resources/Art/Planes/`, every
`*.fbx.meta` references 0 textures, and all 35 aeroplanes render white.

THE FIX, AND WHY IT DOES NOT RE-EXPORT
--------------------------------------
The orientation chain (`PlaneModelFrame`, `PlaneProps.FileNose`, the T-28's
natural rot(270,0,0)/scale 100) was built on the FBX files that are on disk.
Re-exporting them to fix a filename would put all of that at risk for nothing,
so this tool does not touch the FBXs at all.  It goes back to the ORIGINAL GLB
-- the real art, with every map the FBX bridge cannot even carry (FBX has no
glTF ORM slot, so 45 metallic-roughness maps and 13 emissive maps never made it
into the FBX in the first place) -- decodes the images, and writes them as
ordinary PNG assets plus a material map that an `AssetPostprocessor` applies.

    python3 tools/plane_liveries.py [--check]

Writes:
    unity/Assets/Resources/Art/Planes/Tex/<stem>/<role>_<img>.png
    unity/Assets/Resources/Art/Planes/Tex/livery_map.txt     (TSV, read by the importer)

Role prefixes carry the import settings so `OnPreprocessTexture` needs no map:
    alb_   sRGB, trilinear              albp_  sRGB, POINT, no mips (palette)
    nrm_   NormalMap                    mg_    linear (metallic/smoothness)
    emi_   sRGB                         occ_   linear

glTF -> URP channel conversions done here (not in the shader):
    metallicRoughnessTexture  G=roughness B=metallic
      -> _MetallicGlossMap    R=metallic  A=smoothness(1-roughness)
    occlusionTexture          R=occlusion -> _OcclusionMap R (kept as-is)
"""
import json, os, struct, sys, glob, io, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GLB_DIR = os.path.join(ROOT, "public", "models", "planes")
FBX_DIR = os.path.join(ROOT, "unity", "Assets", "Resources", "Art", "Planes")
TEX_DIR = os.path.join(FBX_DIR, "Tex")
MAP_PATH = os.path.join(TEX_DIR, "livery_map.txt")

# ---------------------------------------------------------------- FBX reading
# We only need the material NAME LIST out of each FBX, to key the map by the
# name Unity will show on the imported material.  Blender dedupes a glTF
# material it had to split (p51: PaletteMaterial001 + PaletteMaterial001.001),
# so a trailing .NNN is stripped when matching.


def _fbx_nodes(path):
    b = open(path, "rb").read()
    ver = struct.unpack_from("<I", b, 23)[0]
    wide = ver >= 7500

    def read_node(pos):
        if wide:
            end, nprop, _plen = struct.unpack_from("<QQQ", b, pos)
            pos += 24
        else:
            end, nprop, _plen = struct.unpack_from("<III", b, pos)
            pos += 12
        nlen = b[pos]
        pos += 1
        name = b[pos:pos + nlen].decode("utf8", "replace")
        pos += nlen
        if end == 0:
            return 0, None
        props = []
        for _ in range(nprop):
            t = chr(b[pos])
            pos += 1
            if t in "SR":
                ln = struct.unpack_from("<I", b, pos)[0]
                pos += 4
                data = b[pos:pos + ln]
                pos += ln
                props.append((t, data if t == "R" else data.decode("utf8", "replace")))
            elif t in "CBYIFDL":
                sz = {"C": 1, "B": 1, "Y": 2, "I": 4, "F": 4, "D": 8, "L": 8}[t]
                props.append((t, b[pos:pos + sz]))
                pos += sz
            elif t in "fdlibc":
                _al, _enc, cl = struct.unpack_from("<III", b, pos)
                pos += 12 + cl
                props.append((t, None))
            else:
                raise ValueError("bad FBX property type %r at %d" % (t, pos))
        kids = []
        while pos < end:
            np, kid = read_node(pos)
            if np == 0:
                pos += 25 if wide else 13
                break
            pos = np
            if kid:
                kids.append(kid)
        return end, (name, props, kids)

    pos, out = 27, []
    while pos < len(b) - 100:
        np, node = read_node(pos)
        if np == 0:
            break
        pos = np
        if node:
            out.append(node)
    return out


def fbx_material_names(path):
    names = []

    def walk(nodes, inside_objects):
        for name, props, kids in nodes:
            if name == "Material" and inside_objects and len(props) > 1:
                names.append(props[1][1].split("\x00")[0])
            walk(kids, inside_objects or name == "Objects")

    walk(_fbx_nodes(path), False)
    return names


# ---------------------------------------------------------------- GLB reading
def glb_read(path):
    b = open(path, "rb").read()
    if b[:4] != b"glTF":
        raise ValueError("not a GLB: %s" % path)
    off, js, bin_ = 12, None, b""
    while off < len(b):
        ln, ty = struct.unpack_from("<II", b, off)
        chunk = b[off + 8:off + 8 + ln]
        if ty == 0x4E4F534A:
            js = json.loads(chunk.decode("utf8"))
        elif ty == 0x004E4942:
            bin_ = chunk
        off += 8 + ln
    return js, bin_


def image_bytes(js, bin_, idx):
    im = js["images"][idx]
    if "bufferView" in im:
        bv = js["bufferViews"][im["bufferView"]]
        start = bv.get("byteOffset", 0)
        return bin_[start:start + bv["byteLength"]]
    raise ValueError("image %d has a uri, not a bufferView" % idx)


def tex_source(js, texidx):
    """glTF texture -> (image index, sampler index). EXT_texture_webp puts the
    image under extensions, which is why every one of these files declares that
    extension as REQUIRED and why glTFast refused them all."""
    t = js["textures"][texidx]
    src = t.get("source")
    ext = t.get("extensions", {})
    if src is None and "EXT_texture_webp" in ext:
        src = ext["EXT_texture_webp"]["source"]
    return src, t.get("sampler")


def tex_transform(texinfo):
    """KHR_texture_transform -> (sx, sy, ox, oy) in Unity's tiling/offset.
    glTF UVs have V flipped relative to Unity's, so an offset in V must be
    mirrored; rotation is not representable on a URP Lit map and is ignored."""
    e = (texinfo or {}).get("extensions", {}).get("KHR_texture_transform")
    if not e:
        return 1.0, 1.0, 0.0, 0.0
    sx, sy = e.get("scale", [1.0, 1.0])
    ox, oy = e.get("offset", [0.0, 0.0])
    return float(sx), float(sy), float(ox), 1.0 - float(sy) - float(oy)


# ---------------------------------------------------------------- conversions
MIN_ALPHA = {}          # asset path -> lowest alpha byte in the image (255 = opaque)


def save_albedo(raw, dst, asset=None):
    from PIL import Image
    im = Image.open(io.BytesIO(raw))
    if im.mode not in ("RGB", "RGBA", "L", "LA"):
        im = im.convert("RGBA")
    im.save(dst, "PNG")
    if asset is not None:
        MIN_ALPHA[asset] = min(im.getchannel("A").getdata()) if "A" in im.getbands() else 255
    return im.size


def save_metalgloss(raw, dst):
    """glTF ORM (R=occlusion, G=roughness, B=metallic) -> URP _MetallicGlossMap
    (R=metallic, A=smoothness).  URP samples metallic from R and smoothness
    from A, so handing it the glTF texture unchanged would read ROUGHNESS as
    metallic and leave smoothness at the texture's alpha (usually 1)."""
    from PIL import Image, ImageChops
    im = Image.open(io.BytesIO(raw)).convert("RGBA")
    r, g, b, _a = im.split()
    metal = b
    smooth = ImageChops.invert(g)
    out = Image.merge("RGBA", (metal, metal, metal, smooth))
    out.save(dst, "PNG")
    return out.size


def save_plain(raw, dst):
    from PIL import Image
    im = Image.open(io.BytesIO(raw))
    im.save(dst, "PNG")
    return im.size


# ------------------------------------------------------------------ spitfire
# The ONE fleet member that is not a GLB.  `public/models/spitfire.obj`
# references a .mtl that does not ship, so src/planes.js:601-613 assigns its
# materials BY NAME from a hand-written table (part names are Turkish).  The
# Unity port never carried that table, so the Spitfire is white for a reason of
# its own and no texture pipeline can fix it -- the colours ARE the art.
# three.js reads a hex literal as sRGB and converts it to linear, so the table
# is converted here and every `color` row in the map is linear, like glTF's
# baseColorFactor.
SPITFIRE_OBJ = "Assets/Resources/Art/Props/spitfire.obj"
SPITFIRE_DEFAULT = (0x5a6e42, 0.3, 0.6, None)
SPITFIRE_MATERIALS = {
    "g\u00f6vde":        (0x5a6e42, 0.35, 0.55, None),   # RAF green
    "g\u00f6vdeparca":   (0x77683d, 0.35, 0.55, None),   # earth camo
    "cam":                (0x9fd2ff, 0.10, 0.05, 0.4),
    "cam2":               (0x9fd2ff, 0.10, 0.05, 0.4),
    "pervane":            (0x2a2a2a, 0.20, 0.70, None),
    "jant":               (0x8a8f98, 0.80, 0.35, None),
    "lastik\u00f6n":     (0x151515, 0.00, 0.95, None),
    "arkateker":          (0x151515, 0.00, 0.95, None),
    "arkatekertucu":      (0x555b64, 0.70, 0.40, None),
    "koltuk":             (0x6b4a2f, 0.10, 0.80, None),
    "zevkcubugu":         (0x222222, 0.30, 0.60, None),
}


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def spitfire_rows():
    obj = os.path.join(ROOT, "public", "models", "spitfire.obj")
    names = []
    if os.path.exists(obj):
        with open(obj, "r", errors="replace") as f:
            for line in f:
                if line.startswith("usemtl "):
                    n = line[7:].strip()
                    if n and n not in names:
                        names.append(n)
    rows = []
    for n in names:
        hexc, metal, rough, opacity = SPITFIRE_MATERIALS.get(n, SPITFIRE_DEFAULT)
        r = srgb_to_linear(((hexc >> 16) & 255) / 255.0)
        g = srgb_to_linear(((hexc >> 8) & 255) / 255.0)
        b = srgb_to_linear((hexc & 255) / 255.0)
        a = 1.0 if opacity is None else float(opacity)
        rows.append("spitfire\t%s\tcolor\t%.6f %.6f %.6f %.6f" % (n, r, g, b, a))
        rows.append("spitfire\t%s\tmetal\t%.6f" % (n, metal))
        rows.append("spitfire\t%s\tsmooth\t%.6f" % (n, 1.0 - rough))
        if opacity is not None:
            rows.append("spitfire\t%s\talpha\tBLEND" % n)
    return names, rows


# ---------------------------------------------------------------- main
def run(check_only=False):
    rows = []                      # TSV lines
    stats = collections.Counter()
    per_plane = {}

    glbs = sorted(glob.glob(os.path.join(GLB_DIR, "*.glb")))
    for g in glbs:
        stem = os.path.splitext(os.path.basename(g))[0]
        fbx = os.path.join(FBX_DIR, stem + ".fbx")
        if not os.path.exists(fbx):
            print("LIVERYGEN no-fbx %s" % stem)
            continue
        js, bin_ = glb_read(g)
        mats = js.get("materials", [])
        fnames = fbx_material_names(fbx)
        # Match a glTF material name to the FBX's.  EXACT first -- several GLBs
        # legitimately end a name in digits (`Material.012`), so stripping a
        # trailing `.NNN` unconditionally loses them.  Only then try the
        # stripped form, which is Blender's dedupe suffix for a glTF material it
        # had to split (p51: PaletteMaterial001 + PaletteMaterial001.001).
        base = set(fnames)
        for n in fnames:
            head, _, tail = n.rpartition(".")
            if head and tail.isdigit():
                base.add(head)
        outdir = os.path.join(TEX_DIR, stem)
        written = {}               # (role, imgidx) -> asset path

        def emit(role, texinfo, point_ok=True):
            """Decode the image behind a glTF textureInfo once per role and
            return its Unity asset path."""
            if texinfo is None:
                return None
            idx, samp = tex_source(js, texinfo["index"])
            if idx is None:
                return None
            prefix = role
            if role == "alb" and point_ok and samp is not None:
                s = js.get("samplers", [])[samp] if samp < len(js.get("samplers", [])) else {}
                if s.get("magFilter") == 9728:      # NEAREST -> a palette atlas
                    prefix = "albp"
            key = (prefix, idx)
            if key in written:
                return written[key]
            name = "%s_%d.png" % (prefix, idx)
            path = os.path.join(outdir, name)
            asset = "Assets/Resources/Art/Planes/Tex/%s/%s" % (stem, name)
            if not check_only:
                os.makedirs(outdir, exist_ok=True)
                raw = image_bytes(js, bin_, idx)
                if prefix == "mg":
                    save_metalgloss(raw, path)
                elif prefix in ("alb", "albp", "emi"):
                    save_albedo(raw, path, asset)
                else:
                    save_plain(raw, path)
            written[key] = asset
            stats["images"] += 1
            return asset

        nmat = 0
        for m in mats:
            mname = m.get("name", "")
            if mname not in base:
                stats["unmatched_material"] += 1
                print("LIVERYGEN unmatched %s/%s" % (stem, mname))
                continue
            pbr = m.get("pbrMetallicRoughness", {})
            slots = []
            alb = emit("alb", pbr.get("baseColorTexture"))
            if alb:
                sx, sy, ox, oy = tex_transform(pbr.get("baseColorTexture"))
                slots.append(("alb", alb))
                if (sx, sy, ox, oy) != (1.0, 1.0, 0.0, 0.0):
                    slots.append(("tile", "%.6f %.6f %.6f %.6f" % (sx, sy, ox, oy)))
            nrm = emit("nrm", m.get("normalTexture"), point_ok=False)
            if nrm:
                slots.append(("nrm", nrm))
                sc = (m.get("normalTexture") or {}).get("scale", 1.0)
                slots.append(("nrmscale", "%.6f" % float(sc)))
            mg = emit("mg", pbr.get("metallicRoughnessTexture"), point_ok=False)
            if mg:
                slots.append(("mg", mg))
            emi = emit("emi", m.get("emissiveTexture"), point_ok=False)
            if emi:
                slots.append(("emi", emi))
                ec = m.get("emissiveFactor", [1.0, 1.0, 1.0])
                slots.append(("emicolor", "%.6f %.6f %.6f" % tuple(ec)))
            occ = emit("occ", m.get("occlusionTexture"), point_ok=False)
            if occ:
                slots.append(("occ", occ))
            # The glTF factors multiply the maps exactly as URP's _BaseColor
            # multiplies _BaseMap, and they are the ONLY colour on the four
            # fully untextured airframes (f104 / kawasaki / rq180 / mig21nk:
            # Kawasaki is red, the RQ-180 is near-black).  With a
            # metallic-roughness MAP, URP reads metallic from the map and
            # multiplies smoothness by _Smoothness, so both go to 1 and the map
            # rules; without one they carry the factors.
            bc = pbr.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
            slots.append(("color", "%.6f %.6f %.6f %.6f" % tuple(bc)))
            if mg:
                slots.append(("metal", "1.000000"))
                slots.append(("smooth", "1.000000"))
            else:
                slots.append(("metal", "%.6f" % float(pbr.get("metallicFactor", 1.0))))
                slots.append(("smooth", "%.6f" % (1.0 - float(pbr.get("roughnessFactor", 1.0)))))
            # A BLEND material whose albedo is fully opaque and whose
            # baseColorFactor alpha is 1 renders identically opaque, and opaque
            # is strictly better in URP (depth write, no per-object sorting).
            # a7's whole fuselage is such a material -- gltf-transform's palette
            # pass inherited the flag from a donor.  So translucency is decided
            # by MEASURING the alpha channel, not by trusting the flag.
            mode = m.get("alphaMode", "OPAQUE")
            if mode == "BLEND":
                translucent = float(bc[3]) < 0.99 or (
                    alb is not None and MIN_ALPHA.get(alb, 255) < 250)
                if not translucent:
                    mode = "OPAQUE"
                    stats["blend_opaque"] += 1
            if mode != "OPAQUE":
                slots.append(("alpha", mode))
                if mode == "MASK":
                    slots.append(("cutoff", "%.6f" % float(m.get("alphaCutoff", 0.5))))
            if not slots:
                continue
            nmat += 1
            for role, val in slots:
                rows.append("%s\t%s\t%s\t%s" % (stem, mname, role, val))
        per_plane[stem] = (len(set(v for v in written.values())), nmat)
        stats["planes"] += 1
        stats["materials"] += nmat

    snames, srows = spitfire_rows()
    rows.extend(srows)
    per_plane["spitfire"] = (0, len(snames))
    stats["planes"] += 1
    stats["materials"] += len(snames)

    if not check_only:
        os.makedirs(TEX_DIR, exist_ok=True)
        with open(MAP_PATH, "w") as f:
            f.write("# plane\tmaterial\trole\tvalue   -- generated by tools/plane_liveries.py\n")
            f.write("\n".join(rows) + "\n")
    print("LIVERYGEN planes=%d materials=%d images=%d rows=%d unmatched=%d blendOpaque=%d" % (
        stats["planes"], stats["materials"], stats["images"], len(rows),
        stats["unmatched_material"], stats["blend_opaque"]))
    for stem in sorted(per_plane):
        n, nm = per_plane[stem]
        print("LIVERYGEN %-22s textures=%-3d materials=%d" % (stem, n, nm))
    return 0


if __name__ == "__main__":
    sys.exit(run(check_only="--check" in sys.argv))
