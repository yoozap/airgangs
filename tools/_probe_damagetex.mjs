// Ground truth for the Unity port's 3 new damage-decal-texture files:
//   unity/Assets/Scripts/Combat/DamageTextures.cs       (shared primitives)
//   unity/Assets/Scripts/Combat/ImpactDecalTextures.cs  (src/impactfx.js:168-418)
//   unity/Assets/Scripts/Combat/GunDecalTextures.cs     (src/guns.js:90-171)
//
// Node has no <canvas>, so src/impactfx.js's/src/guns.js's own drawing calls
// cannot be run here any more than they could in the probe that already
// conceded this for src/planedamage.js's art (tools/_probe_damage.mjs's own
// header: its canvas shim no-ops every drawing call). What CAN be done, and
// is done below, is a from-scratch, line-cited REIMPLEMENTATION of the same
// per-pixel maths the C# port uses (point-in-polygon / distance-to-segment /
// gradient-stop / ellipse-membership tests instead of canvas rasterization),
// driven by the SAME seeded mulberry32 PRNG the C# side uses
// (TbilisiFly.Combat.DamageTextures.Rng32) so the two independent
// reimplementations consume random numbers in the same order and therefore
// draw the same shapes. The printed `DAMAGETEX id=... key=value` lines use
// EXACTLY the same key names as Assets/Editor/Probes/DamageTextureProbe.cs's
// Debug.Log lines (see Combat/DamageTextures.cs's TexFacts/AnalyzeBuffer),
// for tools/unity_port_check.py to diff.
//
// Run: node tools/_probe_damagetex.mjs

// ---------------------------------------------------------------------------
// Rng32 — mulberry32, bit-identical to Combat/DamageTextures.cs's Rng32.
// ---------------------------------------------------------------------------
function makeRng32(seed) {
  let a = seed >>> 0;
  return function next() {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// ---------------------------------------------------------------------------
// Compositing / colour, matching DamageTextures.cs's Over/Rgba/Lerp3.
// ---------------------------------------------------------------------------
const TRANSPARENT = { r: 0, g: 0, b: 0, a: 0 };

function over(dst, src) {
  const a = src.a + dst.a * (1 - src.a);
  if (a <= 0.0005) return { ...TRANSPARENT };
  return {
    r: (src.r * src.a + dst.r * (dst.a * (1 - src.a))) / a,
    g: (src.g * src.a + dst.g * (dst.a * (1 - src.a))) / a,
    b: (src.b * src.a + dst.b * (dst.a * (1 - src.a))) / a,
    a,
  };
}

function rgba(r, g, b, a) { return { r: r / 255, g: g / 255, b: b / 255, a }; }

function clamp01(x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

function lerpColor(c0, c1, t) {
  return { r: c0.r + (c1.r - c0.r) * t, g: c0.g + (c1.g - c0.g) * t, b: c0.b + (c1.b - c0.b) * t, a: c0.a + (c1.a - c0.a) * t };
}

// stops: [[t, color], ...] ascending by t
function lerp3(t, stops) {
  t = clamp01(t);
  if (t <= stops[0][0]) return stops[0][1];
  for (let i = 1; i < stops.length; i++) {
    if (t <= stops[i][0]) {
      const span = stops[i][0] - stops[i - 1][0];
      const lt = span > 1e-6 ? (t - stops[i - 1][0]) / span : 0;
      return lerpColor(stops[i - 1][1], stops[i][1], lt);
    }
  }
  return stops[stops.length - 1][1];
}

// ---------------------------------------------------------------------------
// Geometry primitives, matching DamageTextures.cs 1:1.
// ---------------------------------------------------------------------------
function v2(x, y) { return { x, y }; }

function jag(rng, r, n, rough) {
  const pts = new Array(n);
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2;
    const rr = r * (1 - rough + rng() * rough * 2);
    pts[k] = v2(Math.cos(a) * rr, Math.sin(a) * rr);
  }
  return pts;
}

function radialPoly(n, radiusAt) {
  const pts = new Array(n);
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2;
    const rr = radiusAt(k);
    pts[k] = v2(Math.cos(a) * rr, Math.sin(a) * rr);
  }
  return pts;
}

function pointInPolygon(p, poly, offset = { x: 0, y: 0 }) {
  let inside = false;
  const n = poly.length;
  for (let i = 0, j = n - 1; i < n; j = i++) {
    const a = { x: poly[i].x + offset.x, y: poly[i].y + offset.y };
    const b = { x: poly[j].x + offset.x, y: poly[j].y + offset.y };
    if (((a.y > p.y) !== (b.y > p.y)) &&
        (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)) inside = !inside;
  }
  return inside;
}

function distToSegment(p, a, b) {
  const abx = b.x - a.x, aby = b.y - a.y;
  const sq = abx * abx + aby * aby;
  const t = sq > 1e-9 ? clamp01(((p.x - a.x) * abx + (p.y - a.y) * aby) / sq) : 0;
  const px = a.x + abx * t, py = a.y + aby * t;
  return Math.hypot(p.x - px, p.y - py);
}

function distToPolyline(p, poly) {
  let best = Infinity;
  for (let i = 0; i + 1 < poly.length; i++) best = Math.min(best, distToSegment(p, poly[i], poly[i + 1]));
  return best;
}

function distToPolylineClosed(p, poly) {
  let best = Infinity;
  const n = poly.length;
  for (let i = 0; i < n; i++) best = Math.min(best, distToSegment(p, poly[i], poly[(i + 1) % n]));
  return best;
}

function onArc(p, radius, a0, span, width) {
  const r = Math.hypot(p.x, p.y);
  if (Math.abs(r - radius) > width * 0.5) return false;
  let a = Math.atan2(p.y, p.x);
  let d = a - a0;
  d -= Math.floor(d / (Math.PI * 2)) * Math.PI * 2;
  return d <= Math.abs(span);
}

function onRing(p, center, radius, width) {
  return Math.abs(Math.hypot(p.x - center.x, p.y - center.y) - radius) <= width * 0.5;
}

function inCircle(p, center, radius) {
  const dx = p.x - center.x, dy = p.y - center.y;
  return dx * dx + dy * dy <= radius * radius;
}

function inEllipse(p, center, rx, ry, rot) {
  const dx = p.x - center.x, dy = p.y - center.y;
  const cs = Math.cos(-rot), sn = Math.sin(-rot);
  const lx = dx * cs - dy * sn, ly = dx * sn + dy * cs;
  const ex = rx > 1e-6 ? lx / rx : 0, ey = ry > 1e-6 ? ly / ry : 0;
  return ex * ex + ey * ey <= 1;
}

function quadraticPoint(p0, c, p1, t) {
  const u = 1 - t;
  return v2(u * u * p0.x + 2 * u * t * c.x + t * t * p1.x, u * u * p0.y + 2 * u * t * c.y + t * t * p1.y);
}

function sampleQuadratic(p0, c, p1, steps) {
  const pts = new Array(steps + 1);
  for (let i = 0; i <= steps; i++) pts[i] = quadraticPoint(p0, c, p1, i / steps);
  return pts;
}

// ---------------------------------------------------------------------------
// Canvas buffer + analysis, matching DamageTextures.cs's ToTexture/AnalyzeBuffer.
// ---------------------------------------------------------------------------
function makeBuffer(w, h) {
  const buf = new Array(w * h);
  for (let i = 0; i < buf.length; i++) buf[i] = { ...TRANSPARENT };
  return buf;
}

function blend(buf, w, h, px, py, c) {
  const x = Math.floor(px), y = Math.floor(py);
  if (x < 0 || x >= w || y < 0 || y >= h) return;
  const i = y * w + x;
  buf[i] = over(buf[i], c);
}

// Both real ground truths are 8-bit: the actual game's THREE.CanvasTexture is
// backed by a browser <canvas>'s ImageData (Uint8ClampedArray), and the Unity
// port's DamageTextures.ToTexture() writes into TextureFormat.RGBA32 — see
// that function and DamageTextureProbe's Debug.Log, which reads the texture
// back with Texture2D.GetPixels() (byte-quantized, not the raw float buffer).
// This reimplementation composited in float64 and analyzed that buffer
// directly, which is the ACTUAL bug (not the C# side): a few hundred pixels
// near the alphaZeroFrac threshold (0.02) and a handful of near-boundary
// colour values round across their nearest-1/255 tie differently in float64
// than they do once truncated to a byte, which is what both ground truths
// really store. Quantize here before analyzing, matching
// DamageTextures.ToTexture's `Mathf.RoundToInt(x * 255)` byte round-trip.
// Confirmed this is the exact and only cause of the spall mismatch reported
// against DamageTextureProbe (js 0.3728/0.8740/0.9639/0.9631 ->
// quantized 0.3762/0.8745/0.9637/0.9629, matching cs exactly).
function quantize8(buf) {
  for (let i = 0; i < buf.length; i++) {
    const c = buf[i];
    buf[i] = {
      r: Math.round(c.r * 255) / 255,
      g: Math.round(c.g * 255) / 255,
      b: Math.round(c.b * 255) / 255,
      a: Math.round(c.a * 255) / 255,
    };
  }
}

function analyze(buf, w, h) {
  const opaqueThresh = 0.5, zeroThresh = 0.02;
  let opaqueN = 0, zeroN = 0;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  let sumR = 0, sumG = 0, sumB = 0, inkN = 0;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const c = buf[y * w + x];
      if (c.a > opaqueThresh) opaqueN++;
      if (c.a <= zeroThresh) zeroN++;
      if (c.a > zeroThresh) {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
        sumR += c.r; sumG += c.g; sumB += c.b;
        inkN++;
      }
    }
  }
  const total = w * h;
  const f = {
    w, h,
    opaqueFrac: opaqueN / total,
    alphaZeroFrac: zeroN / total,
  };
  if (inkN > 0) {
    f.bboxMinX = minX / w; f.bboxMinY = minY / h;
    f.bboxMaxX = (maxX + 1) / w; f.bboxMaxY = (maxY + 1) / h;
    f.meanR = sumR / inkN; f.meanG = sumG / inkN; f.meanB = sumB / inkN;
  } else {
    f.bboxMinX = f.bboxMinY = f.bboxMaxX = f.bboxMaxY = -1;
    f.meanR = f.meanG = f.meanB = -1;
  }
  f.centerAlpha = buf[Math.floor(h / 2) * w + Math.floor(w / 2)].a;
  const e0 = buf[0].a, e1 = buf[w - 1].a, e2 = buf[(h - 1) * w].a, e3 = buf[(h - 1) * w + (w - 1)].a;
  f.edgeAlpha = (e0 + e1 + e2 + e3) * 0.25;
  return f;
}

// ===========================================================================
// src/impactfx.js:168-209 pitTexture
// ===========================================================================
function drawPit(buf, w, h, rng) {
  const R = 30;
  const lip = radialPoly(28, () => R * (1.28 + rng() * 0.3));                 // :176-180
  const lipStops = [[0, rgba(226, 220, 208, 0.55)], [1, rgba(226, 220, 208, 0)]];
  const lipR0 = R * 0.8, lipR1 = R * 1.55;

  const pit = radialPoly(24, () => R * (0.86 + rng() * 0.18));                // :188-192
  const pitStops = [
    [0, rgba(4, 4, 5, 1)], [0.62, rgba(14, 13, 13, 0.98)],
    [0.9, rgba(38, 34, 31, 0.9)], [1, rgba(52, 47, 42, 0.35)],
  ];
  const pitR1 = R; // :195 approximated concentric (two-centre gradient)

  const rimColor = rgba(120, 112, 100, 0.45);
  const rimWidth = 2.2; // :202-205 strokes the PIT's own path, not a circle

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x - 48 + 0.5, y - 48 + 0.5);
      const r = Math.hypot(p.x, p.y);
      let c = { ...TRANSPARENT };
      if (pointInPolygon(p, lip)) c = over(c, lerp3((r - lipR0) / Math.max(1e-5, lipR1 - lipR0), lipStops));
      if (pointInPolygon(p, pit)) c = over(c, lerp3(r / Math.max(1e-5, pitR1), pitStops));
      if (distToPolylineClosed(p, pit) < rimWidth * 0.5) c = over(c, rimColor);
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/impactfx.js:213-260 spallTexture
// ===========================================================================
function drawSpall(buf, w, h, rng) {
  const baseStops = [
    [0, rgba(255, 255, 255, 0.72)], [0.3, rgba(255, 255, 255, 0.34)],
    [0.68, rgba(255, 255, 255, 0.12)], [1, rgba(255, 255, 255, 0)],
  ];
  const baseR0 = 4, baseR1 = 60;

  const chipCount = 90;
  const chipPos = new Array(chipCount), chipRx = new Array(chipCount), chipRy = new Array(chipCount), chipRot = new Array(chipCount), chipColor = new Array(chipCount);
  for (let k = 0; k < chipCount; k++) {
    const a = rng() * Math.PI * 2;                       // :230
    const r = 6 + Math.pow(rng(), 0.6) * 52;              // :231
    chipColor[k] = rgba(255, 255, 255, 0.1 + rng() * 0.3); // :232
    chipPos[k] = v2(Math.cos(a) * r, Math.sin(a) * r);
    chipRx[k] = 1 + rng() * 3.5;                           // :235
    chipRy[k] = 0.8 + rng() * 2.2;                          // :235
    chipRot[k] = a;
  }

  const crackCount = 7;
  const crackA = new Array(crackCount), crackB = new Array(crackCount), crackColor = new Array(crackCount), crackWidth = new Array(crackCount);
  for (let k = 0; k < crackCount; k++) {
    const a = rng() * Math.PI * 2;                        // :240
    const len = 18 + rng() * 34;                            // :241
    crackColor[k] = rgba(90, 86, 80, 0.18 + rng() * 0.2);   // :242
    crackWidth[k] = 0.8 + rng();                             // :243
    crackA[k] = v2(Math.cos(a) * 7, Math.sin(a) * 7);
    crackB[k] = v2(Math.cos(a + 0.12) * len, Math.sin(a + 0.12) * len); // :246
  }

  const dishStops = [[0, rgba(40, 36, 32, 0.55)], [1, rgba(40, 36, 32, 0)]];
  const dishR0 = 2, dishR1 = 18;

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x - 64 + 0.5, y - 64 + 0.5);
      const r = Math.hypot(p.x, p.y);
      let c = { ...TRANSPARENT };
      if (r <= 62) c = over(c, lerp3((r - baseR0) / Math.max(1e-5, baseR1 - baseR0), baseStops));
      for (let k = 0; k < chipCount; k++) if (inEllipse(p, chipPos[k], chipRx[k], chipRy[k], chipRot[k])) c = over(c, chipColor[k]);
      for (let k = 0; k < crackCount; k++) if (distToSegment(p, crackA[k], crackB[k]) < crackWidth[k] * 0.5) c = over(c, crackColor[k]);
      if (r <= 18) c = over(c, lerp3((r - dishR0) / Math.max(1e-5, dishR1 - dishR0), dishStops));
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/impactfx.js:264-298 exitTexture
// ===========================================================================
function drawExit(buf, w, h, rng) {
  const R = 26, n = 11;
  const petal = new Array(n), petalColor = new Array(n);
  for (let k = 0; k < n; k++) {
    const a = (k / n) * Math.PI * 2 + rng() * 0.3;         // :272
    const len = R * (1.2 + rng() * 0.9);                    // :273
    petalColor[k] = rgba(225, 217, 203, 0.3 + rng() * 0.35); // :274
    const p0 = v2(Math.cos(a - 0.26) * R * 0.8, Math.sin(a - 0.26) * R * 0.8);
    const tip = v2(Math.cos(a) * len, Math.sin(a) * len);
    const p2 = v2(Math.cos(a + 0.26) * R * 0.8, Math.sin(a + 0.26) * R * 0.8);
    petal[k] = [p0, tip, p2];
  }
  const outline = radialPoly(22, () => R * (0.78 + rng() * 0.34));            // :283-287
  const pitStops = [[0, rgba(5, 5, 6, 1)], [0.8, rgba(20, 18, 17, 0.95)], [1, rgba(44, 40, 36, 0.4)]];

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x - 48 + 0.5, y - 48 + 0.5);
      let c = { ...TRANSPARENT };
      for (let k = 0; k < n; k++) if (pointInPolygon(p, petal[k])) c = over(c, petalColor[k]);
      if (pointInPolygon(p, outline)) c = over(c, lerp3(Math.hypot(p.x, p.y) / R, pitStops));
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/impactfx.js:303-340 scuffTexture — 160x64, NOT translated.
// ===========================================================================
function drawScuff(buf, w, h, rng) {
  const y0 = 32;

  const grooveCount = 170;
  const groovePos = new Array(grooveCount), grooveRx = new Array(grooveCount), grooveRy = new Array(grooveCount), grooveColor = new Array(grooveCount);
  for (let k = 0; k < grooveCount; k++) {
    const t = Math.pow(rng(), 0.7);                        // :311
    const x = 30 + t * 124;
    const gw = (1 - t) * 7 + 1.2;
    grooveColor[k] = { r: (22 + t * 40) / 255, g: (20 + t * 38) / 255, b: (18 + t * 34) / 255, a: 0.5 * (1 - t * 0.8) }; // :314
    const ey = y0 + (rng() - 0.5) * gw * 1.6;               // :316
    const rx = 2 + rng() * 5;                                // :316
    groovePos[k] = v2(x, ey);
    grooveRx[k] = rx; grooveRy[k] = gw * 0.5;
  }

  const speckCount = 150;
  const speckPos = new Array(speckCount), speckRx = new Array(speckCount), speckRy = new Array(speckCount), speckColor = new Array(speckCount);
  for (let k = 0; k < speckCount; k++) {
    const t = Math.pow(rng(), 0.5);                         // :321
    const x = 32 + t * 126;
    const a = 0.08 + rng() * 0.26 * (1 - t * 0.6);           // :323
    speckColor[k] = rgba(240, 234, 222, a);
    const ey = y0 + (rng() - 0.5) * (6 + t * 26);            // :325
    const rx = 1 + rng() * 3;                                 // :326
    const ry = 0.7 + rng() * 1.6;                             // :326
    speckPos[k] = v2(x, ey);
    speckRx[k] = rx; speckRy[k] = ry;
  }

  const biteStops = [[0, rgba(6, 6, 7, 0.9)], [1, rgba(6, 6, 7, 0)]];
  const biteCenter = v2(34, y0);
  const biteR0 = 0, biteR1 = 13, biteRx = 13, biteRy = 7;

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x + 0.5, y + 0.5);
      let c = { ...TRANSPARENT };
      for (let k = 0; k < grooveCount; k++) if (inEllipse(p, groovePos[k], grooveRx[k], grooveRy[k], 0)) c = over(c, grooveColor[k]);
      for (let k = 0; k < speckCount; k++) if (inEllipse(p, speckPos[k], speckRx[k], speckRy[k], 0)) c = over(c, speckColor[k]);
      const r = Math.hypot(p.x - biteCenter.x, p.y - biteCenter.y);
      if (inEllipse(p, biteCenter, biteRx, biteRy, 0)) c = over(c, lerp3((r - biteR0) / Math.max(1e-5, biteR1 - biteR0), biteStops));
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/impactfx.js:347-418 starTexture
// ===========================================================================
function drawStar(buf, w, h, rng) {
  const R = 86;
  const n = 7 + Math.trunc(rng() * 4);                      // :355

  const mainPts = new Array(n), endPos = new Array(n), endAngle = new Array(n), endLen = new Array(n);
  const mainColor = new Array(n), mainWidth = new Array(n);
  let a = rng() * Math.PI * 2;                               // :357
  for (let k = 0; k < n; k++) {
    a += (Math.PI * 2 / n) * (0.55 + rng() * 0.9);            // :359
    const len = R * (0.35 + rng() * 0.62);                     // :360
    mainColor[k] = rgba(242, 250, 255, 0.3 + rng() * 0.33);     // :361
    mainWidth[k] = 0.7 + rng() * 0.9;                            // :362
    const steps = 3 + Math.trunc(rng() * 3);                     // :367
    let aa = a;
    let x = Math.cos(a) * 5, y = Math.sin(a) * 5;
    const pts = [v2(x, y)];
    for (let q = 1; q <= steps; q++) {
      aa += (rng() - 0.5) * 0.3;                                 // :370
      const r = 5 + (len - 5) * (q / steps);
      x = Math.cos(aa) * r; y = Math.sin(aa) * r;
      pts.push(v2(x, y));
    }
    mainPts[k] = pts;
    endPos[k] = v2(x, y); endAngle[k] = aa; endLen[k] = len;
  }

  const lateralP0 = new Array(n), lateralC = new Array(n), lateralP1 = new Array(n), lateralColor = new Array(n), lateralLive = new Array(n).fill(false);
  for (let k = 0; k < n; k++) {
    const k2 = (k + 1) % n;
    if (rng() < 0.35) continue;                                  // :384
    const t = 0.3 + rng() * 0.45;                                  // :385
    lateralColor[k] = rgba(232, 244, 252, 0.16 + rng() * 0.2);      // :386
    const mt = t * (0.88 + rng() * 0.2);                            // :390
    const a1 = v2(Math.cos(endAngle[k]) * endLen[k] * t, Math.sin(endAngle[k]) * endLen[k] * t);
    const a2 = v2(Math.cos(endAngle[k2]) * endLen[k2] * t, Math.sin(endAngle[k2]) * endLen[k2] * t);
    const midA = (endAngle[k] + endAngle[k2]) * 0.5;
    const c = v2(Math.cos(midA) * endLen[k] * mt * 0.8, Math.sin(midA) * endLen[k] * mt * 0.8);
    lateralP0[k] = a1; lateralC[k] = c; lateralP1[k] = a2; lateralLive[k] = true;
  }
  const lateralSamples = new Array(n);
  for (let k = 0; k < n; k++) if (lateralLive[k]) lateralSamples[k] = sampleQuadratic(lateralP0[k], lateralC[k], lateralP1[k], 12);

  const coneStops = [[0, rgba(246, 252, 255, 0.8)], [0.55, rgba(240, 249, 255, 0.35)], [1, rgba(240, 249, 255, 0)]];
  const coneR0 = 1.5, coneR1 = 11;
  const holePoly = radialPoly(12, () => 4.2 * (0.82 + rng() * 0.36));          // :408-412
  const holeColor = rgba(10, 12, 15, 0.92);

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x - 96 + 0.5, y - 96 + 0.5);
      let c = { ...TRANSPARENT };
      for (let k = 0; k < n; k++) {
        if (distToPolyline(p, mainPts[k]) < mainWidth[k] * 0.5) c = over(c, mainColor[k]);
        if (lateralLive[k] && distToPolyline(p, lateralSamples[k]) < 0.3) c = over(c, lateralColor[k]);
      }
      const r = Math.hypot(p.x, p.y);
      if (r <= 11) c = over(c, lerp3((r - coneR0) / Math.max(1e-5, coneR1 - coneR0), coneStops));
      if (pointInPolygon(p, holePoly)) c = over(c, holeColor);
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/guns.js:90-124 makeHoleTexture — 64x64, NOT translated (absolute 32+... coords).
// ===========================================================================
function drawHole(buf, w, h, rng) {
  const center = v2(32, 32);

  const dabCount = 26;
  const dabPos = new Array(dabCount), dabR = new Array(dabCount), dabColor = new Array(dabCount);
  for (let k = 0; k < dabCount; k++) {
    const a = rng() * Math.PI * 2;                          // :96
    const r = 10 + rng() * 14;                                // :97
    dabColor[k] = rgba(232, 226, 214, 0.3 + rng() * 0.3);      // :98
    dabPos[k] = v2(center.x + Math.cos(a) * r, center.y + Math.sin(a) * r);
    dabR[k] = 2 + rng() * 3.5;                                  // :100
  }

  const pitStops = [[0, rgba(12, 10, 9, 0.95)], [0.55, rgba(28, 24, 22, 0.75)], [1, rgba(40, 36, 32, 0)]];
  const pitR0 = 1, pitR1 = 13, pitFillR = 13;

  const crackCount = 7;
  const crackA = new Array(crackCount), crackB = new Array(crackCount), crackWidth = new Array(crackCount);
  for (let k = 0; k < crackCount; k++) {
    const a = rng() * Math.PI * 2;                           // :113
    crackWidth[k] = 1 + rng();                                 // :115
    const r1 = 14 + rng() * 8;                                  // :118
    crackA[k] = v2(center.x + Math.cos(a) * 6, center.y + Math.sin(a) * 6);
    crackB[k] = v2(center.x + Math.cos(a) * r1, center.y + Math.sin(a) * r1);
  }
  const crackColor = rgba(20, 17, 15, 0.55);

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x + 0.5, y + 0.5);
      let c = { ...TRANSPARENT };
      for (let k = 0; k < dabCount; k++) if (inCircle(p, dabPos[k], dabR[k])) c = over(c, dabColor[k]);
      const r = Math.hypot(p.x - center.x, p.y - center.y);
      if (r <= pitFillR) c = over(c, lerp3((r - pitR0) / Math.max(1e-5, pitR1 - pitR0), pitStops));
      for (let k = 0; k < crackCount; k++) if (distToSegment(p, crackA[k], crackB[k]) < crackWidth[k] * 0.5) c = over(c, crackColor);
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// src/guns.js:126-171 makeCrackTexture — 96x96, translated to (48,48).
// ===========================================================================
function drawCrack(buf, w, h, rng) {
  const rays = 9;
  const rayA = new Array(rays), rayMid = new Array(rays), rayB = new Array(rays);
  const branchA = new Array(rays), branchB = new Array(rays), rayColor = new Array(rays);
  for (let k = 0; k < rays; k++) {
    const a = (k / rays) * Math.PI * 2 + rng() * 0.5;         // :134
    const len = 26 + rng() * 18;                                // :135
    rayColor[k] = rgba(235, 245, 255, 0.5 + rng() * 0.3);        // :136
    const x = Math.cos(a) * len, y = Math.sin(a) * len;
    const jx = (rng() - 0.5) * 6, jy = (rng() - 0.5) * 6;        // :142 (x-jitter, then y-jitter)
    rayA[k] = v2(Math.cos(a) * 3, Math.sin(a) * 3);
    rayMid[k] = v2(x * 0.55 + jx, y * 0.55 + jy);
    rayB[k] = v2(x, y);
    branchA[k] = v2(x * 0.55, y * 0.55);
    branchB[k] = v2(branchA[k].x + Math.cos(a + 0.9) * 9, branchA[k].y + Math.sin(a + 0.9) * 9);
  }
  const rayHalfW = 1.4 * 0.5, branchHalfW = 0.8 * 0.5;

  const ringCount = 2, baseR = [8, 15];
  const ringR = new Array(ringCount);
  for (let k = 0; k < ringCount; k++) ringR[k] = baseR[k] + rng() * 2;        // :157
  const ringColor = rgba(225, 238, 250, 0.4);
  const ringLineWidth = 0.9;

  const centerStops = [[0, rgba(10, 12, 14, 0.9)], [1, rgba(10, 12, 14, 0)]];
  const centerR0 = 0, centerR1 = 5;

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = v2(x - 48 + 0.5, y - 48 + 0.5);
      let c = { ...TRANSPARENT };
      for (let k = 0; k < rays; k++) {
        if (distToSegment(p, rayA[k], rayMid[k]) < rayHalfW || distToSegment(p, rayMid[k], rayB[k]) < rayHalfW) c = over(c, rayColor[k]);
        if (distToSegment(p, branchA[k], branchB[k]) < branchHalfW) c = over(c, rayColor[k]);
      }
      for (let k = 0; k < ringCount; k++) if (onRing(p, v2(0, 0), ringR[k], ringLineWidth)) c = over(c, ringColor);
      const r = Math.hypot(p.x, p.y);
      if (r <= centerR1) c = over(c, lerp3((r - centerR0) / Math.max(1e-5, centerR1 - centerR0), centerStops));
      blend(buf, w, h, x, y, c);
    }
  }
}

// ===========================================================================
// main — same default seeds as Combat/ImpactDecalTextures.cs /
// Combat/GunDecalTextures.cs's BuildX(uint seed = N) defaults.
// ===========================================================================
function run(id, w, h, seed, draw) {
  const buf = makeBuffer(w, h);
  draw(buf, w, h, makeRng32(seed));
  quantize8(buf);
  const f = analyze(buf, w, h);
  console.log(
    `DAMAGETEX id=${id} w=${f.w} h=${f.h} opaqueFrac=${f.opaqueFrac.toFixed(4)} alphaZeroFrac=${f.alphaZeroFrac.toFixed(4)} ` +
    `bboxMinX=${f.bboxMinX.toFixed(4)} bboxMinY=${f.bboxMinY.toFixed(4)} bboxMaxX=${f.bboxMaxX.toFixed(4)} bboxMaxY=${f.bboxMaxY.toFixed(4)} ` +
    `centerAlpha=${f.centerAlpha.toFixed(4)} edgeAlpha=${f.edgeAlpha.toFixed(4)} ` +
    `meanR=${f.meanR.toFixed(4)} meanG=${f.meanG.toFixed(4)} meanB=${f.meanB.toFixed(4)}`
  );
}

run('pit', 96, 96, 1001, drawPit);
run('spall', 128, 128, 1002, drawSpall);
run('exit', 96, 96, 1003, drawExit);
run('scuff', 160, 64, 1004, drawScuff);
run('star', 192, 192, 1005, drawStar);
run('gunhole', 64, 64, 2001, drawHole);
run('guncrack', 96, 96, 2002, drawCrack);
