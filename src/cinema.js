import * as THREE from 'three';
import { WEAPON_DEFS, loadWeapon } from './weapons.js';
import { newGltfLoader } from './gltf.js';
import { spinPropellers } from './propeller.js';
import { B27 } from './papertargets.js';

// ---------------------------------------------------------------------------
// THE REEL -- a looping cinematic, cut like a film instead of played like a game
// ---------------------------------------------------------------------------
//
// One button (🎬 in the HUD) hides every piece of interface and runs a ~10.5 s
// gunfight on an endless loop: the hero walks up to a firing point, draws his
// pistol out of the holster on his belt, goes to the sights, fires, rolls back
// and fires again at the same sheet, jumps right and fires in the air, spins
// and fires, slides and fires -- then it cuts back to frame one and does it
// again. It is meant to be screen recorded, so there is nothing on screen but
// the film.
//
// WHAT IS ACTUALLY DRIVING IT. Nothing here animates the hero. The reel is a
// SCRIPTED PLAYER: it holds keys down, calls the same roll/slide the X and Z
// keys call, and pulls the same trigger the mouse pulls, so every move is the
// game's own -- real locomotion, the real dodge roll, the real aim pose, real
// 9x19 rounds with real travel time that punch real holes in real paper. The
// only thing the reel owns outright is THE CAMERA and the clock.
//
// THE CAMERA is a cut list, not a chase cam. Each shot is a lens, a path, a
// look-at and a dutch angle, in the stage's own frame (x right, y up, z down
// range), and shots CUT -- no easing between them, because a cut is what makes
// a sequence read as film. The grammar is Kill Bill's and John Wick's, which
// agree on more than they disagree:
//
//   * the floor. Both shoot a gunfight from ankle height on a long lens, the
//     hero walking into a static low frame (Kill Bill's House of Blue Leaves,
//     Wick's church). Shots 1, 8 and 10 are on the deck.
//   * the body, not the bullet. Wick's camera glides WITH the move -- it arcs
//     with the roll (5), tracks the jump side-on (6) and counter-orbits the
//     spin (7), so the geography never breaks.
//   * the insert. One tight beat on the hands (2) is what makes a pickup read
//     as a decision rather than an inventory event.
//   * time. Both stop it: the flash holds, the round crosses on camera, the
//     paper tears (4). That is the bullet, seen (and it is what was asked for).
//
// WHERE. On the warehouse range, at a real lane. The stage is the floor behind
// a firing point, pointed down the rail, and the targets are the sheets
// already hanging there -- the B-27 silhouettes the F/G carrier buttons drive,
// scored by the same papertargets.js -- run out on their carriers to the
// film's marks and sent back where they were when the reel stops. Nothing is
// built for the film but two lights. The pistol is not lying on the floor
// either: it is on his hip, in the holster on the belt, and the draw is the
// game's own draw (foot.js _startDraw), up out of the leather and to the hand.
//
// TWO SCENES, and the 🎬 button is a menu of them (index.html, main.js). A
// scene is a stage, a hero, what he is carrying, a script of beats and a cut
// list -- nothing else in here knows which one is running. See SCENES at the
// foot of this block.
const HERO = 'banana';     // who stars in both of them
const CHEST = 1.3;         // m -- the aim is taken from here, so the arms and
                           // the round are looking at the same thing
// The cards, in stage metres: x across the booths, z down range from the
// stage origin. Each is a lane's own sheet -- his lane, and the second booth
// either side -- run to that distance on its carrier.
const CARDS1 = [
  { x: 0.0, z: 11.0 },     // his own lane: the draw, the roll
  { x: 3.3, z: 12.4 },     // two booths right: the jump, the slide
  { x: 6.6, z: 9.4 },      // four right, where the spin leaves him facing
];
const START_Z = -3.0;      // he walks in from here
const MARK_Z = 0.0;        // and draws at the stage origin
// The origin sits this far behind the firing line. The whole fight -- the
// walk-in, the roll back, the jump, the run and the slide -- happens on the
// open floor behind the booths: the counters are no-climb, and a booth's
// dividers hide whoever is inside it from any camera that is not.
const BEHIND = 6.0;        // m
const BEHIND_MIN = 2.5;    //    ...or as far back as the floor allows
const FADE = 0.16;         // s of black at the seam, which is what hides it

const TURN_DEAD = 0.05;    // rad -- close enough to let the turn key go
const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);
const smooth = (x) => { const k = clamp01(x); return k * k * (3 - 2 * k); };
const easeOut = (x) => 1 - (1 - clamp01(x)) ** 3;
const shortest = (a) => Math.atan2(Math.sin(a), Math.cos(a));

// ---------------------------------------------------------------------------
// SCENE 1 -- THE SCRIPT: what the hero does, and when. Times are REAL seconds from the
// top of the loop, not game seconds: slow motion stretches the world, never the
// edit, so the film is always the same length however much time is bent inside
// it.
// ---------------------------------------------------------------------------
const BEATS1 = [
  // --- he walks in, on Ctrl: a walk, not a jog. Nobody runs to a gun.
  { t: 0.00, go: (c) => { c.hold('KeyW', 'ControlLeft'); c.aimAt(0); } },
  { t: 1.30, go: (c) => c.hold() },
  // --- the draw: the hand goes to the hip and the pistol comes up out of
  //     the leather, the game's own 0.7 s move
  { t: 1.44, go: (c) => c.draw() },
  { t: 1.84, go: (c) => c.game.pistol.setAiming(true) },
  // --- the first round, and time stops around it
  { t: 2.14, go: (c) => c.fire(0) },
  { t: 2.20, go: (c) => c.slow(0.085) },
  { t: 3.00, go: (c) => c.slow(1) },
  // --- the roll BACK, and up firing at the same card
  { t: 3.20, go: (c) => c.roll(Math.PI) },
  { t: 3.80, go: (c) => c.fire(0) },
  { t: 4.12, go: (c) => c.fire(0) },
  // --- right, off the ground, and two in the air
  { t: 4.30, go: (c) => { c.hold('KeyD'); c.aimAt(1); } },
  { t: 4.56, go: (c) => c.hold('KeyD', 'Space') },
  { t: 4.74, go: (c) => { c.hold('KeyD'); c.slow(0.42); } },
  { t: 4.88, go: (c) => c.fire(1) },
  { t: 5.14, go: (c) => c.fire(1) },
  { t: 5.30, go: (c) => { c.slow(1); c.hold(); } },
  // --- all the way round, firing through it
  { t: 5.82, go: (c) => c.spin(1.0) },
  { t: 6.34, go: (c) => { c.aimAt(2); c.fire(2); } },
  // --- a run, then down on the hip, shooting low. A short run: the slide
  //     carries him four metres and it has to end on the open floor, short
  //     of the booths (see `cubicle`).
  { t: 7.00, go: (c) => { c.hold('KeyW'); c.aimAt(1); } },
  { t: 7.36, go: (c) => { c.hold(); c.slide(); } },
  { t: 7.98, go: (c) => c.fire(1) },
  { t: 8.30, go: (c) => c.slow(0.32) },
  { t: 8.44, go: (c) => c.fire(1) },
  { t: 8.78, go: (c) => c.slow(1) },
  // --- and the last one, for the camera sitting on the card
  { t: 9.04, go: (c) => c.fire(1) },
];

// ---------------------------------------------------------------------------
// SCENE 1 -- THE CUT LIST. `cam` and `look` are world points; `S` is the stage frame:
//   S.p(x, y, z)     a fixed point on the stage
//   S.actor(x, y, z) the same offsets, carried by the hero
//   S.card(i, ...)   a target's face, plus offsets
//   S.hip()          the holster on his belt, where the draw starts
//   S.round()        the live round, while one is in the air
// ---------------------------------------------------------------------------
const SHOTS1 = [
  // 1. ON THE DECK, a static low frame he walks into. The look-at starts on
  //    his mark and rises to him as he arrives: the shot says where he is
  //    going before he gets there.
  {
    t: 0, d: 1.50, fov: [37, 32], roll: [-0.03, 0.005], shake: 0.25,
    cam: (k, S) => S.p(1.5 - 0.25 * smooth(k), 0.40, 3.3 - 0.75 * smooth(k)),
    look: (k, S) => S.mix(S.p(0, 0.10, MARK_Z), S.actor(0, 1.05, 0), smooth(clamp01(k * 1.7 - 0.25))),
  },
  // 2. THE INSERT: hip height off his gun side, long lens, on the holster. The
  //    hand comes down into frame, the pistol comes up out of the leather and
  //    the look rises with it.
  {
    t: 1.50, d: 0.56, fov: [31, 29], roll: [0.075, 0.03], shake: 0.5,
    cam: (k, S) => S.actor(1.35, 0.72 + 0.15 * k, 0.25),
    look: (k, S) => S.mix(S.hip(), S.actor(0.30, 1.18, 0.30), smooth(clamp01(k * 1.5 - 0.5))),
  },
  // 3. THE SIGHT PICTURE. Over the right shoulder, down range -- Wick's own
  //    frame, and the one that puts the card and the muzzle in the same shot.
  {
    t: 2.06, d: 0.30, fov: [40, 38], roll: [0.01, 0.02], shake: 0.3,
    cam: (k, S) => S.actor(0.62, 1.62, -1.15 - 0.1 * k),
    look: (k, S) => S.card(0, 0, 0.05, 0),
  },
  // 4. THE ROUND. Time is down to a twelfth, the flash holds, and the camera
  //    runs the bullet's own line to the paper -- arriving as the hole does.
  {
    t: 2.36, d: 0.80, fov: [30, 36], roll: [0.02, -0.025], shake: 0.15,
    cam: (k, S) => S.mix(S.actor(0.85, 1.42, 0.55), S.card(0, 0.95, 0.12, -1.25), easeOut(k)),
    look: (k, S) => S.round() ?? S.card(0, 0, 0.04, 0),
  },
  // 5. THE ROLL, taken on an arc: the camera swings round and in as he goes
  //    over and comes up shooting, so the move is read in one unbroken piece.
  {
    t: 3.16, d: 1.30, fov: [34, 39], roll: [-0.05, 0.035], shake: 0.6,
    cam: (k, S) => S.orbit(-1.25 + 1.0 * smooth(k), 3.9 - 0.55 * k, 0.75 + 0.95 * smooth(k)),
    look: (k, S) => S.actor(0, 1.0 + 0.1 * k, 0.3),
  },
  // 6. THE JUMP, side on and tracking, on a longer lens so the ground slides
  //    past behind him. Time eases off at the top: he hangs there, firing.
  {
    t: 4.46, d: 1.30, fov: [40, 36], roll: [0.05, -0.05], shake: 0.45,
    cam: (k, S) => S.actor(-4.9 + 0.5 * smooth(k), 1.25, 0.8 - 1.1 * smooth(k)),
    look: (k, S) => S.actor(0.1, 1.15, 0),
  },
  // 7. THE SPIN. The camera goes round the other way: he turns left through the
  //    frame while the frame turns right past him, which is what makes a pivot
  //    whip instead of merely rotate.
  {
    t: 5.76, d: 1.20, fov: [48, 44], roll: [-0.06, 0.06], shake: 0.7,
    cam: (k, S) => S.orbit(2.1 + 1.6 * smooth(k), 2.9, 1.45 - 0.25 * k),
    look: (k, S) => S.actor(0, 1.15, 0.1),
  },
  // 8. THE SLIDE, from the floor and head on: he runs at the lens and goes
  //    down on his hip in front of it. The camera sits just short of the
  //    booths -- any further and the counter is between it and him.
  {
    t: 6.96, d: 1.90, fov: [32, 38], roll: [0.04, -0.02], shake: 0.55,
    cam: (k, S) => S.p(2.0, 0.30, 3.3 - 0.4 * smooth(k)),
    look: (k, S) => S.mix(S.actor(0, 0.95, 0), S.actor(0, 0.6, 0.6), smooth(clamp01(k * 1.4 - 0.3))),
  },
  // 9. THE PAPER. Off the card's shoulder, close enough to count the holes, as
  //    the last round arrives. The group is the point of the whole reel.
  {
    t: 8.86, d: 0.84, fov: [36, 33], roll: [-0.02, 0.015], shake: 0.3,
    cam: (k, S) => S.card(1, 0.78 - 0.1 * smooth(k), 0.22, -1.15 + 0.22 * smooth(k)),
    look: (k, S) => S.card(1, 0.02, 0.0, 0),
  },
  // 10. HERO OUT: on the deck looking up at him from three-quarter front,
  //     pushing in, as he comes off the slide with the gun still out. Close
  //     and off his shoulder rather than down the lane, which is the booth's.
  //     Then black, and round again.
  {
    t: 9.70, d: 0.90, fov: [38, 34], roll: [0.035, -0.015], shake: 0.35,
    cam: (k, S) => S.actor(1.5 - 0.2 * smooth(k), 0.42, 1.0 - 0.3 * smooth(k)),
    look: (k, S) => S.actor(0, 1.2 + 0.08 * k, 0),
  },
];

// ---------------------------------------------------------------------------
// SCENE 2 -- THE CRATES. Up, over and round the corner.
// ---------------------------------------------------------------------------
//
// The second film is a stunt, and it is shot on the ammo crates standing on
// the warehouse floor (src/airport.js): three of them, two end to end and one
// stacked on the second, a 9.7 m wall two metres high with a four-metre tower
// at its right-hand end. The hero free-runs at it, climbs the first crate,
// goes along the lid, climbs the tower, and from up there throws a BACKFLIP
// off it -- a real one: the game's air physics fly the arc, the body balls up
// and turns a full circle about its middle (foot.js _startFlip), and the fall
// is a real four-metre fall. He lands on the floor, takes the rifle off his
// back, runs the length of the crates, slams into cover on the end corner
// (foot.js toggleCover -- GTA's cover, the game's own), RELOADS behind it
// where a reload belongs, and only then leans round the edge and empties the
// magazine into the same B-27 the first film is shot at.
//
// THE WEAPON IS THE AKM, not the pistol: a rifle slings across the back, which
// is the only way to carry something through a climb -- both hands are on the
// crate -- and its reload is the one worth filming, the magazine rocked out
// and the carrier racked.
//
// THE ROW'S OWN FRAME, because every mark in this scene is on the crates and
// the crates sit 3.6 degrees off the range: `a` runs along the row from its
// left end, `b` out of the face he hides behind (away from the targets), `y`
// off the floor. S.row(a, b, y) is a point in it; the stage keeps the range's
// frame underneath for the lane, the card and the booths.
const APPROACH = 2.0;      // x real time up to the take-off: the run, both
                           // climbs and the turn on top, undercranked
const FLIP_SLOW = 0.6;     // ...and the flip itself, the other way
const RELOAD_PACE = 1.35;  // x real time behind the wall: the whole magazine
                           // change and the rack, a second shorter
const FLIP_FALL = 4.0;     // m -- the drop off the tower, which is what times the turn
const S2_START = { a: 1.3, b: 2.9 };   // where he stands on frame one
const S2_CORNER_IN = 0.55; // m short of the row's end he takes cover: the shoulder
                           // ends ON the edge, and the lean goes round it

const BEATS2 = [
  // CUT FOR X: eleven and a half seconds, so the approach is undercranked hard
  // (APPROACH x real time) and the reload runs a touch fast too. The hero is
  // not hurried -- the camera is. Every move is still the game's own, at its
  // own weight; there is simply none of it you have already seen.
  { t: 0.00, go: (c) => { c.slow(APPROACH); c.aimAt(null); c.face('in'); c.hold('KeyW', 'ShiftLeft'); } },
  { t: 0.38, go: (c) => c.hold() },            // the lip has him; the climb owns the body
  // REAL TIME BEFORE THE MANTLE ENDS. Coming over the lip the body straightens
  // from lying across it to standing in about a tenth of a second, and the
  // slung rifle -- a long thin thing on his back -- whips with it. Undercrank
  // that at APPROACH and it happens in a twentieth: it reads as the AK coming
  // off him. (It never leaves: measured 5.7 cm from his skin, constant, the
  // whole way up -- tools/_probe_slingpop.mjs.)
  { t: 0.70, go: (c) => c.slow(1) },
  // --- ON THE CRATE, AND HE DOES NOT TURN ON IT. The climb leaves him facing
  //     the range, which is where the card is. A mantle stops with the feet on
  //     the rim, so one stride puts him properly on top of the box.
  { t: 1.05, go: (c) => c.hold('KeyW', 'ControlLeft') },
  { t: 1.42, go: (c) => c.hold() },
  // --- THE RIFLE COMES OFF HIS BACK UP THERE, and it is the game's own sling
  //     draw -- the hand over the shoulder, the AKM up and round into it, all
  //     0.81 s of it. It is snapped nowhere now: standing still on a crate is
  //     exactly where there is room for it. (It was snapped when it sat in the
  //     middle of the run and left him sprinting with his hands behind him.)
  { t: 1.52, go: (c) => c.draw() },
  { t: 2.40, go: (c) => { c.aimAt(0); c.sight(true); } },
  { t: 2.80, go: (c) => c.fire(0) },
  { t: 2.93, go: (c) => c.fire(0) },
  { t: 3.06, go: (c) => c.fire(0) },
  { t: 3.19, go: (c) => { c.fire(0); c.slow(0.4); } },
  { t: 3.45, go: (c) => c.fire(0) },
  { t: 3.67, go: (c) => { c.slow(1); c.fire(0); } },
  // --- and OFF IT BACKWARDS: a backflip over the lip, two metres of air, down
  //     on his feet. The drop is handed in -- this crate is 2 m, not the 3.9 m
  //     tower, and the turn is timed to the arc.
  { t: 3.82, go: (c) => { c.sight(false); c.aimAt(null); } },
  { t: 3.92, go: (c) => { c.slow(FLIP_SLOW); c.flip(c._stage.row.lid); } },
  { t: 5.17, go: (c) => c.slow(1) },
  // --- and down the crates to the corner
  { t: 5.35, go: (c) => {
    c.face('along');
    c.hold('KeyW');
    c.runTo(c._stage.row.len - S2_CORNER_IN, (d) => { d.hold(); d.cover(); });
  } },
  // --- THE WHOLE RELOAD behind the wall -- magazine out, magazine in, carrier
  //     racked -- at RELOAD_PACE, which takes a second off it without losing a
  //     single part of the move.
  { t: 6.55, go: (c) => { c.slow(RELOAD_PACE); c.reload(); } },
  { t: 10.05, go: (c) => c.slow(1) },
  // --- and out round the edge, into the corner's card
  { t: 10.20, go: (c) => { c.face(null); c.aimAt(1); c.sight(true); } },
  { t: 10.65, go: (c) => c.fire(1) },
  { t: 10.78, go: (c) => c.fire(1) },
  { t: 10.91, go: (c) => c.fire(1) },
  { t: 11.04, go: (c) => { c.fire(1); c.slow(0.35); } },
  { t: 11.30, go: (c) => c.fire(1) },
  { t: 11.55, go: (c) => { c.slow(1); c.fire(1); } },
];

// The cut list. Mostly on the hide side of the crates -- the side he is on --
// and never down range of the row, where the camera would be standing in the
// lane it is filming.
const SHOTS2 = [
  // 1. THE RUN IN, from the floor behind him: the wall of crates across the
  //    frame, the hero going at it, the lens pushing with him.
  {
    // ...and it HOLDS past the top of the climb. Coming over the lip the body
    // straightens in a tenth of a second and the slung rifle whips with it;
    // on this moving lens, behind him, that reads as the climb. Cut to a
    // static wide on that exact frame and it reads as the rifle coming off.
    t: 0, d: 1.18, fov: [40, 36], roll: [-0.03, 0.01], shake: 0.45,
    cam: (k, S) => S.row(-2.8, 5.2 - 0.7 * smooth(k), 1.2),
    look: (k, S) => S.actor(0, 0.9, 0),
  },
  // 2. UP ON THE CRATE, and the rifle comes off his back to the shoulder.
  {
    // THE CRATE HAS TO BE IN THE FRAME, under his feet, or he is just a man
    // in the air. The 2.39:1 bars eat a third of the vertical lens, so a box
    // 2 m tall with a 1.8 m hero on it -- 3.8 m of subject -- needs the camera
    // a good 9 m back and a wide-ish lens, not a close one.
    t: 1.18, d: 1.22, fov: [44, 41], roll: [0.04, -0.02], shake: 0.4,
    cam: (k, S) => S.row(S.heroA() + 3.3, 5.5, 1.8),
    look: (k, S) => S.actor(0, 0.2, 0),
  },
  // 3. THE BURST FROM THE CRATE, low and three-quarters: two metres of height
  //    under him, the muzzle flashing, the lane running away past his shoulder.
  {
    t: 2.40, d: 1.35, fov: [44, 40], roll: [-0.03, 0.03], shake: 0.7,
    cam: (k, S) => S.row(S.heroA() + 4.0, 6.3, 1.9),
    look: (k, S) => S.actor(0, 0.3, 0),
  },
  // 4. ...and the card taking them.
  {
    t: 3.75, d: 0.20, fov: [34, 31], roll: [0.02, -0.015], shake: 0.3,
    cam: (k, S) => S.card(0, 0.45 - 0.05 * smooth(k), 0.12, -1.55 + 0.15 * smooth(k)),
    look: (k, S) => S.card(0, 0, 0, 0),
  },
  // 5. THE BACKFLIP off the crate, square to the plane he turns in -- offset
  //    along the row and nowhere else, which is the only angle a somersault
  //    reads from -- and coming down with him. Time at FLIP_SLOW, so the whole
  //    circle is on screen.
  {
    t: 3.95, d: 1.22, fov: [40, 44], roll: [0.04, -0.05], shake: 0.4,
    cam: (k, S) => S.row(S.heroA() + 5.0, 1.4, 2.7 - 1.3 * smooth(k)),
    look: (k, S) => S.actor(0, 0.4, 0),
  },
  // 6. UP OUT OF IT and down the crates to the corner, three-quarters from
  //    the side: head on, a rifle pointed the way he runs reads as empty hands.
  {
    t: 5.17, d: 1.43, fov: [36, 40], roll: [-0.03, 0.025], shake: 0.55,
    cam: (k, S) => S.row(S.heroA() + 4.6, 5.8, 1.0),
    look: (k, S) => S.actor(0, 1.0, 0),
  },
  // 7. THE SLAM into cover, a fixed frame off the corner so the arrival has
  //    weight: back on the crate, rifle up in front of him, the edge at his
  //    shoulder.
  {
    t: 6.60, d: 0.60, fov: [36, 33], roll: [0.06, 0.02], shake: 0.5,
    cam: (k, S) => S.row(12.3, 3.0, 1.7),
    look: (k, S) => S.actor(0, 1.1, 0),
  },
  // 8. THE MAGAZINE, close: the cargador rocked out of the AKM and the next
  //    one in, on the line the wide looks down at half the distance -- the
  //    angle the weapon actually reads from in cover.
  {
    t: 7.20, d: 1.75, fov: [32, 30], roll: [-0.05, -0.02], shake: 0.45,
    cam: (k, S) => S.row(S.heroA() + 2.1, 2.1, 1.25),
    look: (k, S) => S.actor(0, 0.86, 0),
  },
  // 9. ...and out to the whole man for the RACK -- the hand up the receiver,
  //    the carrier pulled and let go -- with the corner he is about to lean
  //    round between him and the range.
  {
    t: 8.95, d: 1.25, fov: [40, 37], roll: [0.03, -0.02], shake: 0.4,
    cam: (k, S) => S.row(13.4, 3.8 - 0.5 * smooth(k), 1.8),
    look: (k, S) => S.actor(0, 1.1, 0),
  },
  // 10. ROUND THE EDGE, over his shoulder and down range -- far enough back
  //     that the muzzle, the corner and the card are all in the one frame.
  {
    t: 10.20, d: 1.30, fov: [42, 38], roll: [-0.02, 0.02], shake: 0.6,
    cam: (k, S) => S.actor(0, 1.7, -2.4).addScaledVector(S.rowU(), 0.85),
    look: (k, S) => S.card(1, 0, 0.1, 0),
  },
  // 11. THE PAPER, as the burst arrives. Clear of the sheet's own plane, or
  //     the lens ends up behind it looking at a blank white wall.
  {
    t: 11.50, d: 0.60, fov: [34, 31], roll: [0.02, -0.015], shake: 0.3,
    cam: (k, S) => S.card(1, 0.45 - 0.06 * smooth(k), 0.12, -1.55 + 0.18 * smooth(k)),
    look: (k, S) => S.card(1, 0, 0, 0),
  },
];

// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// SCENE 3 — START UP AND GO. The one scene with a story rather than a stunt:
// the pilot swings into the cockpit, the hood comes down, the propeller
// catches and winds up, the aeroplane rolls, lifts off — and then it is over
// the city.
//
// The first cut of this was five aerials in a row and the user said so: "i
// see just 1 step ... it not scene like other videos". Three of the shots
// were the same picture (a small aeroplane on a big city from above) and the
// film had nowhere to go. Every shot here is a different SIZE and a different
// subject: a man climbing in, a propeller, a machine leaving the ground, a
// wing over a town, and the aeroplane itself.
//
// It takes off from the real runway at the airport and is over the city for
// the last two shots. That is a CUT, which is what cuts are for — nobody has
// ever asked how an aeroplane got from one shot to the next.
// 11.6 s all in, and the ground half is the half that got cut: the engine
// catches, it goes, and we are over town by 6 s so the city has nearly as
// long as the airport did.
const FLY = {
  sit: 0.50,     // s — he steps over the sill and drops into the seat
  light: 2.10,   // the engine catches
  idle: 3.30,    // ...and settles
  roll: 3.50,    // throttle up, brakes off
  rotate: 5.85,  // wheels off
  cut: 6.15,     // and we are over the city
};
const FLY_ACC = 30;         // m/s^2 — arcade: airborne inside the shot
const FLY_CLIMB = 11;       // m/s up, once the wheels are off
const FLY_SPEED = 148;      // m/s over the city — 530 km/h, and it reads it
const FLY_TURN = 0.046;     // rad/s — at 148 m/s that is a 35 deg bank
const FLY_ALT = 142;        // m over the city — low is what makes it fast
const FLY_SINK = 0.0;       // m/s — it is low enough already
const FLY_RUN = 520;        // m — how far out the city run-in starts
const FLY_HEAD = -0.75;     // rad — the run-in heading, across the centre
const G_ACC = 9.81;
const IDLE_RPM = 0.38;      // of max revs — a propeller ticking over

// WHAT THE FLYPAST DRAWS. Profiling the city shots said 85% of the frame was
// vertexAttribPointer / bindBuffer / drawElementsInstanced — the COUNT of draw
// calls and nothing else. 582 chunks were attached over town for ~2700 draws a
// frame, and 485 of those chunks were props with 1000-1400 m radii that are
// two pixels wide from 142 m up. So for the duration of the reel the props
// pull in and the buildings stop at FLY_CULL, and the haze is turned up to
// meet them — which is how an aerial shot looks anyway. Aerial perspective is
// free depth: the hills behind town go blue and the far edge of the city
// dissolves into it instead of ending on a line.
const FLY_CULL = 2600;      // m — buildings out to here, then haze
const FLY_PROPS = 0.42;     // x prop radii, so a 1400 m balcony reaches 590 m
const FLY_FOG = 0.00045;    // FogExp2 density (the city's own is 6e-5)

const FLIGHT_SHOTS = [
  // 1. HE GETS IN. Low and close at the wing root, wide lens: the hood is up,
  //    the banana steps over the sill and goes down into the seat, and the
  //    lid comes after him. The only shot in the reel with a person in it.
  {
    t: 0, d: 2.10, fov: [34, 31], roll: [0.04, -0.02], shake: 0.4,
    cam: (k, S) => S.plane(-3.4 + 0.5 * smooth(k), 1.25, 3.1 - 0.9 * smooth(k)),
    look: (k, S) => S.seat(0, 0.55, 0),
  },
  // 2. THE PROPELLER. Tight on the spinner from the front quarter as it
  //    catches: three blades, then two, then a disc. Long lens, so the
  //    aeroplane behind it goes soft and the blades are the whole picture.
  {
    t: 2.10, d: 2.55, fov: [26, 23], roll: [-0.03, 0.02], shake: 0.3,
    cam: (k, S) => S.nose(4.8 + 1.3 * smooth(k), 1.05, 3.2 - 0.7 * smooth(k)),
    look: (k, S) => S.nose(0.15, 0.55, 0.2),
  },
  // 3. IT GOES. The camera is on the ground beside the runway and does not
  //    move: the aeroplane comes past it, lifts, and leaves frame. A static
  //    is what makes speed legible — everything that moves is the subject.
  {
    t: 4.65, d: 1.50, fov: [38, 34], roll: [0.02, -0.05], shake: 0.55,
    // close to the line and well down it, so the aeroplane comes AT the lens
    // and is biggest as the wheels leave
    // Off the deck (at 2 m the grass bank stood in front of the aeroplane)
    // and only 62 m down the line — at 88 m the first two seconds of the
    // shot were an empty taxiway waiting for something to happen.
    // ON THE RUNWAY, not out in the grass beside it: at 18 m and at 10.5 m the
    // lens had a tree in front of it (at 10.5 it was inside one). The paved
    // strip the aeroplane rolls down is the one sightline that is reliably
    // clear, so the camera sits just outside the wingtip and low, and the
    // aeroplane comes over it.
    cam: (k, S) => S.ground(5.5, 2.4, 70),
    look: (k, S) => S.plane(0, 1.2, 0),
  },
  // 4. DOWN THE ROOFTOPS. Low, fast and off the raised wing, with the city
  //    running underneath — short, because the point of this one is pace.
  {
    t: 6.15, d: 1.70, fov: [62, 50], roll: [-0.06, 0.03], shake: 0.45,
    // and the camera LOSES it: it slides back a wingspan over the shot, so
    // the aeroplane pulls away from the lens instead of sitting in it
    cam: (k, S) => S.plane(-S.span() * 0.78, 1.0, 2.4 + 8.5 * smooth(k)),
    look: (k, S) => S.plane(S.span() * 0.28, -3.4 - 1.8 * smooth(k), -5),
  },
  // 5. THE GOD'S EYE. Straight down the aeroplane's back at the roofs, the
  //    city streaming bottom to top of frame. The up vector is the NOSE, not
  //    the world's — looking plumb down there is no world up to use, and the
  //    nose is what makes the streets run with the aeroplane instead of
  //    across it. It climbs away as it goes, so the town opens out.
  {
    t: 7.85, d: 1.65, fov: [52, 66], roll: [0.0, 0.0], shake: 0.2,
    cam: (k, S) => S.plane(0, 44 + 62 * smooth(k), 6 + 10 * smooth(k)),
    look: (k, S) => S.plane(0, 0, -2),
    up: (k, S) => S.fwdVec(),
  },
  // 6. THE HERO SHOT. Nose on and under it, the machine coming at the lens
  //    and banking away over the top with the city running out behind.
  {
    t: 9.50, d: 2.10, fov: [40, 54], roll: [0.05, -0.07], shake: 0.45,
    cam: (k, S) => S.plane(2.0 + 1.5 * smooth(k), -5.5 + 11 * smooth(k), -62 + 56 * smooth(k)),
    look: (k, S) => S.plane(0, 0.4, 0),
  },
];

// THE SCENES. `stage` picks which stand the film is built on; `cards` are the
// sheets it shoots at, as a list or as a function of the stage (scene 2 takes
// the lane its corner opens onto, whichever that turns out to be).
// ---------------------------------------------------------------------------
export const SCENES = [
  {
    id: 'range', name: 'სცენა 1 — ტირი', dur: 10.6, gun: 'beretta', stage: 'range',
    cards: CARDS1, beats: BEATS1, shots: SHOTS1,
    // frame one: already walking in, on his mark
    place: (c, s) => ({ at: c._v.copy(s.o).addScaledVector(s.fwd, START_Z), yaw: s.yaw, speed: 1.9 }),
  },
  {
    id: 'crates', name: 'სცენა 2 — ყუთები', dur: 12.1, gun: 'akm', stage: 'crates',
    cards: (s) => [{ at: s.row.boxX, dist: 7.0 }, { at: s.row.cardX, dist: 7.0 }],
    beats: BEATS2, shots: SHOTS2,
    place: (c, s) => ({ at: s.row.at(S2_START.a, S2_START.b, 0, c._v), yaw: s.row.yawIn, speed: 0 }),
  },
  {
    id: 'flight', name: 'სცენა 3 — ფრენა', dur: 11.6, stage: 'flight',
    flight: true, gun: null, cards: [], beats: [], shots: FLIGHT_SHOTS,
  },
];

export class Cinema {
  constructor(game) {
    this.game = game;
    this.active = false;
    this.scene = 0;         // which of SCENES is loaded
    this._sc = SCENES[0];
    this.paused = false;
    this.t = 0;
    this.timeScale = 1;
    // The aim the DIRECTOR is taking, which is what shooting.js uses instead of
    // the crosshair while this runs -- there is no crosshair, and the camera is
    // nowhere near the hero's shoulder.
    this.aim = null;        // unit world direction, for the arms
    this.aimPoint = null;   // and the exact point the round is sent to
    this._busy = false;
    this._beat = 0;
    this._shot = -1;
    this._spin = null;
    this._kick = 0;         // gunshot jolt left in the camera
    this._targets = [];
    this._stage = null;
    this._v = new THREE.Vector3();
    this._look = new THREE.Vector3();
    this._fwd = new THREE.Vector3();
    this._flyEuler = new THREE.Euler();
    this._right = new THREE.Vector3();
    this._S = this._stageApi();
  }

  get foot() { return this.game.foot; }

  // The cut list, for tooling: tools/check_cinema.mjs stops on the middle of
  // every shot and takes a still, which is how the framing gets judged at all.
  shotTimes() { return this._sc.shots.map((s) => ({ t: s.t, d: s.d })); }

  // ---- the button -------------------------------------------------------
  // 🎬 is a menu now (main.js): picking the scene that is already running
  // stops it, picking the other one cuts straight to it.
  toggle(i = 0) {
    if (!this.active) return this.start(i);
    const was = this.scene;
    this.stop();
    return i === was ? false : this.start(i);
  }

  async start(i = 0) {
    const g = this.game;
    this.scene = SCENES[i] ? i : 0;
    this._sc = SCENES[this.scene];
    if (this.active || this._busy) return false;
    if (!g.started || g.crashed) {
      g._setHint?.('🎬 სცენა მხოლოდ თამაშში — დაელოდე ქალაქის ჩატვირთვას');
      return false;
    }
    this._busy = true;
    try {
      // Everything that has to go back exactly as it was. The reel borrows the
      // hero's body, his slot 1 and the camera; it gives all three back.
      const onFoot = !!this.foot.active;
      this._saved = {
        onFoot,
        model: this.foot._modelId,
        rig: this.foot.rig,
        pos: onFoot ? this.foot.pos.clone() : null,
        yaw: onFoot ? this.foot.yaw : 0,
        held: this.foot.held ?? null,
        carried: (g.weapons?.carried ?? []).slice(),
        slotIndex: g.weapons?.index ?? -1,
        planeVisible: g.plane.visible,
        camPos: g.camera.position.clone(),
        camQuat: g.camera.quaternion.clone(),
        fov: g.camera.fov,
        aiming: !!g.pistol?.aiming,
        hq: g.hq,
      };

      const origin = onFoot ? this.foot.pos.clone() : g.plane.position.clone();
      // The range is scenery that builds after the world is playable, so the
      // button can be pressed before there is a lane to stand at. Wait for
      // it, up to half a minute, rather than answer with nothing.
      // the flypast needs no scenery at all — it is the city, from above
      if (!this._sc.flight && !(g.lanes?.lanes?.length)) {
        g._setHint?.('🎬 ტირი იტვირთება…');
        for (let i = 0; i < 120 && !(g.lanes?.lanes?.length); i++) {
          await new Promise((r) => setTimeout(r, 250));
        }
      }
      this._stage = this._sc.flight ? this._flightStage()
        : this._sc.stage === 'crates' ? this._crateStage(origin) : this._rangeStage(origin);
      if (!this._stage) {
        g._setHint?.(this._sc.stage === 'crates'
          ? '🎬 ყუთები ჯერ არ ჩატვირთულა' : '🎬 ტირი ჯერ არ ჩატვირთულა');
        throw new Error(`${this._sc.id}: nowhere to stand the stage up`);
      }
      await this._loadProps();

      // The hero of the reel is the banana, whoever you are playing. Its body
      // is swapped in under the same OnFoot that drives the real one, so every
      // move in the script is the move the keys would have made.
      if (this.foot._modelId !== HERO) {
        this.foot._modelId = HERO;
        await this.foot._loadBody();
        if (this._saved.rig && this._saved.rig !== this.foot.rig) {
          g.scene.remove(this._saved.rig.obj);
        }
      }
      // The flypast is ABOUT the aeroplane, so it is the one film it stays in
      // — and the hero rides in it rather than walking the floor.
      g.plane.visible = true;
      if (!this._sc.flight) {
        g.plane.visible = false;      // the others: it is not in this film
        this._buildStage();
        // His pistol goes on his hip, not on the floor: into slot 1 and
        // straight into the holster, which is where the draw takes it from.
        g.weapons?.setCarried(this._held.slot ?? 1, this._held);
        this.foot._toHolster();
      }
      if (this._sc.flight) await this._warmCity();
      this._showOverlay();
      document.body.classList.add('cinema-mode');
      g.pistol?.setAiming(false);
      g.keys.clear();
      this.active = true;
      this.paused = false;
      this._reset();
      g._setHint?.('🎬 ESC — გამოსვლა');
      if (this._sc.flight) {
        console.log(`cinema: ${this._sc.id} — ${this._sc.dur}s loop, ${FLY_SPEED} m/s at `
          + `${FLY_ALT} m over the city, ${(FLY_SPEED / FLY_TURN).toFixed(0)} m turn radius`);
        this._busy = false;
        return true;
      }
      console.log(`cinema: ${this._sc.id} — ${this._sc.dur}s loop at lane ${this._stage.lane.index}, `
        + `${this._stage.behind.toFixed(1)} m behind the line, booths ${this._stage.wall.toFixed(2)} m deep `
        + `x ${this._stage.wallTop.toFixed(2)} m tall, `
        + `${this._targets.length} sheets at ${this._targets.map((t) => t.dist.toFixed(1)).join('/')} m`);
    } catch (e) {
      console.error('cinema failed to start:', e);
      this._busy = false;
      this.active = false;
      this._teardown();
      return false;
    }
    this._busy = false;
    return true;
  }

  stop() {
    if (!this.active) return false;
    const g = this.game;
    const s = this._saved ?? {};
    this.active = false;
    this.timeScale = 1;
    this.aim = null;
    this.aimPoint = null;
    this._spin = null;
    g.keys.clear();
    this.foot.camRef = null;   // the player reads the keys off the camera again
    document.body.classList.remove('cinema-mode');
    this._hideOverlay();

    // give the hand back, then the body, then the slot
    this._stripHands();
    this._teardown();
    g.plane.visible = s.planeVisible ?? true;
    if (g.weapons) {
      s.carried?.forEach((w, i) => g.weapons.setCarried(i, w ?? null));
      g.weapons.index = s.slotIndex ?? -1;
      g.weapons.slotEls?.forEach((el, k) => el.classList.toggle('sel', k === g.weapons.index));
    }
    this.foot._toHolster();          // his own guns back on his own belt

    const back = () => {
      if (s.onFoot) {
        this.foot._stand(s.pos.x, s.pos.y, s.pos.z, s.yaw);
        if (s.held) this.foot.drawWeapon(s.held);
        g.pistol?.setAiming(s.aiming);
      } else if (this.foot.rig) {
        this.foot.active = true;      // _board expects to be leaving foot mode
        this.foot._board();
      }
      g.camera.position.copy(s.camPos);
      g.camera.quaternion.copy(s.camQuat);
      g.camera.fov = s.fov;
      g.camera.updateProjectionMatrix();
    };
    if (this.foot._modelId !== s.model) {
      const old = this.foot.rig;
      this.foot._modelId = s.model;
      this.foot._loadBody().then(() => {
        if (old && old !== this.foot.rig) g.scene.remove(old.obj);
        back();
      });
    } else {
      back();
    }
    this._saved = null;
    return true;
  }

  // ---- the clock --------------------------------------------------------
  // Called BEFORE the hero updates: advance the edit, fire whatever beats the
  // last frame crossed, and tell the hero which way the keys point.
  update(dt) {
    if (!this.active) return;
    if (this.paused) return;
    this.t += dt;
    if (this.t >= this._sc.dur) this._reset();
    const beats = this._sc.beats;
    while (this._beat < beats.length && this.t >= beats[this._beat].t) {
      const beat = beats[this._beat++];
      try { beat.go(this); } catch (e) { console.error(`cinema beat ${beat.t}s threw:`, e); }
    }
    if (this._sc.flight) {
      // HE GETS IN, once, on the beat. _sitFromHere is the last leg of the
      // game's own boarding — over the sill and down into the seat — so the
      // film shows the move the player's Enter makes, not a special one.
      if (!this._sat && this.t >= FLY.sit) {
        this._sat = true;
        const c = this.game.plane.userData.cockpit;
        if (c) {
          const seat = this.game.plane.localToWorld(c.seat.clone());
          const fwd = this._fwd.set(0, 0, -1).applyQuaternion(this.game.plane.quaternion);
          this.foot._sitFromHere(seat, Math.atan2(-fwd.x, -fwd.z));
        }
      }
      this._flyStep(dt);
    } else this._steer(dt);
  }

  // ...and AFTER it: the chase camera has just written itself, so this is where
  // the film's own camera goes in.
  postUpdate(dt) {
    if (!this.active) return;
    this._kick *= Math.exp(-7 * dt);
    this._lights();
    this._flashLight();
    this._drawCamera();
    const dur = this._sc.dur;
    const fade = this.t < FADE ? 1 - this.t / FADE
      : this.t > dur - FADE ? 1 - (dur - this.t) / FADE : 0;
    if (this._fade) this._fade.style.opacity = fade.toFixed(3);
    if (this._hint) {
      const a = this.t > 2.2 ? 0 : 1;
      if (this._hintA !== a) { this._hintA = a; this._hint.style.opacity = a; }
    }
  }

  // ---- what the script can ask for -------------------------------------
  // Hold exactly these keys and nothing else: the reel is the only thing
  // pressing anything while it runs.
  hold(...codes) {
    const k = this.game.keys;
    k.clear();
    for (const c of codes) k.add(c);
  }

  slow(scale) { this.timeScale = scale; }

  // Which card he is working on. The arms follow it continuously from here, so
  // the gun is already on the target when the trigger goes.
  aimAt(i) {
    this._target = i;
    this._jitter = null;
    // null: he is not shooting at anything yet (he is climbing, with both
    // hands on the crate) -- the arms must not reach for a card
    if (i === null || i === undefined) {
      this.aim = null;
      this.aimPoint = null;
    }
  }

  // ---- scene 2's verbs --------------------------------------------------
  // WHICH WAY HE FACES, when it is not the card that decides it. Three marks
  // on the crate row, named rather than numbered: `in` at the crates, `along`
  // down the row toward the tower, `out` at the range (the way he is looking
  // when he goes over backwards). Cleared with face(null).
  face(mode) {
    const r = this._stage?.row;
    if (!r || !mode) { this._faceYaw = null; return; }
    this._faceYaw = mode === 'along' ? r.yawAlong : mode === 'out' ? r.yawOut : r.yawIn;
  }

  // Up. The game's own climb (foot.js _tryClimb): it looks where he is facing,
  // finds the lip and plans the whole pull from the body's dimensions. Free
  // running into a crate climbs it with no call at all -- this is for the
  // second one, taken from a standing start on the lid.
  // The window matters: the lip has to be within reach (1 m) when the call
  // lands, and at a run that is a tenth of a second. So the ask STANDS and is
  // retried every frame until it takes -- which is what a player does, holding
  // the button down on the way in.
  climb(win = 1.4) {
    this._climbWant = win;
    return this.foot._tryClimb();
  }

  // TWO MARKS ON THE ROW, held closed-loop rather than timed. A body that
  // accelerates, turns and climbs does not arrive anywhere on a stopwatch --
  // a tenth of a second of drift is half a metre at a run, and half a metre
  // is the difference between the lip of a crate and the floor beside it. So
  // the script names the PLACE and the key is held until he is at it.
  //
  // edge(b): backpedal to the lip he is going to go over -- he is facing the
  // range by then and the lip is behind his heels, which is exactly how a man
  // walks to the edge of something he is about to jump off backwards.
  edge(b = -0.42) { this._edgeB = b; }

  // runTo(a): forward until this far along the row, then let go -- and do the
  // next thing (take cover) at the moment he arrives rather than a beat later.
  runTo(a, then = null) { this._stopA = a; this._onStop = then; }

  // LAND IT ROLLING. foot.js turns a touchdown into a roll when the key was
  // held on the way down (_rollBuf -- Assassin's Creed's landing roll, which
  // takes the drop out of the legs). The director holds it for him: set while
  // he is in the air, spent by the landing.
  // `dir`: which way the roll goes when the feet arrive -- 'along' the row
  // (which carries him toward the corner he is heading for), 'out' away from
  // the crates, or a yaw. Armed, the body keeps facing where it was looking
  // and the tumble goes over the shoulder, so he lands still on the range.
  rollLand(dir = 'along') { this._rollLand = dir; this._rollAir = false; }

  // Over. foot.js owns the move; the drop is handed in because the body cannot
  // know it -- the ground under his feet is the crate, not the floor he lands on.
  flip(fall) {
    this.hold();
    this._edgeB = null;      // he is at the lip; the step is over
    const row = this._stage?.row;
    return this.foot._startFlip({
      fall: fall ?? row?.top ?? FLIP_FALL,
      yaw: row?.yawIn ?? this.foot.yaw,
    });
  }

  // Back to the crate: GTA's cover, the game's own (foot.js toggleCover). It
  // looks for a wall within a few steps and RUNS him to it, so the call is
  // made a stride out and the slam is his.
  cover() { return this.foot.toggleCover(); }

  // Round the edge: the same thing the right button does in cover. (Not
  // `aim` -- that is the line the rounds go down, and it is a vector.)
  sight(on) { this.game.pistol?.setAiming(!!on); }

  // THE RELOAD, behind the wall, which is where a reload is done.
  reload() {
    const p = this.game.pistol;
    if (!p || !this.foot.held) return false;
    if (this.foot._draw) this.foot._finishDraw();
    // DRY, so it is the WHOLE reload: magazine out, magazine in, and then the
    // carrier racked to chamber the first round -- which is the half of it the
    // tactical change does not have. The hand rides both (weapons.js follows
    // the CARGADOR and CAMARA bones), so the rack has a hand on it now.
    p.ammo = 0;
    return p.reload();
  }

  // Out of the leather. This is the game's own draw: foot.js lifts the pistol
  // up the holster's axis and carries it to the hand over the clip's 0.7 s
  // while the arms play the move. Slot 1 lights up as it does when a player
  // presses 1.
  // `snap`: take the weapon the instant it is asked for, with none of the
  // travel. A RIFLE'S DRAW IS A REACH OVER THE SHOULDER -- 0.8 s of the hand
  // BEHIND the body on the sling -- and after a four-metre drop the next thing
  // the film shows is him running, so that reach lands squarely in the middle
  // of the run shot with his hands behind him. A director does not wait for
  // the last inch of a draw (fire() has done the same since the first reel).
  draw(snap = false) {
    const g = this.game;
    if (!this._held) return;
    if (g.weapons) {
      g.weapons.index = this._held.slot ?? 1;
      g.weapons.slotEls?.forEach((el, k) => el.classList.toggle('sel', k === g.weapons.index));
    }
    this.foot.drawWeapon(this._held);
    if (snap && this.foot._draw) this.foot._finishDraw();
  }

  // One trigger pull at card `i`. The cooldown and the muzzle bloom are zeroed
  // first: those are there to make a PLAYER pace their shots, and a director
  // does not have to -- every round in a film lands where the cut needs it.
  fire(i) {
    const p = this.game.pistol;
    if (i !== undefined) this.aimAt(i);
    // a director does not wait for the last inch of the draw
    if (this.foot._draw) this.foot._finishDraw();
    if (!p || !this.foot.held) return false;
    p._cool = 0;
    p._bloom = 0;
    if (p.ammo !== null && p.ammo <= 0) p.ammo = p.magSize;
    this._jitter = null;                // every round its own place on the card
    this._steer(0);                     // put the aim on the card for this frame
    const fired = p.fire();
    if (fired) this._kick = Math.min(1.4, this._kick + 1);
    return fired;
  }

  roll(rel = Math.PI) {
    this.foot._rollCd = 0;
    this.foot._startRoll({ x: 0, z: 0, sx: 0, turn: 0, sprint: false },
      { yaw: this.foot.yaw + rel });
  }

  slide() {
    this.foot._slideCd = 0;
    this.foot._startSlide({ force: true, yaw: this.foot.yaw });
  }

  // All the way round, over `dur` seconds. Fed through the same channel the
  // mouse uses, so the body turns the way it turns for a player.
  spin(dur) {
    this._spin = { t: 0, dur, from: this.foot.camYaw, turn: -Math.PI * 2 };
  }

  // ---- the hero's heading and his aim ----------------------------------
  _steer(dt) {
    const g = this.game;
    const f = this.foot;
    if (!f.active) return;
    // The movement keys are read against the camera, which in a film is
    // nowhere useful -- so the hero is told to read them against himself.
    f.camRef = f.yaw;
    // THE MARKS (see edge / runTo). Both run on the ground only, and both let
    // their key go the frame he is there.
    const row = this._stage?.row;
    if (row && f.grounded && !f._climb && !f._flip) {
      const here = this._v.subVectors(f.pos, row.o);
      if (this._edgeB !== null && this._edgeB !== undefined) {
        // NOT WHILE HE IS STILL TURNING. A backpedal taken mid-turn goes
        // diagonally -- it carried him off the END of the tower instead of
        // back to its lip -- so the step waits for the body to be pointed,
        // and it never walks him off that end whatever the mark says.
        const turned = this._faceYaw === null || this._faceYaw === undefined
          || Math.abs(shortest(this._faceYaw - f.camYaw)) < 0.12;
        if (here.dot(row.n) >= this._edgeB) {      // there: the heels are on the lip
          g.keys.delete('KeyS');
          this._edgeB = null;
        } else if (turned && here.dot(row.u) > row.stack0 + 0.12) {
          g.keys.add('KeyS');
        } else {
          g.keys.delete('KeyS');
        }
      }
      if (this._stopA !== null && this._stopA !== undefined
          && here.dot(row.u) >= this._stopA) {
        g.keys.delete('KeyW');
        this._stopA = null;
        const then = this._onStop;
        this._onStop = null;
        then?.(this);
      }
    }
    // ...and a drop asked to end in a roll: the key is held for as long as he
    // is off the ground, and the touchdown spends it.
    if (this._rollLand !== null && this._rollLand !== undefined && this._rollLand !== false) {
      if (!f.grounded) {
        this._rollAir = true;      // he has to LEAVE the ground before he lands on it
        f._rollBuf = 0.3;          // if the touchdown is a hard one, it takes this
      } else if (this._rollAir) {
        // ...and if it is not. A STEP OFF A CRATE IS A GENTLE LANDING, and a
        // gentle landing never goes through _land2 at all -- the ground probe
        // glues the last few centimetres down instead of crossing them, so the
        // buffered roll sat there decaying and he simply stood up (foot.js says
        // so in as many words). Two metres still deserves the roll, so the
        // director starts it himself the moment the feet are down.
        const dir = this._rollLand;
        const row = this._stage?.row;
        const yaw = dir === 'along' && row ? row.yawAlong
          : dir === 'out' && row ? row.yawOut
            : typeof dir === 'number' ? f.yaw + dir : f.yaw + Math.PI;
        this._rollLand = false;
        this._rollAir = false;
        if (!f._roll) {
          f._rollCd = 0;
          f._startRoll({ x: 0, z: 0, sx: 0, turn: 0, sprint: false }, { yaw });
        }
      }
    }
    // AN ASKED-FOR CLIMB, held at the wall until the lip is in reach.
    if (this._climbWant > 0) {
      if (f._climb) this._climbWant = 0;
      else {
        this._climbWant -= dt;
        f._tryClimb();
      }
    }
    // A CLIMB AND A FLIP OWN THE BODY. Both plan their own heading at the
    // start and solve the hero onto world points from there; a director still
    // pushing a turn into them tears the hands off the lip.
    if (f._climb || f._flip) { g.look.yaw = 0; g.look.pitch = 0; return; }
    // ...and he never runs into the booths: at the line past which it is the
    // cubicles, the forward key stops being held, whatever the script says.
    const s = this._stage;
    if (s && g.keys.has('KeyW') && !f._cover
      && this._v.subVectors(f.pos, s.o).dot(s.fwd) > s.cubicle - 0.5) g.keys.delete('KeyW');

    // aimAt(null) means NOT SHOOTING AT ANYTHING: both hands are on a crate.
    // (`?? 0` would turn that null straight back into card 0, and the hero
    // then runs at the target instead of where the script faces him.)
    const card = this._target === null || this._target === undefined
      ? null : this._targets[this._target];
    const hero = f.pos;
    if (card) {
      this._aimPoint(card);   // the sheet swings and rides a carrier: read it fresh
      // a hand's worth of scatter per shot, so the group is a group and not one
      // hole shot nine times
      if (!this._jitter) {
        this._jitter = new THREE.Vector3(
          (Math.random() - 0.5) * 0.14, (Math.random() - 0.5) * 0.11, 0);
      }
      const p = this._v.copy(card.aimAt).add(this._jitter);
      this.aimPoint = this.aimPoint ? this.aimPoint.copy(p) : p.clone();
      this.aim = (this.aim ?? new THREE.Vector3())
        .subVectors(this.aimPoint, this._look.set(hero.x, hero.y + CHEST, hero.z))
        .normalize();
    }

    // Where he faces: a named mark on the crates, the card, or round and round
    // while the spin lasts.
    let want;
    if (this._faceYaw !== null && this._faceYaw !== undefined) {
      want = this._faceYaw;      // a named mark wins: the director said so
    } else if (this._spin) {
      this._spin.t += dt;
      const k = clamp01(this._spin.t / this._spin.dur);
      want = this._spin.from + this._spin.turn * smooth(k);
      if (k >= 1) this._spin = null;
    } else if (this.aim) {
      want = Math.atan2(-this.aim.x, -this.aim.z);
    }
    // ...and HOW he is turned, which is not one thing. WITH A WEAPON OUT the
    // body is locked to the crosshair, so the mouse turns it and the director
    // writes the mouse. BARE-HANDED -- which is the whole first half of scene
    // 2, both hands needed for the crates -- the mouse only swings the camera
    // and the body is steered with A and D at its own rate (foot.js, WoW's
    // keyboard turn). So the director holds the turn key down, the way a
    // player would, and lets go when he is pointed the right way.
    g.keys.delete('KeyA');
    g.keys.delete('KeyD');
    if (want !== undefined && dt > 0) {
      const d = shortest(want - f.camYaw);
      if (f.held) {
        const rate = this._spin ? 14 : 9;   // rad/s -- a flick, not a servo
        g.look.yaw = Math.abs(d) < rate * dt ? d : Math.sign(d) * rate * dt;
      } else {
        g.look.yaw = 0;
        if (Math.abs(d) > TURN_DEAD) g.keys.add(d > 0 ? 'KeyA' : 'KeyD');
      }
    } else {
      g.look.yaw = 0;
    }
    g.look.pitch = this.aim ? -Math.asin(THREE.MathUtils.clamp(this.aim.y, -1, 1)) : 0;
  }

  // ---- the camera -------------------------------------------------------
  _drawCamera() {
    const g = this.game;
    const S = this._S;
    const list = this._sc.shots;
    let i = list.length - 1;
    while (i > 0 && this.t < list[i].t) i--;
    const shot = list[i];
    if (i !== this._shot) {
      this._shot = i;
      this._seed = Math.random() * 100;   // a new shot is a new operator
    }
    const k = clamp01((this.t - shot.t) / shot.d);
    const pos = shot.cam(k, S);
    const look = shot.look(k, S);
    // NOT IN THE BOOTHS. However a shot is written and wherever the hero has
    // drifted, the lens is never between a booth's walls: the open floor is
    // fine, the lane beyond the counter is fine (that is where the paper is),
    // and across the booths themselves it goes OVER the dividers.
    const st = this._stage;
    const z = st.cubicle === undefined ? 0 : this._v.subVectors(pos, st.o).dot(st.fwd);
    if (st.cubicle !== undefined && z > st.cubicle && z < st.counter) {
      const top = st.y + st.wallTop + 0.45;
      if (pos.y < top) pos.y = top;
    }
    // HANDHELD. Not noise for its own sake: a frame that is perfectly still
    // reads as an animation, and a frame that wanders reads as a camera. Two
    // sines well off each other's period, scaled per shot, plus the jolt a
    // round going off puts through the operator's arms.
    const amp = (shot.shake ?? 0) * 0.014 * (1 + this._kick * 2.2);
    const tt = this.t * 2 + this._seed;
    pos.x += Math.sin(tt * 2.3) * amp + Math.sin(tt * 5.7) * amp * 0.4;
    pos.y += Math.sin(tt * 1.9 + 1.1) * amp * 0.8 + Math.sin(tt * 6.3) * amp * 0.35;
    pos.z += Math.sin(tt * 2.7 + 2.2) * amp * 0.6;

    g.camera.position.copy(pos);
    // WHICH WAY IS UP IN FRAME. Normally world up, but a shot looking
    // STRAIGHT DOWN has no answer — lookAt's basis collapses and the frame
    // spins on its own rounding. A top-down names its own up (the way the
    // aeroplane is pointing), which is also what puts the city streaming
    // from the bottom of frame to the top instead of sideways.
    if (shot.up) g.camera.up.copy(shot.up(k, S)).normalize();
    else g.camera.up.set(0, 1, 0);
    g.camera.lookAt(look);
    const roll = shot.roll
      ? shot.roll[0] + (shot.roll[1] - shot.roll[0]) * smooth(k) : 0;
    g.camera.rotateZ(roll + Math.sin(tt * 1.7) * amp * 0.5 + this._kick * 0.012);
    const fov = shot.fov[0] + (shot.fov[1] - shot.fov[0]) * smooth(k);
    if (Math.abs(g.camera.fov - fov) > 0.01) {
      g.camera.fov = fov;
      g.camera.updateProjectionMatrix();
    }
    g.camera.updateMatrixWorld();
  }

  // THE CUT COSTS A FRAME, so pay for it before the clock starts.
  //
  // At t=FLY.cut the camera jumps 18 km into town. The cull sees a teleport,
  // sweeps every record and attaches ~580 building chunks in a SINGLE frame;
  // each one uploads its buffers on first draw and ten new shader programs
  // compile with them. Measured: 303 ms on exactly the frame the first city
  // shot opens on — a stutter on the cut, every single take.
  //
  // So we fly the cull down the run-in now, while the overlay is still coming
  // up, and draw each vantage into a 64 px target: the fill is free, but the
  // uploads and the compiles happen HERE. Removing the chunks afterwards does
  // not free the buffers (only geometry.dispose() does) nor the programs, so
  // by the time the cut comes round it is a re-attach of resident geometry.
  async _warmCity() {
    const g = this.game, st = this._stage;
    if (!st?.city || !g.renderer) return;
    const t0 = performance.now();
    this._flyLook(true);           // warm what the SHOTS draw, not more
    const rt = this._warmRT ||= new THREE.WebGLRenderTarget(64, 64);
    const cam = new THREE.PerspectiveCamera(100, 1, 1, 9000);
    const fwd = new THREE.Vector3(-Math.sin(st.city.yaw), 0, -Math.cos(st.city.yaw));
    const look = new THREE.Vector3();
    const prevRT = g.renderer.getRenderTarget();
    let warmed = 0;
    // three stations down the run-in, each looked at twice: along the nose for
    // the low shots, and plumb down for the god's eye
    for (const k of [0, 0.55, 1.1]) {
      const p = this._v.copy(st.city.at).addScaledVector(fwd, FLY_RUN * k * 1.35);
      cam.position.set(p.x, st.city.base + FLY_ALT, p.z);
      g.world.update(cam.position, 0.016);
      for (const aim of [look.copy(p).addScaledVector(fwd, 800).setY(st.city.base),
        look.clone().set(p.x + 1, st.city.base, p.z)]) {
        cam.lookAt(aim);
        cam.updateMatrixWorld(true);
        g.renderer.setRenderTarget(rt);
        g.renderer.render(g.scene, cam);
        warmed++;
      }
      await new Promise((r) => requestAnimationFrame(r));   // one task per station
    }
    g.renderer.setRenderTarget(prevRT);
    g.world.update(g.camera.position, 0.016);               // and put the airport back
    console.log(`cinema: city warmed in ${(performance.now() - t0).toFixed(0)} ms `
      + `(${warmed} draws, ${g.renderer.info.memory.geometries} geometries resident)`);
  }

  // The flypast's draw budget and its haze, on and off together — see
  // FLY_CULL. The fog object is the scene's own, so the density is saved and
  // put back; everything else in the game reads the same fog.
  _flyLook(on) {
    const g = this.game;
    if (on) {
      if (this._fogWas === undefined) this._fogWas = g.scene.fog?.density ?? null;
      g.world.cullDist = FLY_CULL;
      g.world.propScale = FLY_PROPS;
      g.world._cullFull = true;
    } else {
      g.world.cullDist = 0;
      g.world.propScale = 0;
      g.world._cullFull = true;
      if (g.scene.fog && this._fogWas != null) g.scene.fog.density = this._fogWas;
      this._fogWas = undefined;
    }
  }

  // The stage frame, handed to every shot in the cut list.
  // THE STAGE: two places, because the film cuts between them. The run-up and
  // the take-off happen on the real runway (the city spawn is on 31L); the
  // last two shots are over the middle of town, which is 18 km away and a cut
  // apart.
  _flightStage() {
    const g = this.game;
    const sp = g.world.spawn ?? { x: 0, z: 0, yaw: 0 };
    const rwYaw = sp.yaw ?? 0;
    const rwFwd = new THREE.Vector3(-Math.sin(rwYaw), 0, -Math.cos(rwYaw));
    const rwGround = g._groundAt(sp.x, sp.z, 600, 900) ?? 0;
    const cityYaw = FLY_HEAD;
    const cityFwd = new THREE.Vector3(-Math.sin(cityYaw), 0, -Math.cos(cityYaw));
    const cityStart = new THREE.Vector3().addScaledVector(cityFwd, -FLY_RUN);
    const cityGround = g.world.heightAt?.(cityStart.x, cityStart.z);
    return {
      o: new THREE.Vector3(sp.x, rwGround, sp.z),
      fwd: rwFwd,
      right: new THREE.Vector3(-rwFwd.z, 0, rwFwd.x),
      y: rwGround,
      runway: { at: new THREE.Vector3(sp.x, rwGround, sp.z), yaw: rwYaw, fwd: rwFwd },
      city: {
        at: cityStart,
        yaw: cityYaw,
        base: (cityGround === null || cityGround === undefined) ? 0 : cityGround,
      },
      fly: { pos: new THREE.Vector3(), yaw: 0, y: 0, roll: 0, v: 0, rpm: 0, air: false },
    };
  }

  // ONE FRAME OF THE WHOLE SEQUENCE, driven off the EDIT's clock rather than
  // its own, so the propeller catches on the cut it is supposed to catch on
  // however the frame rate wanders. Only the position is integrated, because
  // only the position has to be continuous.
  _flyStep(dt) {
    const st = this._stage;
    const f = st?.fly;
    if (!f) return;
    const g = this.game;
    const T = this.t;
    const seg = (a, b) => clamp01((T - a) / (b - a));

    if (T < FLY.cut) {
      // ---- on the ground, at the airport -------------------------------
      // revs: nothing, then the start, then idle, then everything
      f.rpm = T < FLY.light ? 0
        : T < FLY.roll ? IDLE_RPM * smooth(seg(FLY.light, FLY.idle))
          : IDLE_RPM + (1 - IDLE_RPM) * smooth(seg(FLY.roll, FLY.rotate));
      if (T >= FLY.roll) f.v = Math.min(FLY_SPEED, f.v + FLY_ACC * dt);
      f.pos.addScaledVector(st.runway.fwd, f.v * dt);
      const gy = g._groundAt(f.pos.x, f.pos.z, f.y + 30, 60) ?? st.runway.at.y;
      if (T < FLY.rotate) {
        f.y = gy + 1.7;
        f.roll = 0;
        f.pitch = 0;
        f.air = false;
      } else {
        f.air = true;
        f.y += FLY_CLIMB * smooth(seg(FLY.rotate, FLY.rotate + 0.8)) * dt;
        if (f.y < gy + 1.7) f.y = gy + 1.7;
        f.pitch = 0.17 * smooth(seg(FLY.rotate, FLY.rotate + 0.6));
        f.roll = 0;
      }
      f.yaw = st.runway.yaw;
    } else {
      // ---- and after the cut, over the city ----------------------------
      if (!f.inCity) {
        f.inCity = true;
        f.pos.copy(st.city.at);
        f.yaw = st.city.yaw;
        f.y = st.city.base + FLY_ALT;
        f.v = FLY_SPEED;
        f.rpm = 1;
        f.air = true;
      }
      // THE HAZE BELONGS TO THE CITY, not to the airport. It is there to meet
      // the shortened draw distance (FLY_CULL) so the far edge of town
      // dissolves instead of ending on a line — and over the runway there is
      // no such edge to hide, only a milky grey wash over a shot that was
      // clean. It comes up across the cut and goes back on teardown.
      if (g.scene.fog) g.scene.fog.density = FLY_FOG;
      const turn = FLY_TURN * smooth(seg(FLY.cut + 0.4, FLY.cut + 2.4));
      f.yaw += turn * dt;
      f.roll = Math.atan((FLY_SPEED * turn) / G_ACC);
      f.pitch = 0.035;
      f.pos.addScaledVector(this._fwd.set(-Math.sin(f.yaw), 0, -Math.cos(f.yaw)), FLY_SPEED * dt);
      f.y -= FLY_SINK * dt;
      // never into the hillside: the run-in crosses ridges 200 m up
      const h = g.world.heightAt?.(f.pos.x, f.pos.z);
      if (h !== null && h !== undefined && f.y < h + 95) f.y = h + 95;
    }

    const p = g.plane;
    p.position.set(f.pos.x, f.y, f.pos.z);
    p.quaternion.setFromEuler(this._flyEuler.set(f.pitch ?? 0, f.yaw, -f.roll, 'YXZ'));
    p.updateMatrixWorld(true);
    g.airborne = f.air;
    g.speed = f.v;
    g.vel.set(-Math.sin(f.yaw), 0, -Math.cos(f.yaw)).multiplyScalar(f.v);
    // THE ENGINE AND THE PROPELLER. Both live in _flightUpdate, which a reel
    // never calls — the first take was a silent aeroplane with a propeller
    // standing still in the air. `start()` is in there too, so it has to be
    // called and not merely updated.
    if (g.sound) {
      if (g.settings?.state?.engine !== false) g.sound.start();
      if (f.rpm > 0 && g.sound.state !== 'running') {
        g.sound.setPlane?.(g.def);
        g.sound.state = 'running';
      }
      g.sound.update(dt, { speed: Math.abs(f.v), throttle: f.rpm > IDLE_RPM ? 1 : 0, airborne: f.air });
    }
    spinPropellers(g.plane, f.rpm * 2700, dt);
  }

  _stageApi() {
    const st = () => this._stage;
    const P = (x, y, z) => {
      const s = st();
      return new THREE.Vector3()
        .copy(s.o)
        .addScaledVector(s.right, x)
        .addScaledVector(s.fwd, z)
        .setY(s.y + y);
    };
    return {
      p: P,
      // ---- the flypast's frame ------------------------------------------
      // A point in the AEROPLANE's own space: +x right wing, +y up, -z nose.
      // Everything in scene 3 is written in it, so a shot stays framed
      // however far round the turn the machine has got.
      plane: (x, y, z) => this.game.plane.localToWorld(new THREE.Vector3(x, y, z)),
      span: () => this.game.plane.userData.halfSpan ?? 5,
      // the way the nose is pointing, in the world
      fwdVec: () => new THREE.Vector3(0, 0, -1).applyQuaternion(this.game.plane.quaternion),
      // THE PILOT'S OWN SEAT, wherever planes.js measured it on this
      // airframe, plus an offset. Hard numbers framed the Spitfire's cockpit
      // on the aerobatic's and vice versa — the seat is 2 m further back on
      // one than the other.
      seat: (x, y, z) => {
        const c = this.game.plane.userData.cockpit;
        const o = c ? c.seat : { x: 0, y: 0.6, z: 0 };
        return this.game.plane.localToWorld(new THREE.Vector3(o.x + x, o.y + y, o.z + z));
      },
      // d metres off the nose, y above the nose: the look-at that puts the
      // horizon where you want it and the city under it
      ahead: (d, y = 0) => this.game.plane.localToWorld(new THREE.Vector3(0, y, -d)),
      // out in front of the SPINNER: x across, y up, z forward of the nose
      nose: (x, y, z) => {
        const b = new THREE.Box3().setFromObject(this.game.plane, true);
        const n = this.game.plane.worldToLocal(new THREE.Vector3(
          this.game.plane.position.x, this.game.plane.position.y, this.game.plane.position.z));
        return this.game.plane.localToWorld(new THREE.Vector3(x, y, n.z - (b.max.z - b.min.z) * 0.5 - z));
      },
      // A POINT PLANTED ON THE GROUND that the aeroplane goes past: x across
      // its line, y off the ground, z along the line from where it started.
      // It does not move, which is the whole point of shot 3.
      ground: (x, y, z) => {
        const r = st().runway;
        return new THREE.Vector3(r.at.x, r.at.y + y, r.at.z)
          .addScaledVector(r.fwd, z)
          .addScaledVector(new THREE.Vector3(-r.fwd.z, 0, r.fwd.x), x);
      },
      // a point fixed in the WORLD, placed off the line the aeroplane flies:
      // x across it, y up, z along it from the stage origin
      mark: (x, y, z) => {
        const f = st().fly;
        const fwd = new THREE.Vector3(-Math.sin(f.yaw), 0, -Math.cos(f.yaw));
        return new THREE.Vector3(f.pos.x, f.y, f.pos.z)
          .addScaledVector(fwd, z)
          .addScaledVector(new THREE.Vector3(-fwd.z, 0, fwd.x), x)
          .setY(f.y + y);
      },
      // straight down from the aeroplane, d metres: the look-at that tips a
      // shot off the horizon and onto the roofs
      below: (d) => {
        const p = this.game.plane.position;
        return new THREE.Vector3(p.x, p.y - d, p.z);
      },
      actor: (x, y, z) => {
        const s = st();
        const h = this.foot.pos;
        return new THREE.Vector3(h.x, h.y + y, h.z)
          .addScaledVector(s.right, x)
          .addScaledVector(s.fwd, z);
      },
      // THE CRATE ROW'S OWN FRAME (scene 2): `a` along the row from its left
      // end, `b` out of the face he hides behind, y off the floor.
      row: (a, b, y) => st().row.at(a, b, y, new THREE.Vector3()),
      rowU: () => st().row.u,                       // along the row, for an offset
      heroA: () => {                                 // where the hero is along it
        const r = st().row;
        return this._v.subVectors(this.foot.pos, r.o).dot(r.u);
      },
      // r metres from the hero at stage angle a (0 = down range, + = to his
      // right), y above his feet
      orbit: (a, r, y) => {
        const s = st();
        const h = this.foot.pos;
        return new THREE.Vector3(h.x, h.y + y, h.z)
          .addScaledVector(s.fwd, Math.cos(a) * r)
          .addScaledVector(s.right, Math.sin(a) * r);
      },
      card: (i, x, y, z) => {
        const t = this._targets[i] ?? this._targets[0];
        const s = st();
        return new THREE.Vector3().copy(t.aimAt)
          .addScaledVector(s.right, x)
          .addScaledVector(s.fwd, z)
          .setY(t.aimAt.y + y);
      },
      round: () => {
        const b = this.game.bullets?.live?.[0];
        return b ? new THREE.Vector3().copy(b.pos) : null;
      },
      // the holster on his belt, where the draw starts
      hip: () => {
        const sk = this.foot.holsterSocket?.sockets?.[0];
        if (!sk) {
          const s = st();
          const h = this.foot.pos;
          return new THREE.Vector3(h.x, h.y + 0.9, h.z).addScaledVector(s.right, 0.2);
        }
        sk.updateWorldMatrix(true, false);
        return new THREE.Vector3().setFromMatrixPosition(sk.matrixWorld);
      },
      mix: (a, b, k) => a.lerp(b, k),
    };
  }

  // A muzzle flash is a light, and in a film it is the only light for a frame.
  _flashLight() {
    const p = this.game.pistol;
    if (!this._flash) return;
    // Driven by INTENSITY, never by .visible: three counts only the visible
    // lights, so flipping this one per muzzle flash changed the scene's light
    // count and recompiled every material in shot -- about 36 programs, a
    // multi-hundred-ms freeze, on every bang of the reel. Same reason the
    // crash fireballs borrow from Game's pool.
    const on = p?.flash?.visible && p._flash > 0;
    if (!on) {
      this._flash.intensity = 0;
      return;
    }
    this._flash.position.copy(p.flash.position);
    this._flash.intensity = 55 * clamp01(p._flash / 0.045);
  }

  // The two lights the reel brings with it: a warm key off the hero's gun side
  // and a cold rim behind him. The city's sun is one hard source from the
  // south-east, which leaves whichever side of him is away from it flat.
  _lights() {
    const s = this._stage;
    if (!s || !this._key) return;
    const h = this.foot.pos;
    this._key.position.set(h.x, h.y + 3.4, h.z)
      .addScaledVector(s.right, 2.6).addScaledVector(s.fwd, 2.2);
    this._key.target.position.set(h.x, h.y + 1.1, h.z);
    this._key.target.updateMatrixWorld();
    this._rim.position.set(h.x, h.y + 2.9, h.z)
      .addScaledVector(s.right, -2.2).addScaledVector(s.fwd, -3.0);
    this._rim.target.position.set(h.x, h.y + 1.2, h.z);
    this._rim.target.updateMatrixWorld();
  }

  // ---- the loop ---------------------------------------------------------
  // Frame one, from wherever the last frame left off: he goes back to his mark
  // already walking (a standing start would show as a hitch every 10 seconds),
  // the pistol goes back in the holster, the sheets come back clean.
  _reset() {
    const g = this.game;
    const s = this._stage;
    this.t = 0;
    this._beat = 0;
    this._shot = -1;
    this.timeScale = 1;
    if (this._sc.flight) {
      // Frame one: the aeroplane parked on the runway with the propeller
      // stopped, and the pilot STANDING ON THE WING about to get in — the
      // film opens on a man climbing into an aeroplane, not on one already
      // flying.
      const f = s.fly;
      f.pos.copy(s.runway.at);
      f.yaw = s.runway.yaw;
      f.y = s.runway.at.y + 1.7;
      f.roll = 0; f.pitch = 0; f.v = 0; f.rpm = 0; f.air = false; f.inCity = false;
      this._kick = 0;
      this._sat = false;
      this.foot._boardSeq = null;
      this.foot._boardAfter = null;
      this.foot.leaveSeat();
      this._flyStep(0);
      // on the wing beside the cockpit, facing the hood
      const c = g.plane.userData.cockpit;
      const side = (c?.halfW ?? 0.7) + 0.5;
      const on = g.plane.localToWorld(new THREE.Vector3(-side, (c?.deck ?? 0.8) + 0.1, (c?.seat.z ?? 0) - 0.1));
      const top = this.foot._shellTop(on.x, on.z, on.y + 3);
      this.foot._stand(on.x, (top ?? on.y) + 0.02, on.z,
        Math.atan2(g.plane.position.x - on.x, g.plane.position.z - on.z) + Math.PI);
      g.keys.clear();
      return;
    }
    this._spin = null;
    this._kick = 0;
    this._target = this._sc.stage === 'crates' ? null : 0;
    this._jitter = null;
    this.aim = null;
    this.aimPoint = null;
    this._faceYaw = null;
    this._climbWant = 0;
    this._rollLand = false;
    this._rollAir = false;
    this._edgeB = null;
    this._stopA = null;
    this._onStop = null;
    this.game.pistol?.setAiming(false);
    this._stripHands();
    g.pistol?.setAiming(false);
    if (g.pistol) g.pistol.ammo = null;
    g.bullets?.live?.splice(0);
    for (const t of this._targets) {
      g.paper?.reset(g.paper.sheets?.get(t.card));
      t.shots = 0;
    }
    // ...and the body itself out of whatever it was in the middle of
    this.foot._flip = null;
    this.foot._climb = null;
    this.foot._roll = null;
    this.foot._slide = null;
    this.foot._air = null;
    if (this.foot._cover) this.foot._leaveCover('cut');
    this.foot.rig?.setRollTuck?.(0);
    if (this.foot.rig?.act) {
      this.foot.rig.act.quaternion.identity();
      this.foot.rig.act.position.x = 0;
      this.foot.rig.act.position.z = 0;
    }
    this._faceYaw = null;
    const mark = this._sc.place(this, s);
    const gy = this._floorAt(mark.at.x, mark.at.z, s.y + 1.5) ?? s.y;
    this.foot._stand(mark.at.x, gy + 0.02, mark.at.z, mark.yaw);
    this.foot.speed = mark.speed ?? 0;
    this.foot._moveYaw = mark.yaw;
    g.keys.clear();
  }

  // The gun out of the hand and straight back in the leather, with none of
  // the put-it-away move: a cut does not wait for a holster.
  _stripHands() {
    const f = this.foot;
    const g = this.game;
    if (f._draw) f._finishDraw();
    f._stow = null;
    f._settle = null;
    if (f.held?.obj) f.held.obj.parent?.remove(f.held.obj);
    f.held = null;
    f._handBone = null;
    g.pistol?.setVisible(false);
    f._toHolster();                     // on the hip for frame one
    if (g.weapons) g.weapons.index = -1;
  }

  // ---- the stage --------------------------------------------------------
  // THE RANGE, at a real lane: the one the hero is standing at, or the middle
  // of the first unit when he is anywhere else in the city. The stage points
  // down the rail and its origin sits a few metres behind the firing line,
  // as far back as the floor allows, so he has room to walk in and roll back.
  _rangeStage(origin) {
    const g = this.game;
    const lanes = (g.lanes?.lanes ?? []).filter((l) => l.papers?.length);
    if (!lanes.length) return null;
    const dist = (l) => Math.hypot(l.stand.x - origin.x, l.stand.z - origin.z);
    let lane = lanes.reduce((a, b) => (dist(b) < dist(a) ? b : a));
    if (dist(lane) > 40) lane = lanes[Math.min(lanes.length - 1, 4)];
    lane.index = lanes.indexOf(lane);
    const fwd = lane.rail.clone().setY(0).normalize();
    const yaw = Math.atan2(-fwd.x, -fwd.z);
    const right = new THREE.Vector3(Math.cos(yaw), 0, -Math.sin(yaw));
    // the floor behind the booth -- the warehouse slab, not the terrain the
    // shed is sunk into, and not the counter top the firing point sits on
    const probe = lane.stand.clone().addScaledVector(fwd, -2.5);
    const floor = this._floorAt(probe.x, probe.z, lane.stand.y + 0.5) ?? lane.stand.y - 1.5;
    const ray = new THREE.Raycaster();
    ray.firstHitOnly = true;
    const list = g.world.collidablesNear
      ? g.world.collidablesNear(lane.stand.x, lane.stand.z) : (g.world.collidables ?? []);
    const from = new THREE.Vector3();
    const cast = (p, dir, far) => {
      ray.set(p, dir);
      ray.far = far;
      return list.length ? ray.intersectObjects(list, true)[0] : null;
    };
    // how far back there is floor to walk in across: the walk-in starts
    // START_Z behind the origin and the roll goes further back still
    let clear = 14;
    const back = fwd.clone().negate();
    for (const ox of [0, 1.0, -1.0]) {
      from.copy(lane.stand).addScaledVector(right, ox).setY(floor + 1.0);
      const h = cast(from, back, 14);
      if (h) clear = Math.min(clear, h.distance);
    }
    const behind = THREE.MathUtils.clamp(clear - 4.2, BEHIND_MIN, BEHIND);
    // THE BOOTHS. A firing point is a cubicle: a counter at the line and a
    // divider wall either side reaching back from it. Nobody in this film --
    // the hero or the camera -- goes inside one: the walls hide whoever is
    // between them from any lens that is not, and a lens between them sees
    // wall. How far back the dividers reach is read off the walls themselves,
    // along the divider line half a booth to each side, so `cubicle` is the
    // stage z past which it is the booths.
    const pitch = lanes.length > 1
      ? Math.abs(this._v.subVectors(lanes[1].stand, lanes[0].stand).dot(right)) || 1.67 : 1.67;
    let wall = 0;
    for (const ox of [pitch / 2, -pitch / 2]) {
      from.copy(lane.stand).addScaledVector(right, ox).addScaledVector(back, 10).setY(floor + 1.0);
      const h = cast(from, fwd, 11);
      if (h) wall = Math.max(wall, 10 - h.distance);
    }
    wall = THREE.MathUtils.clamp(wall, 0.8, 3.0);
    const cubicle = behind - wall - 0.4;     // and a body's width of margin
    // ...and how tall the dividers are, for a lens that has to cross the
    // booths on its way to the paper: it goes over them, never between.
    from.copy(lane.stand).addScaledVector(right, pitch / 2).addScaledVector(back, 0.7).setY(floor + 5);
    const top = cast(from, new THREE.Vector3(0, -1, 0), 5);
    const wallTop = THREE.MathUtils.clamp(top ? top.point.y - floor : 2.2, 1.2, 3.5);
    const counter = behind + 1.0;            // past this it is the lane, not the booth
    const o = lane.stand.clone().addScaledVector(fwd, -behind);
    o.y = this._floorAt(o.x, o.z, floor + 1.5) ?? floor;
    return { lane, lanes, yaw, o, y: o.y, fwd, right, behind, wall, wallTop, cubicle, counter, pitch };
  }

  // ---- the crates -------------------------------------------------------
  // SCENE 2'S STAND: the range's frame, with its origin carried back to the
  // row of ammo crates on the warehouse floor, plus the row's own axes.
  //
  // The crates are found by their COLLIDERS -- `ammo_box_shell` and friends --
  // and never by the crate's own name: the GLB's one mesh arrives as five
  // primitives that the loader names ammo_box_1 .. ammo_box_5, so a traverse
  // for "ammo_box_2" finds a PLANK inside the first crate. Each shell is the
  // crate's exact box (0.97 x 0.40 x 0.44 at scale 5, origin on the floor),
  // so the row's length, depth and both lid heights are measured, not assumed.
  _crateStage(origin) {
    const g = this.game;
    const shell = {};
    g.scene.traverse((o) => {
      if (/^ammo_box(_pair|_stack)?_shell$/.test(o.name)) shell[o.name] = o;
    });
    const one = shell.ammo_box_shell;
    const two = shell.ammo_box_pair_shell;
    const top = shell.ammo_box_stack_shell;
    if (!one || !two || !top) {
      console.warn('cinema: the ammo crates are not in the world yet',
        Object.keys(shell));
      return null;
    }
    for (const m of [one, two, top]) m.updateWorldMatrix(true, false);
    const P = (m) => new THREE.Vector3().setFromMatrixPosition(m.matrixWorld);
    const c1 = P(one), c2 = P(two), c3 = P(top);
    // THE LANE IS THE CRATES' LANE, not the one the player happens to be
    // standing at: the stand is built where the tower is, and the sheet the
    // lean opens onto has to be the one in front of it.
    const s = this._rangeStage(c3);
    if (!s) return null;
    const box = new THREE.Box3().setFromObject(one);
    const size = box.getSize(new THREE.Vector3());
    const crateH = size.y;                                   // 2.0 m: one crate
    // along the row, toward the tower; and out of the face he hides behind
    const u = c2.clone().sub(c1).setY(0).normalize();
    const cl = c1.distanceTo(c2);                            // one crate, end to end
    const len = 2 * cl;                                      // ...and the row is two of them
    const half = cl / 2;
    const depth = new THREE.Vector3().setFromMatrixColumn(one.matrixWorld, 2).length()
      * one.geometry.parameters.depth;
    const n = new THREE.Vector3(-u.z, 0, u.x);
    if (n.dot(s.fwd) > 0) n.negate();                        // AWAY from the range
    const o = c1.clone().addScaledVector(u, -half).addScaledVector(n, depth / 2);
    o.y = this._floorAt(o.x, o.z, c1.y + 1.5) ?? c1.y;
    const row = {
      o, u, n, len, depth, crateH,
      lid: crateH,                                           // the first crate's top
      top: (c3.y - o.y) + crateH,                            // the tower's top
      stack0: c3.clone().sub(o).dot(u) - half,               // where the tower starts along the row
      // A yaw faces (-sin, 0, -cos). `in` is at the crates, which is also at
      // the range beyond them (-n); `out` is away from both (+n); `along` runs
      // down the row toward the tower (+u).
      yawIn: Math.atan2(n.x, n.z),
      yawOut: Math.atan2(-n.x, -n.z),
      yawAlong: Math.atan2(-u.x, -u.z),
      at: (a, b, y, out = new THREE.Vector3()) => out.copy(o)
        .addScaledVector(u, a).addScaledVector(n, b).setY(o.y + y),
      // how far across the booths the row's far corner sits, for the lane the
      // lean opens onto
      cardX: 0,
    };
    const corner = row.at(len - S2_CORNER_IN, 0, 0, new THREE.Vector3());
    row.cardX = this._v.subVectors(corner, s.lane.stand).dot(s.right);
    // ...and the lane in front of the spot he climbs to, which is the card he
    // shoots from up on the crate (he never turns to find one)
    const shootFrom = row.at(S2_START.a, 0, 0, new THREE.Vector3());
    row.boxX = this._v.subVectors(shootFrom, s.lane.stand).dot(s.right);
    // the booths are further ahead now that the origin has moved back
    const shift = this._v.subVectors(o, s.o).dot(s.fwd);
    s.cubicle -= shift;
    s.counter -= shift;
    s.o = o;
    s.y = o.y;
    s.row = row;
    console.log(`cinema: crates — row ${len.toFixed(2)} x ${depth.toFixed(2)} m, `
      + `lid ${row.lid.toFixed(2)} m, tower ${row.top.toFixed(2)} m from ${row.stack0.toFixed(2)} m along, `
      + `corner ${row.cardX.toFixed(2)} m across the booths, ${(-shift).toFixed(1)} m behind the stage origin`);
    return s;
  }

  // The floor under a point: the first surface below it, the terrain only
  // when there is none. g._groundAt takes the higher of the two, and inside
  // the warehouse that is the hillside the shed is dug into, 3.5 m up.
  _floorAt(x, z, fromY) {
    const g = this.game;
    const list = g.world.collidablesNear ? g.world.collidablesNear(x, z) : (g.world.collidables ?? []);
    if (list.length) {
      const ray = new THREE.Raycaster(new THREE.Vector3(x, fromY, z), new THREE.Vector3(0, -1, 0), 0, 8);
      ray.firstHitOnly = true;
      const h = ray.intersectObjects(list, true)[0];
      if (h) return h.point.y;
    }
    return g.world.heightAt?.(x, z) ?? null;
  }

  // The lane whose firing point is at a stage x -- the booth two over, say --
  // among the lanes that run the same way as the stage.
  _laneAt(x) {
    const s = this._stage;
    let best = null;
    let bestD = 0.9;
    for (const l of s.lanes) {
      if (l.rail.dot(s.fwd) < 0.98) continue;
      const d = Math.abs(this._v.subVectors(l.stand, s.lane.stand).dot(s.right) - x);
      if (d < bestD) { bestD = d; best = l; }
    }
    return best;
  }

  // The sheet on a carrier: the mesh papertargets.js adopted.
  _sheetOf(lane) {
    const paper = this.game.paper;
    let m = null;
    for (const p of lane.papers) {
      p.traverse((o) => { if (!m && o.isMesh && paper?.sheets?.has(o)) m = o; });
      if (m) break;
    }
    return m;
  }

  // Run a carrier to `dist` metres from the firing line -- or back to a saved
  // `at` -- the way rangelane.js does, without the motor: a cut does not wait
  // for a trolley.
  _runTo(lane, dist, at = null) {
    lane.at = at ?? THREE.MathUtils.clamp(dist - lane.range, lane.min, lane.max);
    lane.v = 0;
    lane.obj.position.copy(lane.home).addScaledVector(lane.rail, lane.at);
    lane.obj.parent?.worldToLocal(lane.obj.position);
    lane.obj.updateMatrixWorld(true);
    return lane.range + lane.at;
  }

  // WHERE ON THE SHEET. The B-27's X ring sits where a chest sits, not in the
  // middle of the paper, so that is where the rounds go: the card's own axes
  // (the ones papertargets.js scores on), read fresh, because the sheet swings
  // on its top edge and rides a carrier.
  _aimPoint(t) {
    const m = t.mesh;
    const card = this.game.paper?.sheets?.get(m);
    m.updateWorldMatrix(true, false);
    if (!card) return t.aimAt.setFromMatrixPosition(m.matrixWorld);
    const centre = m.localToWorld(m.geometry.boundingBox.getCenter(this._v));
    const rx = this._fwd.setFromMatrixColumn(m.matrixWorld, card.rightAx).normalize();
    const uy = this._right.setFromMatrixColumn(m.matrixWorld, card.upAx).normalize();
    if (uy.y < 0) uy.negate();
    return t.aimAt.copy(centre)
      .addScaledVector(rx, (B27.cu - 0.5) * card.W)
      .addScaledVector(uy, (0.5 - B27.cv) * card.H);
  }

  // The scene's weapon, loaded once per weapon and kept: the pistol for the
  // first film, the AKM for the second (a rifle slings on the back, which is
  // the only way to carry one up a crate).
  async _loadProps() {
    // The flypast carries no weapon: the pilot's hands are on the stick.
    if (!this._sc.gun) { this._held = null; return; }
    const def = WEAPON_DEFS[this._sc.gun];
    this._guns ??= {};
    if (!this._guns[def.id]) {
      // THE GAME'S OWN LOADER, not a bare glTF load. loadWeapon is what hangs
      // the weapon's PARTS ANIMATION off it (obj.userData.anim): the AKM's
      // magazine on its CARGADOR bone, the bolt carrier, the trigger. Loading
      // the file here by hand gave the reel a weapon with no moving parts at
      // all -- the magazine never left the well through the whole reload, in
      // the film only (tools/_probe_reelmag.mjs), while the same reload in
      // the game worked. It also measures the muzzle, the port and the butt.
      this._guns[def.id] = { ...def, obj: await loadWeapon(def) };
    }
    this._held = this._guns[def.id];
  }

  _buildStage() {
    const g = this.game;
    const s = this._stage;
    this._props = new THREE.Group();
    this._props.name = 'cinema_stage';
    g.scene.add(this._props);

    // THE CARDS ARE THE RANGE'S. Each is the sheet on a real carrier, run out
    // to its mark on the rail -- the same target the F/G buttons drive, with
    // the same printed silhouette, scored by the same papertargets.js -- and
    // where the booth two over has no lane, his own sheet stands in.
    this._targets.length = 0;
    this._moved = [];
    const cards = typeof this._sc.cards === 'function' ? this._sc.cards(s) : this._sc.cards;
    for (const c of cards) {
      // `x` is an offset from HIS lane across the booths; `at` is the same
      // thing named outright (scene 2 takes whichever lane its corner opens
      // onto). `z` is from the stage origin, `dist` is from the firing line.
      const off = c.at ?? c.x ?? 0;
      const lane = (!c.at && !c.x ? s.lane : this._laneAt(off)) ?? s.lane;
      const mesh = this._sheetOf(lane);
      if (!mesh) continue;
      if (!this._moved.some((m) => m.lane === lane)) {
        this._moved.push({ lane, at: lane.at });
        // the firing line is `behind` ahead of the stage origin: a sheet 11 m
        // from his mark is 7.5 m down the lane
        this._runTo(lane, c.dist ?? (c.z - s.behind));
      }
      const t = { lane, mesh, card: mesh, shots: 0, dist: lane.range + lane.at,
        aimAt: new THREE.Vector3() };
      this._aimPoint(t);
      this._targets.push(t);
    }
    if (!this._targets.length) throw new Error('no sheets hanging in the range');

    // the reel's own two lights
    this._key = new THREE.SpotLight(0xffe6c4, 120, 16, 0.75, 0.6, 1.6);
    this._rim = new THREE.SpotLight(0xa8ccff, 90, 16, 0.8, 0.7, 1.6);
    for (const l of [this._key, this._rim]) {
      l.castShadow = false;
      this._props.add(l);
      this._props.add(l.target);
    }
    this._flash = new THREE.PointLight(0xffcf8a, 0, 14, 2);
    this._props.add(this._flash);   // stays visible at intensity 0, see _flashLight
  }

  _teardown() {
    const g = this.game;
    this._flyLook(false);       // draw distance and haze back to the city's own
    // the carriers back where the player left them, and the reel's pistol
    // off the belt: the sheets stay, holes and all, they are the range's
    for (const m of this._moved ?? []) this._runTo(m.lane, 0, m.at);
    this._moved = [];
    this._targets.length = 0;
    if (this._held) {
      this.foot._fromHolster(this._held);
      this._held.obj?.parent?.remove(this._held.obj);
    }
    if (this._props) {
      // the held copy is parented to a hand by now; the stage owns the rest
      this._props.parent?.remove(this._props);
      this._props = null;
    }
    this._key = this._rim = this._flash = null;
    this._stage = null;
  }

  // ---- the frame around the film ---------------------------------------
  _showOverlay() {
    if (!document.getElementById('cinema-css')) {
      const st = document.createElement('style');
      st.id = 'cinema-css';
      st.textContent = `
/* Every piece of interface, gone. The film is the only thing on screen. */
body.cinema-mode #hud,
body.cinema-mode #mapwin,
body.cinema-mode #settingswin,
body.cinema-mode #target-card,
body.cinema-mode #chrome { display: none !important; }
body.cinema-mode #game-container canvas { cursor: none; }
#cinema-overlay { position: fixed; inset: 0; z-index: 90; pointer-events: none; }
/* 2.39:1 bars -- the shape says film before a single frame has played */
#cinema-overlay .cb { position: absolute; left: 0; right: 0; height: var(--cb, 0px);
  background: #000; }
#cinema-overlay .cb.t { top: 0; } #cinema-overlay .cb.b { bottom: 0; }
#cinema-overlay .cv { position: absolute; inset: 0;
  background: radial-gradient(ellipse 72% 62% at 50% 50%,
    rgba(0,0,0,0) 55%, rgba(0,0,0,.34) 100%); }
#cinema-overlay .cg { position: absolute; inset: -50%; opacity: .055;
  background-image: url("data:image/svg+xml;utf8,\
<svg xmlns='http://www.w3.org/2000/svg' width='160' height='160'>\
<filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='3'/></filter>\
<rect width='160' height='160' filter='url(%23n)'/></svg>");
  animation: cgrain .5s steps(1) infinite; }
@keyframes cgrain {
  0% { transform: translate(0,0) } 25% { transform: translate(-3%,2%) }
  50% { transform: translate(2%,-3%) } 75% { transform: translate(-2%,-2%) }
  100% { transform: translate(3%,1%) } }
#cinema-overlay .cf { position: absolute; inset: 0; background: #000; opacity: 1; }
#cinema-overlay .ch { position: absolute; right: 14px; bottom: 10px;
  font: 10px/1 ui-monospace, Menlo, monospace; letter-spacing: .18em;
  color: rgba(255,255,255,.4); transition: opacity .6s ease; }`;
      document.head.appendChild(st);
    }
    if (!this._overlay) {
      const el = document.createElement('div');
      el.id = 'cinema-overlay';
      el.innerHTML = '<i class="cb t"></i><i class="cb b"></i><i class="cv"></i>'
        + '<i class="cg"></i><i class="cf"></i><i class="ch">ESC</i>';
      document.body.appendChild(el);
      this._overlay = el;
      this._fade = el.querySelector('.cf');
      this._hint = el.querySelector('.ch');
    }
    this._overlay.style.display = '';
    this._hintA = undefined;
    this._bars();
    this._onResize ??= () => this._bars();
    window.addEventListener('resize', this._onResize);
  }

  _bars() {
    const w = window.innerWidth;
    const h = window.innerHeight;
    const bar = Math.max(0, Math.round((h - w / 2.39) / 2));
    this._overlay?.style.setProperty('--cb', `${bar}px`);
  }

  _hideOverlay() {
    if (this._overlay) this._overlay.style.display = 'none';
    if (this._onResize) window.removeEventListener('resize', this._onResize);
  }

  dispose() {
    if (this.active) this.stop();
    this._hideOverlay();
    this._overlay?.remove();
    this._overlay = null;
  }
}
