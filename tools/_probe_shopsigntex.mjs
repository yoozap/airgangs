// Ground truth for the Unity port's unity/Assets/Scripts/City/ShopSignTextures.cs
// — the Canvas2D PIXEL half of src/shopfronts.js's draw()/drawBrand()
// (508-606) and the whole of drawMark() (418-506) that SignPool.cs's own
// header left uncovered (decision-only: DrawSpec.MarkKind/MarkColor/MarkAccent).
//
// Node has no <canvas>, so this is a from-scratch REIMPLEMENTATION of the
// same per-pixel maths the C# port uses (point-in-polygon / distance-to-
// segment / arc-membership / quadratic-curve sampling instead of canvas
// rasterization), following the exact precedent of tools/_probe_damagetex.mjs
// for Combat/DamageTextures.cs. Prints `SHOPSIGN id=<case> key=value` lines
// with the SAME key names Assets/Editor/Probes/ShopSignTextureProbe.cs prints,
// for tools/unity_port_check.py (or manual diff) to compare.
//
// IMPORTANT (DamageTextureProbe's own lesson): both ground truths are 8-bit
// (browser Uint8ClampedArray canvas pixels / Unity RGBA32), so this probe
// quantises every pixel to 8-bit BEFORE computing facts — compositing in
// float64 and analysing that would make THIS side the inaccurate one.
//
// Run: node tools/_probe_shopsigntex.mjs

const W = 512, H = 80;

// ---------------------------------------------------------------------------
// Compositing / colour — matches Combat/DamageTextures.cs's Over/Lerp3.
// ---------------------------------------------------------------------------
function over(dst, src) {
  const a = src.a + dst.a * (1 - src.a);
  if (a <= 0.0005) return { r: 0, g: 0, b: 0, a: 0 };
  return {
    r: (src.r * src.a + dst.r * (dst.a * (1 - src.a))) / a,
    g: (src.g * src.a + dst.g * (dst.a * (1 - src.a))) / a,
    b: (src.b * src.a + dst.b * (dst.a * (1 - src.a))) / a,
    a,
  };
}
function clamp01(x) { return x < 0 ? 0 : x > 1 ? 1 : x; }
function lerpColor(c0, c1, t) {
  return { r: c0.r + (c1.r - c0.r) * t, g: c0.g + (c1.g - c0.g) * t, b: c0.b + (c1.b - c0.b) * t, a: c0.a + (c1.a - c0.a) * t };
}
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
function hexToColor(hex, fallback) {
  let s = hex || fallback;
  if (!s || s[0] !== '#') return { r: 1, g: 1, b: 1, a: 1 };
  if (s.length === 4) s = '#' + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
  const n = parseInt(s.slice(1), 16);
  return { r: ((n >> 16) & 255) / 255, g: ((n >> 8) & 255) / 255, b: (n & 255) / 255, a: 1 };
}

// ---------------------------------------------------------------------------
// Geometry primitives — matches DamageTextures.cs 1:1.
// ---------------------------------------------------------------------------
function distToSegment(p, a, b) {
  const abx = b.x - a.x, aby = b.y - a.y;
  const len2 = abx * abx + aby * aby;
  const t = len2 > 1e-9 ? clamp01(((p.x - a.x) * abx + (p.y - a.y) * aby) / len2) : 0;
  const px = a.x + abx * t, py = a.y + aby * t;
  return Math.hypot(p.x - px, p.y - py);
}
function distToPolyline(p, poly) {
  let best = Infinity;
  for (let i = 0; i + 1 < poly.length; i++) best = Math.min(best, distToSegment(p, poly[i], poly[i + 1]));
  return best;
}
function pointInPolygon(p, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i], b = poly[j];
    if (((a.y > p.y) !== (b.y > p.y)) && (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)) inside = !inside;
  }
  return inside;
}
function inCircle(p, c, r) { const dx = p.x - c.x, dy = p.y - c.y; return dx * dx + dy * dy <= r * r; }
function onArc(p, radius, a0, span, width) { // p already relative to the arc's own centre
  const r = Math.hypot(p.x, p.y);
  if (Math.abs(r - radius) > width * 0.5) return false;
  const a = Math.atan2(p.y, p.x);
  let d = a - a0;
  d -= Math.floor(d / (Math.PI * 2)) * Math.PI * 2;
  return d <= Math.abs(span);
}
function quadPoint(p0, c, p1, t) {
  const u = 1 - t;
  return { x: u * u * p0.x + 2 * u * t * c.x + t * t * p1.x, y: u * u * p0.y + 2 * u * t * c.y + t * t * p1.y };
}
function sampleQuadratic(p0, c, p1, steps) {
  const pts = [];
  for (let i = 0; i <= steps; i++) pts.push(quadPoint(p0, c, p1, i / steps));
  return pts;
}
function leafPoly(p0, c0, p1, c1) {
  const a = sampleQuadratic(p0, c0, p1, 16);
  const b = sampleQuadratic(p1, c1, p0, 16);
  return a.concat(b.slice(1, b.length - 1));
}

// ---------------------------------------------------------------------------
// Paint buffer — mirrors ShopSignTextures.cs's PaintBuffer/PaintMark exactly.
// ---------------------------------------------------------------------------
function fillRect(buf, x0, y0, w, h, c) {
  const x1 = Math.min(W, x0 + w), y1 = Math.min(H, y0 + h);
  x0 = Math.max(0, x0); y0 = Math.max(0, y0);
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) { const i = y * W + x; buf[i] = over(buf[i], c); }
}
function strokeRect(buf, x, y, w, h, lineWidth, c) {
  const half = lineWidth * 0.5;
  const x0 = Math.max(0, Math.floor(x - half)), x1 = Math.min(W, Math.ceil(x + w + half));
  const y0 = Math.max(0, Math.floor(y - half)), y1 = Math.min(H, Math.ceil(y + h + half));
  const tl = { x, y }, tr = { x: x + w, y }, bl = { x, y: y + h }, br = { x: x + w, y: y + h };
  for (let py = y0; py < y1; py++) {
    for (let px = x0; px < x1; px++) {
      const p = { x: px + 0.5, y: py + 0.5 };
      const d = Math.min(Math.min(distToSegment(p, tl, tr), distToSegment(p, bl, br)), Math.min(distToSegment(p, tl, bl), distToSegment(p, tr, br)));
      if (d <= half) { const i = py * W + px; buf[i] = over(buf[i], c); }
    }
  }
}
function applyLitGradient(buf) {
  const stops = [[0, { r: 1, g: 1, b: 1, a: 0.16 }], [0.45, { r: 1, g: 1, b: 1, a: 0 }], [1, { r: 0, g: 0, b: 0, a: 0.20 }]];
  for (let y = 0; y < H; y++) {
    const t = y / (H - 1);
    const src = lerp3(t, stops);
    for (let x = 0; x < W; x++) { const i = y * W + x; buf[i] = over(buf[i], src); }
  }
}
function scanFill(buf, x0, y0, x1, y1, test, c) {
  for (let py = y0; py < y1; py++) for (let px = x0; px < x1; px++) if (test(px + 0.5, py + 0.5)) { const i = py * W + px; buf[i] = over(buf[i], c); }
}
function paintMark(buf, kind, cx, cy, r, color, accent) {
  const x0 = Math.max(0, Math.floor(cx - r * 1.3)), x1 = Math.min(W, Math.ceil(cx + r * 1.3));
  const y0 = Math.max(0, Math.floor(cy - r * 1.3)), y1 = Math.min(H, Math.ceil(cy + r * 1.3));
  const lineWidth = Math.max(2, r * 0.22);
  switch (kind) {
    case 'cross': {
      const inV = (px, py) => px >= cx - r * 0.28 && px <= cx + r * 0.28 && py >= cy - r && py <= cy + r;
      const inH = (px, py) => px >= cx - r && px <= cx + r && py >= cy - r * 0.28 && py <= cy + r * 0.28;
      scanFill(buf, x0, y0, x1, y1, (px, py) => inV(px, py) || inH(px, py), color);
      break;
    }
    case 'fir': {
      for (let i = 0; i < 3; i++) {
        const t = -r + i * r * 0.62, w = r * (0.55 + i * 0.28);
        const tri = [{ x: cx, y: cy + t }, { x: cx + w, y: cy + t + r * 0.78 }, { x: cx - w, y: cy + t + r * 0.78 }];
        scanFill(buf, x0, y0, x1, y1, (px, py) => pointInPolygon({ x: px, y: py }, tri), color);
      }
      const trunk = accent || color;
      fillRect(buf, Math.round(cx - r * 0.14), Math.round(cy + r * 0.72), Math.round(r * 0.28), Math.round(r * 0.34), trunk);
      break;
    }
    case 'leaf': {
      const poly = leafPoly({ x: cx, y: cy + r }, { x: cx + r * 1.1, y: cy + r * 0.1 }, { x: cx, y: cy - r }, { x: cx - r * 1.1, y: cy + r * 0.1 });
      scanFill(buf, x0, y0, x1, y1, (px, py) => pointInPolygon({ x: px, y: py }, poly), color);
      break;
    }
    case 'drop': {
      const poly = leafPoly({ x: cx, y: cy - r }, { x: cx + r * 0.95, y: cy + r * 0.2 }, { x: cx, y: cy + r }, { x: cx - r * 0.95, y: cy + r * 0.2 });
      scanFill(buf, x0, y0, x1, y1, (px, py) => pointInPolygon({ x: px, y: py }, poly), color);
      break;
    }
    case 'circle': {
      const c0 = { x: cx, y: cy };
      scanFill(buf, x0, y0, x1, y1, (px, py) => inCircle({ x: px, y: py }, c0, r * 0.92), color);
      if (accent) scanFill(buf, x0, y0, x1, y1, (px, py) => inCircle({ x: px, y: py }, c0, r * 0.5), accent);
      break;
    }
    case 'chevron': {
      for (let i = 0; i < 2; i++) {
        const off = i * r * 0.85;
        const poly = [{ x: cx - r + off, y: cy - r }, { x: cx + r * 0.1 + off, y: cy }, { x: cx - r + off, y: cy + r }];
        const strokeColor = (i === 1 && accent) ? accent : color;
        scanFill(buf, x0, y0, x1, y1, (px, py) => distToPolyline({ x: px, y: py }, poly) <= lineWidth * 0.5, strokeColor);
      }
      break;
    }
    case 'arch': {
      const archLw = r * 0.34;
      const c1 = { x: cx - r * 0.42, y: cy + r * 0.5 }, c2 = { x: cx + r * 0.42, y: cy + r * 0.5 };
      const rr = r * 0.55;
      scanFill(buf, x0, y0, x1, y1, (px, py) =>
        onArc({ x: px - c1.x, y: py - c1.y }, rr, Math.PI, Math.PI, archLw) ||
        onArc({ x: px - c2.x, y: py - c2.y }, rr, Math.PI, Math.PI, archLw), color);
      break;
    }
    case 'star': {
      const poly = [];
      for (let i = 0; i < 10; i++) {
        const a = i * Math.PI / 5 - Math.PI / 2;
        const rr = (i % 2) ? r * 0.45 : r;
        poly.push({ x: cx + Math.cos(a) * rr, y: cy + Math.sin(a) * rr });
      }
      scanFill(buf, x0, y0, x1, y1, (px, py) => pointInPolygon({ x: px, y: py }, poly), color);
      break;
    }
    case 'square': {
      fillRect(buf, Math.round(cx - r * 0.85), Math.round(cy - r * 0.85), Math.round(r * 1.7), Math.round(r * 1.7), color);
      if (accent) fillRect(buf, Math.round(cx - r * 0.4), Math.round(cy - r * 0.4), Math.round(r * 0.8), Math.round(r * 0.8), accent);
      break;
    }
    default: break;
  }
}

function paintBuffer(spec) {
  const buf = new Array(W * H).fill(null).map(() => ({ r: 0, g: 0, b: 0, a: 0 }));
  const bg = hexToColor(spec.bg, '#555555');
  if (spec.isNamedPlate) {
    fillRect(buf, 0, 0, W, H, bg);
    strokeRect(buf, 9, 9, 494, 62, 2, { r: 214 / 255, g: 182 / 255, b: 110 / 255, a: 0.75 });
    return buf;
  }
  if (spec.hasBrand) {
    fillRect(buf, 0, 0, W, H, bg);
    applyLitGradient(buf);
    if (spec.markKind && spec.markKind !== 'none') {
      const markColor = hexToColor(spec.markColor, '#ffffff');
      const markAccent = spec.markAccent ? hexToColor(spec.markAccent, '#000000') : null;
      paintMark(buf, spec.markKind, 52, 40, 26, markColor, markAccent);
    }
    return buf;
  }
  fillRect(buf, 0, 0, W, H, bg);
  if (spec.isCivic) {
    const frame = spec.lightBg ? hexToColor('#1a3a6e') : { r: 1, g: 1, b: 1, a: 0.8 };
    strokeRect(buf, 6, 6, 500, 68, 3, frame);
    strokeRect(buf, 12, 12, 488, 56, 3, frame);
  } else {
    fillRect(buf, 0, 66, W, 14, { r: 1, g: 1, b: 1, a: 0.14 });
  }
  return buf;
}

// Quantise to 8-bit (Uint8ClampedArray-equivalent) BEFORE analysis — the
// DamageTextureProbe lesson restated in this file's own header.
function quantize(buf) {
  return buf.map(c => ({
    r: Math.round(clamp01(c.r) * 255) / 255,
    g: Math.round(clamp01(c.g) * 255) / 255,
    b: Math.round(clamp01(c.b) * 255) / 255,
    a: Math.round(clamp01(c.a) * 255) / 255,
  }));
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
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
        sumR += c.r; sumG += c.g; sumB += c.b; inkN++;
      }
    }
  }
  const total = w * h;
  const f = {
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

// ---------------------------------------------------------------------------
// Test cases — real brand-book rows (public/tbilisi/brands.json, one per
// drawMark kind) + the named-plate / civic(x2) / plain CAT_STYLE cases.
// ---------------------------------------------------------------------------
const CASES = {
  mark_fir: { hasBrand: true, bg: '#EC1B24', fg: '#FFFFFF', markKind: 'fir', markColor: '#157946', markAccent: '#EC1B24' }, // SPAR
  mark_square: { hasBrand: true, bg: '#12256B', fg: '#F58220', markKind: 'square', markColor: '#FFFFFF', markAccent: '#12256B' }, // Libre
  mark_drop: { hasBrand: true, bg: '#FFFFFF', fg: '#E31E24', markKind: 'drop', markColor: '#E31E24', markAccent: '#FFFFFF' }, // Aversi (no accent -> markColor=fg)
  mark_leaf: { hasBrand: true, bg: '#0FB061', fg: '#FFFFFF', markKind: 'leaf', markColor: '#FFFFFF', markAccent: '#0FB061' }, // IOLI
  mark_circle: { hasBrand: true, bg: '#064287', fg: '#FFFFFF', markKind: 'circle', markColor: '#F2682B', markAccent: '#064287' }, // Gulf
  mark_cross: { hasBrand: true, bg: '#33BEC4', fg: '#FFFFFF', markKind: 'cross', markColor: '#FFFFFF', markAccent: '#33BEC4' }, // Pharm House
  mark_arch: { hasBrand: true, bg: '#DA291C', fg: '#FFFFFF', markKind: 'arch', markColor: '#FFC72C', markAccent: '#DA291C' }, // McDonald's
  mark_star: { hasBrand: true, bg: '#111214', fg: '#FFFFFF', markKind: 'star', markColor: '#DA0E81', markAccent: '#111214' }, // navne
  mark_chevron: { hasBrand: true, bg: '#FFFFFF', fg: '#004A97', markKind: 'chevron', markColor: '#E3000F', markAccent: '#FFFFFF' }, // Carrefour Market
  mark_none: { hasBrand: true, bg: '#FFFFFF', fg: '#000000', markKind: 'none' }, // Liberty Bank
  named_plate: { isNamedPlate: true, bg: '#2b2723' },
  civic_light: { isCivic: true, lightBg: true, bg: '#f4f1e8' }, // school
  civic_dark: { isCivic: true, lightBg: false, bg: '#1a3a6e' }, // police
  plain_fascia: { bg: '#1c7a3c' }, // supermarket
};

// One line per case, every key on it — matches the project convention
// (DamageTextureProbe.cs / tools/_probe_damagetex.mjs's own `DAMAGETEX id=...`
// single-line shape) so the two sides' lines pair up exactly, not just their
// key sets (see this task's own "two trivia" warning on shape mismatches).
for (const [id, spec] of Object.entries(CASES)) {
  const raw = paintBuffer(spec);
  const buf = quantize(raw);
  const f = analyze(buf, W, H);
  console.log(
    `SHOPSIGN id=${id} w=${W} h=${H} opaqueFrac=${f.opaqueFrac.toFixed(4)} alphaZeroFrac=${f.alphaZeroFrac.toFixed(4)} ` +
    `bboxMinX=${f.bboxMinX.toFixed(4)} bboxMinY=${f.bboxMinY.toFixed(4)} bboxMaxX=${f.bboxMaxX.toFixed(4)} bboxMaxY=${f.bboxMaxY.toFixed(4)} ` +
    `centerAlpha=${f.centerAlpha.toFixed(4)} edgeAlpha=${f.edgeAlpha.toFixed(4)} ` +
    `meanR=${f.meanR.toFixed(4)} meanG=${f.meanG.toFixed(4)} meanB=${f.meanB.toFixed(4)}`
  );
}
