import * as THREE from 'three';

// PAPER TARGETS — the card, the holes in it, and the score.
//
// The range already had paper hanging in it (src/airport.js) and the rounds
// that reach it are real rounds (src/ballistics.js). What was missing is the
// only part a shooter actually looks at: a scoring face, and a HOLE where the
// bullet went. Both are real here rather than suggested:
//
//  * THE CARD is the ISSF 25 m precision pistol target, by its own numbers:
//    500 mm square, the 10 ring 50 mm across, every ring out 50 mm wider than
//    the last, and rings 7 to 10 filled black as the aiming mark (200 mm).
//    Those are the dimensions on the real card, so a group measured on this one
//    means what it means on a range.
//
//  * THE HOLE is the bullet's own diameter -- 9.27 mm for a Makarov, 9.01 for a
//    Beretta -- drawn into the card's own texture at the point the round passed
//    through. Not a decal quad: paper takes dozens of holes and each one has to
//    be in the right place to a millimetre, so the card owns a canvas and the
//    holes are painted into it. A round arriving at a slant cuts an OVAL, wider
//    by 1/cos of the angle, which is exactly what a canted hole looks like.
//    Each one gets the grey BULLET WIPE a real hole has (the ring of lube and
//    bore fouling the bullet leaves as it passes) and a torn fibre edge.
//
//  * THE SCORE uses the scoring rule, not the obvious one: a shot counts the
//    higher ring if the HOLE BREAKS THE LINE -- so it is scored on the hole's
//    inner edge (r - bullet radius), which is what a plug gauge measures. It is
//    worth a point a shot and it is the difference between a game and a range.
//
// Group size is the extreme spread: the distance between the two furthest holes,
// centre to centre, which is how a group is measured everywhere. Mean radius is
// there too, because it is the honest one for more than three shots.
const CARD_MM = 500;        // mm — the real card is 500 x 500
const RING_MM = 50;         // mm — the 10 ring, and every ring out is +50 wide
const BLACK_RING = 7;       // rings 7..10 are the black aiming mark
const PX = 1024;            // canvas pixels across the card (~0.49 mm/px)
// A canvas this size is 4 MB, and the range hangs forty sheets. So the BLANK
// face is painted ONCE and shared by every card, and a card only gets a canvas
// of its own when it takes its first hole -- which is the only time its face
// stops being identical to every other one. The few most recently shot cards
// keep theirs; older ones go back to the shared blank, holes and all, because a
// range that remembers forty cards is a range that costs 190 MB to stand in.
const LIVE_CARDS = 6;
const INNER_TEN = 25;       // mm — the inner ten, for counting Xs
const MAX_SHOTS = 400;      // per card, before the oldest stop being counted

const RINGS = 10;
const mmToPx = PX / CARD_MM;

// THE FACE THE RANGE ALREADY PRINTS. The sheets come with a B-27 police
// silhouette on them -- head and shoulders in black, X / 9 / 8 / 7 rings over
// the chest, a red X ring, three small bullseyes -- and that face is the target.
// It is not replaced: a card keeps its own printed art and the holes are punched
// into a copy of it, which is the whole difference between shooting at a target
// and shooting at a wall with a drawing on it.
//
// These numbers were measured off that texture rather than guessed: the X ring's
// centre and half-axes as fractions of the image, and the printed rings as
// multiples of those axes, read along four diagonals (the printed numerals sit
// at the cardinals and would be mistaken for ring lines).
export const B27 = {
  cu: 0.4993, cv: 0.3590,      // the X ring's centre, in texture coordinates
  au: 0.0469, av: 0.0703,      // and its half-axes
  rings: [[1.16, 10], [2.33, 9], [3.50, 8], [4.70, 7]],
  body: 5,                     // anywhere else on the silhouette
};

export class PaperTargets {
  constructor(game) {
    this.game = game;
    this.sheets = new Map();   // mesh -> card
    this.active = null;        // the card last shot at
    this._v = new THREE.Vector3();
    this._a = new THREE.Vector3();
    this._b = new THREE.Vector3();
    this._hud = null;
    this._hide = 0;
  }

  // ---- the card ----------------------------------------------------------
  // Adopted from the range: the sheet keeps its own geometry and its hinge, and
  // trades its material for a target face it owns outright.
  adopt(mesh) {
    if (!mesh?.isMesh || this.sheets.has(mesh)) return null;
    const geo = mesh.geometry;
    if (!geo) return null;
    geo.computeBoundingBox();
    const size = geo.boundingBox.getSize(new THREE.Vector3());
    // The sheet is a flat quad in SOME pair of its own axes: the thinnest one is
    // the paper's thickness, the other two are its width and height. Which of
    // those two is "up" is a question for the world, not for the mesh.
    const axes = [0, 1, 2].sort((a, b) => size.getComponent(a) - size.getComponent(b));
    const flat = axes[0];
    const plane = [axes[1], axes[2]];
    // THE SHEET'S REAL SIZE, not its local one. These sheets come in at 56 x 89
    // of the model's own units and hang in the world at about 0.6 x 0.9 m: the
    // scale is up the node chain, and a card measured in the wrong units puts
    // every hole off the edge of its own face.
    mesh.updateWorldMatrix(true, false);
    const ws = new THREE.Vector3();
    mesh.matrixWorld.decompose(new THREE.Vector3(), new THREE.Quaternion(), ws);
    const world = (ax) => Math.abs(size.getComponent(ax) * ws.getComponent(ax));
    // which of the two in-plane axes hangs vertically in the world decides which
    // way up the card is printed
    const dir = (ax) => this._v.setFromMatrixColumn(mesh.matrixWorld, ax).normalize();
    const vert = Math.abs(dir(plane[0]).y) > Math.abs(dir(plane[1]).y) ? 0 : 1;
    const upAx = plane[vert];
    const rightAx = plane[1 - vert];
    const W = world(rightAx) || 0.5;
    const H = world(upAx) || 0.5;
    const card = {
      mesh, canvas: null, ctx: null, tex: null,
      flat, plane, size, upAx, rightAx, W, H,
      shots: [], score: 0, tens: 0, spread: 0,
      // The 500 mm card, printed as large as the sheet will take it. On a bigger
      // sheet it sits in the middle of plain paper, exactly as a real card does
      // on a backer; on a smaller one the whole thing shrinks and KEEPS ITS
      // PROPORTIONS, so a ring is still a tenth of the card and the millimetres
      // reported are the millimetres on the paper in front of you.
      cardMM: Math.min(CARD_MM, W * 1000, H * 1000),
      cw: PX,
      ch: Math.max(64, Math.round(PX * H / W)),
    };
    card.pxPerMM = card.cw / (W * 1000);
    // THE SHEET KEEPS ITS OWN FACE. The printed silhouette is the target, so it
    // is left exactly as it is until a round goes through it -- which also means
    // an unshot card costs nothing at all, no canvas and no texture.
    const printed = mesh.material?.map?.image ? mesh.material : null;
    card.printed = printed;
    card.face = printed?.map?.image ?? null;
    if (!printed) mesh.material = this._blankMaterial(card);
    // The card is kept in the map and NOWHERE ELSE. three deep-copies userData
    // through JSON on every Object3D.clone(), so a card parked there -- holding a
    // reference back to its own mesh -- makes cloning that sheet throw on a
    // circular structure. userData takes the surface tag and nothing else.
    mesh.userData.surface = 'paper';   // ballistics.js: a round goes straight through
    this.sheets.set(mesh, card);
    return card;
  }

  // The blank face every unshot card wears: one canvas, one texture, one
  // material, shared by the lot of them.
  // Sheets of the same shape share one blank -- the range hangs ten of a kind.
  _blankMaterial(card) {
    const key = `${card.cw}x${card.ch}x${Math.round(card.cardMM)}`;
    this._blanks ??= new Map();
    // The card has to remember WHICH blank it is wearing on both paths. The
    // range hangs ten sheets of a kind and the first one to ask builds the
    // shared blank; every other one took this early return and never recorded
    // it, so the first round through any card but the first threw on
    // `card.blank.canvas` -- inside the bullet resolve, which killed the rest
    // of that frame's rounds with it. That is what "lag when I shoot" was.
    const had = this._blanks.get(key);
    if (had) {
      card.blank = had;
      return had.mat;
    }
    const canvas = document.createElement('canvas');
    canvas.width = card.cw;
    canvas.height = card.ch;
    const blank = { canvas, ctx: canvas.getContext('2d') };
    this._paint(card, blank.ctx);
    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.anisotropy = 8;
    blank.tex = tex;
    blank.mat = new THREE.MeshStandardMaterial({
      map: tex, roughness: 0.96, metalness: 0, side: THREE.DoubleSide,
    });
    this._blanks.set(key, blank);
    card.blank = blank;
    return blank.mat;
  }

  // The first round through this card: it stops being one of the identical
  // blanks and gets a face of its own to keep its holes in.
  _goLive(card) {
    if (card.canvas) return card;
    const canvas = document.createElement('canvas');
    const src = card.face;
    if (src) {
      // the printed face, copied pixel for pixel. The holes go into the COPY, so
      // the silhouette every other sheet is wearing is never touched.
      canvas.width = Math.max(512, src.width || 512);
      canvas.height = Math.round(canvas.width * ((src.height || 512) / (src.width || 512)));
    } else {
      canvas.width = card.cw;
      canvas.height = card.ch;
    }
    const ctx = canvas.getContext('2d');
    if (src) ctx.drawImage(src, 0, 0, canvas.width, canvas.height);
    else {
      this._blankMaterial(card);
      ctx.drawImage(card.blank.canvas, 0, 0);
    }
    card.canvas = canvas;
    card.ctx = ctx;
    card.tex = new THREE.CanvasTexture(canvas);
    const old = card.printed?.map;
    if (old) {
      // whatever the printed texture did -- flipped, wrapped, offset -- the copy
      // has to do too, or the face lands on the sheet sideways
      card.tex.flipY = old.flipY;
      card.tex.wrapS = old.wrapS;
      card.tex.wrapT = old.wrapT;
      card.tex.offset.copy(old.offset);
      card.tex.repeat.copy(old.repeat);
      card.tex.center.copy(old.center);
      card.tex.rotation = old.rotation;
    }
    card.tex.colorSpace = THREE.SRGBColorSpace;
    card.tex.anisotropy = 8;
    card.mesh.material = card.printed
      ? Object.assign(card.printed.clone(), { map: card.tex })
      : new THREE.MeshStandardMaterial({
        map: card.tex, roughness: 0.96, metalness: 0, side: THREE.DoubleSide,
      });
    this._live ??= [];
    this._live.push(card);
    while (this._live.length > LIVE_CARDS) this._retire(this._live.shift());
    return card;
  }

  // Back to a blank: the oldest shot-at card hands its canvas back.
  _retire(card) {
    if (!card?.canvas) return;
    const live = card.mesh.material;
    card.mesh.material = card.printed ?? card.blank.mat;
    if (live !== card.printed && live !== card.blank?.mat) live.dispose?.();
    card.tex.dispose();
    card.canvas = null;
    card.ctx = null;
    card.tex = null;
    card.shots.length = 0;
    card.score = 0;
    card.tens = 0;
    card.spread = 0;
    if (this.active === card) this.active = null;
  }

  // The face, drawn once (and again on a reset). Plain white card, the black
  // aiming mark, the ring lines and the numbers that go in the white rings.
  _paint(card, ctx = card.ctx) {
    const c = ctx;
    const cx = card.cw / 2;
    const cy = card.ch / 2;
    const k = card.pxPerMM * (card.cardMM / CARD_MM); // px per nominal card mm
    c.fillStyle = '#efece4';            // paper, not screen white
    c.fillRect(0, 0, card.cw, card.ch);
    // paper grain, so it reads as card instead of as a flat fill
    c.save();
    c.globalAlpha = 0.05;
    for (let i = 0; i < 2600; i++) {
      c.fillStyle = Math.random() < 0.5 ? '#000' : '#fff';
      c.fillRect(Math.random() * card.cw, Math.random() * card.ch, 2, 1);
    }
    c.restore();
    const rPx = (ring) => (RING_MM / 2 + (RINGS - ring) * (RING_MM / 2)) * k;
    // the black
    c.beginPath();
    c.arc(cx, cy, rPx(BLACK_RING), 0, Math.PI * 2);
    c.fillStyle = '#15161a';
    c.fill();
    // rings: white lines inside the black, black lines outside it
    c.lineWidth = Math.max(1.5, 0.6 * k);
    for (let ring = 1; ring <= RINGS; ring++) {
      const r = rPx(ring);
      if (r > Math.max(cx, cy)) continue;
      c.beginPath();
      c.arc(cx, cy, r, 0, Math.PI * 2);
      c.strokeStyle = ring >= BLACK_RING ? 'rgba(255,255,255,0.85)' : 'rgba(20,20,24,0.9)';
      c.stroke();
    }
    // the inner ten, the one that decides a tie
    c.beginPath();
    c.arc(cx, cy, (INNER_TEN / 2) * k, 0, Math.PI * 2);
    c.strokeStyle = 'rgba(255,255,255,0.75)';
    c.lineWidth = Math.max(1, 0.4 * k);
    c.stroke();
    // numbers, in the white rings, at the four cardinals as on the real card
    c.fillStyle = '#15161a';
    c.font = `600 ${Math.round(13 * k)}px system-ui, sans-serif`;
    c.textAlign = 'center';
    c.textBaseline = 'middle';
    for (let ring = 1; ring < BLACK_RING; ring++) {
      const mid = (rPx(ring) + rPx(ring + 1)) / 2;
      for (const [dx, dy] of [[0, -1], [0, 1], [-1, 0], [1, 0]]) {
        const x = cx + dx * mid;
        const y = cy + dy * mid;
        if (x < 10 || y < 10 || x > card.cw - 10 || y > card.ch - 10) continue;
        c.fillText(String(ring), x, y);
      }
    }
    if (card.tex) card.tex.needsUpdate = true;
  }

  // ---- a round goes through ----------------------------------------------
  // `point` is where it passed the paper, `dir` which way it was going. The hole
  // lands where the round actually was, to the millimetre, because that is the
  // entire point of a paper target.
  hit(mesh, point, dir, cart, uv = null) {
    const card = this.sheets.get(mesh);
    if (!card) return null;
    this._goLive(card);
    const m = card.mesh;
    m.updateWorldMatrix(true, false);
    // the sheet's own frame, read fresh: these sheets swing on their top edge
    // the axes the card was printed on, not a fresh guess
    const rx = this._a.setFromMatrixColumn(m.matrixWorld, card.rightAx).normalize();
    const uy = this._b.setFromMatrixColumn(m.matrixWorld, card.upAx).normalize();
    if (uy.y < 0) uy.negate();
    // THE CARD'S CENTRE, through the FULL matrix. The sheet hangs from a pivot
    // at its top edge and the range comes in at 1/100 scale, so the mesh origin
    // is neither the middle of the paper nor in the same units as it: rotating
    // the geometry centre without scaling it put the card's middle 50 m away
    // and every round "missed" a sheet it had gone straight through.
    const centre = card.mesh.localToWorld(
      card.mesh.geometry.boundingBox.getCenter(this._v));
    const off = point.clone().sub(centre);
    const xm = off.dot(rx);
    const ym = off.dot(uy);
    // WHERE ON THE FACE. The ray already worked this out -- its uv is the point
    // in the texture's own coordinates, so it is right however the sheet's UVs
    // are laid out, flipped or rotated. The world-space fallback is for a hit
    // that arrives without one.
    const cw = card.canvas.width;
    const ch = card.canvas.height;
    const flip = card.tex?.flipY !== false;
    const px = uv ? uv.x * cw : cw / 2 + xm * 1000 * card.pxPerMM;
    const py = uv ? (flip ? 1 - uv.y : uv.y) * ch : ch / 2 - ym * 1000 * card.pxPerMM;
    if (px < -40 || py < -40 || px > cw + 40 || py > ch + 40) return null;
    // the bullet's own diameter, and how far it is stretched by the angle it
    // arrived at: a round through the card at 60 degrees cuts a 2:1 oval
    const dmm = (cart?.bullet ?? 9) ;
    const nrm = this._v.copy(rx).cross(uy).normalize();
    const cosI = Math.max(0.2, Math.abs(nrm.dot(dir)));
    const ang = Math.atan2(dir.clone().sub(nrm.clone().multiplyScalar(dir.dot(nrm))).dot(uy),
      dir.clone().sub(nrm.clone().multiplyScalar(dir.dot(nrm))).dot(rx));
    // the hole's size in THIS canvas: the face is stretched over the sheet, so
    // the pixels per millimetre come from the sheet's real width
    const pxPerMM = cw / (card.W * 1000);
    // what is printed under the hole, read before the hole covers it: the black
    // of the silhouette, the red of the X ring, or the white paper around it
    const under = this._sample(card, px, py);
    this._punch(card, px, py, dmm * pxPerMM, 1 / cosI, ang);
    // ---- the score ----
    // Measured in real millimetres on the real card, through the sheet's scale.
    // the real millimetres on the real paper, and the rings as PRINTED on this
    // card (a sheet too small for a 500 mm card gets a smaller one, rings and
    // all, so the number it gives back still means what it says)
    const xmm = xm * 1000;
    const ymm = ym * 1000;
    let ring, inner = false, r;
    if (card.face && uv) {
      // THE PRINTED B-27: scored on its own chest rings, which are ovals and are
      // not centred on the sheet -- they sit where a chest sits. Distance is
      // measured in units of the X ring's own axes, so the oval is handled by
      // the measurement rather than by a special case.
      const u = uv.x;
      const v = flip ? 1 - uv.y : uv.y;
      const k = Math.hypot((u - B27.cu) / B27.au, (v - B27.cv) / B27.av);
      // the line rule, in the same units: half a bullet, as a fraction of an axis
      const bite = (dmm / 2 / 1000) / (B27.au * card.W);
      const edge = Math.max(0, k - bite);
      ring = 0;
      for (const [lim, val] of B27.rings) {
        if (edge <= lim) { ring = val; break; }
      }
      // off the rings: still a hit if it is on the silhouette, a miss if it went
      // through the white paper around it
      if (!ring) ring = under && under.dark ? B27.body : 0;
      inner = ring === 10;
      // how far off the aim point, in real millimetres on the paper
      r = Math.hypot((u - B27.cu) * card.W, (v - B27.cv) * card.H) * 1000;
    } else {
      r = Math.hypot(xmm, ymm) * (CARD_MM / card.cardMM);
      // THE LINE RULE: the shot takes the higher ring if the hole breaks it, so
      // it is the hole's inner edge that is scored, not its centre.
      const edge = Math.max(0, r - dmm / 2);
      ring = Math.max(0, Math.min(RINGS, RINGS + 1 - Math.ceil(edge / (RING_MM / 2))));
      inner = edge <= INNER_TEN / 2;
      r = Math.hypot(xmm, ymm);
    }
    const shot = { x: xmm, y: ymm, r, ring, inner };
    card.shots.push(shot);
    if (card.shots.length > MAX_SHOTS) card.shots.shift();
    card.score += ring;
    if (shot.inner) card.tens++;
    // extreme spread: the two furthest holes, centre to centre
    for (const s of card.shots) {
      const d = Math.hypot(s.x - shot.x, s.y - shot.y);
      if (d > card.spread) card.spread = d;
    }
    this.active = card;
    this._hide = 15;
    this._refresh(shot);
    return shot;
  }

  // What the card has printed at this point, before a hole covers it. Used to
  // tell a hit on the black of the silhouette from one through the paper beside
  // it -- the difference between a five and a miss.
  _sample(card, x, y) {
    try {
      const d = card.ctx.getImageData(Math.max(0, Math.min(card.canvas.width - 1, x | 0)),
        Math.max(0, Math.min(card.canvas.height - 1, y | 0)), 1, 1).data;
      const lum = (d[0] * 0.3 + d[1] * 0.6 + d[2] * 0.1) / 255;
      return { dark: lum < 0.5, red: d[0] > 140 && d[1] < 90 && d[2] < 90 };
    } catch {
      return null;   // a tainted canvas: score it as a hit rather than a miss
    }
  }

  // One hole: the wipe ring, the torn edge, the hole itself.
  _punch(card, x, y, dpx, elong, ang) {
    const c = card.ctx;
    const r = dpx / 2;
    c.save();
    c.translate(x, y);
    c.rotate(ang);
    c.scale(elong, 1);
    // BULLET WIPE: the grey ring of lube and bore fouling every hole in paper
    // has around it. It is the first thing you see on a real target.
    const wipe = c.createRadialGradient(0, 0, r * 0.7, 0, 0, r * 1.75);
    wipe.addColorStop(0, 'rgba(60,58,56,0.85)');
    wipe.addColorStop(0.55, 'rgba(90,88,86,0.45)');
    wipe.addColorStop(1, 'rgba(120,118,116,0)');
    c.fillStyle = wipe;
    c.beginPath();
    c.arc(0, 0, r * 1.75, 0, Math.PI * 2);
    c.fill();
    // THE HOLE, with a torn edge: paper does not cut clean, it tears into
    // fibres, so the rim wanders by a tenth of the calibre.
    c.beginPath();
    const steps = 18;
    for (let i = 0; i <= steps; i++) {
      const a = (i / steps) * Math.PI * 2;
      const rr = r * (0.9 + Math.random() * 0.2);
      const px = Math.cos(a) * rr;
      const py = Math.sin(a) * rr;
      if (i === 0) c.moveTo(px, py); else c.lineTo(px, py);
    }
    c.closePath();
    c.fillStyle = '#0b0b0d';
    c.fill();
    // a few fibres pushed out through the back, catching the light
    c.strokeStyle = 'rgba(245,242,235,0.5)';
    c.lineWidth = Math.max(1, r * 0.12);
    for (let i = 0; i < 5; i++) {
      const a = Math.random() * Math.PI * 2;
      c.beginPath();
      c.moveTo(Math.cos(a) * r * 0.85, Math.sin(a) * r * 0.85);
      c.lineTo(Math.cos(a) * r * 1.35, Math.sin(a) * r * 1.35);
      c.stroke();
    }
    c.restore();
    card.tex.needsUpdate = true;
  }

  // A fresh card: the holes go, the score goes with them.
  reset(card = this.active) {
    if (!card?.canvas) return false;
    card.shots.length = 0;
    card.score = 0;
    card.tens = 0;
    card.spread = 0;
    // a fresh card is the face it was printed with, whichever that is
    if (card.face) {
      card.ctx.drawImage(card.face, 0, 0, card.canvas.width, card.canvas.height);
    } else {
      card.ctx.drawImage(card.blank.canvas, 0, 0);
    }
    card.tex.needsUpdate = true;
    this._refresh(null);
    return true;
  }

  // ---- the score card ----------------------------------------------------
  _ensureHud() {
    if (this._hud) return this._hud;
    if (!document.body) return null;
    if (!document.getElementById('paper-target-css')) {
      const st = document.createElement('style');
      st.id = 'paper-target-css';
      st.textContent = `
#target-card{position:fixed;right:18px;bottom:120px;z-index:40;display:none;
  min-width:186px;padding:10px 12px;border:1px solid rgba(120,220,255,.35);
  border-radius:10px;background:rgba(8,14,22,.82);backdrop-filter:blur(6px);
  color:#cfeaff;font:12px/1.45 ui-monospace,Menlo,monospace;letter-spacing:.04em;
  box-shadow:0 6px 24px rgba(0,0,0,.45)}
#target-card .tc-ring{font-size:30px;line-height:1;font-weight:700;color:#8ff0c0}
#target-card .tc-ring small{font-size:11px;color:#7fa8c4;font-weight:400}
#target-card .tc-row{display:flex;justify-content:space-between;gap:12px}
#target-card .tc-k{color:#6f93ad}
#target-card button{margin-top:8px;width:100%;padding:4px 0;cursor:pointer;
  border:1px solid rgba(120,220,255,.35);border-radius:6px;background:rgba(20,40,60,.7);
  color:#cfeaff;font:11px ui-monospace,Menlo,monospace;letter-spacing:.06em}
#target-card button:hover{background:rgba(30,70,100,.8)}`;
      document.head.appendChild(st);
    }
    const el = document.createElement('div');
    el.id = 'target-card';
    el.innerHTML = `<div class="tc-ring" id="tc-ring">–</div>
<div class="tc-row"><span class="tc-k">ჯამი</span><span id="tc-sum">0</span></div>
<div class="tc-row"><span class="tc-k">გასროლა</span><span id="tc-n">0</span></div>
<div class="tc-row"><span class="tc-k">საშუალო</span><span id="tc-avg">0.0</span></div>
<div class="tc-row"><span class="tc-k">ჯგუფი</span><span id="tc-grp">–</span></div>
<button id="tc-reset">ახალი ბლანკი</button>`;
    document.body.appendChild(el);
    el.querySelector('#tc-reset').addEventListener('pointerdown', (e) => {
      e.stopPropagation();
      this.reset();
    });
    this._hud = el;
    return el;
  }

  _refresh(shot) {
    const el = this._ensureHud();
    const card = this.active;
    if (!el || !card) return;
    const n = card.shots.length;
    el.style.display = 'block';
    const ring = el.querySelector('#tc-ring');
    // where on the clock face it landed, which is how a shot is called
    const clock = shot && shot.r > 8
      ? ` <small>${Math.round(((Math.atan2(shot.x, shot.y) / Math.PI * 6) + 12) % 12) || 12}h`
        + ` · ${Math.round(shot.r)} მმ</small>` : '';
    ring.innerHTML = shot ? `${shot.ring}${shot.inner ? 'x' : ''}${clock}` : '–';
    el.querySelector('#tc-sum').textContent = String(card.score);
    el.querySelector('#tc-n').textContent = String(n);
    el.querySelector('#tc-avg').textContent = n ? (card.score / n).toFixed(1) : '0.0';
    el.querySelector('#tc-grp').textContent = n > 1
      ? `${card.spread < 100 ? card.spread.toFixed(0) + ' მმ' : (card.spread / 10).toFixed(1) + ' სმ'}`
      : '–';
  }

  update(dt) {
    if (this._hide > 0) {
      this._hide -= dt;
      if (this._hide <= 0 && this._hud) this._hud.style.display = 'none';
    }
  }

  dispose() {
    for (const card of this.sheets.values()) card.tex?.dispose();
    for (const b of this._blanks?.values() ?? []) { b.tex.dispose(); b.mat.dispose(); }
    this.sheets.clear();
    this._hud?.remove();
    this._hud = null;
  }
}
