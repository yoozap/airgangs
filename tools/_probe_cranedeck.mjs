// WHAT IS THERE TO STAND ON? Reads a GLB in Blender and reports every
// horizontal surface big enough to put a pair of boots on, by height: the
// carrier deck, the outrigger pads, the cab roof, the steps. Those are the
// boxes a climbable machine needs, and the numbers to build them from.
//   node tools/_probe_cranedeck.mjs public/models/crane_machine.glb [scale]
// (a .mjs wrapper so it runs like the other probes; the work is Blender's)
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
const GLB = process.argv[2] || 'public/models/crane_machine.glb';
const SCALE = +(process.argv[3] || 1);
const BLENDER = '/Applications/Blender.app/Contents/MacOS/Blender';
const py = `
import bpy, sys, math
from collections import defaultdict
from mathutils import Vector
glb = sys.argv[sys.argv.index('--') + 1]
scale = float(sys.argv[sys.argv.index('--') + 2])
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
objs = [o for o in bpy.data.objects if o.type == 'MESH']
lo = Vector((1e9, 1e9, 1e9)); hi = Vector((-1e9, -1e9, -1e9))
for o in objs:
    for c in o.bound_box:
        p = o.matrix_world @ Vector(c)
        lo = Vector((min(lo[i], p[i]) for i in range(3)))
        hi = Vector((max(hi[i], p[i]) for i in range(3)))
print(f"MODEL {len(objs)} meshes, bbox x {lo.x*scale:.2f}..{hi.x*scale:.2f}  y {lo.y*scale:.2f}..{hi.y*scale:.2f}  z {lo.z*scale:.2f}..{hi.z*scale:.2f} (Blender Z up)")
# every upward-facing face, bucketed by height above the model's own floor
BUCKET = 0.10
decks = defaultdict(lambda: {"a": 0.0, "lo": Vector((1e9, 1e9)), "hi": Vector((-1e9, -1e9))})
for o in objs:
    m = o.matrix_world
    me = o.data
    me.calc_loop_triangles()
    for t in me.loop_triangles:
        n = (m.to_3x3() @ t.normal).normalized()
        if n.z < 0.80:                 # a floor, not a flank
            continue
        vs = [m @ me.vertices[i].co for i in t.vertices]
        z = sum(v.z for v in vs) / 3 - lo.z
        a = t.area * (m.to_scale().x * m.to_scale().y)
        d = decks[round(z / BUCKET)]
        d["a"] += a
        for v in vs:
            d["lo"] = Vector((min(d["lo"].x, v.x), min(d["lo"].y, v.y)))
            d["hi"] = Vector((max(d["hi"].x, v.x), max(d["hi"].y, v.y)))
rows = sorted(decks.items(), key=lambda kv: kv[0])
print(f"{'height m':>9} {'area m2':>8}   x extent (m)        y extent (m)")
for k, d in rows:
    if d["a"] * scale * scale < 0.6:   # smaller than a pair of boots
        continue
    h = k * BUCKET * scale
    print(f"{h:9.2f} {d['a']*scale*scale:8.2f}   {d['lo'].x*scale:7.2f}..{d['hi'].x*scale:6.2f}   {d['lo'].y*scale:7.2f}..{d['hi'].y*scale:6.2f}")
`;
const tmp = '/private/tmp/claude-501/-Users-wazzap-tbilisifly/e1c0de98-33c5-4e26-8bfd-4dcf76bc1b13/scratchpad/_deck.py';
fs.writeFileSync(tmp, py);
const outTxt = execFileSync(BLENDER, ['-b', '-P', tmp, '--', GLB, String(SCALE)], { encoding: 'utf8', maxBuffer: 1 << 28 });
console.log(outTxt.split('\n').filter((l) => /^(MODEL|\s*-?\d|\s*height)/.test(l)).join('\n'));
