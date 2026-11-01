#!/usr/bin/env python3
"""Ground truth for the CityData port, computed straight from city.json.gz
using the same algorithm src/citymap.js uses. Compare with CityProbe.cs."""
import base64, gzip, json, struct

d = json.load(gzip.open("public/tbilisi/city.json.gz"))
m, t = d["meta"], d["terrain"]
print(f"CITYP origin={m['origin']['lat']:.4f},{m['origin']['lon']:.4f} "
      f"mlat={m['mlat']:.5f} mlon={m['mlon']:.5f}")

raw = base64.b64decode(t["data"])
full = struct.unpack("<%dH" % (len(raw) // 2), raw)
cls_raw = base64.b64decode(t["classes"]) if t.get("classes") else None

W, H, STEP = t["w"], t["h"], t["step"]
w2, h2 = (W - 1) // 2 + 1, (H - 1) // 2 + 1
step2 = STEP * 2
heights = [0.0] * (w2 * h2)
classes = [0] * (w2 * h2)
for r in range(h2):
    fr = min(r * 2, H - 1)
    for c in range(w2):
        fc = min(c * 2, W - 1)
        heights[r * w2 + c] = full[fr * W + fc] / 10
        if cls_raw: classes[r * w2 + c] = cls_raw[fr * W + fc]

print(f"CITYP grid w={w2} h={h2} step={step2:.1f} "
      f"minx={t['minx']:.1f} minz={t['minz']:.1f}")
print(f"CITYP buildings n={len(d['buildings'])}")
b0 = d["buildings"][0]
print(f"CITYP b0 height={b0[0]/10:.2f} verts={(len(b0)-1)//2} "
      f"x0={b0[1]/10:.2f} z0={b0[2]/10:.2f}")

def clamp(v, a, b): return a if v < a else (b if v > b else v)

def height_at(x, z):
    fx = clamp((x - t["minx"]) / step2, 0, w2 - 1.001)
    fz = clamp((z - t["minz"]) / step2, 0, h2 - 1.001)
    c, r = int(fx), int(fz)
    dx, dz = fx - c, fz - r
    i = r * w2 + c
    h00, h10 = heights[i], heights[i + 1]
    h01, h11 = heights[i + w2], heights[i + w2 + 1]
    if dx + dz <= 1:  # the triangle the mesh draws, not bilinear
        return h00 + dx * (h10 - h00) + dz * (h01 - h00)
    return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11)

def class_at(x, z):
    c = int(clamp((x - t["minx"]) / step2, 0, w2 - 1.001))
    r = int(clamp((z - t["minz"]) / step2, 0, h2 - 1.001))
    return classes[r * w2 + c]

for x, z in [(0,0),(1000,2000),(-3000,5000),(5000,-1000),
             (-11277/10,108152/10),(8000,8000),(-8000,-4000)]:
    print(f"CITYP h x={x:.1f} z={z:.1f} y={height_at(x,z):.4f} cls={class_at(x,z)}")

print(f"CITYP heights n={len(heights)} min={min(heights):.2f} max={max(heights):.2f} "
      f"mean={sum(heights)/len(heights):.4f}")
