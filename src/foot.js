import * as THREE from 'three';
import { makeCharacter, raceById, HERO_HEIGHT } from './characters.js';
// `poses` rather than the three solvers by name: that is what makes retuning an
// arm show up in the running scene instead of after a reload (see weapons.js).
import { findHandBone, attachToHand, findArmChain, findArmChainL, poses, MUZZLE_DIP, GEAR_DEFS, biasWorn, loadWeapon, isHandBone, isRifle, findHandBoneL, RIFLE_CARRY_DIP, chestForward } from './weapons.js';
import { patchClass } from './hotpatch.js';
import { newGltfLoader } from './gltf.js';
import { CONTROLS } from './planes.js';
import { blendReloadPose } from './gunanim.js';
import { drawGun } from './audiofx.js';

// On-foot mode — GTA-style: park the plane, press Enter, and your personage
// climbs out and runs around the city. Enter near the plane climbs back in.
// Movement is tuned like GTA rather than like a simulation: gravity is about
// twice real so a jump is a short snappy arc instead of a floaty one, the
// character accelerates hard on the ground and barely at all in the air, and
// small obstacles are taken without the player having to aim at them.
const GRAVITY = 18.5;      // m/s² — snappy, deliberately not 9.81
const WALK_SPEED = 1.9;    // m/s — no modifier held... reserved for Ctrl
const BACK_MULT = 0.45;    // S — backpedal, much slower than a run
const SPRINT_MULT = 1.6;   // Shift
const FOOT_FOV = 62;       // deg — the game's normal lens
const AIM_FOV = 45;        // deg — GTA IV's over-the-shoulder aim lens
const ACCEL_GROUND = 16;   // m/s² — reaches jog speed in about a third of a second
const ACCEL_AIR = 4.5;     // m/s² — you keep most of the momentum you jumped with
const TURN_RATE = 3.1;     // rad/s — A/D swing the body round (~180°/s, WoW's turn)
const TURN_SPRINT = 0.7;   // a sprint has weight: it turns wider than a jog
const JUMP_V = 6.0;        // m/s — about a 1 m apex under this gravity
// THE JUMP'S BODY. The arc is physics (JUMP_V, GRAVITY); this is what the hero
// does inside it -- the Hollywood/AC leap: drive one knee, leave the take-off leg
// stretched out behind, reach with both arms, then bring the legs down together
// and bent to take the landing. See CharacterRig.setAirPose.
const JUMP_RUN = 0.25;     // × JUMP_V extra at a full sprint — a running jump is a
                           // BIGGER jump: part of the run goes into it, which is
                           // the difference between hopping a kerb and clearing
                           // the gap between two roofs.
const AIR_IN = 0.09;       // s — blend into the air pose once the feet are off
const AIR_OUT = 0.22;      // s — the knees absorbing the landing and standing up
const AIR_LEAN = 0.3;      // rad — how far the torso leans into a running leap
const LAND_DIP = 0.55;     // of a full crouch the knees give up to the landing
const COYOTE = 0.11;       // s of grace to still jump after walking off an edge
const JUMP_BUFFER = 0.14;  // s — a jump pressed just before landing still fires
const STEP_UP = 0.65;      // m — kerbs and stairs, climbed without ceremony
const CLIMB_MAX = 2.6;     // m — the tallest ledge you can pull yourself onto
const CLIMB_REACH = 1.0;   // m — how far ahead a ledge is looked for
const CLIMB_OUT = 0.22;    // s of easing out of the mantle's pose when a climb
                           // ends. Dropped in one frame, the body snapped from
                           // lying across the lip to standing in a tenth of a
                           // second -- 0.7 m of chest travel -- and whipped the
                           // slung rifle hard enough to read as it falling off.
const CLIMB_CLEAR = 1.7;   // m — headroom needed above a ledge to land on it
// ASSASSIN'S CREED'S CLIMB. Four beats, each its own phase in _climbFrame:
//   REACH  — the hands go to the lip and close on it; the body swings in under
//            them (a hop up to them if they are higher than the arms reach);
//   HANG   — held, feet in the air, until the player decides: a fresh W or
//            Space pulls up, S or C lets go. Only when the feet would actually
//            dangle (HANG_MIN) -- a kerb is a step and a parapet a vault;
//   PULL   — the elbows bend and the body rises BESIDE the wall under hands
//            that never move, the lead foot lands on the face and pushes, the
//            trailing knee comes up under the chest;
//   MANTLE — chest over the lip, palms flat on the top pushing DOWN, the lead
//            foot on the top, and the body steps up to standing.
// Every distance is planned from the BODY'S OWN dimensions (CharacterRig
// .climbMetrics): the banana's arms are 0.52 m long and the astronaut's 0.92,
// so the one hangs 1.4 m under the edge and the other 1.9.
const HANG_DROP_MAX = 2.0; // m — cap on how far under the edge the feet hang
const HANG_ARM = 0.95;     // × arm length in a dead hang: never locked straight
const HANG_OFF = 0.26;     // m — the hips hang this far off the wall face
const HANG_MIN = 0.7;      // m — the feet must dangle at least this high to HOLD a hang
const CATCH_TIME = 0.14;   // s — from standing to hands closed on a lip within reach
const CATCH_HOP = 0.30;    // s per metre of hop when the lip is above the reach: a jump, not a lift
const HANG_REACH = 0.22;   // s — an airborne catch, to hanging still
const PULL_RATE = 0.75;    // s per metre of lift -- a real pull-up, not a hoist
const PULL_MIN = 0.50;     // s — the shortest pull, straight from the catch (short arms, short lift)
const PULL_MIN_HANG = 0.60; // s — ...and out of a dead hang, where there is no momentum
const PULL_MOMENTUM = 0.75; // × when the catch flows straight into the pull
const HANG_DEAD = 0.25;    // s hung before the momentum is gone
const PULL_HIP = -0.08;    // m — the hip joints end the pull this far UNDER the lip
const CHEST_OVER = 0.12;   // m — ...with the shoulders at least this far above it
const LEAN_TOP = 0.30;     // rad — the chest folds over the edge, pivoting at the hips
const MANTLE_TIME = 0.55;  // s — chest-over to standing
const PUSH_IN = 0.12;      // m — the palms press the top this far in from the lip
const STAND_IN = 0.30;     // m — ...and the feet end this far in from the corner
const PLANT_AT = 0.40;     // pull progress when the lead foot lands on the face: a
                           // 0.6-0.75 m leg only pushes ~0.3 m of rise, so it goes on
                           // once the hips are near enough the lip to use it
const PLANT_REACH0 = 0.70; // × leg length under the hip at landing (sets the plant depth)
const PLANT_IN = 0.15;     // m — the top plant, past the corner (reach-capped)
const PLANT_RELEASE = 0.95; // leg reach at which a plant lets go: the push is spent
const PLANT_FADE = 0.10;   // s — plant weight ramps
const HAND_REACH_FROM = 0.99; // × arm length: the hands start to let go here...
const HAND_REACH_TO = 1.06;   // ...and are gone here, as the arm runs out
const HAND_OVER = 0.07;    // m — fingers over the edge, not on its corner
const HANG_SWAY_X = 0.02;  // rad — the pendulum a hanging body is, about the hands
const HANG_SWAY_Z = 0.025;
const HINT_HANG = { up: 0.05, right: 0.95, fwd: 0.3 };    // elbows out, hanging
const HINT_PULL = { up: -0.35, right: 0.75, fwd: -0.55 }; // elbows back as the shoulders pass the lip
const HINT_PRESS = { up: -0.8, right: 0.35, fwd: -0.45 }; // pressing down on the top
// a human's climbing dimensions, for a rig that cannot measure its own
const CLIMB_MET_FALLBACK = {
  shoulderY: 1.05, shoulderZ: 0.04, shoulderHalf: 0.19, armLen: 0.52,
  hipY: 0.85, hipZ: 0, hipHalf: 0.09, legLen: 0.75, ankleOff: 0.1, torso: 0.2,
};
// the pull's height curve: slow out of the hang, fast through the middle (the
// foot's push), soft into the top -- and monotone, so the body never dips
const pullLift = (u) => u - 0.12 * Math.sin(2 * Math.PI * u);
const ease01 = (k) => { const x = THREE.MathUtils.clamp(k, 0, 1); return x * x * (3 - 2 * x); }; // smoothstep, clamped
const wrapPi = (a) => Math.atan2(Math.sin(a), Math.cos(a));
const _UP = new THREE.Vector3(0, 1, 0);
const FALL_SAFE = 7;       // m — drops shorter than this cost nothing
const FALL_FATAL = 26;     // m — and this kills
// Crouch — GTA's duck, on GTA San Andreas's key (C). Slow, low and quiet: the
// posture you move in when you do not want to be noticed. These bodies have no
// crouch clip, so the pose is a squash about the FEET (the rig's origin is the
// ground, so the soles stay planted) plus a forward lean.
const CROUCH_SPEED = 1.5;   // m/s — a crouched creep, slower than a walk
// THE STROLL — Cmd. Not a slower run: a different way of carrying yourself. A
// gentleman's walk, the pace of a man who is not going anywhere in particular --
// unhurried, chest up, hands clasped behind the back, and he turns like a ship
// rather than pivoting on his heel (the carriage itself is in characters.js,
// CharacterRig.swingArms).
//
// TAPPED, not held, and that is not a style choice: Cmd HELD is a minefield in a
// browser -- Cmd+W closes the tab and Cmd+Q quits Chrome, and a web page is not
// allowed to stop either of them -- so the key is a toggle and nothing is ever
// held down together with it. Shift cancels it, because wanting to sprint is
// saying you are in a hurry after all.
const GENT_SPEED = 1.15;   // m/s — a promenade. A walk is ~1.4, Ctrl's step 1.9
const GENT_TURN = 0.5;     // × the turn rate: a gentleman does not pivot
const GENT_CYCLE = 0.34;   // the leg cycle's floor at this pace. The normal floor
                           // is 0.6, which at 1.15 m/s is a jog played slowly.
const GENT_IN = 0.22;      // s to settle into the carriage, and back out of it
const CROUCH_SQUASH = 0.26; // fallback squash for the legless bodies only
// ROLL — the dodge roll, on X. A full-body tumble: it commits (no steering
// once it starts), it covers ground faster than a sprint for half a second,
// and it comes out on the feet. NOT on a double-tap of Shift: Shift is HELD
// down for a sprint, so the one moment you most want a roll is the one moment
// a double-tap cannot be read — and a double-tap always costs a quarter second
// of waiting to see whether the second tap is coming.
const ROLL_KEY = 'KeyX';
// Three phases, because a real roll has three and they are not the same kind of
// motion: a DIVE off the legs, then the body rolling over its own rounded back,
// then a PUSH back up onto the feet. The dive and the stand-up are timed -- they
// are leg work. The middle is not timed at all: it turns at v/R, the rate a
// wheel of that size turns at that speed, so the back never skates along the
// pavement. Roll in faster and the body comes over sooner, exactly as it should.
const ROLL_DIVE = 0.14;   // s of lunge before the shoulder takes the ground
const ROLL_UP = 0.16;     // s of standing back up at the end
const ROLL_P_DIVE = 0.13; // of the full turn the dive itself accounts for
const ROLL_P_UP = 0.86;   // where the stand-up begins (so ~0.73 is rolled contact)
const ROLL_MAX = 1.6;     // s — a hard stop: nothing may leave the hero a ball
const ROLL_BOOST = 1.05;  // × runSpeed at the dive. A roll KEEPS the run flowing
                          // (AC) instead of stopping dead (Souls), but it does
                          // not beat legs: nobody rolls faster than they sprint,
                          // and a sprint carries its own speed in anyway.
const ROLL_DRAG = 0.78;   // of that left by the end of the contact phase
const ROLL_OUT = 0.85;    // and back to this as the feet push off again
const ROLL_R = 0.5;       // m — ball radius used until this body is measured
const ROLL_IN = 0.12;     // s of easing the tumble in from whatever attitude the
                          // body was already in -- rolling OUT OF A SLIDE starts
                          // from a body lying back on the road, and standing it
                          // upright for one frame first is a visible snap.
const ROLL_CD = 0.55;     // s before another roll will fire
const ROLL_BUFFER = 0.3;  // s — press it while falling and you LAND in the roll
const ROLL_BREAK = 8;     // m of drop the landing roll absorbs (AC's signature)
// THE AIR DIVE — X while you are off the ground. Mad Max's dive: the body throws
// itself flat and FLIES for a moment, almost weightless, then starts turning
// over, and that turn runs on into the landing roll that was already there. It is
// not a second jump: it buys distance, not height, and it costs you the landing.
const DIVE_PUSH = 1.45;   // × runSpeed thrown forward at the dive
const DIVE_DRAG = 1.2;    // m/s² — air only, so it barely slows down
const DIVE_LIFT = 1.4;    // m/s of kick up, so a dive out of a fall still sails
const DIVE_G = 0.3;       // × gravity while flying: the almost-weightless part
const DIVE_FLY = 0.5;     // s of weightless flight before gravity comes back
// WHICH WAY THE BODY POINTS WHILE IT FLIES. Not at the floor: through the flight
// the hero stays near upright with only a lean on him, FACING WHERE HE IS
// SHOOTING, and he only goes over -- head down, into the roll -- as the ground
// comes up to meet him. Pitching him face-first the moment he leaves the ground
// is a bellyflop, and it is also the one attitude you cannot shoot from.
const DIVE_LEAN = 0.25;   // rad — the lean he carries in the air (~14°)
const DIVE_PITCH = 1.35;  // rad — and how far over he is by the time he rolls
const DIVE_NEAR = 3.4;    // m above the ground where he starts going over
const DIVE_ROLL_H = 1.7;  // m above the ground where the roll itself takes him
const DIVE_MAX = 2.5;     // s — safety stop: off a cliff the floor never arrives
// THE BACKFLIP — off a height, backwards, all the way over (see _startFlip).
// No key: the reel asks for it. A real one leaves at about 5 m/s and travels
// barely a metre, which is the whole trick — it goes UP and OVER, not away.
const FLIP_UP = 4.8;      // m/s off the legs — a metre of rise under this gravity
const FLIP_BACK = 2.8;    // m/s backward: enough to clear the thing he jumped off
const FLIP_SPIN = 0.9;    // of the flight the turn takes — upright before the feet land
const FLIP_TUCK_IN = 0.22;  // of the turn spent balling up
const FLIP_TUCK_OUT = 0.74; // ...and from here, opening out for the ground
const ROLL_SHOULDER = 0.36; // rad — the tumble is canted over ONE SHOULDER, the
                          // way a parkour roll is: head past the opposite hip,
                          // never straight over the crown of the head.
const ROLL_CORE = HERO_HEIGHT / 2; // m — a standing body's middle
const smooth01 = (x) => x * x * (3 - 2 * x);
// SLIDE — Z. Drop into a feet-first slide out of a run: you keep the speed you
// brought, friction takes it back, and THE HILL GETS A VOTE. Tbilisi is built on
// slopes, so a slide down Mtatsminda runs away with you and the same slide uphill
// is over in a metre -- which is exactly how sliding works.
const SLIDE_KEY = 'KeyZ';
const SLIDE_MIN = 0.55;     // × runSpeed needed before one will start at all
const SLIDE_KICK = 1.25;    // the push off the back foot as you go down — a
                            // slide LEAVES faster than the run that fed it
const SLIDE_MU = 0.55;      // friction, and the ONE number that sets how far a
                            // slide goes. Real cloth on real asphalt is ~0.85 and
                            // stops you in 8 m, which is short for a game; 0.30
                            // ran 28 m, which is too long. 0.55 is ~15 m out of a
                            // sprint and ~6 m out of a jog, on the flat. Raise it
                            // to shorten, lower it to lengthen.
const SLIDE_G = 9.81;       // m/s² — REAL gravity for the friction and the slope,
                            // not this file's doubled GRAVITY: that number is
                            // there to make jumps snappy, and handing it to a
                            // slide would stop it dead inside a metre.
const SLIDE_STOP = 1.3;     // m/s — below this you get up
const SLIDE_MAX = 4.5;      // s — safety stop (a long downhill slide needs it)
const SLIDE_CD = 0.45;      // s before the next one
const SLIDE_IN = 0.12;      // s to go down
const SLIDE_UP = 0.2;       // s to get back up
const SLIDE_RECLINE = 1.0;  // rad — how far back the body lies (~57°)
const SLIDE_BANK = 0.26;    // rad — and onto which hip
const SLIDE_STEER = 1.15;   // rad/s — weight shifts bend the line, they do not carve
const SLIDE_LOOK = 1.4;     // m — how far ahead/behind the slope is sampled
const SLIDE_CLEAR = 0.03;   // m — clearance left under the lowest part of the body
// Z IN THE AIR: THE SAME DIVE, LANDING IN A SLIDE. X's dive goes over the
// hero's own head and rolls out of it; Z's throws him exactly as far and
// exactly as flat, and then over the last couple of metres he sits BACK, puts
// his feet out in front and meets the road already sliding. One move, two
// endings -- the roll keeps the run going, the slide spends the speed on ground
// and leaves him low. Everything about the flight itself is the dive's.
const SLIDE_BUFFER = 0.3;   // s — press it while falling and you LAND sliding
const SLIDE_BREAK = 6;      // m of drop the landing slide absorbs. Less than the
                            // roll's ROLL_BREAK: feet, hip and a reclined back
                            // take a fall, but not the way a whole tumble does.
const SLIDE_LAYBACK = 0.5;  // of the dive's approach to the floor spent laying
                            // back. The pose has to be FINISHED before the feet
                            // arrive, not catching up after them.
// HOW FAR THE ARMS WILL REACH to follow the aim line while the body is off
// vertical, measured in the CHEST's own frame -- and not as one round cone,
// because a shoulder is not round. Swinging the gun ACROSS the chest runs out of
// travel quickly (past this the hand has to come back through the ribs), while
// swinging it UP or DOWN the chest barely runs out at all: you can point your
// arms at your own feet. One isotropic cone cannot say both, and a slide spends
// 57° of its budget on the recline alone -- which is why a round cone left no
// room to move the gun sideways at all.
const AIM_YAW = 1.5;        // rad — ~86° across the chest, either way
const AIM_UP = 1.35;        // rad — ~77° above the chest's forward
const AIM_DOWN = -1.75;     // rad — ~100° below it (arms down past the hips)
const BOARD_DIST = 7; // m — how close to the plane Enter re-boards
const EYE = 1.55; // m — camera look-at height on the character
// ---- COVER (V) -------------------------------------------------------------
const COVER_FIND = 3.5;      // m — how far a wall is looked for on the press: a few steps, and the hero RUNS to it
const COVER_ARRIVE = 0.3;    // m — close enough to turn and settle onto it
const COVER_RUN = 0.85;      // × runSpeed on the way to the wall
const COVER_SLAM = -0.28;    // rad — the back HITS the wall: the lean overshoots to this and the body spring brings it back
const COVER_SLAM_T = 0.12;   // s the hit lasts
const COVER_BREATH = 0.012;  // rad of chest rise and fall, 15 breaths a minute
const COVER_SWAY = 0.018;    // rad of slow weight shift foot to foot
const COVER_HEAD_MAX = 1.1;  // rad the head turns to look where the camera looks
const COVER_MOVE_LEAN = 0.06; // rad of sideways lean into the shuffle at full pace
const COVER_ACC_LEAN = 0.012; // rad per m/s² of lateral acceleration (the stop at a corner rocks the body)
const COVER_CAM_HZ = 0.85;   // Hz — the cover camera is a critically damped spring, not a lerp: it has weight
const PEEK_FOLLOW = 16;      // 1/s — the body comes round to the aim BEHIND the camera, not with it
const COVER_PUSH = 2.2;      // m/s — leaving forward is a push off the wall
const COVER_GAP = 0.05;      // m — daylight left between the back and the brick
const COVER_IN = 0.26;       // s — the turn and the step back onto the wall
const COVER_SPEED = 1.8;     // m/s — the shuffle along the wall
const COVER_CROUCH_SPEED = 1.25;
const COVER_HIGH = 1.35;     // m — a wall at least this tall hides a standing body
const COVER_LOW = STEP_UP + 0.1; // m — lower than this is a kerb, not cover
const COVER_EDGE = 0.34;     // m — half a body wide: the shoulder stops here at a corner
const COVER_NEAR = 0.7;      // m — a corner within this of the shoulder IS the corner: aiming or
                             // firing slides the hero onto it rather than stepping off the wall
const PEEK_SLIDE = 3.2;      // m/s — that slide onto the edge, as the lean-out starts
const COVER_LOOK = 0.5;      // m past the shoulder the wall's end is looked for exactly
const COVER_SEEK = 1.7;      // m — aiming slides you to a corner this far along
const COVER_LEAVE = 0.16;    // s of pushing out from the wall before you leave it
const COVER_LEAN = 0.09;     // rad — the back pressed into the wall
const COVER_BASE_TURN = 0.2; // rad the body stands turned toward the camera's side -- nobody is flat to a wall
const COVER_MOVE_TURN = 0.32; // rad — ...more toward the way it is shuffling
const COVER_CORNER_TURN = 0.48; // rad — ...and at a corner, shoulder to the edge, ready to come round it
const COVER_SHOULDER = 0.24; // m — half a body wide: a turned body's far shoulder needs this much more room off the wall
const COVER_CHEST = 0.15;    // rad — ...and the shoulders a little
const COVER_SHUFFLE = 0.15;  // of the strafe turn the legs show in the shuffle (the back stays on the wall)
const COVER_LOOK_MAX = 1.05; // rad — the cover camera swings this far either way
const COVER_CAM_SWING = 5;   // 1/s — how fast the cover camera swings round to the other side
const COVER_CAM_BACK = 3.9;  // m — the cover camera, along the wall away from the corner
const COVER_CAM_SIDE = 1.5;  // m — ...and out from the face
const COVER_CAM_UP = 1.95;   // m
const COVER_CAM_MIN = 0.9;   // m — never closer to the face than this
const COVER_CAM_AHEAD = 1.6; // m — looking past the hero toward the corner
// HOW FAR OUT TO SHOOT. Only as far as the gun needs: round the corner on the
// gun's own side the body's centre stops at the edge (half a body showing, the
// gun hand and the lean carry the muzzle clear); round the other corner the
// gun hand is on the far side of the body and the step is a hand longer.
const PEEK_OUT_GUN = COVER_EDGE + 0.06;   // m past the base to the body's centre
const PEEK_OUT_OFF = COVER_EDGE + 0.30;
// LOOKING ROUND THE CORNER without the gun: pushing toward a corner you are
// already standing at turns the hero toward it and leans him out a little, and
// the camera swings to the open side to see down the far face -- GTA's look
// from cover, to see what is happening before committing to the lean-out.
const LOOK_IN = 0.25;        // s
const LOOK_OUT = 0.2;        // m the body leans past its cover spot
const LOOK_TURN = 0.5;       // rad more toward the corner (on top of COVER_CORNER_TURN)
const LOOK_BANK = 0.15;      // rad of lean toward the corner
const LOOK_SWING = 0.55;     // rad the cover camera swings toward the open side
const LOOK_AHEAD = 1.2;      // m further past the corner the camera looks
const PEEK_IN = 0.2;         // s — out, and back
const PEEK_MIN = 1.35;       // rad — the aim never swings back toward the open past this: the
                             // camera is over the OUTSIDE shoulder and would go through the wall
const PEEK_MAX = 2.95;       // rad — ...nor back into the wall on the far side
const PEEK_BANK = 0.3;       // rad — the lean out round the corner: the chest and the gun go out, the feet stay
const PEEK_CAM = 30;         // 1/s — the aim camera snaps round when the lean-out starts
const SHOT_BUFFER = 0.4;     // s a trigger pulled while still stepping out is kept
const OUT_STEP = 1.2;        // m — mid-wall, aiming steps the hero this far off the wall into the open
                             // (room for the camera: closer than ~1.1 m behind, it is inside the banana's curved head)
const OUT_IN = 0.22;         // s — out, and back
const OUT_HALF = 1.3;        // rad — the arc either way of straight out from the wall
const COVER_CAM_WALL = 0.4;  // m — the aim camera never goes nearer the wall's face than this
const POP_IN = 0.22;         // s — up over a low wall
const POP_CROUCH = 0.15;     // of the crouch kept while shooting over it
const POP_HALF = 1.45;       // rad — the arc over a low wall, either way of straight over
const BLIND_IN = 0.2;        // s — the arm out round the edge
const BLIND_OUT = 0.25;      // s — ...and back
const BLIND_ACROSS = 0.25;   // the blind shots are sprayed a little across, toward the open
const BLIND_TURN = 0.48;     // rad the body turns toward the edge while the arm is out
const BLIND_REACH = 0.3;     // m past the edge the hand goes
// THE OTHER CORNER IS NOT THE SAME CORNER. Firing blind round the edge on the
// GUN HAND's side the arm is already pointing at it: measured on the banana the
// reach is 0.49 m out of an arm 0.52 m long. Round the corner on the OTHER side
// the same target is 0.86 m away -- 65% past what the arm can do -- and a reach
// that cannot be made is laid down the line to it anyway, which from the right
// shoulder to a target on the left runs THROUGH THE CHEST: half the forearm
// inside the body and the gun on the skin instead of round the brick (user:
// "corner is by left hand, your hand with gun is goin inside body").
// Nobody reaches across themselves to do this. They turn into the corner, shift
// their weight onto that foot and put the gun hand round it SHOULDER FIRST --
// so that is what the three numbers below do: the turn, the step, and a hand
// that only goes as far as the edge, not a foot past it.
const BLIND_TURN_OFF = 1.0;  // rad — ...and most of the way round on the other hand's corner
const BLIND_REACH_OFF = 0.12;
const BLIND_STEP_OFF = 0.18; // m the weight goes onto the corner foot with it
// how far BEHIND the wall's face the hand goes: hugging the brick (-0.12) is
// right for the arm that is already on that side, and is 22 cm of reach the
// other arm has not got -- turning toward the corner swings that shoulder OUT
// from the wall, not along it. Past the edge there is no wall to hug anyway.
const BLIND_HUG = -0.12;
const BLIND_HUG_OFF = 0.06;
const HINT_BLIND = { up: 0.3, right: 0.5, fwd: -0.6 };   // elbow back and a little up
// ...and on the wrong-side corner it HANGS, the way an elbow under a hand held
// at shoulder height does. (It wants to be out on the gun side, which is where
// HINT_BLIND puts it -- but reaching the other way that direction lies nearly
// ALONG the line to the hand, applyReachPose's `perp` is then whatever is left
// of a vector it just cancelled, and the elbow flipped from one side of that
// line to the other between frames: elbowEll 3.88 one frame, 0.23 the next.
// Down is square to a reach that is level, so it stays put.)
const HINT_BLIND_OFF = { up: -0.7, right: 0.3, fwd: -0.25 };
const HINT_OVER = { up: 0.9, right: 0.5, fwd: -0.2 };    // elbow high: over the top
const HINT_COVER = '🧱 საფარი — ზურგით კედელთან · იარე კედლის გასწვრივ · RMB — დამიზნება (კუთხესთან — კუთხიდან) · LMB — სროლა · C — ჩაცუცქება · V / კედლისგან მოშორებით — გამოსვლა';
const HINT_COVER_LOW = '🧱 დაბალი საფარი — RMB — ზემოდან დამიზნება · კუთხესთან RMB — კუთხიდან · LMB — ბრმა სროლა · Space — გადახტომა · V — გამოსვლა';
const HINT_CORNER = '📐 კუთხე — კუთხისკენ მიწოლა: გახედვა · RMB: კუთხიდან დამიზნება · LMB: ბრმა სროლა · V — გამოსვლა';
// scratch for _moveArm: the arm as the animation left it, the carry, the grip
const _mqa = new THREE.Quaternion();
const _mqb = new THREE.Quaternion();
const _mqc = new THREE.Quaternion();
const _mqd = new THREE.Quaternion();
const _mm = new THREE.Matrix4();
const _rideM = new THREE.Matrix4();
const _rideB = new THREE.Matrix4();
const _tv = new THREE.Vector3();          // the tucked aim, for the seat
const _tq = { bones: [], from: [], fwd: new THREE.Vector3() };   // applyTuckPose scratch
const _gm1 = new THREE.Matrix4();
const _gm2 = new THREE.Matrix4();
const _gm3 = new THREE.Matrix4();
const _gm4 = new THREE.Matrix4();
// module scratch for the holster solve, which runs on a draw and not per frame
const _gv1 = new THREE.Vector3();
const _gv2 = new THREE.Vector3();
// the seat's own scratch: a grip point, a knee direction, the plane's rotation
const _seatV = new THREE.Vector3();
const _seatK = new THREE.Vector3();
const _seatH = new THREE.Vector3();
// A few centimetres of slack on both ends of the seat fit: the measured
// extent is the folded pose's, and the plants and reaches move limbs a
// little past it.
const SEAT_MARGIN = 0.05;
// how far a hand on a control can travel as the stick and throttle move,
// which the once-settled seat has to leave room for
const SEAT_TRAVEL = 0.08;
const _seatQ = new THREE.Quaternion();
// Where the ELBOW goes with a hand on a cockpit control. DOWN and BACK, and
// barely outboard at all: a cockpit is a tube about 0.8 m across and the
// elbow is the part of a reaching arm that travels furthest from the line to
// the hand, so an outboard hint puts it through the side of the aeroplane.
// (It did: the astronaut, which has the longest arms here at 0.92 m, flew a
// Spitfire with its elbow and glove out in the slipstream.) Tucked back
// toward the hip is also simply where a pilot's elbows are.
const SEAT_HINT_R = { up: -1.0, right: 0.10, fwd: -0.55 };
const SEAT_HINT_L = { up: -1.0, right: 0.14, fwd: -0.50 };
const _boardRay = new THREE.Raycaster();
const _DOWN = new THREE.Vector3(0, -1, 0);
const _gv3 = new THREE.Vector3();
const _gv4 = new THREE.Vector3();
const _gv5 = new THREE.Vector3();
const _gv6 = new THREE.Vector3();
// THE DRAW, Red Dead's way: the HAND leads and the gun never moves on its own.
//   reach    the hand goes down to the grip, elbow back and out, the gun-side
//            shoulder going back and down with it; the gun stays in the leather
//   grip     a beat on the grip -- the contact frame every draw has
//   lift     straight up the holster's own axis, slow against the leather and
//            then free, the elbow high and back (clearing leather is a lift
//            from the shoulder, the chicken wing of every hip draw)
//   present  the hand sweeps forward and up to the carry on an arc, the gun
//            rolling from muzzle-down to muzzle-forward WITH the wrist, past
//            the line a touch, and the muzzle settles in the hand after
const DRAW_REACH = 0.17;
const DRAW_GRIP = 0.05;
const DRAW_LIFT = 0.13;
const DRAW_PRESENT = 0.22;
const DRAW_DUR = DRAW_REACH + DRAW_GRIP + DRAW_LIFT + DRAW_PRESENT;   // 0.57 s
const DRAW_SETTLE = 0.3;        // s the muzzle takes to stop rocking in the hand
const DRAW_SETTLE_AMP = 0.08;   // rad of that rock
const HINT_REACH = { up: -0.25, right: 0.75, fwd: -0.6 };   // elbow back and out
const HINT_LIFT = { up: 0.6, right: 0.65, fwd: -0.5 };      // ...then high: the wing
const HINT_CARRY = { up: -0.75, right: 0.55, fwd: -0.35 };  // the carry's own shape
// THE SLING, for a rifle: the same move, over the SHOULDER instead of out of
// the leather. The hand reaches up and back over the gun-side shoulder to the
// grip on the back, the rifle comes off the back -- out, then up -- and swings
// over the shoulder down into the hold; put away, it goes up over the shoulder
// and down onto the back. Heavier than a pistol, so slower.
const SLING_REACH = 0.22;
const SLING_GRIP = 0.05;
const SLING_LIFT = 0.2;
const SLING_PRESENT = 0.34;
const SLING_DUR = SLING_REACH + SLING_GRIP + SLING_LIFT + SLING_PRESENT;   // 0.81 s
const SLING_STOW = 0.95;        // s to put it away
const SLING_OUT = 0.14;         // m it comes off the back before it goes up
const SLING_UP = 0.34;          // m over the shoulder it clears
const SLING_WAY_OUT = 0.14;     // m beside the shoulder the rifle passes, over and above it
const SLING_WAY_UP = 0.3;
const SLING_STAND_FWD = 0.3;    // m ahead of the pocket the rifle is stood on end for a stow
const HINT_SLING = { up: 0.75, right: 0.6, fwd: -0.35 };     // elbow high and out: over the shoulder
// a quadratic Bezier that PASSES THROUGH `via` at its midpoint
const _bz = new THREE.Vector3();
function bendThrough(out, a, via, b, t) {
  _bz.copy(via).multiplyScalar(2).addScaledVector(a, -0.5).addScaledVector(b, -0.5);   // the control point
  const u = 1 - t;
  return out.copy(a).multiplyScalar(u * u).addScaledVector(_bz, 2 * u * t).addScaledVector(b, t * t);
}
const HINT_RIFLE = { up: -0.85, right: 0.4, fwd: -0.2 };     // the rifle hold's own elbow
const GUN_TWIST = 0.14;         // rad the gun-side shoulder goes back at the grip
const GUN_DIP = 0.07;           // rad it drops
const AIM_TWIST_MAX = 1.0;      // rad the chest can turn off the legs toward the aim (blade included)
const AIM_ARM_YAW = 0.85;       // rad the ARM can cross the (turned) chest without going through it
const RIFLE_RELOAD_TILT = 0.35; // of the clip's weapon heading a RIFLE takes in a
                           // magazine change: a receiver tilted over, not a gun swung about
const RIFLE_DIP = RIFLE_CARRY_DIP;   // rad the muzzle hangs below the line at the low ready (weapons.js builds the hold on it)
// BLADED. A rifle is shot with the torso turned toward the gun side: the stock
// is in the right shoulder, so the LEFT shoulder has to come forward for the
// support hand to reach the handguard at all -- square to the target, on a
// body with the banana's shoulders and arms, it falls 20 cm short.
const RIFLE_BLADE = 0.5;        // rad the chest turns toward the gun shoulder with a rifle up (more and the grip is out of the arm's reach on a deep body)
const _hint = { up: 0, right: 0, fwd: 0 };
const hintMix = (a, b, k) => {
  _hint.up = a.up + (b.up - a.up) * k;
  _hint.right = a.right + (b.right - a.right) * k;
  _hint.fwd = a.fwd + (b.fwd - a.fwd) * k;
  return _hint;
};
const _dq1 = new THREE.Quaternion();
const _dq2 = new THREE.Quaternion();
const _dq3 = new THREE.Quaternion();
const _mqe = new THREE.Quaternion();
// The same weapon orientation turned so its muzzle (+X) points straight up,
// by the smallest rotation that does it: how a rifle is carried over the
// shoulder, and the one attitude that keeps a 90 cm gun out of a 50 cm body.
// (Own scratch, never a shared _gv: muzzleUp used _gv4 for "up" while the
// stow was holding its waypoint in _gv4 -- the Bezier then bent through the
// world origin and the rifle spent half a second 18 km away.)
const _mzA = new THREE.Vector3();
const _mzU = new THREE.Vector3();
const _mzQ = new THREE.Quaternion();
function muzzleUp(q, out) {
  _mzA.set(1, 0, 0).applyQuaternion(q);
  _mzQ.setFromUnitVectors(_mzA, _mzU.set(0, 1, 0));
  return out.copy(_mzQ).multiply(q).normalize();
}
// a to b by t, where t may run PAST 1: the rotation between them as an axis
// and an angle, scaled -- a slerp cannot overshoot, and a presented pistol does
const _spA = new THREE.Vector3();
function slerpPast(a, b, t, out) {
  _dq2.copy(b).multiply(_dq3.copy(a).invert()).normalize();
  if (_dq2.w < 0) { _dq2.x = -_dq2.x; _dq2.y = -_dq2.y; _dq2.z = -_dq2.z; _dq2.w = -_dq2.w; }
  const w = Math.min(1, _dq2.w);
  const sn = Math.sqrt(Math.max(1e-16, 1 - w * w));
  const ang = 2 * Math.acos(w);
  if (ang < 1e-6) return out.copy(b);
  _spA.set(_dq2.x / sn, _dq2.y / sn, _dq2.z / sn);
  return out.copy(_dq3.setFromAxisAngle(_spA, ang * t)).multiply(a).normalize();
}

const FOOT_PROBE = 0.9; // m — how far below the feet the IK looks for ground

export class OnFoot {
  constructor(game, characterSel) {
    this.game = game;
    this.active = false;
    this.rig = null;
    this.vy = 0;
    this.yaw = 0;
    this.camYaw = 0; // the camera's own heading while a weapon is out
    // WHICH WAY THE MOVEMENT KEYS POINT. Normally that is read off the chase
    // camera (W is "away from the camera"), which is right while the camera is
    // behind the hero and meaningless while it is not -- a cinematic puts it on
    // the floor twenty metres away (src/cinema.js). A director sets this to the
    // heading the keys should be measured from, and null gives the camera back.
    this.camRef = null;
    this._moveYaw = 0; // where the legs are carrying us (strafing with a gun)
    this._strafing = false;
    this.crouched = false; // C — ducked, slow and quiet
    this._crouchT = 0;     // 0..1 eased, so standing up is not a snap
    this._stroll = false;  // Cmd — the gentleman's walk
    this._strollT = 0;     // 0..1 eased, so the carriage is taken up, not snapped
    this._gripT = 0;       // 0..1 eased — how closed the empty hands are
    this._turnV = 0;       // rad/s the body is turning, smoothed (the arms feel it)
    this._accV = 0;        // m/s² it is changing pace, smoothed (so do the arms)
    this.speed = 0;
    this.grounded = true;
    this._coyote = 0;      // s of jump grace left
    this._jumpBuf = 0;     // s of buffered jump press left
    this._jumpWas = false; // Space edge detector
    this._climb = null;    // { t, from, to } while pulling onto a ledge
    this._climbOut = null; // ...and the fade out of its pose when it ends
    this._roll = null;     // { t, yaw, from } while tumbling (X)
    this._rollCd = 0;      // s of roll cooldown left
    this._rollBuf = 0;     // s of buffered roll press left (rolling out of a fall)
    this._rollWas = false; // X edge detector
    this._slide = null;    // { t, blend, ending, yaw, side } while sliding (Z)
    this._slideCd = 0;     // s of slide cooldown left
    this._slideWas = false; // Z edge detector
    this._slideBuf = 0;    // s of buffered slide press left (sliding out of a dive)
    this._slideE = new THREE.Euler(); // scratch for the slide's recline
    this._aimV = new THREE.Vector3();  // scratch for the on-the-move aim clamp
    this._aimA = new THREE.Vector3();
    this._aimOut = new THREE.Vector3();
    this._aimS = new THREE.Vector3();
    this._climbDir = new THREE.Vector3(); // scratch for the climb probe
    this._v3 = new THREE.Vector3();       // scratch for the hands on the ledge
    this._v4 = new THREE.Vector3();
    this._q1 = new THREE.Quaternion();
    this._q2 = new THREE.Quaternion();
    this._q3 = new THREE.Quaternion();
    this._q4 = new THREE.Quaternion();
    this._v5 = new THREE.Vector3();
    this._climbLead = 'L';  // the leg that found the wall last time; they alternate
    this._air = null;      // { w, p, lead, out, fall } while off the ground
    this._jumpV = JUMP_V;  // the impulse this jump actually left with
    this._dive = null;     // { t, yaw, spin, side, slide } while flying (X/Z in the air)
    this._aimQ = new THREE.Quaternion();
    this._seatF = new THREE.Vector3();  // scratch: the weapon's heading mid-reload
    this._stowV = new THREE.Vector3();  // scratch: the weapon on its way to the belt
    this._stow = null;                  // the holstering move, while it runs
    this._presentT = -1;                // s since a draw landed the gun in the hand
    this._aimTwist = 0;                 // rad the torso is turned toward the aim
    this._settle = null;                // and the rock as it beds in
    this._seatU = new THREE.Vector3();
    this._fallFrom = null; // m — the height a fall started at
    this._land = 0;        // s of landing squash left
    this._planeMeshes = null; // cached raycast targets for the parked plane
    this._planeMeshT = 0;
    this._rollQ = new THREE.Quaternion(); // scratch for the tumble pose
    this._rollAxis = new THREE.Vector3();
    this._rollV = new THREE.Vector3();
    this._ray = new THREE.Raycaster();
    this._ray.firstHitOnly = true;
    // What the personage is WEARING, by gear id, and what is sitting in its
    // holster. Kept across body reloads: the hero still owns the belt even if
    // the model it hangs on had to be fetched again.
    this.worn = new Map();
    this._inHolster = null;      // the weapon on the strong-hand hip
    this._holstered = [];        // everything on the belt, one per socket
    this._flip = null;           // { t, dur, p, tuck } while a backflip is in the air
    this._cover = null;          // back to a wall (see toggleCover / _coverFrame)
    this._coverW = 0;            // 0..1 eased: how far behind that wall the body is
    this.aimOverride = null;     // the blind arm's line, while it is out (shooting.js reads it)
    // Preload the hero's personage body so the first Enter is instant. The
    // promise is kept: a resumed session may want to stand the hero up before
    // the body has finished loading.
    this.ready = Promise.resolve();
    this._modelId = null;
    if (characterSel?.race) {
      const race = raceById(characterSel.race);
      this.runSpeed = race.runSpeed;
      this._modelId = characterSel.model ?? race.defModel;
      this.playerName = characterSel.name;
      this.ready = this._loadBody();
    }
    this.runSpeed ??= 6.5;
  }

  _loadBody() {
    return makeCharacter(this._modelId)
      .then((rig) => {
        this.rig = rig;
        this._waistW = 0;
        this._waistY = 0;
        // Worn gear hangs off the OLD skeleton. A reload (a retry, a different
        // body) leaves it parented to bones that are no longer in the scene, so
        // the belt silently disappears -- re-seat everything on the new rig.
        for (const w of this.worn.values()) this._seatGear(w);
        this._holstered = [];
        this._inHolster = null;
        this._draw = null; this._stow = null; this._settle = null;
        this._toHolster();
        // THE BELT IS STANDARD ISSUE. It used to be loot on the warehouse
        // bench, which meant a hero who had not found it yet put a pistol away
        // into thin air -- owned, invisible, and nowhere on the body. Everyone
        // wears one from the start now; the one on the bench is still there to
        // be picked up and simply does nothing the second time.
        this.wearGear(GEAR_DEFS.belt);
        this.game.gear?.set?.('belt', { ...GEAR_DEFS.belt });
      })
      .catch((e) => {
        // Swallowing this used to mean a hero saved OUTSIDE the plane woke up
        // back inside it with no explanation -- the body 404s (a model being
        // rebuilt, a bad id), rig stays null, and resume() quietly gave up.
        console.error(`personage ${this._modelId} failed to load:`, e);
      });
  }

  get pos() {
    return this.rig.obj.position;
  }

  // Enter pressed: climb out when parked, climb in when near AN aeroplane.
  toggle() {
    const g = this.game;
    if (!g.started || g.crashed) return;
    if (this._boardSeq) return;              // already on the way in or out
    if (this.active) {
      // WHICHEVER ONE YOU ARE STANDING NEXT TO. "i need to can sit in all
      // planes" -- the hero is no longer married to the machine its personage
      // was created with. Every parked aeroplane in the world is in the target
      // registry (the plane hall puts its thirty-six there, and game.parkPlane
      // puts every machine you walk away from there), and Enter takes the
      // nearest one within reach of its own wing.
      const pick = this._nearestPlane();
      if (!pick) {
        g._setHint('✈ თვითმფრინავი შორსაა — მიუახლოვდი და დააჭირე Enter');
        return;
      }
      if (pick.rec && !g.swapToPlane(pick.rec)) {
        g._setHint('✈ ამ თვითმფრინავში ახლა ვერ ჩაჯდები');
        return;
      }
      if (!this._startClimb(1)) this._board();
      return;
    }
    if (g.airborne || Math.abs(g.speed) > 1.5) {
      g._setHint('⚠ ჯერ დაჯექი და გააჩერე თვითმფრინავი — მერე Enter');
      return;
    }
    if (!this.rig) {
      g._setHint('პერსონაჟი იტვირთება… (ან შექმენი მთავარ მენიუში)');
      return;
    }
    this._exitPlane();
  }

  // WALK UP TO AN AEROPLANE AND IT TELLS YOU ITS NAME. There is no plane
  // picker any more and the hall is a shed full of machines with nothing
  // saying you may have any of them, so the aeroplane says so itself: stand
  // within reach of a parked one and the hint names it and the key. Changes
  // only when the aeroplane under your nose changes, so it never flickers and
  // never argues with the hints the rest of the on-foot code puts up.
  _planePrompt(dt) {
    this._promptT = (this._promptT ?? 0) + dt;
    if (this._promptT < 0.25) return;        // four times a second is plenty
    this._promptT = 0;
    const g = this.game;
    const near = this._nearestPlane();
    const id = near?.rec?.def?.id ?? (near ? '@own' : null);
    if (id === this._promptId) return;
    this._promptId = id;
    if (!near) return;
    g._setHint?.(near.rec
      ? `✈ ${near.rec.def.name} — Enter: ჩაჯექი`
      : this._footHint());
  }

  // THE NEAREST AEROPLANE YOU COULD GET INTO, or null. Your own counts, and so
  // does every parked one the target registry knows about -- which is the hall's
  // whole fleet plus anything you have left standing somewhere. "Within reach"
  // is measured off each machine's OWN size: you board an An-225 from further
  // out than a Spitfire because you have to.
  //
  // Returns { rec, wrapper, d }; `rec` is null for the machine already yours,
  // which is the caller's signal that no swap is needed.
  _nearestPlane() {
    const g = this.game;
    const reach = (w) => Math.max(BOARD_DIST, (w.userData.halfSpan ?? 6) + 3);
    let best = null;
    if (g.plane) {
      const d = this.pos.distanceTo(g.plane.position);
      if (d < reach(g.plane)) best = { rec: null, wrapper: g.plane, d };
    }
    for (const rec of (g.targets?.list ?? [])) {
      const w = rec.wrapper;
      if (!w || w === g.plane || !w.parent || rec.dead) continue;
      w.getWorldPosition(_seatV);
      const d = this.pos.distanceTo(_seatV);
      if (d >= reach(w)) continue;
      if (!best || d < best.d) best = { rec, wrapper: w, d };
    }
    return best;
  }

  // `instant` skips the climb — the airport teleport and the R restart both
  // put the hero somewhere else entirely, and watching a body climb down a
  // plane that is no longer there is worse than no animation at all.
  _exitPlane(instant = false) {
    const g = this.game;
    if (!instant && this._startClimb(-1)) return;
    g.stopEngine?.();   // teleported out: the engine does not keep running
    // Step out under the left wing, a stride clear of the tip.
    const left = new THREE.Vector3(-1, 0, 0).applyQuaternion(g.plane.quaternion);
    const p = g.plane.position.clone().addScaledVector(left, g.halfSpan + 2);
    const gY = g._groundAt(p.x, p.z);
    const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(g.plane.quaternion);
    // step out facing where the nose points
    this._stand(p.x, (gY ?? g.plane.position.y - 1.7) + 0.02, p.z, Math.atan2(-fwd.x, -fwd.z));
    g.saveState?.(true, 'climbed out'); // out of the plane is worth remembering
  }

  // ---- climbing in and out --------------------------------------------------
  // A pilot does not teleport into a seat. The hood swings up, the personage
  // walks to the port side, steps up onto the wing root beside the cockpit and
  // lowers itself in — and the same three moves backwards on the way out. The
  // user asked to see it: "i need see how personage is clumb in plane, and
  // plane hood will open".
  //
  // The whole thing is worked out ONCE, in world space, at the moment it
  // starts: the aeroplane is parked (boarding is refused otherwise), so three
  // fixed points and a time are all it takes, and nothing can drift under it.
  _startClimb(dir) {
    const g = this.game;
    const c = g.plane?.userData?.cockpit;
    if (!c || !this.rig || g.airborne || Math.abs(g.speed) > 1.5) return false;
    g.plane.updateMatrixWorld(true);
    const L = (x, y, z) => g.plane.localToWorld(new THREE.Vector3(x, y, z));
    // BESIDE THE HULL at the cockpit station. That is the one place an
    // aeroplane gives you a face to get your hands on: out under the wingtip
    // there is nothing to climb, you walk under a wing.
    const side = (c.halfW ?? 0.6) + 0.6;
    const seat = L(0, c.seat.y, c.seat.z);
    const step = L(-side, c.deck - 0.5, c.seat.z + 0.3);
    const gy = g._groundAt(step.x, step.z, step.y + 6, 10);
    const ground = new THREE.Vector3(step.x, (gy ?? g.plane.position.y - 1.7) + 0.02, step.z);
    const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(g.plane.quaternion);
    const noseYaw = Math.atan2(-fwd.x, -fwd.z);
    if (dir > 0) {
      // ALREADY UP THERE? Nothing to walk and nothing to climb — the body
      // only lowers itself into the seat from where it is standing.
      const local = g.plane.worldToLocal(this.pos.clone());
      if (local.y > c.deck - 1.3 && Math.abs(local.x) < g.halfSpan
        && local.z > c.seat.z - g.halfSpan && local.z < c.seat.z + g.halfSpan) {
        this._sitFromHere(seat, noseYaw);
        return true;
      }
      // Otherwise WALK OVER AND CLIMB IT — the real climb, the same hang,
      // wall-plant, pull and mantle as any ledge in the city (_tryClimb). The
      // hand-rolled lift I wrote first was a slide up an invisible ramp, and
      // the user said so: "clumb is not realistic like assasin physic".
      //
      // The walk takes as long as the walk actually is: a fixed first beat
      // made a hero standing at the wingtip (Enter reaches halfSpan + 3 m)
      // arrive at 17 m/s like a dropped puck.
      const spot = this._boardSpot(c) ?? { pos: ground, yaw: this._faceYaw(ground, seat) ?? noseYaw };
      const walk = THREE.MathUtils.clamp(this.pos.distanceTo(spot.pos) / 4.2, 0.12, 2.2);
      this._boardSeq = {
        dir, stage: 'walk', t: 0, dur: walk, seat, ground: spot.pos, noseYaw,
        faceYaw: spot.yaw,
        start: this.pos.clone(), startYaw: this.yaw, walking: walk > 0.35,
      };
      g._setHint('🧗 ადიხარ ბორტზე…');
    } else {
      // Mixture off before anything else: the blades have to be stopping while
      // the pilot climbs out past them, not after. (A piston engine has a
      // recording of exactly this; a turbine runs its revs down instead.)
      g.stopEngine?.();
      g.sound?.belt();
      // out: off the seat first, so leave the cockpit before anything moves
      this.leaveSeat();
      g.scene.add(this.rig.obj);
      this.rig.obj.position.copy(seat);
      this.rig.obj.rotation.set(0, noseYaw, 0);
      this.yaw = this.camYaw = noseYaw;
      // OVER THE SILL, NOT THROUGH THE SIDE. A straight line from the seat
      // out to the wing passes diagonally through the fuselage, and the body
      // goes with it. The way out of a cockpit is: stand up in it, swing a
      // leg over the rail, step down — three moves, none of which crosses the
      // skin. ("sometimes my body is inside planes body, i need realizm")
      const up = L(0, c.deck + 0.52, c.seat.z);               // standing up in it
      const rim = L(-(c.halfW * 0.72), c.deck + 0.40, c.seat.z); // astride the rail
      const wing = L(-(c.halfW + 0.5), c.deck, c.seat.z - 0.2);
      // The wing's surface, off the aeroplane's OWN shell. _groundAt is no
      // use here: the shell is only a collidable while the hero is on its
      // feet, and it is not on them yet — so the probe found the apron three
      // metres below and the climb-out stepped off into thin air.
      const wy = this._shellTop(wing.x, wing.z, wing.y + 3);
      if (wy !== null) wing.y = wy + 0.02;
      this._boardSeq = {
        dir, t: 0, dur: 1.8, seat, up, rim, step: wing, noseYaw,
        outYaw: this._faceYaw(seat, wing) ?? noseYaw,
      };
      g._setHint('🚶 გადმოდიხარ ფრთაზე…');
    }
    this.rig.play('Idle');
    return true;
  }

  // The top of the aeroplane's own box shell straight down from (x, z), or
  // null if there is nothing of it there. Used while the shell is not in the
  // collision set, which is most of the time.
  _shellTop(x, z, fromY) {
    const shell = this.game.plane?.userData?.shell;
    if (!shell) return null;
    this.game.plane.updateMatrixWorld(true);
    _boardRay.set(_gv2.set(x, fromY, z), _DOWN);
    _boardRay.far = 12;
    const hit = _boardRay.intersectObject(shell, true)[0];
    return hit ? hit.point.y : null;
  }

  // WHERE CAN THIS BODY ACTUALLY GET A GRIP? An aeroplane is not a wall: most
  // of the way round one there is nothing within reach of a pair of hands.
  // On the grown aerobatic the deck is 2.94 m over the apron and the climb
  // reaches 2.6, so the only holds are out on the WING, a couple of metres
  // forward of the cockpit — measured 2.16 to 2.49 m there.
  //
  // So the spot is searched for rather than assumed: the port flank is swept
  // from the cockpit outwards and the first place _findLedge answers from
  // wins. Nearest the cockpit first, so the hero boards where a pilot would.
  _boardSpot(c) {
    const g = this.game;
    const save = this.pos.clone();
    const saveYaw = this.yaw;
    const saveG = this._gY;
    const saveGround = this.grounded;
    let best = null;
    for (const dz of [-0.8, -2, -3.2, 0.8, -4.4, 2]) {
      for (const off of [0.6, 1.0]) {
        const p = g.plane.localToWorld(new THREE.Vector3(-(c.halfW + off), 0, c.seat.z + dz));
        const gy = g.world.heightAt?.(p.x, p.z) ?? (g.plane.position.y - 1.7);
        this.pos.set(p.x, gy + 0.02, p.z);
        this.grounded = true;
        this._gY = gy;
        const d = _gv2.set(g.plane.position.x - p.x, 0, g.plane.position.z - p.z).normalize();
        this.yaw = Math.atan2(-d.x, -d.z);
        if (this._findLedge(d.x, d.z)) { best = { pos: this.pos.clone(), yaw: this.yaw }; break; }
      }
      if (best) break;
    }
    this.pos.copy(save);
    this.yaw = saveYaw;
    this._gY = saveG;
    this.grounded = saveGround;
    return best;
  }

  // Lower into the seat from wherever the body is standing. The last beat of
  // boarding, whether the hero climbed up itself or was walked over by
  // _startClimb.
  _sitFromHere(seat, noseYaw) {
    // IN OVER THE SILL TOO. Straight from the wing to the seat is the same
    // diagonal through the fuselage side, run backwards.
    const g = this.game;
    const c = g.plane?.userData?.cockpit;
    const up = c
      ? g.plane.localToWorld(new THREE.Vector3(0, c.deck + 0.52, c.seat.z))
      : null;
    this._boardSeq = {
      dir: 1, stage: 'sit', t: 0, dur: 0.95, up,
      seat, noseYaw, start: this.pos.clone(), startYaw: this.yaw,
    };
    this.game._setHint('🪂 ჩაჯექი კაბინაში…');
    this.rig.play('Idle');
  }

  // One frame of getting in or out. Returns true while it owns the body.
  _climbStep(dt) {
    const q = this._boardSeq;
    if (!q) return false;
    const g = this.game;
    q.t = Math.min(1, q.t + dt / q.dur);
    const ease = (u) => u * u * (3 - 2 * u);
    const seg = (u, a, b) => THREE.MathUtils.clamp((u - a) / (b - a), 0, 1);
    const pos = _gv1;
    let yaw;
    let sit = 0;

    if (q.stage === 'walk') {
      // up to the aeroplane's flank, turning onto it as it goes
      pos.lerpVectors(q.start, q.ground, q.walking ? q.t : ease(q.t));
      yaw = this._yawTo(q.startYaw, q.faceYaw, Math.min(1, q.t * 3));
      this.rig.play(q.walking ? 'Run' : 'Idle');
      this._poseBoard(dt, pos, yaw, 0);
      if (q.t < 1) return true;
      // HAND THE BODY TO THE CLIMB. Facing the hull with the cockpit rim
      // overhead, this is an ordinary ledge, and _tryClimb plans the whole
      // thing — hands on the lip, a foot on the side, the pull and the
      // mantle — in the wall's own frame.
      this._boardSeq = null;
      this.yaw = this.camYaw = q.faceYaw;
      this.rig.obj.rotation.set(0, this.yaw, 0);
      this.grounded = true;
      this._boardAfter = { seat: q.seat, noseYaw: q.noseYaw };
      if (this._tryClimb()) return true;
      // nothing to grab (a flat-decked machine, or the hero is already level
      // with the sill): go straight in rather than refuse to board
      this._boardAfter = null;
      this._sitFromHere(q.seat, q.noseYaw);
      return true;
    }

    if (q.stage === 'sit') {
      // up and over the rail first, then down into the seat
      if (q.up && q.t < 0.45) {
        const k = ease(seg(q.t, 0, 0.45));
        pos.lerpVectors(q.start, q.up, k);
        yaw = this._yawTo(q.startYaw, q.noseYaw, Math.min(1, k * 1.6));
        sit = 0;
        this._poseBoard(dt, pos, yaw, sit);
        return true;
      }
      const k = ease(q.up ? seg(q.t, 0.45, 1) : q.t);
      pos.lerpVectors(q.up ?? q.start, q.seat, k);
      yaw = q.noseYaw;
      sit = k;
      this._poseBoard(dt, pos, yaw, sit);
      if (q.t < 1) return true;
      this._boardSeq = null;
      this._board();
      return true;
    }

    // ---- climbing OUT ------------------------------------------------------
    // Out of the straps, over the sill, STANDING ON THE WING — and that is
    // where it stops. The hero climbed up here; it can get itself down, by
    // walking off and taking the drop like any other ledge. Setting it down
    // on the apron instead was a 2.5 m float through the air.
    //
    // The hood has to be out of the way first, so the first beat is a pause.
    const u = q.t;
    if (u < 0.12) { pos.copy(q.seat); yaw = q.noseYaw; sit = 1; }
    else if (u < 0.48) {                       // stand up, straight out of the seat
      const k = ease(seg(u, 0.12, 0.48));
      pos.lerpVectors(q.seat, q.up, k);
      yaw = q.noseYaw;
      sit = 1 - k;
    } else if (u < 0.74) {                     // a leg over the rail
      pos.lerpVectors(q.up, q.rim, ease(seg(u, 0.48, 0.74)));
      yaw = this._yawTo(q.noseYaw, q.outYaw, seg(u, 0.48, 0.74) * 0.6);
    } else {                                   // down onto the wing
      pos.lerpVectors(q.rim, q.step, ease(seg(u, 0.74, 1)));
      yaw = this._yawTo(q.noseYaw, q.outYaw, 0.6 + 0.4 * seg(u, 0.74, 1));
    }
    this._poseBoard(dt, pos, yaw, sit);
    if (q.t < 1) return true;
    this._boardSeq = null;
    this._stand(q.step.x, q.step.y, q.step.z, q.outYaw);
    g.saveState?.(true, 'climbed out');
    return true;
  }

  // Put the body at `pos` facing `yaw`, `sit` deep into the seated pose, and
  // point the camera at it. Shared by every beat of getting in and out.
  _poseBoard(dt, pos, yaw, sit) {
    const g = this.game;
    this.rig.update(dt);
    const hip = sit > 0.02 ? this.rig.setSeated(sit) : (this.rig.setSeated(0), 0);
    this.yaw = yaw;
    this.rig.obj.rotation.set(0, yaw, 0);
    this.rig.obj.position.set(pos.x, pos.y - (hip ?? 0) * sit, pos.z);
    this.pos.copy(this.rig.obj.position);
    // the camera watches from where it already is, so getting in reads as one
    // continuous shot instead of a cut
    g.camera.lookAt(pos.x, pos.y + EYE * 0.8, pos.z);
  }

  // yaw that faces b from a, or null when they are the same spot
  _faceYaw(a, b) {
    const dx = b.x - a.x;
    const dz = b.z - a.z;
    return dx * dx + dz * dz < 1e-4 ? null : Math.atan2(dx, dz) + Math.PI;
  }

  _yawTo(a, b, k) {
    let d = (b - a) % (Math.PI * 2);
    if (d > Math.PI) d -= Math.PI * 2;
    if (d < -Math.PI) d += Math.PI * 2;
    return a + d * k;
  }

  // Every frame the hero is in the aeroplane: the climb if one is running,
  // otherwise just sitting there.
  updatePilot(dt) {
    if (this._climbStep(dt)) return;
    this.updateSeated(dt);
  }

  // Log back in on foot: the hero was walking when the session ended, so put
  // it back on its own two feet at the exact spot instead of in the cockpit.
  // Put the hero back on its feet where the session ended. If the body is not
  // there yet, try ONCE more rather than silently seating the player in the
  // plane -- being somewhere you did not park is worse than a moment's wait.
  resume([x, y, z], yaw) {
    const stand = () => {
      if (this.game.disposed || this.active) return;
      const g = this.game;
      const gY = g._groundAt(x, z, y + 1.2, 1.2);
      this._stand(x, (gY ?? y) + 0.02, z, yaw);
    };
    return this.ready.then(() => {
      if (this.rig) return stand();
      console.warn('resuming on foot but the body is missing — retrying');
      this.ready = this._loadBody();
      return this.ready.then(() => {
        if (this.rig) return stand();
        this.game._setHint('⚠ გმირის მოდელი ვერ ჩაიტვირთა — დაბრუნდი თვითმფრინავში');
      });
    });
  }

  // ---- worn gear ----------------------------------------------------------
  // A belt is not a weapon: it has no slot and no trigger, it is simply WORN.
  // It goes on the pelvis so it swings with the walk, and it is scaled to the
  // hips of whatever body is wearing it -- these 34 personages range from a
  // soldier to a banana, and a fixed-size belt either floats around the thin
  // ones or cuts through the wide ones.
  async wearGear(def) {
    if (!def?.id || !this.rig) return false;
    if (this.worn.has(def.id)) return true;
    let obj = def.obj ?? null;
    if (!obj) {
      try {
        obj = await loadWeapon(def);
      } catch (e) {
        console.warn(`gear ${def.id} failed to load:`, e?.message ?? e);
        return false;
      }
    }
    if (!this.rig) return false;        // climbed back in while it downloaded
    obj.traverse((o) => {
      if (!o.isMesh) return;
      o.castShadow = true;
      o.receiveShadow = true;
      // Worn kit hangs off a BONE, and a bone-parented mesh is frustum-culled
      // against a stale matrixWorld -- the skeleton moves it after the cull
      // test, so it is checked in the wrong place and silently never draws.
      // Same trap the held weapon falls into (see weapons.js).
      o.frustumCulled = false;
    });
    // No depth bias: the hands have to pass in FRONT of the belt, and the
    // strap is fitted to clear the body on its own (see _centreBelt).
    biasWorn(obj);
    const names = def.sockets ?? (def.socket ? [def.socket] : []);
    const sockets = names.map((n) => obj.getObjectByName(n)).filter(Boolean);
    const worn = { ...def, obj, sockets, socket: sockets[0] ?? null };
    this._seatGear(worn);
    this.worn.set(def.id, worn);
    // A pistol the hero already owns but is not holding now has somewhere to be
    if (!this.held) this._toHolster();
    return true;
  }

  removeGear(id) {
    const w = this.worn.get(id);
    if (!w) return false;
    // whatever is sitting in it comes off with it
    for (const gun of [...this._holstered]) {
      if (w.obj?.getObjectById(gun.obj.id)) this._fromHolster(gun);
    }
    this._unskinBelt(w);
    w.obj.parent?.remove(w.obj);
    this.worn.delete(id);
    return true;
  }

  // Put a piece of gear on the body: find the bone it belongs to, size it to
  // this body, and hang it there.
  _seatGear(w) {
    const rig = this.rig;
    const bones = [];
    rig.obj.traverse((o) => { if (o.isBone) bones.push(o); });
    // a new body, or a refit: the pockets are measured again and whatever is
    // holstered is seated again (_seatInPocket), because the conform below
    // moves the strap and the hangers with it
    for (const sk of w.sockets ?? []) { delete sk.userData.pocket; delete sk.userData.seatedFor; delete sk.userData.standExtra; }
    const norm = (n) => (n || '').toLowerCase().replace(/[^a-z]/g, '');
    const hips = bones.find((b) => /pelvis|hips$|hip$/.test(norm(b.name)))
      ?? bones.find((b) => /spine|torso|abdomen/.test(norm(b.name)))?.parent
      ?? null;
    // A refit on the body it is already on (a conform toggled, a re-seat):
    // the skinned copies from last time are in the rig's tree, and left there
    // they would be measured as body. Off first, back on at the end.
    this._unskinBelt(w);
    const width = this._waistWidth(hips);
    const scale = (width * (w.fit ?? 1)) / (w.width ?? 0.33);

    if (hips) {
      hips.updateWorldMatrix(true, false);
      const bs = new THREE.Vector3();
      hips.matrixWorld.decompose(new THREE.Vector3(), new THREE.Quaternion(), bs);
      // the bone carries the body's 1.8 m normalisation scale and the belt
      // inherits it, so divide it back out or it arrives hula-hoop sized
      w.obj.scale.setScalar(scale / (bs.x || 1));
      hips.add(w.obj);
      // A belt is worn ON THE WAIST, and the pelvis bone is at the hip JOINT,
      // which is lower. Sit it at the height the waist was actually measured
      // at -- the belt then lands where the body is narrowest instead of around
      // the top of the thighs.
      hips.updateWorldMatrix(true, false);
      const at = new THREE.Vector3().setFromMatrixPosition(hips.matrixWorld);
      if (this._waistY) {
        // `_waistY` is a height above the rig's ORIGIN, so it is still right
        // after the hero has walked somewhere else; a world height, cached at
        // load, put a re-seated belt 469 m under the feet.
        rig.obj.updateWorldMatrix(true, false);
        at.y = new THREE.Vector3().setFromMatrixPosition(rig.obj.matrixWorld).y + this._waistY;
      }
      // ...and a bit lower than the narrowest point. A belt rides on the hips,
      // not around the ribs, and sitting it on the measured waist put it up
      // where the hands swing -- the arms went through it on every stride.
      at.y -= (w.drop ?? 0);
      w.obj.position.set(0, 0, 0);
      w.obj.position.copy(hips.worldToLocal(at));
      // The belt is modelled in the GAME frame (ring flat, buckle forward,
      // holster on the right). Bone axes disagree wildly between these rigs, so
      // the wanted WORLD rotation is built from the body's own facing and the
      // bone's rotation is taken back out -- the same solve seatWeapon does.
      //
      // YAW ONLY. Taking the root's full rotation tipped the ring with
      // whatever the body was doing, and a tilted ring has one side up inside
      // the belly and the other hanging out below it -- which is what "the
      // belt is inside the body" looked like from behind. A belt lies FLAT on
      // a standing body however the model above it is leaning.
      const bq = new THREE.Quaternion();
      hips.matrixWorld.decompose(new THREE.Vector3(), bq, new THREE.Vector3());
      rig.obj.updateWorldMatrix(true, false);
      const rq = new THREE.Quaternion();
      rig.obj.matrixWorld.decompose(new THREE.Vector3(), rq, new THREE.Vector3());
      const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(rq);
      fwd.y = 0;
      if (fwd.lengthSq() > 1e-8) {
        fwd.normalize();
        rq.setFromEuler(new THREE.Euler(0, Math.atan2(-fwd.x, -fwd.z), 0, 'YXZ'));
      }
      w.obj.quaternion.copy(bq).invert().multiply(rq);
      this._centreBelt(w);
      this._skinBelt(w);
    } else {
      // No pelvis to hang it on (the astronaut, the props). Ride the body group
      // at a measured waist height instead: it does not swing with the hips,
      // but it is on the body and it is the right size.
      w.obj.scale.setScalar(scale);
      rig.act.add(w.obj);
      w.obj.quaternion.identity();
      w.obj.position.set(0, (rig.height ?? 1.8) * 0.53, 0);
    }
    w.scale = scale;
  }

  // STRETCH THE STRAP ONTO THE BODY.
  //
  // Scaling a belt up until it clears the widest point is not what a belt does
  // -- it stands off everywhere else, and on a round body like the banana it
  // reads as a hoop the hero is standing inside. A real one takes the shape of
  // whatever it is buckled around.
  //
  // So it is conformed, not scaled: the body's own radius is measured at the
  // belt's height in 32 directions, and every vertex of the belt is pushed
  // outward by however far the STRAP at its own direction had to move to land
  // on that profile. Band, buckle, both holsters -- the hangers travel with
  // the strap point they are welded to instead of being stretched with it, so
  // a holster keeps its shape wherever the body pushes it out to.
  //
  // (tools/import_belt.py does the same arithmetic against a constant radius,
  // to make the modelled strap round in the first place. Same idea, and this
  // is the half of it that cannot be baked: it depends on the body.)
  _centreBelt(w) {
    this._fitBelt(w);
    this._clearHangers(w);
  }

  // THE HANGERS CLEAR THE HIPS. The ring is fitted at the band's own height,
  // and a body is wider lower down: the pocket hangs 13 cm below the strap,
  // which on a padded hip is inside the thigh -- and the pistol seated in it
  // goes in with it. So each hanger is measured against the body at ITS
  // heights (every mesh, thighs included) along its own bearing, and the whole
  // hanger is stood off rigidly by the worst shortfall, socket and all. A real
  // holster stands off the hip for exactly this reason.
  _clearHangers(w) {
    const root = w.obj;
    if (!root || !w.sockets?.length) return;
    root.updateMatrixWorld(true);
    const band = this._ringOf(w);
    if (!band) return;
    const inv = _gm1.copy(root.matrixWorld).invert();
    const wscale = root.getWorldScale(_gv4).x || 1;
    const bandYL = _gv4.copy(band.c).applyMatrix4(inv).y;
    const rq = root.getWorldQuaternion(new THREE.Quaternion());
    const origin = root.getWorldPosition(new THREE.Vector3());
    const meshes = this._gearMeshes(w);
    const v = new THREE.Vector3();
    const ray = this._ray;
    const MARGIN = 0.008;                      // belt units: ~1 cm of air
    for (const sk of w.sockets) {
      const az = Math.atan2(sk.position.z, sk.position.x);
      const rad = new THREE.Vector3(Math.cos(az), 0, Math.sin(az));
      const dirW = rad.clone().applyQuaternion(rq).setY(0).normalize();
      // the hanger: everything below the band on this bearing
      const cluster = [];
      let yLo = Infinity; let yHi = -Infinity;
      for (const o of meshes) {
        o.updateWorldMatrix(true, false);
        const m = _gm2.multiplyMatrices(inv, o.matrixWorld);
        const pos = o.geometry.attributes.position;
        for (let i = 0; i < pos.count; i++) {
          v.fromBufferAttribute(pos, i).applyMatrix4(m);
          if (v.y > bandYL - 0.03) continue;
          const d = Math.atan2(Math.sin(Math.atan2(v.z, v.x) - az), Math.cos(Math.atan2(v.z, v.x) - az));
          if (Math.abs(d) > 0.5) continue;
          cluster.push({ o, i, y: v.y, r: v.x * rad.x + v.z * rad.z });
          if (v.y < yLo) yLo = v.y;
          if (v.y > yHi) yHi = v.y;
        }
      }
      if (cluster.length < 40) continue;
      // the body along this bearing at six heights down the hanger
      const yMidW = v.set(0, (yLo + yHi) / 2, 0).applyMatrix4(root.matrixWorld).y;
      const hull = this._torsoHull(yMidW, 'lower');
      if (!hull) continue;
      let shortfall = 0;
      for (let k = 0; k <= 5; k++) {
        const yL = yHi - (yHi - yLo) * (k / 5);
        let inner = Infinity;
        for (const c of cluster) if (Math.abs(c.y - yL) < 0.012 && c.r < inner) inner = c.r;
        if (!Number.isFinite(inner)) continue;
        const yW = v.set(0, yL, 0).applyMatrix4(root.matrixWorld).y;
        const from = new THREE.Vector3(origin.x, yW, origin.z).addScaledVector(dirW, 3);
        ray.set(from, dirW.clone().negate());
        ray.far = 3;
        const hit = ray.intersectObject(hull, false)[0];
        ray.far = Infinity;
        if (!hit) continue;
        const bodyR = Math.hypot(hit.point.x - origin.x, hit.point.z - origin.z) / wscale;
        shortfall = Math.max(shortfall, bodyR + MARGIN - inner);
      }
      // a hip is a few centimetres wider than a waist; more than that is a
      // measurement gone wrong (an arm, a cape), and the pocket stays put
      if (shortfall <= 0 || shortfall > 0.06) {
        if (shortfall > 0.06) console.log(`belt: ${sk.name} stand-off of ${Math.round(shortfall * wscale * 1000)} mm not believed`);
        continue;
      }
      // THE STRAP STAYS ON THE BODY. The hanger moves out by a centimetre and
      // a half at most -- a holster does sit a little proud of the belt --
      // and whatever the hip still wants beyond that is taken by the PISTOL
      // standing proud of the pocket (_seatInPocket reads `standExtra`), never
      // by the belt: pushing the band out to follow the pocket (tried) made a
      // belt wider than the body it was on.
      const push = Math.min(shortfall, 0.015);
      for (const o of meshes) {
        if (!o.geometry.userData._conformed) {
          o.geometry = o.geometry.clone();
          o.geometry.userData._conformed = true;
        }
      }
      for (const c of cluster) {
        const pos = c.o.geometry.attributes.position;
        pos.setX(c.i, pos.getX(c.i) + rad.x * push);
        pos.setZ(c.i, pos.getZ(c.i) + rad.z * push);
        pos.needsUpdate = true;
      }
      for (const o of meshes) {
        o.geometry.computeVertexNormals();
        o.geometry.computeBoundingSphere();
      }
      sk.position.addScaledVector(rad, push);
      sk.updateWorldMatrix(false, true);
      sk.userData.standExtra = shortfall - push;
      console.log(`belt: ${sk.name} hanger stood off the hip by ${Math.round(push * wscale * 1000)} mm`
        + (shortfall > push ? `, pistol a further ${Math.round((shortfall - push) * wscale * 1000)} mm` : ''));
    }
  }

  _fitBelt(w) {
    const parent = w.obj.parent;
    if (!parent) return;
    w.obj.updateMatrixWorld(true);
    const band = this._ringOf(w);
    if (!band) return;
    const body = this._bodySection(band.y);
    if (!body) return;

    // Put the ring's own centre on the body's axis first: the profile below is
    // measured about that point, and an origin sitting outside the body makes
    // nonsense of every direction.
    //
    // ...then push it BACK off that axis. A belt is not concentric with a
    // body: the buckle stands off the belly at the front and the strap sits
    // close across the small of the back, which is where the room is. `back`
    // in GEAR_DEFS is how far, in metres.
    const shift = new THREE.Vector3(body.c.x - band.c.x, 0, body.c.z - band.c.z);
    if (w.back) {
      this.rig.obj.updateWorldMatrix(true, false);
      const rq = new THREE.Quaternion();
      this.rig.obj.matrixWorld.decompose(new THREE.Vector3(), rq, new THREE.Vector3());
      const back = new THREE.Vector3(0, 0, 1).applyQuaternion(rq);
      back.y = 0;
      if (back.lengthSq() > 1e-8) shift.addScaledVector(back.normalize(), w.back);
    }
    w.obj.position.copy(parent.worldToLocal(
      new THREE.Vector3().setFromMatrixPosition(w.obj.matrixWorld).add(shift)));
    w.obj.updateMatrixWorld(true);

    // ONE CLEAN RING, sized to clear the body, unless the body is asking for
    // the conform. Pushing each direction of the strap onto the body's own
    // surface is the better idea and it is not reliable enough to ship: on a
    // body whose cross-section at belt height is not a simple loop -- a banana
    // tapering into its legs -- some directions measure a surface that is not
    // the waist, and the strap gets dragged through the hip. A ring that is
    // honestly a little loose never does that. `conform: true` in GEAR_DEFS
    // turns the clever version back on.
    if (!w.conform) { this._scaleBelt(w, body, band); return; }

    const N = 32;
    const bandY = band.y;
    const halfBand = 0.025;
    const v = new THREE.Vector3();
    const binOf = (x, z) => {
      let a = Math.atan2(z, x);
      if (a < 0) a += Math.PI * 2;
      return Math.min(N - 1, Math.floor((a / (Math.PI * 2)) * N));
    };
    // A direction nothing landed in is a GUESS, and a profile that is mostly
    // guesses is how a belt ends up with one lobe a metre across. Gaps are
    // closed from their neighbours, but only if there were few of them: past a
    // quarter of the circle this gives up and the caller keeps the plain
    // scaled fit, which is never beautiful and is never wrong either.
    const fill = (prof) => {
      const holes = prof.filter((x) => x <= 0).length;
      if (holes > N / 4) return false;
      for (let pass = 0; pass < 4; pass++) {
        for (let k = 0; k < N; k++) {
          if (prof[k] > 0) continue;
          const nb = [prof[(k - 1 + N) % N], prof[(k + 1) % N]].filter((x) => x > 0);
          if (nb.length) prof[k] = nb.reduce((a, b) => a + b, 0) / nb.length;
        }
      }
      return prof.every((x) => x > 0);
    };

    // WHERE IS THE BODY, REALLY. Not binned vertices -- that is what kept
    // getting it wrong: a gap between a limb and the torso, or a direction
    // with nothing sampled in it, and the strap was dragged bodily through a
    // hip. Fire a ray out of the belt's own axis in each direction and take
    // the surface it actually hits. A direction that hits nothing keeps the
    // strap where it was rather than guessing.
    const torso = this._torsoHull(bandY);
    const bodyProf = new Array(N).fill(0);
    if (!torso) { this._scaleBelt(w, body, band); return; }
    const origin = new THREE.Vector3();
    w.obj.getWorldPosition(origin);
    origin.y = bandY;
    const ray = this._ray;
    const dir = new THREE.Vector3();
    const invS = 1 / (w.obj.getWorldScale(new THREE.Vector3()).x || 1);
    let hits = 0;
    for (let k = 0; k < N; k++) {
      const a = ((k + 0.5) / N) * Math.PI * 2;
      dir.set(Math.cos(a), 0, Math.sin(a)).applyQuaternion(
        w.obj.getWorldQuaternion(new THREE.Quaternion())).normalize();
      dir.y = 0;
      if (dir.lengthSq() < 1e-8) continue;
      dir.normalize();
      // from OUTSIDE, pointing back in: a ray starting inside a closed body
      // leaves through the far wall and measures the wrong side of it.
      ray.set(v.copy(origin).addScaledVector(dir, 3), dir.clone().negate());
      ray.far = 3;
      const hit = ray.intersectObject(torso, false)[0];
      ray.far = Infinity;
      if (!hit) continue;
      bodyProf[k] = v.subVectors(hit.point, origin).setY(0).length() * invS;
      hits++;
    }
    if (hits < N * 0.6) {
      console.log(`belt: conform off, only ${hits}/${N} directions hit the torso`);
      this._scaleBelt(w, body, band); return;
    }
    if (!fill(bodyProf)) {
      console.log('belt: conform off, too many gaps in the body profile');
      this._scaleBelt(w, body, band); return;
    }

    // the STRAP, same bins. The band is picked in WORLD height -- the belt
    // carries a rotation and the bone's own scale, so "the local y that means
    // waist height" is not a number you can divide your way to -- while the
    // radius is read in LOCAL units, which is where the vertices have to be
    // written back.
    const strapProf = new Array(N).fill(0);
    const wv = new THREE.Vector3();
    const meshes = this._gearMeshes(w);
    for (const o of meshes) {
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        v.fromBufferAttribute(pos, i);
        wv.copy(v).applyMatrix4(o.matrixWorld);
        if (Math.abs(wv.y - bandY) > halfBand) continue;
        const r = Math.hypot(v.x, v.z);
        const k = binOf(v.x, v.z);
        if (r > strapProf[k]) strapProf[k] = r;
      }
    }
    if (!fill(strapProf)) {
      console.log('belt: conform off, too many gaps in the strap profile');
      this._scaleBelt(w, body, band); return;
    }

    // how far each direction has to move, with the fit as the standoff
    const fit = w.fit ?? 1;
    const push = bodyProf.map((r, k) => (r * fit) - strapProf[k]);
    {
      const mm = (x) => Math.round(x * 1000);
      const lo = Math.min(...bodyProf); const hi = Math.max(...bodyProf);
      console.log(`belt: conformed, ${hits}/${N} hits, body radius ${mm(lo)}..${mm(hi)} mm, `
        + `push ${mm(Math.min(...push))}..${mm(Math.max(...push))} mm`);
    }
    const at = (x, z) => push[binOf(x, z)];
    for (const o of meshes) {
      if (!o.geometry.userData._conformed) {
        o.geometry = o.geometry.clone();      // never deform a shared buffer
        o.geometry.userData._conformed = true;
      }
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        const x = pos.getX(i);
        const z = pos.getZ(i);
        const r = Math.hypot(x, z);
        if (r < 1e-5) continue;
        const d = at(x, z) / r;
        pos.setX(i, x + x * d);
        pos.setZ(i, z + z * d);
      }
      pos.needsUpdate = true;
      o.geometry.computeVertexNormals();
      o.geometry.computeBoundingSphere();
    }
    // the holsters ride out with the strap they hang from
    for (const sk of w.sockets ?? []) {
      const r = Math.hypot(sk.position.x, sk.position.z);
      if (r < 1e-5) continue;
      const d = at(sk.position.x, sk.position.z) / r;
      sk.position.x += sk.position.x * d;
      sk.position.z += sk.position.z * d;
    }
  }

  // The fallback: one scale for the whole strap, sized so its typical radius
  // clears the body's widest point. Used when the body is too coarse at that
  // height to measure a profile from.
  _scaleBelt(w, body, band) {
    const r = this._ringRadius(w, band.y);
    if (!r) return;
    const grow = THREE.MathUtils.clamp((body.r * (w.fit ?? 1)) / r, 0.5, 1.8);
    w.obj.scale.multiplyScalar(grow);
    w.scale *= grow;
  }

  // The strap's median radius about its own origin, in world metres.
  _ringRadius(w, y) {
    const N = 16;
    const best = new Array(N).fill(0);
    const v = new THREE.Vector3();
    const c = w.obj.getWorldPosition(new THREE.Vector3());
    for (const o of this._gearMeshes(w)) {
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
        if (Math.abs(v.y - y) > 0.025) continue;
        let a = Math.atan2(v.z - c.z, v.x - c.x);
        if (a < 0) a += Math.PI * 2;
        const k = Math.min(N - 1, Math.floor((a / (Math.PI * 2)) * N));
        best[k] = Math.max(best[k], Math.hypot(v.x - c.x, v.z - c.z));
      }
    }
    const used = best.filter((x) => x > 0).sort((a, b) => a - b);
    return used.length >= N / 2 ? used[Math.floor(used.length / 2)] : 0;
  }

  // The strap's own ring, in world space: the verts at the median height of the
  // whole piece, which is the band -- a holster hangs well below it.
  _ringOf(w) {
    const pts = [];
    const v = new THREE.Vector3();
    for (const o of this._gearMeshes(w)) {
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      // EVERY vertex, no stride: the back of this strap is a handful of quads
      // and a sampled sweep walks straight past it. 2.5k verts, once.
      for (let i = 0; i < pos.count; i++) {
        pts.push(v.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld).clone());
      }
    }
    if (pts.length < 8) return null;
    const ys = pts.map((p) => p.y).sort((a, b) => a - b);
    const y = ys[Math.floor(ys.length / 2)];
    const band = pts.filter((p) => Math.abs(p.y - y) < 0.025);
    if (band.length < 8) return null;
    let lox = Infinity; let hix = -Infinity; let loz = Infinity; let hiz = -Infinity;
    for (const p of band) {
      lox = Math.min(lox, p.x); hix = Math.max(hix, p.x);
      loz = Math.min(loz, p.z); hiz = Math.max(hiz, p.z);
    }
    return { c: new THREE.Vector3((lox + hix) / 2, y, (loz + hiz) / 2), y };
  }

  // MAKE THE BELT PART OF THE BODY.
  //
  // Hung off the pelvis bone it is one rigid ring: it follows the hips, and it
  // cannot deform. Roll, slide, crouch or tuck into a jump and the body bends
  // around a circle that stays a perfect circle -- so the strap ends up
  // through the belly on one side and out in the air on the other. That is the
  // same thing worn kit does in any engine until it is SKINNED, and this skins
  // it: the strap stops being a prop on a bone and becomes geometry on the
  // hero's own skeleton, bending exactly as the body bends.
  //
  // Weights come from the BONES, not from the body's vertices. A nearest-vertex
  // transfer is the textbook way and it needs the body's weights to be sane,
  // which across 39 bodies of wildly different provenance they are not. Two
  // nearest bones out of the hip-and-spine set, by inverse distance, is cruder
  // and behaves the same on every one of them -- and for a belt, which only
  // ever spans a hip and the spine above it, the difference is invisible.
  //
  // The HOLSTER SOCKETS stay on the rigid group, which is kept and hidden. A
  // weapon hanging off a skinned vertex would need the same solve every frame;
  // hanging off the hips is what it did before and what it looks like.
  _skinBelt(w) {
    const rig = this.rig;
    if (!rig) return false;
    this._unskinBelt(w);   // a body reload: the old copies are on a dead skeleton
    let body = null;
    let bestArea = 0;
    rig.obj.traverse((o) => {
      if (!o.isSkinnedMesh || !o.skeleton?.bones?.length || !o.geometry) return;
      o.geometry.computeBoundingBox?.();
      const b = o.geometry.boundingBox;
      if (!b) return;
      const sz = b.getSize(new THREE.Vector3());
      const area = sz.x * sz.y + sz.y * sz.z + sz.z * sz.x;
      if (area > bestArea) { bestArea = area; body = o; }
    });
    if (!body) return false;                 // not a skinned body: stay rigid
    const skel = body.skeleton;
    body.updateMatrixWorld(true);
    skel.update();

    // the bones a belt is allowed to hang from, for the fallback
    const cand = [];
    skel.bones.forEach((b, i) => {
      if (!/pelvis|hips?$|spine|torso|abdomen|waist|belly|body/i.test(b.name || '')) return;
      b.updateWorldMatrix(true, false);
      cand.push({ i, p: new THREE.Vector3().setFromMatrixPosition(b.matrixWorld) });
    });
    if (!cand.length) return false;

    // bone matrices exactly as the skinning shader builds them
    const boneM = skel.bones.map((b, i) =>
      new THREE.Matrix4().multiplyMatrices(b.matrixWorld, skel.boneInverses[i]));
    const bindM = body.bindMatrix.clone();
    const bindMI = body.bindMatrixInverse.clone();
    const bodyInv = new THREE.Matrix4().copy(body.matrixWorld).invert();

    // THE SKIN'S OWN WEIGHTS, off the surface the strap sits on. Two nearest
    // bones by distance (the fallback below) was how the belt went INTO the
    // body on every stride: the banana leans into a run by bending its spine,
    // the skin across its back moves with that bend, and a strap weighted by
    // where the bones happen to be does not -- so the back of it ended up a
    // hand's width inside the fruit. Copying the weights of the nearest body
    // vertex at band height makes the strap a piece of that surface: whatever
    // the skin does there, the belt does, which is the only way to never be
    // inside it. The holsters take the weights of the strap point above them,
    // so a pocket moves as one piece with the band it hangs from.
    const band = this._ringOf(w);
    // the waist's own vertices first; on a body whose belt line falls on the
    // hips mesh (cyberfem: the torso mesh starts above the band) the skin
    // there is thigh-weighted, and that is still what the belt sits on
    let samples = this._skinSamples(skel, band?.y ?? null, 'torso');
    if (samples.length < 12) samples = this._skinSamples(skel, band?.y ?? null, 'lower');
    const useSkin = samples.length >= 12;   // a coarse waist still beats bone distance
    const bandY = band?.y ?? 0;
    const nearSample = (p) => {
      // measured at band height: a vertex hanging below the band maps to the
      // strap point straight above it, not to the thigh beside it
      const y = THREE.MathUtils.clamp(p.y, bandY - 0.03, bandY + 0.03);
      let best = null; let bd = Infinity;
      for (const sm of samples) {
        const dx = sm.p.x - p.x; const dy = sm.p.y - y; const dz = sm.p.z - p.z;
        const d = dx * dx + dy * dy + dz * dz;
        if (d < bd) { bd = d; best = sm; }
      }
      return best;
    };

    // LIFT THE SOCKETS OFF THE MESH FIRST. They are modelled as children of the
    // strap mesh, and the rigid strap is about to be hidden -- and `visible =
    // false` prunes the whole subtree, so hiding it would hide the sockets and
    // the holstered weapon hanging in them. attach() keeps their world
    // transform, so nothing moves.
    for (const sk of w.sockets ?? []) {
      if (sk.parent && sk.parent !== w.obj) w.obj.attach(sk);
    }

    const made = [];
    const v = new THREE.Vector3();
    const skinM = new THREE.Matrix4();
    const S = new THREE.Matrix4();
    const idx = [0, 0, 0, 0];
    const wt = [0, 0, 0, 0];
    // weights for a world point: the skin's, or the two nearest bones'
    const weigh = (p) => {
      if (useSkin) {
        const sm = nearSample(p);
        if (sm) { for (let j = 0; j < 4; j++) { idx[j] = sm.si[j]; wt[j] = sm.sw[j]; } return; }
      }
      let a0 = null; let a1 = null; let d0 = Infinity; let d1 = Infinity;
      for (const c of cand) {
        const d = c.p.distanceToSquared(p);
        if (d < d0) { d1 = d0; a1 = a0; d0 = d; a0 = c; }
        else if (d < d1) { d1 = d; a1 = c; }
      }
      const w0 = 1 / Math.max(1e-4, Math.sqrt(d0));
      const w1 = a1 ? 1 / Math.max(1e-4, Math.sqrt(d1)) : 0;
      const sum = w0 + w1;
      idx[0] = a0.i; idx[1] = a1 ? a1.i : a0.i; idx[2] = a0.i; idx[3] = a0.i;
      wt[0] = w0 / sum; wt[1] = w1 / sum; wt[2] = 0; wt[3] = 0;
    };
    // the blended bone matrix for the weights just chosen
    const blend = () => {
      const e = skinM.elements;
      for (let m = 0; m < 16; m++) e[m] = 0;
      for (let j = 0; j < 4; j++) {
        if (!(wt[j] > 0)) continue;
        const ej = boneM[idx[j]].elements;
        for (let m = 0; m < 16; m++) e[m] += ej[m] * wt[j];
      }
      return skinM;
    };
    const meshes = this._gearMeshes(w);
    for (const o of meshes) {
      o.updateWorldMatrix(true, false);
      const geo = o.geometry.clone();
      const pos = geo.attributes.position;
      const si = new Uint16Array(pos.count * 4);
      const sw = new Float32Array(pos.count * 4);
      for (let k = 0; k < pos.count; k++) {
        v.fromBufferAttribute(pos, k).applyMatrix4(o.matrixWorld);   // world
        weigh(v);
        for (let j = 0; j < 4; j++) { si[k * 4 + j] = idx[j]; sw[k * 4 + j] = wt[j]; }
        // where this vertex has to sit in BIND space to land where it is now:
        // the shader does bindMatrixInverse * skinMatrix * bindMatrix * p, so
        // run that backwards.
        S.multiplyMatrices(blend(), bindM).premultiply(bindMI).invert();
        v.applyMatrix4(bodyInv).applyMatrix4(S);
        pos.setXYZ(k, v.x, v.y, v.z);
      }
      pos.needsUpdate = true;
      geo.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(si, 4));
      geo.setAttribute('skinWeight', new THREE.Float32BufferAttribute(sw, 4));
      geo.computeVertexNormals();
      const sm = new THREE.SkinnedMesh(geo, o.material);
      sm.name = 'belt_skinned';
      sm.castShadow = true;
      sm.receiveShadow = true;
      sm.frustumCulled = false;      // skinned: culled against a stale sphere
      sm.position.copy(body.position);
      sm.quaternion.copy(body.quaternion);
      sm.scale.copy(body.scale);
      body.parent.add(sm);
      sm.bind(skel, bindM);
      o.visible = false;             // the rigid copy stays, carrying the sockets
      made.push(sm);
    }
    if (!made.length) return false;
    w.skinned = made;

    // THE SOCKETS RIDE THE STRAP. They are rigid children of the hips bone, and
    // the strap is not any more: it bends with the skin. A pocket that stays
    // on the bone while the band it hangs from moves with the body is a pistol
    // floating beside its own holster halfway through a roll. So each socket
    // takes the weights of the strap point it hangs from and is re-posed from
    // them every frame (_rideSockets) -- the same transform the skin gives
    // that vertex, applied to the whole socket.
    w.ride = [];
    w.rideSkel = skel;
    w.rideBody = body;
    for (const sk of w.sockets ?? []) {
      sk.updateWorldMatrix(true, false);
      v.setFromMatrixPosition(sk.matrixWorld);
      weigh(v);
      // what the skin does to this point now, and the socket's place under it
      const r = { socket: sk, si: idx.slice(), sw: wt.slice(), local: new THREE.Matrix4() };
      r.local.copy(this._skinXform(w, r, new THREE.Matrix4())).invert().multiply(sk.matrixWorld);
      w.ride.push(r);
    }
    console.log(`belt: skinned ${useSkin ? `off ${samples.length} skin vertices` : `to ${cand.length} bones (${samples.length} skin samples)`} (${made.length} mesh) on ${body.name}`);
    return true;
  }

  // The body's skin at band height: world position and the four weights of
  // every TORSO vertex within a hand of the belt, on any skinned mesh of the
  // body that shares the skeleton the belt is bound to.
  _skinSamples(skel, bandY, mode = 'torso') {
    if (bandY === null) return [];
    const out = [];
    const v = new THREE.Vector3();
    // THE RECURSIVE update, not updateWorldMatrix: only SkinnedMesh's own
    // updateMatrixWorld refreshes bindMatrixInverse, and getVertexPosition
    // goes through it. Left stale on a body that has moved since it loaded,
    // every skinned vertex came back a hero's position off -- which read as
    // "no skin anywhere near the belt" and dropped cyberfem to bone weights.
    this.rig?.obj.updateMatrixWorld(true);
    for (const o of this._bodyMeshes()) {
      if (!o.isSkinnedMesh || !o.skeleton) continue;
      const si = o.geometry.attributes.skinIndex;
      const sw = o.geometry.attributes.skinWeight;
      if (!si || !sw) continue;
      // bone indices are per skeleton: map this mesh's onto the belt's
      const same = o.skeleton === skel;
      const map = same ? null : o.skeleton.bones.map((b) => skel.bones.indexOf(b));
      const torso = this._torsoTest(o, mode);
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        if (torso && !torso(i)) continue;
        o.getVertexPosition(i, v).applyMatrix4(o.matrixWorld);
        if (Math.abs(v.y - bandY) > 0.2) continue;
        const ii = [si.getX(i), si.getY(i), si.getZ(i), si.getW(i)];
        const ww = [sw.getX(i), sw.getY(i), sw.getZ(i), sw.getW(i)];
        if (map) {
          let sum = 0;
          for (let j = 0; j < 4; j++) {
            ii[j] = map[ii[j]] ?? -1;
            if (ii[j] < 0) { ii[j] = 0; ww[j] = 0; }
            sum += ww[j];
          }
          if (sum < 1e-4) continue;
          for (let j = 0; j < 4; j++) ww[j] /= sum;
        }
        out.push({ p: v.clone(), si: ii, sw: ww });
      }
    }
    return out;
  }

  // Every frame: put each holster socket where the skin has taken the strap
  // point it hangs from. Runs after the body has been posed for the frame.
  _rideSockets() {
    for (const w of this.worn.values()) this._rideOne(w);
    // ...and the back sling, which is not worn gear but hangs off the same
    // skin (see _backSocket).
    if (this._backSock?.rig === this.rig) this._rideOne(this._backSock);
  }

  _rideOne(w) {
    if (!w?.ride?.length || !w.rideSkel || !w.rideBody) return;
    for (const r of w.ride) {
      this._skinXform(w, r, _rideM).multiply(r.local);
      const sk = r.socket;
      if (!sk.parent) continue;
      sk.parent.updateWorldMatrix(true, false);
      _rideB.copy(sk.parent.matrixWorld).invert().multiply(_rideM);
      _rideB.decompose(sk.position, sk.quaternion, sk.scale);
      sk.updateWorldMatrix(false, true);
    }
  }

  // What the skin does, right now, to a point carrying these weights: the
  // world transform body * bindInv * (sum w_j bone_j) * bind.
  //
  // NOT `matrixWorld * bindMatrixInverse * ...`, although that is the formula
  // the shader runs. In the default 'attached' bind mode three.js refreshes
  // bindMatrixInverse to the inverse of the mesh's matrixWorld only when the
  // renderer walks the scene -- so read mid-frame, after the hero has moved,
  // the two do not cancel and the product is the hero's per-frame
  // displacement: the pistol floated off the hip by a stride a frame at a run.
  // In 'attached' mode the pair cancels by definition, so it is left out.
  _skinXform(w, r, out) {
    const skel = w.rideSkel;
    const body = w.rideBody;
    const e = out.elements;
    for (let m = 0; m < 16; m++) e[m] = 0;
    for (let j = 0; j < 4; j++) {
      if (!(r.sw[j] > 0)) continue;
      const b = skel.bones[r.si[j]];
      if (!b) continue;
      b.updateWorldMatrix(true, false);
      _rideB.multiplyMatrices(b.matrixWorld, skel.boneInverses[r.si[j]]);
      const ej = _rideB.elements;
      for (let m = 0; m < 16; m++) e[m] += ej[m] * r.sw[j];
    }
    out.multiply(body.bindMatrix);
    if (body.bindMode === 'detached') {
      body.updateWorldMatrix(true, false);
      out.premultiply(body.bindMatrixInverse).premultiply(body.matrixWorld);
    }
    return out;
  }

  // Throw away the skinned copies and show the rigid original again. Called
  // before re-skinning onto a new body, and when the gear comes off.
  _unskinBelt(w) {
    for (const m of w.skinned ?? []) {
      m.parent?.remove(m);
      m.geometry?.dispose();
    }
    w.skinned = null;
    w.ride = null;
    w.obj?.traverse((o) => { if (o.isMesh) o.visible = true; });
  }

  // THE BODY'S OWN MESHES, and nothing hung on it. Everything worn or carried
  // lives inside the rig's tree -- the belt under the pelvis bone, its skinned
  // copies beside the skin, a pistol in a socket or in a hand -- and a body
  // measurement that walks the whole tree measures the kit as body. The hull
  // the belt was being fitted to WAS the belt (a rigid mesh has no skin
  // weights, so the torso test let every vertex of it through), which is why
  // the conform landed every direction within a centimetre of where it
  // started and looked like it was working.
  _bodyMeshes() {
    const rig = this.rig;
    if (!rig) return [];
    const skip = new Set();
    for (const w of this.worn.values()) {
      if (w.obj) skip.add(w.obj);
      for (const m of w.skinned ?? []) skip.add(m);
    }
    if (this.held?.obj) skip.add(this.held.obj);
    for (const g of this._holstered ?? []) if (g?.obj) skip.add(g.obj);
    for (const g of this._onBack ?? []) if (g?.obj) skip.add(g.obj);
    const out = [];
    const walk = (o) => {
      if (skip.has(o)) return;
      if (o.isMesh && o.geometry?.attributes?.position) out.push(o);
      for (const c of o.children) walk(c);
    };
    walk(rig.obj);
    return out;
  }

  // The gear's own meshes: the walk stops at a socket, because what hangs in
  // one is a pistol, and a pistol is neither strap to be fitted to the body
  // nor skin to be bent with it. Re-seating the belt with a weapon holstered
  // used to push the pistol out radially and freeze a skinned copy of it on
  // the hip.
  _gearMeshes(w) {
    const skip = new Set(w.sockets ?? []);
    const out = [];
    const walk = (o) => {
      if (skip.has(o)) return;
      if (o.isMesh && o.geometry?.attributes?.position) out.push(o);
      for (const c of o.children) walk(c);
    };
    if (w.obj) walk(w.obj);
    return out;
  }

  // The torso as a RAYCASTABLE SHELL, in world space, around a given height.
  //
  // The body is a skinned mesh: its vertex buffer is the bind pose and the
  // thing standing in the world is somewhere else entirely, so the triangles
  // are baked out through the skinning and given their own BVH. Only the slab
  // the belt sits in is kept -- a belt never asks about shoulders -- which is
  // what keeps this to a few hundred triangles and one build per body.
  _torsoHull(y, mode = 'torso') {
    const rig = this.rig;
    if (!rig) return null;
    const cache = mode === 'lower' ? (this._hullLower ??= {}) : (this._hullTorso ??= {});
    if (cache.mesh && cache.rig === rig && Math.abs(cache.y - y) < 0.02) return cache.mesh;
    rig.obj.updateMatrixWorld(true);
    const tri = [];
    const a = new THREE.Vector3();
    const b = new THREE.Vector3();
    const c = new THREE.Vector3();
    const SLAB = 0.45;
    for (const o of this._bodyMeshes()) {
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      const idx = o.geometry.index;
      const torso = this._torsoTest(o, mode);
      const n = idx ? idx.count : pos.count;
      const vat = (i, out) => {
        const vi = idx ? idx.getX(i) : i;
        if (o.isSkinnedMesh && o.getVertexPosition) o.getVertexPosition(vi, out);
        else out.fromBufferAttribute(pos, vi);
        return { out: out.applyMatrix4(o.matrixWorld), vi };
      };
      for (let i = 0; i < n; i += 3) {
        const A = vat(i, a); const B = vat(i + 1, b); const C = vat(i + 2, c);
        // all three on the torso, and the triangle somewhere near the belt
        if (torso && !(torso(A.vi) && torso(B.vi) && torso(C.vi))) continue;
        if (Math.min(a.y, b.y, c.y) > y + SLAB || Math.max(a.y, b.y, c.y) < y - SLAB) continue;
        tri.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
      }
    }
    if (tri.length < 9 * 20) return null;      // too little body to measure
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(tri, 3));
    geo.computeBoundsTree?.();
    const mesh = new THREE.Mesh(geo, new THREE.MeshBasicMaterial());
    mesh.matrixAutoUpdate = false;             // the world transform is baked in
    mesh.updateMatrixWorld(true);
    cache.mesh = mesh;
    cache.rig = rig;
    cache.y = y;
    return mesh;
  }

  // WHICH VERTICES ARE THE TORSO.
  //
  // A belt has to clear the body, not the arms hanging beside it and not the
  // legs below it -- and at hip height both are in the way. Measuring the
  // silhouette instead of the torso made carl, whose hands hang at belt
  // height, read as a body a metre across.
  //
  // So the sweep keeps only vertices whose heaviest bone is the pelvis or the
  // spine. Returns a test, or null when the body is not skinned (a prop, a
  // one-piece mesh) and everything has to count.
  // `mode` 'torso' is the waist alone; 'lower' adds the legs -- what a holster
  // hanging down the thigh has to clear -- and still leaves the arms out,
  // because a hand hanging at hip height is not a hip.
  _torsoTest(mesh, mode = 'torso') {
    if (!mesh.isSkinnedMesh || !mesh.skeleton) return null;
    const si = mesh.geometry.attributes.skinIndex;
    const sw = mesh.geometry.attributes.skinWeight;
    if (!si || !sw) return null;
    const keep = new Set();
    const legs = /thigh|upleg|upperleg|leg|knee|shin|calf|hip|butt|glute|pelvis/i;
    mesh.skeleton.bones.forEach((b, i) => {
      const n = b.name || '';
      if (/pelvis|hips?$|spine|torso|abdomen|waist|belly|body|root/i.test(n)) keep.add(i);
      else if (mode === 'lower' && legs.test(n) && !/arm|hand|finger|thumb|clav|shoulder/i.test(n)) keep.add(i);
    });
    if (!keep.size) return null;
    return (v) => {
      let best = -1;
      let bw = 0;
      for (let c = 0; c < 4; c++) {
        const w = sw.getComponent(v, c);
        if (w > bw) { bw = w; best = si.getComponent(v, c); }
      }
      return best < 0 || keep.has(best);
    };
  }

  // The body's cross-section at a given height: centre and widest radius.
  _bodySection(y) {
    const rig = this.rig;
    if (!rig) return null;
    rig.obj.updateMatrixWorld(true);
    const pts = [];
    const v = new THREE.Vector3();
    for (const o of this._bodyMeshes()) {
      o.updateWorldMatrix(true, false);
      const pos = o.geometry.attributes.position;
      const torso = this._torsoTest(o);
      for (let i = 0; i < pos.count; i++) {
        if (torso && !torso(i)) continue;
        if (o.isSkinnedMesh && o.getVertexPosition) o.getVertexPosition(i, v);
        else v.fromBufferAttribute(pos, i);
        v.applyMatrix4(o.matrixWorld);
        if (Math.abs(v.y - y) > 0.05) continue;
        pts.push(v.clone());
      }
    }
    if (pts.length < 8) return null;
    const lo = new THREE.Vector3(Infinity, 0, Infinity);
    const hi = new THREE.Vector3(-Infinity, 0, -Infinity);
    for (const p of pts) {
      lo.x = Math.min(lo.x, p.x); hi.x = Math.max(hi.x, p.x);
      lo.z = Math.min(lo.z, p.z); hi.z = Math.max(hi.z, p.z);
    }
    const c = new THREE.Vector3((lo.x + hi.x) / 2, y, (lo.z + hi.z) / 2);
    let r = 0;
    for (const p of pts) r = Math.max(r, Math.hypot(p.x - c.x, p.z - c.z));
    return { c, r };
  }

  // How wide is this body at the waist, in metres?
  //
  // Measured off the MESH, not off the bones. The femoral spacing is the
  // anatomically correct thing to scale a belt by and it is wrong here: these
  // are stylised bodies, and the astronaut is a bulky suit whose thighs sit
  // close together inside it -- scaled to its bones the belt vanished inside
  // the costume. What the belt has to clear is the body, so measure the body.
  //
  // A skinned mesh's vertex buffer holds the BIND pose, which is exactly what
  // is wanted: a standing body, measured once, before any animation moves it.
  _waistWidth(hips = null) {
    if (this._waistW) return this._waistW;
    const rig = this.rig;
    rig.obj.updateMatrixWorld(true);
    const meshes = this._bodyMeshes();
    // The waist height is kept ABOVE THE RIG'S ORIGIN, not as a world height:
    // the hero walks, and a belt seated later has to land on the body where
    // it is now, not where it was measured.
    const rigY = new THREE.Vector3().setFromMatrixPosition(rig.obj.matrixWorld).y;

    // THE WAIST, which is not the same as "everything at waist height".
    //
    // Two wrong answers were tried first. A horizontal slab through the body
    // measures the ARMS as well -- they hang at exactly that height -- and the
    // astronaut came out 1.98 m across and wore a hula hoop. Taking only the
    // vertices the PELVIS bone owns goes the other way: on a bulky suit almost
    // all of it is weighted to the spine, 21 vertices qualified, and the belt
    // ended up inside the costume.
    //
    // So: the slab, minus the arms. Which vertices are arm is a question the
    // skin weights answer exactly, whatever the body is shaped like.
    const ARM = /arm|hand|shoulder|clavicle|finger|thumb|index|middle|pinky|ring|wrist/i;
    const v = new THREE.Vector3();
    const box = new THREE.Box3();
    for (const m of meshes) {
      m.geometry.computeBoundingBox();
      box.union(m.geometry.boundingBox.clone().applyMatrix4(m.matrixWorld));
    }
    const xs = [];
    const zs = [];
    let n = 0;
    if (!box.isEmpty()) {
      const y0 = box.min.y + (box.max.y - box.min.y) * 0.46;
      const y1 = box.min.y + (box.max.y - box.min.y) * 0.56;
      for (const m of meshes) {
        const p = m.geometry.attributes.position;
        const si = m.geometry.attributes.skinIndex;
        const sw = m.geometry.attributes.skinWeight;
        const bones = m.skeleton?.bones ?? null;
        const isArm = bones ? bones.map((b) => ARM.test(b.name || '')) : null;
        for (let k = 0; k < p.count; k++) {
          v.fromBufferAttribute(p, k);
          if (m.applyBoneTransform && m.isSkinnedMesh) m.applyBoneTransform(k, v);
          v.applyMatrix4(m.matrixWorld);
          if (v.y < y0 || v.y > y1) continue;
          if (isArm && si && sw) {
            // drop it if the bone with the most say over it is an arm
            let best = -1;
            let bw = 0;
            for (let c = 0; c < 4; c++) {
              const w = sw.getComponent(k, c);
              if (w > bw) { bw = w; best = si.getComponent(k, c); }
            }
            if (best >= 0 && isArm[best]) continue;
          }
          xs.push(v.x);
          zs.push(v.z);
          n++;
        }
      }
    }
    let width = 0;
    if (n > 20) {
      // A PERCENTILE, not the extremes. One stray vertex -- a cape, a hanging
      // sash, a bit of cloth -- sets the min/max and the belt is sized to that
      // instead of to the body. The 94th percentile ignores the outliers and
      // still contains the torso.
      const spread = (a) => {
        a.sort((p, q) => p - q);
        const lo = a[Math.floor(a.length * 0.03)];
        const hi = a[Math.min(a.length - 1, Math.floor(a.length * 0.97))];
        return hi - lo;
      };
      // the belt is a near-circle, so it has to clear the WIDER of the two
      width = Math.max(spread(xs), spread(zs)) * 1.03;
      this._waistY = box.min.y + (box.max.y - box.min.y) * 0.51 - rigY;
    } else if (!box.isEmpty()) {
      // nothing usable (a rigid prop body): a waist is about a fifth of a
      // standing figure, and these are all normalised to 1.8 m
      width = (box.max.y - box.min.y) * 0.19;
      this._waistY = box.min.y + (box.max.y - box.min.y) * 0.51 - rigY;
    }
    // Clamped to what a belt can plausibly be. The bulkiest costumes measure
    // wider than this at the waist, and a 0.7 m ring reads as a hula hoop --
    // better to have it sit a little into a padded suit than around it.
    this._waistW = THREE.MathUtils.clamp(width || 0.33, 0.26, 0.55);
    this._waistDebug = { width: +width.toFixed(3), verts: n };
    return this._waistW;
  }

  // The holster on whatever is being worn, if anything has one.
  get holsterSocket() {
    for (const w of this.worn.values()) if (w.socket) return w;
    return null;
  }

  // Park the owned-but-not-drawn pistol on the belt. Does nothing when there is
  // no belt on, which is the whole point: no belt, nowhere to put it.
  _toHolster() {
    const g = this.game;
    const belt = this.holsterSocket;
    // A RIFLE DOES NOT GO IN A HOLSTER. It is slung across the back, belt or
    // no belt -- so it is dealt with first, and never counts for a pocket.
    for (const w of g.weapons?.carried ?? []) {
      if (w?.obj && isRifle(w) && w !== this.held && !this._onBack?.includes(w)) this._toBack(w);
    }
    if (!belt?.sockets?.length) return false;
    // Everything the hero OWNS and is not holding, in slot order: the first
    // goes on the strong-hand hip, the next on whatever the belt has left.
    // A belt with two holsters is two places for a weapon to be.
    const owned = (g.weapons?.carried ?? [])
      .filter((w) => w?.obj && w.kind !== 'gear' && !isRifle(w) && w !== this.held);
    if (!owned.length) return false;
    this._fromHolster();
    for (let i = 0; i < belt.sockets.length && i < owned.length; i++) {
      const gun = owned[i];
      const obj = gun.obj;
      // The hand bias belongs to the hand: off again now it is on the belt.
      biasWorn(obj);
      obj.traverse((o) => { if (o.isMesh) o.frustumCulled = false; });
      belt.sockets[i].add(obj);
      obj.position.set(0, 0, 0);
      obj.quaternion.identity();
      // The socket's own axes already ARE the holstered pose (import_belt.py
      // wrote them that way), so the weapon only has to come out its normal
      // SIZE. Divide by the socket's real world scale, not by the belt's
      // bookkeeping number -- that one never had the skeleton's own
      // normalisation in it, so the pistol was arriving 18% too big.
      belt.sockets[i].updateWorldMatrix(true, false);
      const sockScale = belt.sockets[i].getWorldScale(_gv1).x || 1;
      obj.scale.setScalar(((gun.scale ?? 1) * (belt.holsterScale ?? 1)) / sockScale);
      obj.visible = true;
      this._seatInPocket(belt, belt.sockets[i], gun);
      this._holstered.push(gun);
    }
    this._inHolster = this._holstered[0] ?? null;
    return this._holstered.length > 0;
  }

  // IN THE LEATHER, NOT BESIDE IT.
  //
  // The socket is modelled at the centre of the pocket's mouth, and the
  // pistol hangs off it by its ORIGIN -- which is the butt of the grip. The
  // slide and barrel sit a few centimetres forward of the grip, so hung by
  // the butt the barrel ran down the pocket's front edge and the muzzle
  // stopped short of its bottom: from the side it read as a gun hanging next
  // to its holster. So the pocket is measured off the strap's own vertices
  // (everything hanging below the band at this socket's bearing) and the gun
  // off its own, both in the belt's frame, and the SOCKET is moved until the
  // part of the gun that is below the mouth sits centred between the pocket's
  // walls with the muzzle a few millimetres off its bottom. Moving the socket
  // rather than the gun means the stow, the draw and the strap ride all land
  // on the corrected place too. Once per gun per socket.
  _seatInPocket(belt, sock, gun) {
    if (!sock || !gun?.obj || sock.userData.seatedFor === gun.id) return;
    const w = belt;
    const root = w.obj;
    if (!root) return;
    root.updateWorldMatrix(true, true);
    const inv = _gm1.copy(root.matrixWorld).invert();
    // the socket's bearing on the ring, and ITS OWN AXES -- the pocket may be
    // flared off the hip (_clearHangers), so "down the pocket", "between its
    // walls" and "front to back" are the socket's muzzle, side and slide-up
    // axes, not the belt's vertical and radial
    sock.updateWorldMatrix(true, false);
    const sp = _gv1.setFromMatrixPosition(sock.matrixWorld).applyMatrix4(inv);
    const az = Math.atan2(sp.z, sp.x);
    const sq = sock.quaternion;                       // belt-local: the socket is the belt's child
    const mAx = _gv2.set(1, 0, 0).applyQuaternion(sq).normalize();   // muzzle: down the pocket
    const uAx = _gv3.set(0, 1, 0).applyQuaternion(sq).normalize();   // slide top: forward
    const sAx = new THREE.Vector3().crossVectors(mAx, uAx).normalize(); // out of the hip
    const rel = new THREE.Vector3();
    const along = (p, a) => rel.copy(p).sub(sp).dot(a);
    // the pocket: strap vertices below the band within ~30 deg of the socket
    let pocket = sock.userData.pocket;
    if (!pocket) {
      const band = this._ringOf(w);
      if (!band) return;
      const bandY = _gv4.copy(band.c).applyMatrix4(inv).y;
      const v = new THREE.Vector3();
      const ss = []; const us = []; let mTop = Infinity; let mBot = -Infinity;
      for (const o of this._gearMeshes(w)) {
        o.updateWorldMatrix(true, false);
        const m = _gm2.multiplyMatrices(inv, o.matrixWorld);
        const pos = o.geometry.attributes.position;
        for (let i = 0; i < pos.count; i++) {
          v.fromBufferAttribute(pos, i).applyMatrix4(m);
          if (v.y > bandY - 0.03) continue;                // the band itself
          const d = Math.atan2(Math.sin(Math.atan2(v.z, v.x) - az), Math.cos(Math.atan2(v.z, v.x) - az));
          if (Math.abs(d) > 0.5) continue;
          ss.push(along(v, sAx)); us.push(along(v, uAx));
          const mm = along(v, mAx);
          if (mm < mTop) mTop = mm;                        // the mouth
          if (mm > mBot) mBot = mm;                        // the bottom
        }
      }
      if (ss.length < 40) return;                          // no pocket here
      ss.sort((a, b) => a - b); us.sort((a, b) => a - b);
      const q = (arr, f) => arr[Math.floor((arr.length - 1) * f)];
      pocket = {
        sLo: q(ss, 0.05), sHi: q(ss, 0.95), uLo: q(us, 0.05), uHi: q(us, 0.95), mTop, mBot,
      };
      sock.userData.pocket = pocket;
    }
    // the gun AS IT WOULD HANG in this socket -- it need not be in it yet (a
    // stow seats the socket before it starts, so the move lands on the
    // corrected place instead of hopping there at the end)
    const sockScale = sock.getWorldScale(_gv4).x || 1;
    const gs = ((gun.scale ?? 1) * (w.holsterScale ?? 1)) / sockScale;
    const gunM = _gm3.copy(sock.matrixWorld).multiply(_gm4.makeScale(gs, gs, gs));
    gun.obj.updateWorldMatrix(true, true);
    const gunInv = _gm4.copy(gun.obj.matrixWorld).invert();
    const gv = new THREE.Vector3();
    let n = 0; let sSum = 0; let sMin = Infinity; let mMax = -Infinity; let uLo = Infinity; let uHi = -Infinity;
    gun.obj.traverse((o) => {
      if (!o.isMesh || !o.geometry?.attributes?.position) return;
      // mesh -> gun root -> socket (as holstered) -> belt
      const m = _gm2.multiplyMatrices(gunInv, o.matrixWorld).premultiply(gunM).premultiply(inv);
      const pos = o.geometry.attributes.position;
      for (let i = 0; i < pos.count; i++) {
        gv.fromBufferAttribute(pos, i).applyMatrix4(m);
        const mm = along(gv, mAx);
        if (mm > mMax) mMax = mm;                            // the muzzle
        const u = along(gv, uAx);
        if (u < uLo) uLo = u;
        if (u > uHi) uHi = u;
        if (mm < pocket.mTop) continue;                      // above the mouth
        const ss = along(gv, sAx);
        sSum += ss; n++;
        if (ss < sMin) sMin = ss;
      }
    });
    if (!n) return;
    // the barrel between the walls -- or, on a hip wider than the belt, as far
    // out of the pocket as the hip needs (_clearHangers leaves the remainder
    // here): the gun stands proud of the leather rather than inside the thigh
    const extra = sock.userData.standExtra ?? 0;
    const ds = Math.max((pocket.sLo + pocket.sHi) / 2 - sSum / n, (pocket.sLo + extra) - sMin);
    // The WHOLE gun centred on the pocket, front to back: the slide's nose at
    // the pocket's front edge, the grip's heel at its back. Centring only the
    // part below the mouth (tried) drove the grip out behind the leather.
    const du = (pocket.uLo + pocket.uHi) / 2 - (uLo + uHi) / 2;
    const dm = (pocket.mBot - 0.004) - mMax;                   // muzzle just off the bottom
    // belt-local, which is exactly the frame the socket's position is in
    sock.position.addScaledVector(sAx, ds).addScaledVector(uAx, du).addScaledVector(mAx, dm);
    sock.updateWorldMatrix(false, true);
    const dt = du; const dr = ds; const dy = -dm;              // for the log below
    sock.userData.seatedFor = gun.id;
    // the strap ride was recorded with the socket where it was
    const r = w.ride?.find((x) => x.socket === sock);
    if (r && w.rideBody) {
      this._skinXform(w, r, _gm2);
      r.local.copy(_gm2).invert().multiply(sock.matrixWorld);
    }
    const mm = (x) => Math.round(x * 1000 * (root.getWorldScale(_gv4).x || 1));
    console.log(`holster: ${gun.id} seated ${mm(dt)} mm along, ${mm(dr)} mm out, ${mm(dy)} mm down into the pocket `
      + `(pocket s ${mm(pocket.sLo)}..${mm(pocket.sHi)} u ${mm(pocket.uLo)}..${mm(pocket.uHi)} m ${mm(pocket.mTop)}..${mm(pocket.mBot)}; `
      + `gun s ${mm(sSum / n)} u ${mm(uLo)}..${mm(uHi)} m ${mm(mMax)}, n ${n})`);
  }

  // PUSH IT OUT OF THE BODY.
  //
  // The socket sits on the strap, and the weapon hangs 13 cm BELOW the strap --
  // where the body can be wider than it is at the belt. On the banana it is:
  // the belt rides at a radius of 0.24 and the body at the butt of the grip is
  // 0.26, so the pistol was seated correctly and still buried in the fruit.
  // Nothing about the belt's own fit can fix that; the holster has to stand
  // off far enough for what hangs out of it to clear.
  _clearBody(obj, socket) {
    obj.updateWorldMatrix(true, false);
    const box = new THREE.Box3().setFromObject(obj);
    const c = box.getCenter(_gv1);
    const sect = this._bodySection(c.y);
    if (!sect) return;
    const axis = _gv2.set(this.pos.x, c.y, this.pos.z);
    const out = _gv3.set(c.x - axis.x, 0, c.z - axis.z);
    const r = out.length();
    if (r < 1e-4) return;
    const need = sect.r + Math.max(box.max.x - box.min.x, box.max.z - box.min.z) * 0.35;
    if (r >= need) return;                       // already clear
    out.multiplyScalar((need - r) / r);          // the shortfall, in world
    socket.updateWorldMatrix(true, false);
    const before = _gv4.setFromMatrixPosition(socket.matrixWorld);
    const after = socket.worldToLocal(before.add(out));
    obj.position.add(after);                     // in the socket's own frame
  }

  // Take one weapon off the belt, or all of them when none is named.
  // ---- the sling ------------------------------------------------------------
  // WHERE A RIFLE RIDES when it is put away: slung across the back, muzzle
  // down by the left hip, stock up at the right shoulder, the receiver flat
  // against the back. The place is measured once per body -- the back's
  // surface at chest height, in the rig's own frame, where the body faces -Z
  // and the back is therefore +Z -- and hung off the chest bone so it bends
  // and turns with the torso.
  _backSocket() {
    const rig = this.rig;
    if (!rig) return null;
    if (this._backSock?.rig === rig) return this._backSock.sock;
    const chest = rig._chestBone?.() ?? rig._torsoRef?.();
    if (!chest) return null;
    rig.obj.updateMatrixWorld(true);
    const inv = new THREE.Matrix4().copy(rig.obj.matrixWorld).invert();
    chest.updateWorldMatrix(true, false);
    const cp = new THREE.Vector3().setFromMatrixPosition(chest.matrixWorld).applyMatrix4(inv);
    // the widest point of the back over the stretch the rifle lies across:
    // a round body (the banana) curves away above and below it, so the
    // receiver is centred THERE and sunk a hair into it, and the ends of the
    // rifle come as close to the skin as a straight stick can
    let backZ = -Infinity, backY = cp.y;
    const v = new THREE.Vector3();
    for (const m of this._bodyMeshes()) {
      if (m.isSkinnedMesh) m.skeleton.update();
      const n = m.geometry.attributes.position.count;
      for (let i = 0; i < n; i += 2) {
        m.getVertexPosition(i, v).applyMatrix4(m.matrixWorld).applyMatrix4(inv);
        if (Math.abs(v.y - cp.y) < 0.2 && Math.abs(v.x) < 0.12 && v.z > backZ) { backZ = v.z; backY = v.y; }
      }
    }
    if (!Number.isFinite(backZ)) { backZ = 0.15; backY = cp.y; }
    // which way is the body's right, in the rig frame: where the right shoulder is
    let upR = null;
    rig.obj.traverse((o) => { if (!upR && o.isBone && /^(upperarm_r|rightarm|upperarmr|bras1\.D)$/i.test(o.name)) upR = o; });
    let rs = 1;
    if (upR) {
      upR.updateWorldMatrix(true, false);
      rs = new THREE.Vector3().setFromMatrixPosition(upR.matrixWorld).applyMatrix4(inv).x < 0 ? -1 : 1;
    }
    // the socket's axes ARE the slung pose: +X the muzzle (down and to the
    // left), +Z the rifle's right side (against the back), +Y what is left
    const X = new THREE.Vector3(-rs * 0.45, -0.89, 0).normalize();
    const Z = new THREE.Vector3(0, 0, -1);
    const Y = new THREE.Vector3().crossVectors(Z, X).normalize();
    Z.crossVectors(X, Y).normalize();
    const pos = new THREE.Vector3(rs * 0.05, backY + 0.02, backZ + 0.02);
    const m = new THREE.Matrix4().makeBasis(X, Y, Z).setPosition(pos);
    const sock = new THREE.Object3D();
    sock.name = 'back_sling';
    // THE CHEST'S WORLD MATRIX, READ AGAIN HERE. The scan above calls
    // skeleton.update() on every body mesh, which rewrites the bone matrices
    // -- so the one taken before it is stale by the time the socket's local
    // transform is worked out against it, and the sling ends up bound at an
    // offset. Standing still that is a few centimetres and invisible; the
    // moment a climb bends the torso it opens into a gap and the rifle hangs
    // off his back in mid-air.
    chest.updateWorldMatrix(true, false);
    const local = new THREE.Matrix4().copy(chest.matrixWorld).invert().multiply(rig.obj.matrixWorld).multiply(m);
    local.decompose(sock.position, sock.quaternion, sock.scale);
    chest.add(sock);
    this._backSock = { rig, sock };
    // (NO SKIN RIDE HERE. Binding the socket to the body's own skinned mesh
    // the way the belt's holsters are bound slid the rifle 22 cm DOWN his back
    // the moment the climb ended -- the chest held to within 4 cm while the
    // socket dropped away from it. The belt gets away with it because it binds
    // to a skinned copy it makes itself; the body's mesh is an attached bind,
    // whose bindMatrixInverse three.js only refreshes when the renderer walks
    // the scene, so read mid-frame the transform does not cancel and the
    // socket drifts with the hero. The sling is a rigid child of the chest and
    // measures a constant 5.7 cm off the skin through a whole climb.)
    return sock;
  }

  _toBack(gun) {
    const sock = this._backSocket();
    if (!sock || !gun?.obj) return false;
    const obj = gun.obj;
    biasWorn(obj);
    obj.traverse((o) => { if (o.isMesh) o.frustumCulled = false; });
    sock.add(obj);
    obj.position.set(0, 0, 0);
    obj.quaternion.identity();
    sock.updateWorldMatrix(true, false);
    const ws = sock.getWorldScale(_gv1).x || 1;
    obj.scale.setScalar((gun.scale ?? 1) / ws);
    obj.visible = true;
    delete obj.userData.baseScaleY;
    obj.userData.anim?.reset();
    this._onBack ??= [];
    if (!this._onBack.includes(gun)) this._onBack.push(gun);
    return true;
  }

  _fromBack(gun) {
    const i = this._onBack?.indexOf(gun) ?? -1;
    if (i < 0) return false;
    this._onBack.splice(i, 1);
    gun.obj?.parent?.remove(gun.obj);
    return true;
  }

  _fromHolster(which = null) {
    const keep = [];
    for (const gun of this._holstered) {
      if (which && gun !== which) { keep.push(gun); continue; }
      if (gun?.obj?.parent) gun.obj.parent.remove(gun.obj);
    }
    const taken = which ?? this._holstered[0] ?? null;
    this._holstered = keep;
    this._inHolster = keep[0] ?? null;
    return taken;
  }

  // ---- weapon in hand -----------------------------------------------------
  // Draw: hang the weapon's own Object3D off the hero's right hand bone, so it
  // follows the run cycle for free. Holster: take it off again and keep it --
  // the hero still OWNS it. With a belt on it goes back into the holster
  // instead of nowhere.
  drawWeapon(weapon) {
    if (!weapon?.obj || !this.rig) return false;
    this.holster();
    const bone = this._gunHand(weapon);
    this._heldSide = this._rifleSide();
    if (!bone) {
      console.warn(`no hand bone on ${this._modelId} — weapon not drawn`);
      return false;
    }
    // Out of the holster it is a MOVE, not a teleport -- the mirror of the
    // stow: up out of the leather along the holster's own axis, then carried
    // to the hand. Only from the belt, though: a weapon that was never on the
    // belt has nowhere to come from and simply appears, as before.
    if (this.active && !this._draw
      && ((this._holstered.includes(weapon) && this.holsterSocket && this.game.gunAnim?.ready)
        || this._onBack?.includes(weapon))) {
      if (this._startDraw(weapon, bone)) return true;
    }
    this._fromHolster(weapon);   // off the belt and into the hand
    this._fromBack(weapon);      // or off the back
    attachToHand(bone, weapon.obj, weapon, this.rig.obj);
    this._handBone = bone;
    weapon.obj.visible = true;
    this.held = weapon;
    this.game.pistol?.setVisible(true);   // reticle up whenever a weapon is out
    this._aimOnDraw();
    drawGun(this.game.sound?.volume ?? 0.7);   // leather, and the weight of it
    return true;
  }

  // WHICH SHOULDER the rifle is on: the settings' weapon side (right by
  // default; Counter-Strike's left hand). The pistols stay right-handed.
  _rifleSide() {
    return this.game.settings?.state?.side === 'left' ? 'L' : 'R';
  }

  // The hand a weapon hangs off: the right, or for a rifle on the left
  // shoulder, the left.
  _gunHand(weapon) {
    const root = this.rig.obj;
    return isRifle(weapon) && this._rifleSide() === 'L'
      ? (findHandBoneL(root) ?? findHandBone(root)) : findHandBone(root);
  }

  // A weapon comes out READY: down the sights, over the shoulder, by default
  // (the setting turns it off; the right button still flips it either way).
  _aimOnDraw() {
    const g = this.game;
    if (g.settings?.state?.aimOnDraw === false) return;
    // ...never in cover: a weapon change at a corner is not a lean-out
    if (this._cover) return;
    g.pistol?.setAiming(true);
  }

  // Put the weapon away. With a belt on and a weapon in the hand this is a
  // MOVE, not a teleport -- see _stow below -- and the hand bone is kept until
  // it finishes, because the arms are still doing something with it.
  holster() {
    if (this._draw) this._finishDraw();    // mid-draw: into the hand, then away again
    if (this.held) drawGun(this.game.sound?.volume ?? 0.7);  // and back in the leather
    if (this.held?.obj && this.active && !this._stow
      && (isRifle(this.held) || (this.holsterSocket && this.game.gunAnim?.ready))) {
      if (this._startStow()) return true;
    }
    this._handBone = null;
    this.game.pistol?.setVisible(false);
    if (this.game.camera.fov !== FOOT_FOV) {
      this.game.camera.fov = FOOT_FOV;      // drop the aim zoom with the weapon
      this.game.camera.updateProjectionMatrix();
    }
    const was = this.held;
    if (!was?.obj) {
      this.held = null;
      this._toHolster();                    // owned but never drawn — still goes on the belt
      return;
    }
    // drop the hand-over-gun depth bias, or a weapon lying on the floor
    // z-fights with whatever it is lying on
    was.obj.traverse((o) => {
      if (!o.isMesh || !o.material) return;
      for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
        m.polygonOffset = false;
        m.needsUpdate = true;
      }
    });
    was.obj.parent?.remove(was.obj);
    was.obj.scale.setScalar(1);
    this.held = null;
    // crouch gives the held gun a compensating Y stretch; it must not follow it
    // onto the belt
    delete was.obj.userData.baseScaleY;
    was.obj.userData.anim?.reset();       // slide forward, magazine in: at rest on the belt
    this._toHolster();
  }

  // ---- putting it away, the long way ---------------------------------------
  //
  // RED DEAD's holster, which is the one worth copying: the gun does not blink
  // from the hand onto the hip. The arm carries it DOWN and ACROSS on the
  // clip's own keyframes, the weapon follows the hand on a sagging arc rather
  // than a straight line -- it has weight, and weight does not travel in
  // straight lines -- it goes muzzle-first into the pocket, and then it
  // SETTLES: a last little rock in the leather as it comes to rest, which is
  // the detail that sells the whole thing.
  //
  // The arm pose is the same overlay the reload uses (src/gunanim.js), run off
  // the clip's own holster segment. The weapon is parked in the world for the
  // trip so neither the hand nor the belt owns it halfway.
  _startStow() {
    const g = this.game;
    const gun = this.held;
    const belt = this.holsterSocket;
    const rifle = isRifle(gun);
    const sock = rifle ? this._backSocket() : belt?.sockets?.[0];
    if (!sock) return false;
    if (!rifle) this._seatInPocket(belt, sock, gun);
    gun.obj.updateWorldMatrix(true, false);
    const from = new THREE.Matrix4().copy(gun.obj.matrixWorld);
    // the hand's place on the grip, so the arm can follow the gun down (_moveArm)
    const handFromGun = new THREE.Matrix4()
      .compose(gun.obj.position, gun.obj.quaternion, gun.obj.scale).invert();
    g.scene.add(gun.obj);
    from.decompose(gun.obj.position, gun.obj.quaternion, gun.obj.scale);
    // the hand bias belongs to the hand, and it has left the hand
    gun.obj.traverse((o) => {
      if (!o.isMesh || !o.material) return;
      for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
        m.polygonOffset = false;
        m.needsUpdate = true;
      }
    });
    delete gun.obj.userData.baseScaleY;
    this.held = null;
    g.pistol?.setVisible(false);
    if (g.camera.fov !== FOOT_FOV) {
      g.camera.fov = FOOT_FOV;
      g.camera.updateProjectionMatrix();
    }
    const dur = rifle ? SLING_STOW : (g.gunAnim.clipTime('holster') || 0.46);
    sock.updateWorldMatrix(true, false);
    this._stow = {
      gun, t: 0, dur: Math.max(0.34, dur), rifle, sock,
      fromP: gun.obj.position.clone(), fromQ: gun.obj.quaternion.clone(),
      fromS: gun.obj.scale.x || 1, settle: 0, handFromGun,
      // its size where it is going, as a WORLD scale -- the move runs in
      // world space and _toBack/_toHolster re-parent at the end. (The sling's
      // was the socket-local figure, which under a bone carrying the body's
      // normalisation is tens of times bigger: the rifle grew to three metres
      // on its way over the shoulder, and that is what was "inside the body".)
      toS: rifle ? (gun.scale ?? 1) : (gun.scale ?? 1) * (belt.holsterScale ?? 1),
    };
    if (!rifle) g.gunAnim.play('holster', { force: true, fade: 0.05 });
    return true;
  }

  // ---- and out again ----------------------------------------------------------
  //
  // The draw is the stow run backwards, and it is NOT symmetrical in feel: the
  // first part is slow -- the gun is pulled against the leather, straight up
  // the holster's axis -- and then it comes up fast into the hand, with a
  // little rise over the line rather than a sag under it, because now the arm
  // is lifting the weight instead of lowering it. Where "the hand" is, is where
  // the CARRY pose will put it once the gun is in it: _moveArm solves that pose
  // first each frame and this reads the target off it, so the gun converges on
  // the hand's real destination while the arm itself is reaching for the gun.
  _startDraw(weapon, bone) {
    const g = this.game;
    const belt = this.holsterSocket;
    const rifle = isRifle(weapon);
    const sock = rifle ? this._backSocket() : belt?.sockets?.[0];
    if (!sock) return false;
    weapon.obj.updateWorldMatrix(true, false);
    const from = new THREE.Matrix4().copy(weapon.obj.matrixWorld);
    this._fromHolster(weapon);
    this._fromBack(weapon);
    // Where it will sit in the hand -- in the CARRY pose, seated to the live
    // aim, exactly as the held path seats it on the frame after this move
    // ends. Seating it on the idle arm (attachToHand alone) captured a pose
    // 10 cm off the real one, and the gun hopped as it landed.
    const raw = g.pistol?.aimDir?.();
    const aim = this._roll || this._slide || this._dive ? this._moveAim(raw) : raw;
    const chain = this.rig._armChain !== undefined ? this.rig._armChain
      : (this.rig._armChain = findArmChain(this.rig.obj));
    if (chain) { _mqa.copy(chain.upper.quaternion); _mqb.copy(chain.fore.quaternion); }
    const aiming = !!g.pistol?.aiming || (rifle && g.settings?.state?.aimOnDraw !== false);
    if (rifle) poses.applyRifleHold(this.rig, this.rig.obj, bone, aim, weapon, aiming ? 'aim' : 'carry', this._rifleSide());
    else poses.applyPistolPose(this.rig, this.rig.obj, bone, aim);
    attachToHand(bone, weapon.obj, weapon, this.rig.obj);
    poses.seatWeapon(bone, weapon.obj, weapon, this.rig.obj, aim, aiming ? 0 : rifle ? RIFLE_DIP : MUZZLE_DIP);
    const localP = weapon.obj.position.clone();
    const localQ = weapon.obj.quaternion.clone();
    const localS = weapon.obj.scale.clone();
    // ...and the arm back as it was: the pose was borrowed for the measurement,
    // and a body whose arms nothing else writes would otherwise start the draw
    // already holding the carry
    if (chain) {
      chain.upper.quaternion.copy(_mqa);
      chain.fore.quaternion.copy(_mqb);
      chain.upper.updateWorldMatrix(false, true);
    }
    g.scene.add(weapon.obj);
    from.decompose(weapon.obj.position, weapon.obj.quaternion, weapon.obj.scale);
    // its place IN the socket: it stays seated to the live belt through the
    // reach, because the body turns toward the hip while the hand comes down
    // and the holster turns with it
    sock.updateWorldMatrix(true, false);
    const inSock = new THREE.Matrix4().copy(sock.matrixWorld).invert().multiply(from);
    this._draw = {
      gun: weapon, bone, sock, t: 0, dur: rifle ? SLING_DUR : DRAW_DUR, inSock, rifle,
      fromP: weapon.obj.position.clone(), fromQ: weapon.obj.quaternion.clone(),
      fromS: weapon.obj.scale.x || 1, localP, localQ, localS,
      mouthP: new THREE.Vector3(), mouthQ: new THREE.Quaternion(), mouthS: 1,
      handFromGun: new THREE.Matrix4().compose(localP, localQ, localS).invert(),
      leather: false,
    };
    this._handBone = bone;
    this._heldSide = this._rifleSide();
    this._presentT = -1;
    // A STOW'S SETTLE MUST NOT OUTLIVE IT. The rock in the leather writes the
    // weapon's LOCAL rotation for a third of a second after the stow; a draw
    // inside that window re-parented the gun to the world and the settle
    // went on writing "identity plus a rock" onto a world rotation -- the
    // rifle stuck straight out of the back like a spear through the chest.
    this._settle = null;
    if (!rifle) g.gunAnim.play('draw', { force: true, fade: 0.05 });
    return true;
  }

  _updateDraw(dt) {
    const d = this._draw;
    d.t += dt;
    const t = d.t;
    const gun = d.gun.obj;
    // where it will sit in the hand, read off the CARRY pose the arm is
    // holding this frame (solved in _moveArm just before this)
    d.bone.updateWorldMatrix(true, false);
    const toP = _gv1, toQ = _dq1, toS = _gv2;
    _gm1.compose(d.localP, d.localQ, d.localS).premultiply(d.bone.matrixWorld)
      .decompose(toP, toQ, toS);
    const ph = d.rifle
      ? { reach: SLING_REACH, grip: SLING_GRIP, lift: SLING_LIFT, present: SLING_PRESENT }
      : { reach: DRAW_REACH, grip: DRAW_GRIP, lift: DRAW_LIFT, present: DRAW_PRESENT };
    const tLift = ph.reach + ph.grip;
    const tPres = tLift + ph.lift;
    if (t < tPres) {
      // still in or over the leather: seated to the LIVE socket
      d.sock.updateWorldMatrix(true, false);
      _gm2.copy(d.sock.matrixWorld).multiply(d.inSock).decompose(d.fromP, d.fromQ, _gv3);
      d.fromS = _gv3.x || 1;
    }
    if (t < tLift) {
      // the reach and the grip: the hand comes to the gun, not the other way
      gun.position.copy(d.fromP);
      gun.quaternion.copy(d.fromQ);
      gun.scale.setScalar(d.fromS);
    } else if (t < tPres) {
      // clearing leather: the sound is HERE, where the weapon moves, and the
      // lift is up the holster's own axis -- against the muzzle, which is the
      // socket's +X (import_belt.py holster_frame). Slow against the leather,
      // then free: it arrives at the mouth already moving, and the present
      // picks it up at that speed.
      if (!d.leather) { d.leather = true; drawGun(this.game.sound?.volume ?? 0.7); }
      const c = (t - tLift) / ph.lift;
      const e = c * c * (2 - c);
      // out of the leather: up the holster's axis; off the back: out from the
      // back (the rifle's own right side faces the body there) and then up
      // over the shoulder, the out part first
      const up = d.rifle
        ? _gv3.set(0, 0, -1).applyQuaternion(d.fromQ).multiplyScalar(SLING_OUT * Math.min(1, e * 1.6))
          .add(_gv4.set(0, SLING_UP * e, 0))
        : _gv3.set(-1, 0, 0).applyQuaternion(d.fromQ).multiplyScalar(0.135 * e);
      gun.position.copy(d.fromP).add(up);
      if (d.rifle) {
        // off the back the rifle comes up MUZZLE FIRST -- vertical beside the
        // shoulder, stock hanging down behind -- so that nothing of it sweeps
        // through the body on the way over; it levels out in front, later
        muzzleUp(d.fromQ, _dq3);
        gun.quaternion.copy(d.fromQ).slerp(_dq3, e);
      } else {
        // the muzzle begins to come round in the last of the lift, with the wrist
        gun.quaternion.copy(d.fromQ).slerp(toQ, 0.12 * c * c);
      }
      gun.scale.setScalar(d.fromS);
      d.mouthP.copy(gun.position);
      d.mouthQ.copy(gun.quaternion);
      d.mouthS = d.fromS;
    } else {
      // THE PRESENT. Fast off the leather and slowing into the hand's place: an
      // ease-out, since the lift handed it over at speed. The hand swings on
      // the elbow, so the path bows UP and OUT of the straight line; the
      // muzzle rolls from down to forward and goes a tenth past the line
      // before it comes back (a back-ease), which is the weight of the thing.
      const c = Math.min(1, (t - tPres) / ph.present);
      const ep = 1 - Math.pow(1 - c, 2.4);
      const arc = Math.sin(Math.PI * c);
      const way = d.rifle ? this._slingWaypoint(this._rifleSide(), _gv4) : null;
      if (way) {
        // from behind, up beside the shoulder and over it, down to the hold's
        // place -- still on end -- and only then levelled out in front (the
        // rotation below runs late for a rifle), where a swinging barrel has
        // nothing to sweep through
        bendThrough(this._stowV, d.mouthP, way, toP, 1 - Math.pow(1 - Math.min(1, c / 0.7), 2.4));
      } else {
        this._stowV.copy(d.mouthP).lerp(toP, ep);
        this._stowV.y += arc * 0.06;        // lifted, so it bows a little over the line
        _gv3.copy(d.mouthP).sub(this.pos).setY(0);
        if (_gv3.lengthSq() > 1e-6) this._stowV.addScaledVector(_gv3.normalize(), arc * 0.04);
      }
      gun.position.copy(this._stowV);
      // a rifle keeps its muzzle up for the first third of the swing, until
      // the grip is clear of the shoulder in front, then comes down level
      const cr = d.rifle ? THREE.MathUtils.clamp((c - 0.5) / 0.5, 0, 1) : c;
      const bk = 1.70158, u = cr - 1;
      const er = 1 + (bk + 1) * u * u * u + bk * u * u;
      slerpPast(d.mouthQ, toQ, er, gun.quaternion);
      gun.scale.setScalar(THREE.MathUtils.lerp(d.mouthS, toS.x, ep));
      if (c >= 1) this._finishDraw(true);
    }
  }

  // Into the hand, now. Called at the end of the move and by anything that
  // needs the weapon definitely held this instant -- a holster mid-draw.
  _finishDraw(settle = false) {
    const d = this._draw;
    if (!d) return;
    this._draw = null;
    if (settle) this._presentT = 0;    // the muzzle rocks in the hand for a beat
    d.bone.add(d.gun.obj);
    d.gun.obj.position.copy(d.localP);
    d.gun.obj.quaternion.copy(d.localQ);
    d.gun.obj.scale.copy(d.localS);
    d.gun.obj.visible = true;
    this.held = d.gun;
    this._handBone = d.bone;
    this.game.pistol?.setVisible(true);   // reticle up whenever a weapon is out
    this._aimOnDraw();
  }

  _updateStow(dt) {
    const s = this._stow;
    const sock = s.sock ?? this.holsterSocket?.sockets?.[0];
    if (!sock) { this._stow = null; this._toHolster(); return; }
    s.t += dt;
    const k = Math.min(1, s.t / s.dur);
    // where it has to end up: the seated pose, in world
    sock.updateWorldMatrix(true, false);
    const toP = new THREE.Vector3();
    const toQ = new THREE.Quaternion();
    const toS = new THREE.Vector3();
    sock.matrixWorld.decompose(toP, toQ, toS);
    const want = s.toS ?? (s.gun.scale ?? 1);
    // RED DEAD's holster is two moves, not one. The hand CARRIES the gun down
    // to the hip -- and a thing with weight does not travel in a straight line,
    // it sags below the line between the two points and comes back up into it.
    // Then, lined up muzzle-down over the mouth, it goes STRAIGHT DOWN into
    // the leather. That second part is the whole reason it reads as holstering
    // rather than as an object sliding to a new position: a pistol enters a
    // holster along the holster's own axis or it does not enter it at all.
    const CARRY = 0.68;                       // of the move spent getting there
    // "up out of the holster" is against the MUZZLE, and the socket's +X is
    // the muzzle (import_belt.py holster_frame) -- not its +Y, which is the
    // slide top and points forward
    // ...and for a rifle "the mouth" is up over the shoulder, off the back
    const up = s.rifle
      ? _gv1.set(0, 0, -1).applyQuaternion(toQ).multiplyScalar(SLING_OUT).add(_gv3.set(0, SLING_UP, 0))
      : _gv1.set(-1, 0, 0).applyQuaternion(toQ).multiplyScalar(0.135);
    const mouth = _gv2.copy(toP).add(up);     // lined up over the opening
    if (k < CARRY) {
      const c = k / CARRY;
      const e = c * c * (3 - 2 * c);
      const way = s.rifle ? this._slingWaypoint(this._rifleSide(), _gv4) : null;
      const stand = way ? this._slingStand(this._rifleSide(), _gv5) : null;
      if (way && stand) {
        // FIRST the rifle is brought in front of the shoulder and stood on end
        // there -- where a vertical rifle is outside any body, whatever the
        // aim had the hold doing -- and only THEN carried up beside the
        // shoulder, over it and down behind. Turned while it travelled, the
        // barrel swept through the body above the shoulder (the banana is 40
        // cm wide up there); run straight from the hand to the back, it cut
        // the corner through the shoulder.
        const STAND = 0.35;
        muzzleUp(toQ, _dq3);
        if (c < STAND) {
          const u = c / STAND, su = u * u * (3 - 2 * u);
          this._stowV.copy(s.fromP).lerp(stand, su);
          s.gun.obj.quaternion.copy(s.fromQ).slerp(_dq3, su);
        } else {
          const u = (c - STAND) / (1 - STAND), su = u * u * (3 - 2 * u);
          bendThrough(this._stowV, stand, way, mouth, su);
          s.gun.obj.quaternion.copy(_dq3);
        }
      } else {
        this._stowV.copy(s.fromP).lerp(mouth, e);
        this._stowV.y -= Math.sin(Math.PI * c) * 0.085;   // a pistol carried down sags under the line
        s.gun.obj.quaternion.copy(s.fromQ).slerp(toQ, e);  // square with the holster by the time it arrives
      }
      s.gun.obj.scale.setScalar(THREE.MathUtils.lerp(s.fromS, want, e));
    } else {
      // the insertion: down the holster's own axis, slowing as it beds in
      const c = (k - CARRY) / (1 - CARRY);
      const ei = 1 - (1 - c) * (1 - c);
      this._stowV.copy(mouth).lerp(toP, ei);
      if (s.rifle) { muzzleUp(toQ, _dq3); s.gun.obj.quaternion.copy(_dq3).slerp(toQ, ei); }
      else s.gun.obj.quaternion.copy(toQ);
      s.gun.obj.scale.setScalar(want);
    }
    s.gun.obj.position.copy(this._stowV);
    if (k < 1) return;
    // in the leather, and it rocks once as it comes to rest
    this._stow = null;
    this._toHolster();
    this._settle = { t: 0, q: s.gun.obj.quaternion.clone(), gun: s.gun };
  }

  // The last rock in the holster: a damped swing about the belt's own forward,
  // over in a third of a second. Nothing heavy stops dead.
  _updateSettle(dt) {
    const st = this._settle;
    st.t += dt;
    const k = st.t / 0.34;
    if (k >= 1 || !st.gun.obj.parent) {
      if (st.gun.obj.parent) st.gun.obj.quaternion.copy(st.q);
      this._settle = null;
      return;
    }
    const a = Math.sin(k * Math.PI * 3) * (1 - k) * 0.17;
    st.gun.obj.quaternion.copy(st.q);
    st.gun.obj.rotateZ(a);
  }

  // ---- the arm, while the gun is on its way --------------------------------
  //
  // The hand goes WITH the gun. The clip's draw and holster are first-person
  // segments -- the arm leaves the bottom of the frame -- which on a body you
  // can see is an arm held out at chest height while the pistol slides to the
  // hip by itself (it did). So for these two moves the arm is not played, it
  // is SOLVED: the weapon runs its authored path (_updateStow, _updateDraw) and
  // the gun arm reaches for wherever the grip is this frame, two bones, elbow
  // back and out (applyReachPose). Hand on the gun from the first frame to the
  // last.
  //
  // Runs AFTER the body's animation has written the arm for this frame (the
  // mixer, the swing) and in place of the carry pose: reading the hand bone
  // before that was the other half of the old bug -- the draw was converging
  // on the hand of the idle pose, which was never where the hand was drawn.
  _moveArm(dt, rig) {
    const g = this.game;
    const hand = this._handBone;
    const chain = rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(rig.obj));
    const raw = g.pistol?.aimDir?.();
    const aim = this._roll || this._slide || this._dive ? this._moveAim(raw) : raw;
    // the arm as the body's own animation left it: what a stow ends on and what
    // a draw starts from
    if (chain) { _mqa.copy(chain.upper.quaternion); _mqb.copy(chain.fore.quaternion); }
    if (hand) _mqe.copy(hand.quaternion);    // the wrist as the clip has it
    // ...and the carry, which a draw lands on and a stow leaves from. Solved
    // for real every frame because the draw reads its destination off it.
    // For a rifle that is the rifle's own hold.
    const mv = this._draw ?? this._stow;
    const side = this._rifleSide();
    const aiming = !!g.pistol?.aiming || (mv?.rifle && this._draw && g.settings?.state?.aimOnDraw !== false);
    const carry = chain && hand && (mv?.rifle
      ? poses.applyRifleHold(rig, rig.obj, hand, aim, mv.gun, aiming ? 'aim' : 'carry', side)
      : poses.applyPistolPose(rig, rig.obj, hand, aim));
    if (carry) { _mqc.copy(chain.upper.quaternion); _mqd.copy(chain.fore.quaternion); }
    if (this._draw) this._updateDraw(dt);
    if (this._stow) this._updateStow(dt);
    const rec = this._draw ?? this._stow;
    if (!rec) {
      // the move just finished: into the hand (held, posed below) or into the
      // leather, in which case the hand is done
      if (!this.held) this._handBone = null;
      return;
    }
    if (!chain || !hand) return;         // a body with no arm to solve: the gun still travels
    // the hand's place on the grip, from where the gun is now
    rec.gun.obj.updateWorldMatrix(true, false);
    _mm.copy(rec.gun.obj.matrixWorld).multiply(rec.handFromGun);
    const t = rec.t;
    // WHERE THE ELBOW GOES is the move. Down to the hip it is back and out;
    // clearing the leather it is high and back; coming round to the carry it
    // drops into the carry's own shape. A stow is the carry's shape throughout.
    let hint = rec.rifle ? HINT_RIFLE : HINT_CARRY;
    let present = 0;   // 0..1 through the present of a draw (the support hand comes in with it)
    if (this._draw && rec.rifle) {
      // over the shoulder: the elbow stays high and out until the rifle is
      // coming down in front, then drops into the hold's shape
      const tLift = SLING_REACH + SLING_GRIP, tPres = tLift + SLING_LIFT;
      if (t < tPres) hint = HINT_SLING;
      else {
        present = Math.min(1, (t - tPres) / SLING_PRESENT);
        hint = hintMix(HINT_SLING, HINT_RIFLE, present * present * (3 - 2 * present));
      }
    } else if (this._draw) {
      const tLift = DRAW_REACH + DRAW_GRIP, tPres = tLift + DRAW_LIFT;
      if (t < tLift) hint = HINT_REACH;
      else if (t < tPres) hint = hintMix(HINT_REACH, HINT_LIFT, Math.min(1, 2 * (t - tLift) / DRAW_LIFT));
      else {
        const c = Math.min(1, (t - tPres) / DRAW_PRESENT);
        hint = hintMix(HINT_LIFT, HINT_CARRY, c * c * (3 - 2 * c));
      }
    } else if (rec.rifle) {
      // up over the shoulder and down onto the back: the elbow goes up with it
      const c = Math.min(1, t / (rec.dur * 0.68));
      hint = hintMix(HINT_RIFLE, HINT_SLING, c * c * (3 - 2 * c));
    }
    if (!poses.applyReachPose(rig, rig.obj, hand, _gv1.setFromMatrixPosition(_mm), aim, hint)) return;
    // THE GUN IS RIGID IN THE HAND. The two-bone reach puts the hand ON the
    // grip; the wrist then takes the grip's own orientation, so when the
    // weapon rolls from muzzle-down to muzzle-forward it is the hand turning
    // it over -- not a pistol rotating inside a hand that is not moving,
    // which is exactly what "the gun is flying" looks like.
    // (Only a real hand bone: the astronaut's "hand" is its forearm, and
    // turning THAT to the grip would undo the reach that just aimed it.)
    const wrist = isHandBone(hand);
    if (wrist) {
      _mm.decompose(_gv3, _dq1, _gv4);
      hand.parent.updateWorldMatrix(true, false);
      hand.parent.getWorldQuaternion(_dq2);
      hand.quaternion.copy(_dq2.invert()).multiply(_dq1);
    }
    // The seams. A draw comes OFF the body's own arm over the reach -- that
    // blend IS the hand travelling down to the grip -- and lands on the carry
    // in the last of the present; a stow leaves the carry and goes out to the
    // hanging arm at the end. The wrist blends with the rest of the arm.
    const left = rec.dur - t;
    const sm = (x) => { x = THREE.MathUtils.clamp(x, 0, 1); return x * x * (3 - 2 * x); };
    const reachT = rec.rifle ? SLING_REACH : DRAW_REACH;
    const toIdle = this._draw ? 1 - sm(t / reachT) : 1 - sm(left / 0.12);
    const toCarry = this._draw ? 1 - sm(left / 0.08) : 1 - sm(t / 0.08);
    if (toCarry > 0 && carry) {
      chain.upper.quaternion.slerp(_mqc, toCarry);
      chain.fore.quaternion.slerp(_mqd, toCarry);
    }
    if (toIdle > 0) {
      chain.upper.quaternion.slerp(_mqa, toIdle);
      chain.fore.quaternion.slerp(_mqb, toIdle);
    }
    const back = Math.max(toIdle, toCarry);
    if (back > 0 && wrist) hand.quaternion.slerp(_mqe, back);
    if (back > 0) chain.upper.updateWorldMatrix(false, true);
    // THE SUPPORT HAND on a rifle: it comes onto the handguard as the rifle
    // comes down in front on a draw, and lets go as the rifle goes up over
    // the shoulder on a stow
    if (rec.rifle) {
      const w = this._draw ? present : 1 - Math.min(1, t / (rec.dur * 0.5));
      if (w > 0.001) poses.applyRifleSupport(rig, rig.obj, rec.gun.obj, rec.gun, w, aim, side);
    }
  }

  // THE WAY PAST THE SHOULDER for a slung rifle: a point beside and above the
  // gun-side shoulder, outside the body, that the rifle passes through on its
  // way between the hold in front and the back. A straight run from the hand
  // in front to the back cuts the corner through the shoulder -- the rifle
  // was buried to the grip halfway through every stow.
  _slingWaypoint(side, out) {
    const rig = this.rig;
    const pocket = rig?.rifleShoulder?.(side, out, null);   // off the legs' facing: the body's own frame
    if (!pocket) return null;
    const chest = rig._chestBone?.();
    if (chest) {
      chest.updateWorldMatrix(true, false);
      _gv3.setFromMatrixPosition(chest.matrixWorld);
      _gv3.subVectors(pocket, _gv3).setY(0);
      if (_gv3.lengthSq() > 1e-6) out.addScaledVector(_gv3.normalize(), SLING_WAY_OUT);
    }
    out.y += SLING_WAY_UP;
    return out;
  }

  // WHERE A RIFLE IS STOOD ON END before it goes over the shoulder: in front
  // of the gun-side pocket along the BODY's facing, a hand's width up. The
  // hold it comes from can be anywhere the aim was -- straight across the
  // chest after a slide or a flick the body has not caught up with -- and a
  // rifle stood up THERE and carried from there went through the chest.
  _slingStand(side, out) {
    const rig = this.rig;
    const pocket = rig?.rifleShoulder?.(side, out, null);
    if (!pocket) return null;
    const an = rig.anim ?? rig.obj;
    an.updateWorldMatrix(true, false);
    _gv6.setFromMatrixColumn(an.matrixWorld, 2).negate().setY(0);
    if (_gv6.lengthSq() < 1e-6) _gv6.set(0, 0, -1);
    out.addScaledVector(_gv6.normalize(), SLING_STAND_FWD);
    out.y += 0.05;
    return out;
  }

  // How far into the reach-to-the-hip the body is: a raised-cosine window
  // over the reach, the grip and the lift of a draw (peak on the grip), and
  // over the last part of a stow. Drives the shoulder going back and down in
  // swingBody.
  _gunReachEnv() {
    const d = this._draw, s = this._stow;
    let x = -1;
    if (d) x = d.t / (DRAW_REACH + DRAW_GRIP + DRAW_LIFT);
    else if (s) x = (s.t - s.dur * 0.45) / (s.dur * 0.55);
    if (x < 0 || x > 1) return 0;
    return 0.5 * (1 - Math.cos(2 * Math.PI * x));
  }

  // Everything that is the same however the hero got out here.

  // ---- COVER (V) --------------------------------------------------------------
  //
  // V at a wall: the hero turns, steps back onto it and flattens himself against
  // it, back to the brick -- GTA's cover. From there the wall is the world. The
  // keys shuffle him along it and it stops him at its corner with his shoulder
  // on the edge; aiming (RMB) at a corner leans him out round it, weapon up, and
  // brings him back when the aim is let go; the trigger behind the wall is an
  // arm round the edge firing blind; a wall lower than the chest is crouched
  // behind and shot over the top. Ducking (C) works in it, and a crouched hero
  // peeks round the corner crouched. Leaving is V, pushing away from the wall,
  // X (a roll out of it) or Space -- which at a low wall is the vault.
  //
  // The cover is re-read off the wall every frame (_coverFrame): a curved
  // facade is followed, a wall that is shot away lets you go, and the corner is
  // found by the same probe missing. Nothing here is a baked cover point.
  toggleCover() {
    if (!this.active) return false;
    if (this._cover) { this._leaveCover('key'); return true; }
    if (this._climb || this._roll || this._slide || this._dive || !this.grounded) return false;
    const c = this._findCover();
    if (!c) {
      this.game._setHint?.('აქ საფარი არ არის — მიდი კედელთან და დააჭირე V');
      return false;
    }
    this._enterCover(c);
    return true;
  }

  _footHint() {
    return 'W/S — სირბილი · A/D — მიბრუნება · Q/E — გვერდულა · C — ჩაცუცქება · V — საფარი · Shift — სპრინტი · X — გადაგორება · Z — სრიალი '
      + '· Ctrl — ნაბიჯი · Cmd — გასეირნება · Space — ხტომა / აძვრომა '
      + '· F — აღება · Enter — ჩაჯდომა';
  }

  _coverNear() {
    const g = this.game;
    const pl = this._planeTargets();
    const near = g.world.collidablesNear(this.pos.x, this.pos.z);
    return pl ? near.concat(pl) : near;
  }

  // One ray into the world: the hit's point, its normal turned to face the ray,
  // and the distance -- or null. Every cover question (is there a wall here,
  // where does it end, is anything in the way along it) goes through this one
  // probe, so they all agree on what a wall is.
  _wallHit(near, x, y, z, dir, far) {
    this._ray.set(this._v5.set(x, y, z), dir);
    this._ray.far = far;
    const got = this._ray.intersectObjects(near, true)[0];
    this._ray.far = Infinity;
    if (!got || !got.face) return null;
    const n = got.face.normal.clone().transformDirection(got.object.matrixWorld);
    if (n.dot(dir) > 0) n.negate();
    return { point: got.point.clone(), n, dist: got.distance };
  }

  // The wall to take: the facing first, then where the legs are going, then
  // round the clock -- GTA takes the nearest cover in the stick's general
  // direction. Chest height says whether there is a wall at all, a waist-high
  // probe catches what the chest ray sails over (a counter, a parapet), and a
  // head-high probe says whether it hides a standing body or only a crouched one.
  _findCover() {
    const near = this._coverNear();
    if (!near.length) return null;
    const yaws = [this.yaw];
    if (Math.abs(this.speed) > 0.3) {
      yaws.push(this._strafing ? (this._moveYaw ?? this.yaw) : this.yaw + (this.speed < 0 ? Math.PI : 0));
    }
    for (const d of [0.6, -0.6, 1.2, -1.2, 1.9, -1.9, 2.6, -2.6, Math.PI]) yaws.push(this.yaw + d);
    const dir = this._v4;
    const p = this.pos;
    let best = null;
    for (let i = 0; i < yaws.length; i++) {
      dir.set(-Math.sin(yaws[i]), 0, -Math.cos(yaws[i]));
      const wall = (h, far) => {
        const w = this._wallHit(near, p.x, p.y + h, p.z, dir, far);
        return w && Math.abs(w.n.y) < 0.45 ? w : null;   // a roof or a ramp is not a wall
      };
      const chest = wall(1.1, COVER_FIND);
      const hit = chest ?? wall(COVER_LOW, COVER_FIND);
      if (!hit) continue;
      const high = wall(COVER_HIGH, hit.dist + 0.6);
      const score = hit.dist + 0.3 * Math.min(i, 2);   // nearest wins; the facing gets a head start
      if (!best || score < best.score) {
        best = { point: hit.point, n: hit.n.setY(0).normalize(), low: !high, dist: hit.dist, score };
      }
    }
    return best;
  }

  // Half this body's depth, so the back stands off the wall by its own thickness
  // and not by a number that fits one of the 39 bodies.
  _coverOff() {
    if (this._halfDepth) return this._halfDepth + COVER_GAP;
    // in the RIG's frame, not the world's: a hero standing at an angle has his
    // arm span leaking into the world box's depth
    const rig = this.rig;
    rig.obj.updateWorldMatrix(true, false);
    const inv = rig.obj.matrixWorld.clone().invert();
    const box = new THREE.Box3();
    const m = new THREE.Matrix4();
    for (const o of this._bodyMeshes()) {
      if (!o.geometry) continue;
      if (!o.geometry.boundingBox) o.geometry.computeBoundingBox();
      o.updateWorldMatrix(true, false);
      box.union(o.geometry.boundingBox.clone().applyMatrix4(m.multiplyMatrices(inv, o.matrixWorld)));
    }
    // A skinned mesh's geometry box is the BIND pose in bind units -- the
    // height normalisation lives on the bones, not on the mesh's own matrix --
    // so the depth is read as a fraction of the box's own height.
    const sz = box.getSize(new THREE.Vector3());
    const depth = Number.isFinite(sz.z) && sz.y > 0.05 ? (sz.z / sz.y) * HERO_HEIGHT : 0.4;
    this._halfDepth = THREE.MathUtils.clamp(depth / 2, 0.1, 0.4);
    return this._halfDepth + COVER_GAP;
  }

  _enterCover(c) {
    const g = this.game;
    const n = c.n.clone();
    const t = new THREE.Vector3(-n.z, 0, n.x);   // along the wall: the hero's right, facing out
    const off = this._coverOff();
    // the back goes to the wall at its nearest point, stood off it by half a body
    const d = this._v3.subVectors(this.pos, c.point).dot(n);
    const base = this.pos.clone().addScaledVector(n, off - d);
    base.y = this.pos.y;
    if (this._slide) this._endSlide();
    // how tall a low wall is, for the arm that fires over it
    let top = COVER_HIGH;
    if (c.low) {
      const near = this._coverNear();
      const w = this._wallHit(near, base.x - n.x * 0.4, base.y + COVER_HIGH + 0.4, base.z - n.z * 0.4,
        this._v4.set(0, -1, 0), COVER_HIGH + 0.4);
      if (w) top = THREE.MathUtils.clamp(w.point.y - base.y, COVER_LOW, COVER_HIGH);
    }
    this._cover = {
      n, t, base, off, low: !!c.low, top,
      from: this.pos.clone(), fromYaw: this.yaw,
      phase: 'approach', time: 0, slam: 0, slamCam: false, aimYaw: this.yaw,
      blend: 0,
      peek: 0, pop: 0, out: 0, look: 0, blind: 0, blindWant: false, blindCd: 0, blindSide: 0,
      side: 0,                                  // the side the body leans out on
      corner: 0,                                // ±1: the shoulder is on the wall's end
      edge: { 1: Infinity, [-1]: Infinity },    // m along ±t to the wall's end
      block: { 1: false, [-1]: false },         // something in the way along ±t
      camSide: 1, camS: 1, lookYaw: 0, pushOut: 0, lost: 0, hintCorner: null,
      moveDir: 0, moveOut: false, heldWas: false,   // the latched key direction (see _coverFrame)
      turn: 0,                                  // rad the body stands turned off the wall's normal (eased)
      shotBuf: 0,                               // s left of a trigger pull kept until the gun is clear
      wasCrouched: this.crouched, forced: false,
    };
    const cv = this._cover;
    // close already: straight to the turn; otherwise the hero runs there first
    if (Math.hypot(base.x - this.pos.x, base.z - this.pos.z) <= COVER_ARRIVE) cv.phase = 'settle';
    else this.speed = Math.max(0, this.speed);
    if (cv.low && !this.crouched) { this.crouched = true; cv.forced = true; }
    this._strafing = false;
    this._stroll = false;
    this.aimOverride = null;
    g.pistol?.setAiming(false);
    g._setHint?.(cv.low ? HINT_COVER_LOW : HINT_COVER);
  }

  _leaveCover(how = 'key') {
    const c = this._cover;
    if (!c) return;
    this._cover = null;
    this.aimOverride = null;
    if (c.forced && how !== 'vault') this.crouched = c.wasCrouched;
    // leaving forward is a PUSH off the brick, not a step: the body spring
    // reads the jump in pace and leans into it
    if (how === 'out' && c.blend >= 1) { this.speed = Math.max(this.speed, COVER_PUSH); this._strafing = false; }
    this.camYaw = this.yaw;
    this.game.look.yaw = 0;
    if (how !== 'silent') this.game._setHint?.(this._footHint());
  }

  _wrapA(a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

  // shooting.js asks before every round: behind the wall the trigger does nothing
  coverCanFire() {
    const c = this._cover;
    return !c || c.peek > 0.5 || c.pop > 0.5 || c.out > 0.5 || c.blind > 0.7;
  }

  // the trigger pulled while still stepping out (game.js): kept, and fired the
  // moment the weapon is clear of the wall -- a click is never simply lost
  coverShot() {
    if (this._cover) this._cover.shotBuf = SHOT_BUFFER;
  }

  // the trigger held behind the wall (game.js): the blind arm is out while it is
  coverBlind(down) {
    if (this._cover) this._cover.blindWant = !!down;
  }

  // WHICH SIDE OF THE BODY THE WEAPON HAND IS ON, +1 right, -1 left. The same
  // rule _gunHand goes by: only a RIFLE changes hands with the setting -- a
  // pistol stays in the right one -- so this cannot be read off _rifleSide
  // alone or a left-handed player's pistol would be looked for on the wrong
  // side of him at every corner.
  _gunSide() { return isRifle(this.held) && this._rifleSide() === 'L' ? -1 : 1; }

  // ONE FRAME IN COVER. Re-reads the wall, finds its ends, reads the keys against
  // the camera, and writes the hero's speed, place and facing for the frame --
  // the rest of update() (ground, animation, crouch, arms, gun, camera) runs on
  // as it does everywhere else.
  _coverFrame(dt, m, armed) {
    const g = this.game;
    const c = this._cover;
    const near = this._coverNear();
    const n = c.n, t = c.t;
    const wrap = (a) => { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; };
    c.base.y = this.pos.y;                  // the ground pass moved us: keep up with it
    c.time += dt;
    // ---- THE WAY TO THE WALL IS RUN, not slid: GTA's man goes to his cover on
    // his feet, and only at the wall turns and puts his back to it
    if (c.phase === 'approach') {
      const dx = c.base.x - this.pos.x, dz = c.base.z - this.pos.z;
      const dist = Math.hypot(dx, dz);
      if (dist <= COVER_ARRIVE || c.time > 2.5) {
        c.phase = 'settle';
        c.from.copy(this.pos);
        c.fromYaw = this.yaw;
      } else {
        const want = THREE.MathUtils.clamp(dist * 3, 1.3, this.runSpeed * COVER_RUN);
        this.speed += THREE.MathUtils.clamp(want - this.speed, -ACCEL_GROUND * dt, ACCEL_GROUND * dt);
        const len = Math.min(dist, this.speed * dt);
        this.pos.x += (dx / dist) * len;
        this.pos.z += (dz / dist) * len;
        this.yaw += wrap(Math.atan2(-dx, -dz) - this.yaw) * Math.min(1, 12 * dt);
        if (armed) g.look.yaw = 0;
        this.camYaw = this.yaw + g.look.yaw;
        this._strafing = false;
        return;
      }
    }
    const wasSettled = c.blend >= 1;
    c.blend = Math.min(1, c.blend + dt / COVER_IN);
    const settled = c.blend >= 1;
    if (settled && !wasSettled) {
      // THE BACK HITS THE WALL. A beat of lean past the rest, a thud in the
      // feet, a dip in the camera -- the spring brings all three back.
      c.slam = COVER_SLAM_T;
      c.slamCam = true;
      this.game.footsteps?.land(this, 0.35);
    }
    c.slam = Math.max(0, c.slam - dt);
    const yawN = Math.atan2(-n.x, -n.z);    // facing OUT from the wall
    const h = c.low ? COVER_LOW - 0.2 : 1.0;
    const back = this._v3.copy(n).negate();
    const far = c.off + 0.7;

    // ---- the wall itself, re-read: it moves under us round a curve, and it can
    // stop being there at all
    const w = this._wallHit(near, c.base.x + n.x * 0.1, c.base.y + h, c.base.z + n.z * 0.1, back, far);
    if (w && Math.abs(w.n.y) < 0.45) {
      const nn = w.n.setY(0).normalize();
      if (nn.angleTo(n) < 0.6) {
        n.lerp(nn, 1 - Math.exp(-16 * dt)).normalize();
        t.set(-n.z, 0, n.x);
        // (copy first, THEN the height back: .copy() has already overwritten it)
        const by = c.base.y;
        c.base.copy(w.point).addScaledVector(n, c.off);
        c.base.y = by;
      }
      c.lost = 0;
    } else {
      c.lost += dt;
      if (c.lost > 0.3) { this._leaveCover('lost'); return; }
    }

    // ---- the corners: where the wall ends either side, and what is in the way
    const reach = COVER_EDGE + COVER_LOOK;
    for (const s of [1, -1]) {
      const at = (d) => this._wallHit(near, c.base.x + n.x * 0.1 + t.x * s * d, c.base.y + h,
        c.base.z + n.z * 0.1 + t.z * s * d, back, far);
      if (at(reach)) {
        // no end within the exact look: a coarse look further out, for the seek
        c.edge[s] = !at(reach + 0.45) ? reach + 0.25 : !at(COVER_SEEK) ? COVER_SEEK - 0.2 : Infinity;
      } else {
        let lo = 0, hi = reach;     // there is wall at 0 (above) and none at `reach`
        for (let i = 0; i < 6; i++) { const mid = (lo + hi) / 2; if (at(mid)) lo = mid; else hi = mid; }
        c.edge[s] = (lo + hi) / 2;
      }
      c.block[s] = !!this._wallHit(near, c.base.x + n.x * 0.12, c.base.y + h, c.base.z + n.z * 0.12,
        this._v4.copy(t).multiplyScalar(s), COVER_EDGE + 0.12);
    }
    const eR = c.edge[1] - COVER_EDGE, eL = c.edge[-1] - COVER_EDGE;
    const atR = eR <= 0.03, atL = eL <= 0.03;
    c.corner = atR && atL ? (c.camSide || 1) : atR ? 1 : atL ? -1 : 0;
    if (c.corner) c.camSide = c.corner;
    // ...and the corner a step away, which aiming and firing treat as the
    // corner (a hero "at the corner" is rarely at it to the centimetre)
    c.nearSide = c.corner || (Math.min(eR, eL) <= COVER_NEAR ? (eR <= eL ? 1 : -1) : 0);
    // the slide onto that edge, shoulder exactly on it, used by the peek and the blind arm
    const toEdge = (side) => {
      const room = Math.max(0, c.edge[side] - COVER_EDGE);
      if (room > 0.002) c.base.addScaledVector(t, side * Math.min(room, PEEK_SLIDE * dt));
    };

    const aiming = armed && !!g.pistol?.aiming;

    // ---- the keys, read against the camera, GTA's way: along the wall is the
    // shuffle, straight out from it is the way you leave. READ ONCE, ON THE
    // PRESS, and LATCHED for as long as a key is held: the camera swings round
    // to the side the hero is walking toward, and a direction re-read against a
    // camera that has just swung is the opposite direction -- the camera then
    // swung back, the hero jittered on the spot and the view flapped between
    // the two ways along the wall every frame.
    const held = m.z !== 0 || m.x !== 0 || m.sx !== 0;
    let along = 0, out = 0;
    if (settled && held) {
      if (!c.heldWas) {
        const my = this.camYaw + Math.atan2(-(m.x + m.sx), -m.z);
        const d = this._v4.set(-Math.sin(my), 0, -Math.cos(my));
        const a = d.dot(t);
        c.moveDir = Math.abs(a) > 0.35 ? Math.sign(a) : 0;
        c.moveOut = d.dot(n) > 0.7;
      }
      along = c.moveDir;
      out = c.moveOut ? 1 : 0;
    } else {
      c.moveDir = 0;
      c.moveOut = false;
    }
    c.heldWas = held && settled;
    const outOf = Math.max(c.peek, c.pop, c.out);
    c.pushOut = out > 0.7 && outOf < 0.02 ? c.pushOut + dt : 0;
    if (c.pushOut > COVER_LEAVE) { this._leaveCover('out'); return; }
    // X rolls out of it, Space leaves it -- or, at a low wall, VAULTS it: turn to
    // the wall, stand, and the climb below takes the Space press from here
    if (m.roll && !this._rollWas && settled) { this._leaveCover('roll'); return; }
    if (this._jumpBuf > 0 && settled) {
      if (c.low) {
        this.yaw = yawN + Math.PI;
        this.camYaw = this.yaw;
        this._leaveCover('vault');
      } else this._leaveCover('jump');
      this.crouched = false;
      return;
    }

    // ---- aiming: round the corner, over the top, or -- in the middle of a
    // high wall -- OFF it: GTA's man steps out of the cover and aims into the
    // open, and is back on the wall the moment the aim is let go. The trigger
    // alone does the same from the hip for as long as it is held, so a shot is
    // never refused in cover: there is always somewhere to shoot from.
    let peekWant = 0, popWant = 0, outWant = 0;
    // (off the wall the aim starts where the camera already looks, inside the
    // arc -- the view continues instead of swinging round to the hero's facing)
    const startOut = () => {
      c.aimYaw = yawN - THREE.MathUtils.clamp(wrap(yawN - this.camYaw), -OUT_HALF, OUT_HALF);
      this.camYaw = c.aimYaw;
      g.look.yaw = 0;
    };
    // A RELOAD IN COVER IS A RELOAD BEHIND THE WALL. GTA pulls the man back in
    // the moment the magazine comes out -- both hands are busy, the gun is not
    // pointing at anything, and standing round the corner for the second and a
    // half it takes is how you get shot. So the lean, the pop, the step off the
    // wall and the blind arm all give way to it (the aim is NOT dropped: hold
    // RMB through the change and he comes straight back out of the same corner
    // on the same heading when the magazine is in).
    const reloading = !!g.pistol?.reloading;
    const hipOut = c.blindWant && !reloading && !c.nearSide && !c.low && c.peek < 0.01 && c.pop < 0.01;
    if (settled && aiming && !reloading) {
      if (c.peek > 0.01 || (c.nearSide && c.out < 0.01)) {
        peekWant = 1;
        if (c.peek <= 0.01) {
          // starting: this side, and looking WHERE THE CAMERA ALREADY LOOKS,
          // clamped into the corner's arc -- the view continues instead of
          // being thrown round to a fixed angle (which read as the hero
          // suddenly shooting the other way)
          c.side = c.nearSide || c.side || 1;
          c.camSide = c.side;
          const phi0 = THREE.MathUtils.clamp(wrap(yawN - this.camYaw) * c.side, PEEK_MIN, PEEK_MAX);
          c.aimYaw = yawN - c.side * phi0;
          this.camYaw = c.aimYaw;
          g.look.yaw = 0;
        }
        toEdge(c.side);                       // onto the edge, shoulder on it
      } else if (c.pop > 0.01 || (c.low && c.out < 0.01)) {
        popWant = 1;
        if (c.pop <= 0.01) {                  // starting: round to face the wall, from where the camera looks
          const yawW = yawN + Math.PI;
          c.aimYaw = yawW + THREE.MathUtils.clamp(wrap(this.camYaw - yawW), -POP_HALF, POP_HALF);
          this.camYaw = c.aimYaw;
          g.look.yaw = 0;
        }
      } else {
        outWant = 1;
        if (c.out <= 0.01) startOut();
      }
    } else if (settled && hipOut) {
      outWant = 1;
      if (c.out <= 0.01) startOut();
    }
    const dp = dt / PEEK_IN, dq = dt / POP_IN, dout = dt / OUT_IN;
    c.peek = THREE.MathUtils.clamp(c.peek + THREE.MathUtils.clamp(peekWant - c.peek, -dp, dp), 0, 1);
    c.pop = THREE.MathUtils.clamp(c.pop + THREE.MathUtils.clamp(popWant - c.pop, -dq, dq), 0, 1);
    c.out = THREE.MathUtils.clamp(c.out + THREE.MathUtils.clamp(outWant - c.out, -dout, dout), 0, 1);
    // the hip shots, off the wall with the trigger held
    if (c.out > 0.7 && c.blindWant && !aiming) {
      c.blindCd -= dt;
      if (c.blindCd <= 0 && g.pistol?.fire()) {
        const cart = g.pistol.cart;
        c.blindCd = cart?.auto ? 0 : (cart?.rate ?? 0.16) * 1.6;
      }
    }
    if (c.shotBuf > 0) {
      c.shotBuf -= dt;
      if (this.coverCanFire() && g.pistol?.fire()) c.shotBuf = 0;
    }

    // ---- blind fire: the trigger behind the wall puts an arm round the edge, or
    // up over a low wall, and empties the gun that way for as long as it is held
    const canBlind = armed && !aiming && settled && !reloading
      && Math.max(c.peek, c.pop, c.out) < 0.02 && (c.nearSide !== 0 || c.low);
    if (c.blindWant && canBlind && c.blind <= 0.01) c.blindSide = c.nearSide;   // 0 = over the top
    if (c.blind > 0.01 && c.blindSide) toEdge(c.blindSide);                      // the arm goes round the REAL edge
    const bw = c.blindWant && canBlind ? 1 : 0;
    const db = dt / (bw ? BLIND_IN : BLIND_OUT);
    c.blind = THREE.MathUtils.clamp(c.blind + THREE.MathUtils.clamp(bw - c.blind, -db, db), 0, 1);
    if (c.blind > 0.01) {
      const bs = c.blindSide;
      const ao = (this._blindDir ??= new THREE.Vector3());
      ao.copy(n).negate().addScaledVector(t, bs * BLIND_ACROSS);
      ao.y = bs ? 0 : -0.08;
      this.aimOverride = ao.normalize();
      c.blindCd -= dt;
      if (c.blind > 0.7 && c.blindWant && c.blindCd <= 0 && g.pistol?.fire()) {
        const cart = g.pistol.cart;
        c.blindCd = cart?.auto ? 0 : (cart?.rate ?? 0.16) * 1.6;
      }
    } else this.aimOverride = null;

    // ---- the shuffle along the wall, stopped by its corner (shoulder on the
    // edge) or by anything standing on its line
    let want = 0;
    if (settled && Math.max(c.peek, c.pop, c.out) < 0.02 && c.blind < 0.02 && along !== 0) {
      want = along * (this.crouched ? COVER_CROUCH_SPEED : COVER_SPEED);
    }
    let s = Math.sign(want);
    if (s && (c.block[s] || c.edge[s] - COVER_EDGE <= 0.005)) want = 0;
    const dv = want - this.speed;
    this.speed += Math.sign(dv) * Math.min(Math.abs(dv), ACCEL_GROUND * dt);
    if (Math.abs(this.speed) < 0.01) this.speed = 0;
    if (this.speed !== 0) {
      s = Math.sign(this.speed);
      let step = this.speed * dt;
      const room = Math.max(0, c.edge[s] - COVER_EDGE);
      if (c.block[s]) { step = 0; this.speed = 0; }
      else if (Math.abs(step) > room) { step = s * room; this.speed = 0; }
      c.base.addScaledVector(t, step);
      if (step !== 0) c.camSide = s;
    }
    // ---- pushing INTO the corner you stand at: the look round it
    const lookWant = settled && c.corner && c.moveDir === c.corner
      && Math.max(c.peek, c.pop, c.out) < 0.02 && c.blind < 0.02 ? 1 : 0;
    const dl = dt / LOOK_IN;
    c.look = THREE.MathUtils.clamp(c.look + THREE.MathUtils.clamp(lookWant - c.look, -dl, dl), 0, 1);

    // the camera's side, eased: it swings round the hero, it does not cut
    c.camS += ((c.camSide || 1) - c.camS) * (1 - Math.exp(-COVER_CAM_SWING * dt));

    // ---- where the body faces
    // LEANING OUT, UP, OR OFF: the mouse moves the AIM (c.aimYaw, the camera's
    // heading, clamped to the arc the cover allows) and the body comes round
    // to it a beat behind -- the arms, solved to the camera's line, cover the
    // gap. A body that snaps 90 degrees with the camera is a turret.
    const follow = (target) => {
      this.yaw += wrap(target - this.yaw) * Math.min(1, PEEK_FOLLOW * dt);
      this.camYaw = target;
    };
    if (c.peek > 0.01 && peekWant) {
      c.aimYaw += g.look.yaw; g.look.yaw = 0;
      const phi = THREE.MathUtils.clamp(wrap(yawN - c.aimYaw) * c.side, PEEK_MIN, PEEK_MAX);
      follow(c.aimYaw = yawN - c.side * phi);
    } else if (c.pop > 0.01 && popWant) {
      c.aimYaw += g.look.yaw; g.look.yaw = 0;
      const yawW = yawN + Math.PI;
      follow(c.aimYaw = yawW + THREE.MathUtils.clamp(wrap(c.aimYaw - yawW), -POP_HALF, POP_HALF));
    } else if (c.out > 0.01 && outWant) {
      c.aimYaw += g.look.yaw; g.look.yaw = 0;
      follow(c.aimYaw = yawN - THREE.MathUtils.clamp(wrap(yawN - c.aimYaw), -OUT_HALF, OUT_HALF));
    } else if (!settled) {
      // the turn onto the wall
      this.yaw = c.fromYaw + wrap(yawN - c.fromYaw) * smooth01(c.blend);
      if (armed) g.look.yaw = 0;
    } else {
      // ON THE WALL, BUT NOT FLAT TO IT. GTA's man in cover stands turned a
      // little toward whatever side matters -- the way the camera looks, the
      // way he is shuffling, and hardest toward a corner, shoulder on the
      // edge, ready to come round it. The blind arm turns him to the edge too.
      const moving = Math.abs(this.speed) > 0.3;
      let turnWant = c.corner ? c.corner * (COVER_CORNER_TURN + LOOK_TURN * smooth01(c.look))
        : moving ? Math.sign(this.speed) * COVER_MOVE_TURN
          : (c.camSide || 1) * COVER_BASE_TURN;
      if (c.blind > 0.01) {
        const bt = c.blindSide === this._gunSide() ? BLIND_TURN : BLIND_TURN_OFF;
        turnWant += (c.blindSide * bt - turnWant) * c.blind;
      }
      c.turn += (turnWant - c.turn) * (1 - Math.exp(-8 * dt));
      const wantYaw = yawN - c.turn;
      this.yaw += wrap(wantYaw - this.yaw) * (1 - Math.exp(-14 * dt));
      // the mouse (locked, or a bare-handed drag) looks around from the wall --
      // kept in ONE place whatever the hands hold, because a weapon change
      // empties them for a few frames and a camera reading two sources jumped
      c.lookYaw = THREE.MathUtils.clamp(c.lookYaw + g.look.yaw, -COVER_LOOK_MAX, COVER_LOOK_MAX);
      g.look.yaw = 0;
    }
    // ---- and where it stands
    if (!settled) {
      const k = smooth01(c.blend);
      this.pos.x = c.from.x + (c.base.x - c.from.x) * k;
      this.pos.z = c.from.z + (c.base.z - c.from.z) * k;
    } else {
      const gunSide = this._gunSide();
      const peekOut = c.side === gunSide ? PEEK_OUT_GUN : PEEK_OUT_OFF;
      const lean = smooth01(c.peek) * peekOut * c.side + smooth01(c.look) * LOOK_OUT * c.corner
        // the weight onto the corner foot, putting the far shoulder round the edge
        + (c.blindSide && c.blindSide !== gunSide ? smooth01(c.blind) * BLIND_STEP_OFF * c.blindSide : 0);
      // turned on the wall, the far shoulder swings back: stand off by that
      // much -- and stepped off it to aim, by OUT_STEP
      const room = COVER_SHOULDER * Math.sin(Math.abs(c.turn)) * (1 - smooth01(c.peek))
        + OUT_STEP * smooth01(c.out);
      this.pos.x = c.base.x + t.x * lean + n.x * room;
      this.pos.z = c.base.z + t.z * lean + n.z * room;
    }
    this._strafing = false;

    // ---- hints
    const hc = c.nearSide && Math.max(c.peek, c.pop, c.out) < 0.02 && c.blind < 0.02 ? c.nearSide : 0;
    if (hc !== c.hintCorner) {
      c.hintCorner = hc;
      g._setHint?.(hc ? HINT_CORNER : c.low ? HINT_COVER_LOW : HINT_COVER);
    }
  }

  // THE COVER CAMERA. Back to the wall there is no "behind the hero" to sit in
  // -- that is the wall -- so it does what GTA's does: drops back along the
  // wall on the open side and looks along it toward the corner, the hero in
  // profile against the brick and the street past the edge in frame. The
  // mouse swings it round the hero (clamped, and never into the wall).
  _coverCamera(dt, armed) {
    const g = this.game, c = this._cover;
    const s = c.camS;      // -1..1: mid-swing the camera is out in front of the hero, coming round
    // looking round the corner the camera swings to the OPEN side (a camera
    // nearer the wall sees less past the edge, not more) and looks further
    // past the corner, down the far face
    const lk = smooth01(c.look);
    const look = c.lookYaw - s * LOOK_SWING * lk;
    const off = this._aimV.copy(c.t).multiplyScalar(-s * COVER_CAM_BACK * (1 - 0.2 * lk))
      .addScaledVector(c.n, COVER_CAM_SIDE + (1 - Math.abs(s)) * COVER_CAM_BACK * 0.6);
    off.applyAxisAngle(_UP, look);
    const outD = off.dot(c.n);
    if (outD < COVER_CAM_MIN) off.addScaledVector(c.n, COVER_CAM_MIN - outD);
    const pitch = THREE.MathUtils.clamp(g.look.pitch, -0.5, 0.9);
    const low = this._crouchT;
    const camTarget = this.pos.clone().add(off);
    camTarget.y += COVER_CAM_UP - 0.45 * low + 1.8 * Math.sin(Math.max(0, pitch));
    const camGY = g._groundAt(camTarget.x, camTarget.z, camTarget.y, 0.5);
    if (camGY !== null && camTarget.y < camGY + 1.0) camTarget.y = camGY + 1.0;
    // A SPRING, critically damped: the camera has weight, swings in behind the
    // hero as he settles on the wall, lags the shuffle a little and dips when
    // his back hits the brick -- none of which a lerp has.
    const cs = this._covCam ??= { v: new THREE.Vector3(), on: false };
    if (!cs.on) { cs.v.set(0, 0, 0); cs.on = true; }
    if (c.slamCam) { cs.v.y -= 1.3; c.slamCam = false; }
    const w0 = 2 * Math.PI * COVER_CAM_HZ;
    const hh = Math.min(dt, 1 / 30);
    this._v3.subVectors(camTarget, g.camera.position).multiplyScalar(w0 * w0).addScaledVector(cs.v, -2 * w0);
    cs.v.addScaledVector(this._v3, hh);
    g.camera.position.addScaledVector(cs.v, hh);
    const at = this._aimA.copy(this.pos).addScaledVector(c.t, s * (COVER_CAM_AHEAD + LOOK_AHEAD * lk))
      .addScaledVector(c.n, -0.6 * lk);
    at.y += EYE - 0.5 * low - 1.5 * pitch;
    g.camera.lookAt(at);
    // the heading the keys are read against next frame
    const f = this._aimS.subVectors(at, g.camera.position);
    this.camYaw = Math.atan2(-f.x, -f.z);
    if (Math.abs(g.camera.fov - FOOT_FOV) > 0.05) {
      g.camera.fov += (FOOT_FOV - g.camera.fov) * (1 - Math.exp(-10 * dt));
      g.camera.updateProjectionMatrix();
    }
  }

  // both arms' bones, gun side first: upper, fore, hand
  _coverBones(rig) {
    if (rig._coverBones) return rig._coverBones;
    const R = rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(rig.obj));
    const L = rig._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(rig.obj));
    const out = [];
    for (const ch of [R, L]) {
      if (!ch) continue;
      out.push(ch.upper, ch.fore);
      const hand = ch.fore.children.find((b) => b.isBone);
      if (hand) out.push(hand);
    }
    rig._coverBones = out;
    return out;
  }

  // THE HOLD BEHIND THE WALL, laid over whatever hold this frame already solved
  // and weighted by how far behind the wall the hero is (`w`): the pistol up
  // beside the shoulder, muzzle to the sky, both hands on it (applyCoverArms);
  // the rifle stood up in the shoulder pocket; and the blind arm round the edge
  // over either. Blended on the bones, so stepping out and back is a move, not
  // a cut.
  _coverHold(rig, w) {
    const held = this.held, hand = this._handBone, root = rig.obj;
    const c = this._cover;
    const bones = this._coverBones(rig);
    if (!bones.length || !held?.obj) return;
    const sv = this._coverSave ??= { q: [], gq: new THREE.Quaternion(), gp: new THREE.Vector3(), bq: [] };
    for (let i = 0; i < bones.length; i++) (sv.q[i] ??= new THREE.Quaternion()).copy(bones[i].quaternion);
    sv.gq.copy(held.obj.quaternion);
    sv.gp.copy(held.obj.position);
    // the body's own up and forward, for the weapon's line
    rig.act.updateWorldMatrix(true, false);
    const q = rig.act.getWorldQuaternion(this._aimQ);
    const up = this._v3.set(0, 1, 0).applyQuaternion(q);
    const fwd = this._v4.set(0, 0, -1).applyQuaternion(q);
    const aim = (this._cvAim ??= new THREE.Vector3());
    const upv = (this._cvUp ??= new THREE.Vector3());
    const rifle = isRifle(held);
    const side = this._rifleSide();
    if (rifle) {
      // HIGH READY, IN FRONT OF HIM. Not in the shoulder pocket: pressed to a
      // wall, anything at the flank is inside a body this deep, and both hands
      // measured 5 cm in. weapons.js 'cover' builds it forward off the gun
      // shoulder instead -- butt at the belt, barrel up past the chest, both
      // hands on it in clear air -- and hands back the line it used.
      aim.copy(up).multiplyScalar(0.95).addScaledVector(fwd, 0.3).normalize();
      poses.applyRifleHold(rig, root, hand, aim, held, 'cover', side);
      poses.seatWeapon(hand, held.obj, held, root, rig._rifleLine ?? aim, 0, rig._rifleUp);
      poses.applyRifleSupport(rig, root, held.obj, held, 1, aim, side);
    } else {
      poses.applyCoverArms(rig, root, hand, true, aim, upv);
      poses.seatWeapon(hand, held.obj, held, root, aim, 0, upv);
    }
    // the blind arm, over the hold: round the edge, or straight up over the top
    if (c && c.blind > 0.01 && this.aimOverride) {
      const n = c.n, t = c.t, bs = c.blindSide;
      for (let i = 0; i < 3; i++) if (bones[i]) (sv.bq[i] ??= new THREE.Quaternion()).copy(bones[i].quaternion);
      const target = this._v5.copy(c.base);
      const own = bs === this._gunSide();          // the corner this hand is already on
      if (bs) {
        // round the edge at THIS body's shoulder height (the banana's is 1.05 m, a man's 1.45)
        bones[0].updateWorldMatrix(true, false);
        const shY = this._v4.setFromMatrixPosition(bones[0].matrixWorld).y - this.pos.y;
        target.addScaledVector(t, bs * (COVER_EDGE + (own ? BLIND_REACH : BLIND_REACH_OFF)))
          .addScaledVector(n, own ? BLIND_HUG : BLIND_HUG_OFF);
        target.y += shY + 0.04;
      } else {
        target.addScaledVector(n, -(c.off + 0.12));
        target.y += c.top + 0.22;
      }
      poses.applyReachPose(rig, root, hand, target, this.aimOverride,
        bs ? (own ? HINT_BLIND : HINT_BLIND_OFF) : HINT_OVER);
      poses.seatWeapon(hand, held.obj, held, root, this.aimOverride, 0);
      if (rifle) poses.applyRifleSupport(rig, root, held.obj, held, 1, this.aimOverride, side);
      const k = 1 - smooth01(c.blind);
      for (let i = 0; i < 3; i++) if (bones[i]) bones[i].quaternion.slerp(sv.bq[i], k);
    }
    // ...and all of it only as far behind the wall as the hero is
    if (w < 0.999) {
      for (let i = 0; i < bones.length; i++) bones[i].quaternion.slerp(sv.q[i], 1 - w);
      held.obj.quaternion.slerp(sv.gq, 1 - w);
      held.obj.position.lerp(sv.gp, 1 - w);
    }
    for (const b of bones) b.updateWorldMatrix(false, false);
  }

  // Bare-handed behind the wall: both arms down and back, palms to the brick.
  // Booked through _boneSet so the clip gets its arms back the moment the wall
  // is left -- an idle that does not animate the arms would otherwise keep
  // them flat to a wall that is no longer there.
  _coverArmsBare(rig, w) {
    const bones = this._coverBones(rig);
    if (!bones.length) return;
    if (w <= 0.001) {
      for (const b of bones) rig._releaseBone(b);
      this._coverArmsOn = false;
      return;
    }
    this._coverArmsOn = true;
    const sv = this._coverSave ??= { q: [], gq: new THREE.Quaternion(), gp: new THREE.Vector3(), bq: [] };
    for (let i = 0; i < bones.length; i++) (sv.q[i] ??= new THREE.Quaternion()).copy(rig._clipQ(bones[i]));
    poses.applyCoverArms(rig, rig.obj, null, false);
    for (let i = 0; i < bones.length; i++) {
      const b = bones[i];
      const q = this._q1.copy(b.quaternion).slerp(sv.q[i], 1 - w);
      b.quaternion.copy(sv.q[i]);        // the clip's value back, so _boneSet books it as the base
      rig._boneSet(b, q);
    }
  }

  _stand(x, y, z, yaw) {
    const g = this.game;
    this.pos.set(x, y, z);
    this.yaw = yaw;
    this.rig.obj.rotation.y = this.yaw;
    this.vy = 0;
    this.speed = 0;
    // feet-on-the-ground state: never land a fall or finish a climb that
    // belongs to a session that ended minutes ago
    this.grounded = true;
    this.crouched = false;
    this._crouchT = 0;
    this._stroll = false;
    this._strollT = 0;
    if (this._climb) this._endClimb('abort');
    this._climb = null;
    this._roll = null;
    this._rollCd = 0;
    this._rollBuf = 0;
    this._rollWas = false;
    this._slide = null;
    this._slideCd = 0;
    this._slideWas = false;
    this._slideBuf = 0;
    this._air = null;
    this._dive = null;
    this._cover = null;
    this._coverW = 0;
    this.aimOverride = null;
    this.rig.setAirPose?.(0, 1, 1);  // hand any folded bones back
    // A roll cut short by diving into the cockpit must not leave the body
    // curled up: hand `act` back untucked before anything else poses it.
    this.rig.act.quaternion.identity();
    this.rig.act.position.set(0, 0, 0);
    this.rig.act.scale.set(1, 1, 1);
    this._fallFrom = null;
    this._coyote = 0;
    this._jumpBuf = 0;
    this._land = 0;
    this.leaveSeat();
    // OUT OF THE SEAT IS OUT OF THE ENGINE. Only the climb down the wing used
    // to cut the mixture; the 🛬 teleport into the warehouse, a cinema cue and
    // a session resumed on foot all came through here instead and left the
    // aeroplane running behind them.
    g.stopEngine?.();
    g.scene.add(this.rig.obj);
    this.rig.play('Idle');
    this.active = true;
    // the plane's instruments and its action bar belong to the plane
    g.combatHud?.setVisible(false);
    g.skillbar?.setVisible(false);
    g.weapons?.setVisible(true);   // the personage's own slots take over
    g.look.yaw = 0;
    g.look.pitch = 0;
    g._setHint(this._footHint());
    this._snapCamera();
  }

  _board() {
    const g = this.game;
    this.active = false;
    g.combatHud?.setVisible(true);
    g.skillbar?.setVisible(true);
    g.weapons?.setVisible(false);
    g.pickups?.setVisible(false);  // no take-card from the cockpit
    g.pistol?.setVisible(false);
    if (this._draw) this._finishDraw();
    if (this._stow) { this._stow = null; this._settle = null; this._toHolster(); }
    if (this._cover) this._leaveCover('silent');
    this.sitInPlane();
    // Strapped in, then CONTACT. The engine is cold -- the hero just walked up
    // to a parked aeroplane -- so this is the starter, the catch and the spool
    // to idle, and the machine will not move until it is running.
    g.sound?.belt();
    g.igniteEngine?.();
    g.look.yaw = 0;
    g.look.pitch = 0;
    g._setHint(g._takeoffHint());
    g._snapCamera();
    g.saveState?.(true, 'boarded the plane'); // back in the cockpit
  }

  // ---- in the cockpit -------------------------------------------------------
  // Boarding used to DELETE the personage from the scene: the plane flew
  // itself and the hero existed only as a saved state. The user wants to see
  // who is flying -- "our caracters must sit in plane good" -- so the body is
  // parented to the aircraft instead, sat on the seat that planes.js measured
  // out of that airframe's own cockpit.
  //
  // It hangs off the WRAPPER, not off the scaled model group inside it: the
  // wrapper is the plane's own unit-scale frame, so a personage parented
  // there keeps its 1.80 m whatever the aircraft had to be grown to.
  sitInPlane() {
    const g = this.game;
    if (!this.rig) return false;
    const seat = g.plane?.userData?.cockpit?.seat;
    if (!seat) {                      // no cockpit measured: the old behaviour
      g.scene.remove(this.rig.obj);
      this.seated = false;
      return false;
    }
    g.plane.add(this.rig.obj);
    this.rig.obj.rotation.set(0, 0, 0); // the body's -Z and the nose are the same way
    this.rig.obj.position.copy(seat);
    this.rig.play('Idle');
    this.seated = true;
    this._cockpit = g.plane.userData.cockpit;
    this._seatPan = new THREE.Vector3();   // where this body's pan ends up
    this._ctl = { pitch: 0, roll: 0, rudder: 0, throttle: 0 };
    this._ext = null;                 // settled on the first frame that has controls
    this.updateSeated(0);             // pose it NOW, not on the next frame
    return true;
  }

  // WHERE THIS PARTICULAR BODY'S SEAT PAN ENDS UP. The cockpit's own pan
  // (cockpit.seat) is where a 0.92 m seated pilot fits exactly; these thirteen
  // personages are not that, and the aeroplanes are no longer grown to make
  // them that (src/planes.js PILOT_GROW_MAX), so each body is settled into the
  // hull on its own measurements:
  //
  //   * the crown must not come out through a closed canopy, and
  //   * NOTHING may go below the hull's underside -- which is the rule that
  //     was missing. The old clamp only pushed down, so a body too tall for a
  //     shallow cockpit was driven through the floor and out of the belly.
  //
  // When both cannot hold, the head wins and the aeroplane keeps its pilot
  // inside it: a head a little high in a bubble canopy reads as a pilot, a
  // pair of legs under the wing root reads as a bug.
  _seatFor(hip) {
    const c = this._cockpit;
    const rig = this.rig;
    const y0 = c.seat.y - (hip ?? 0);        // hips on the pan
    // How tall the body IS once it is FOLDED INTO THE SEAT -- measured once,
    // off the seated pose alone (characters.js setSeated), with the parked
    // boots already carried to the ends of their shins. Deliberately not
    // measured off the finished pose: the hands and feet are solved onto
    // controls fixed in the AEROPLANE, so the body's extent then depends on
    // where the body was put, and feeding that back in is a loop that chases
    // its own tail (it moved an astronaut 46 cm between two frames).
    const crown = this._ext?.top ?? rig.sitTop ?? rig.bodyH ?? 1.8;
    const low = this._ext?.low ?? rig.sitBot ?? 0;
    const hi0 = Number.isFinite(c.ceil) ? c.ceil - crown : Infinity;  // head under the glass
    const lo0 = Number.isFinite(c.bot) ? c.bot - low : -Infinity;     // nothing under the belly
    // THE MARGIN IS A LUXURY, NOT A RULE. A few centimetres of slack at each
    // end covers the limbs that the plants and reaches move past the measured
    // pose -- but spending it unconditionally costs 10 cm of fit, and on a
    // cockpit that clears by 5 it is the MARGIN that pushes the head out
    // through the hood. So it is taken only where there is room to take it.
    const roomy2 = hi0 - SEAT_MARGIN > lo0 + SEAT_MARGIN;
    const hi = roomy2 ? hi0 - SEAT_MARGIN : hi0;
    const lo = roomy2 ? lo0 + SEAT_MARGIN : lo0;
    // A COCKPIT THAT CANNOT HOLD A PILOT AT ALL. Some of these models are
    // stylised to the point where there is less hull at the cockpit than
    // there is personage: the Extra 330's canopy top is 0.67 m over its
    // fuselage floor and a seated body is 1.3. One of the two rules has to
    // break, and breaking either one all the way is the ugly answer -- a head
    // and shoulders standing out of a painted-on hood, or a pair of legs
    // hanging under the wing root. Split it: the body sits in the MIDDLE of
    // what hull there is, and each end pokes out by half as much.
    if (lo > hi) return (lo + hi) / 2;
    return THREE.MathUtils.clamp(y0, lo, hi);
  }

  // Out of the cockpit: the body goes back to being a thing in the world.
  leaveSeat() {
    if (!this.seated) return;
    this.seated = false;
    // The controls centre themselves when the hands come off them. Nothing
    // drives them once _flyControls stops running, so a stick left hard over
    // stays hard over in an empty cockpit.
    const parts = this.game.plane?.userData?.controls;
    if (parts) {
      parts.stick?.rotation.set(0, 0, 0);
      if (parts.pedalL) parts.pedalL.rotation.x = 0;
      if (parts.pedalR) parts.pedalR.rotation.x = 0;
      if (parts.throttle) parts.throttle.rotation.x = CONTROLS.throttleThrow * 0.5; // closed
    }
    this.rig?.obj.parent?.remove(this.rig.obj);
    this.rig?.setSeated?.(0);         // hand the folded bones back
  }

  // Every frame the hero is flying. The mixer runs first (it owns every bone
  // the idle clip touches) and the seat is written on top of it, exactly the
  // way the crouch and the roll do it.
  updateSeated(dt) {
    if (!this.seated || !this.rig) return;
    // THE CLOCK STOPS IN THE SEAT. A strapped-in pilot is still, and the idle
    // clip is not: measured at the arm-chain tip it swings the hand 10 cm fore
    // and aft at 0.7 Hz. _poseSeated puts the hands on the stick with
    // _boneTwist, which is an OFFSET from wherever the clip left the bone and
    // refreshes that base every time the mixer writes — so the whole idle
    // sway came through the seated pose and the hands bounced as if he were
    // still running. Stepping the mixer by ZERO (rather than not at all) keeps
    // it asserting the same frame of the clip, which is what makes the base
    // _boneTwist reads identical frame to frame.
    this.rig.update(0);
    const g = this.game;
    const c = this._cockpit;
    const rig = this.rig;
    const hip = rig.setSeated(1);
    // Drop the body so its HIPS are on the seat. A personage's origin is
    // between its soles, so seating it by that origin leaves it floating a
    // metre above the cockpit with its legs through the panel.
    const y = this._seatFor(hip);
    rig.obj.position.set(c.seat.x, y, c.seat.z);
    // THE FURNITURE GOES WITH THE PILOT. The kit is authored with its origin
    // on the seat pan, and the pan belongs at THIS body's hip joint -- not
    // where the cockpit measured one for a standard 0.92 m pilot. Leaving it
    // at the measured height is how you get a pilot sunk into the hull
    // holding a stick up by the canopy, 30 cm above his hands; and quoting it
    // off the pelvis instead of the hip joint is how a short-legged body ends
    // up with a rudder bar it cannot reach (characters.js sitHip).
    const hp = rig.sitHip;
    const pan = this._seatPan.set(
      c.seat.x + (hp?.x ?? 0),
      y + (hp ? hp.y : (hip ?? 0)),
      c.seat.z + (hp?.z ?? 0),
    );
    const kit = g.plane.userData.cockpitKit;
    if (kit) kit.position.copy(pan);
    this._flyControls(dt, pan);

    // SETTLE THE SEAT ON THE FINISHED POSE, once. The clamp above runs on the
    // FOLDED estimate (characters.js sitTop/sitBot), which is measured before
    // the hands and feet are solved onto the controls -- and the solve brings
    // the arms DOWN out of the idle's carry onto the stick and the throttle,
    // 34 cm off the top of a banana. Sinking the pilot by that 34 cm costs
    // exactly the head room that decides whether you can see it through the
    // canopy at all. So the moment the furniture is actually attached (it
    // loads asynchronously), the real extent is measured and the seat is
    // placed again on it.
    //
    // ONCE. Not every frame -- a per-frame box of a skinned body is per-vertex
    // work -- and not iterated either: the controls are fixed in the AEROPLANE
    // and the body hangs off its own answer, so feeding the measurement back
    // round is a loop that chases its own tail (it moved an astronaut 46 cm
    // between two frames before this was a single shot).
    if (!this._ext && g.plane.userData.controls) {
      const hipY = rig.sitHip ? rig.sitHip.y : (hip ?? 0);
      const place = (yy) => {
        rig.obj.position.y = yy;
        pan.y = yy + hipY;
        if (kit) kit.position.copy(pan);
        this._flyControls(0, pan);
      };
      const b = rig._localBox();
      this._ext = { top: b.max.y, low: b.min.y };
      const y2 = this._seatFor(hip);
      if (Math.abs(y2 - y) > 0.004) {
        place(y2);
        // AND CHECK THAT IT HELD. Moving the pilot moves the controls with
        // it, the hands and feet follow them, and the body's extent is not
        // quite what it was when it was measured -- so the new seat is only
        // kept if the head really did stay under the glass and the feet above
        // the belly. When it does not hold, the conservative seat (the folded
        // estimate, which can only ever be too cautious) goes back. Two
        // passes, never a loop: this is the measurement that moved an
        // astronaut 46 cm a frame when it was allowed to iterate.
        const c2 = rig._localBox();
        const crownOk = !Number.isFinite(c.ceil) || y2 + c2.max.y <= c.ceil + 0.01;
        const bellyOk = !Number.isFinite(c.bot) || y2 + c2.min.y >= c.bot - 0.01;
        if (crownOk && bellyOk) {
          // ...with room for the CONTROLS TO MOVE. The settled extent is
          // measured at one position of the stick and the throttle, and on a
          // body whose highest point is a hand rather than a head (the
          // banana's is) the box grows and shrinks as the levers travel. The
          // slack covers that travel, so a seat settled at idle does not put
          // a knuckle through the canopy at full power.
          this._ext = { top: Math.max(this._ext.top, c2.max.y) + SEAT_TRAVEL,
            low: Math.min(this._ext.low, c2.min.y) - SEAT_TRAVEL };
        } else {
          this._ext = { top: rig.sitTop ?? c2.max.y, low: rig.sitBot ?? c2.min.y };
          place(y);
        }
      }
    }
  }

  // ---- flying it --------------------------------------------------------
  // "all personages must sit in plane like real, with all real animations".
  // Sitting still in a chair is not flying: a pilot HOLDS the stick and it
  // moves where the aeroplane is being asked to go, the feet are on the
  // rudder bar and it goes in and out with the yaw, the left hand is on the
  // throttle and rides the engine, and the head looks where the camera looks.
  // All four are solved every frame onto the real furniture -- the same
  // two-bone reach the holster draw and the rifle hold are built from -- so
  // this works on a banana, an elephant and a Mixamo man alike.
  _flyControls(dt, pan) {
    const g = this.game;
    const rig = this.rig;
    const parts = g.plane.userData.controls;
    const stickIn = g.airborne || Math.abs(g.speed) > 0.2 ? g._readStick?.() : null;
    const want = {
      pitch: stickIn?.pitch ?? 0,
      roll: stickIn?.roll ?? 0,
      rudder: stickIn?.rudder ?? 0,
      throttle: g.powerFrac?.() ?? 0,
    };
    // A hand does not snap a stick across the cockpit: the controls chase the
    // input, and the hands chase the controls. (Hard-cutting them also makes
    // the two-bone solve flicker between elbow solutions.)
    const ctl = this._ctl;
    const k = dt > 0 ? Math.min(1, dt * 7) : 1;
    for (const key of ['pitch', 'roll', 'rudder', 'throttle']) {
      ctl[key] += (want[key] - ctl[key]) * k;
    }
    if (parts) {
      // the stick leans BACK for nose-up (pull) and INTO the turn
      if (parts.stick) parts.stick.rotation.set(ctl.pitch * CONTROLS.stickPitch,
        0, ctl.roll * CONTROLS.stickRoll);
      // left pedal forward on left rudder, right pedal forward on right
      if (parts.pedalL) parts.pedalL.rotation.x = ctl.rudder * CONTROLS.pedalThrow;
      if (parts.pedalR) parts.pedalR.rotation.x = -ctl.rudder * CONTROLS.pedalThrow;
      // the lever is back at idle and forward at full power
      if (parts.throttle) parts.throttle.rotation.x = (0.5 - ctl.throttle) * CONTROLS.throttleThrow;
    }

    // Where the hands and feet have to BE, in the world: the grip empties
    // inside the moving parts, read straight off their own matrices, so a
    // hand on the stick travels with the stick instead of hovering where the
    // stick used to be.
    const grip = (part) => (part?.userData?.grip
      ? part.userData.grip.getWorldPosition(_seatV)
      : null);
    const legs = rig._legRig?.();
    if (legs) {
      g.plane.getWorldQuaternion(_seatQ);
      for (const side of ['L', 'R']) {
        const L = legs[side];
        const target = grip(side === 'L' ? parts?.pedalL : parts?.pedalR);
        if (!target || !L?.thigh) continue;
        // A LEG THAT CANNOT REACH THE PEDAL MUST NOT BE SENT AT IT. The solve
        // straightens the leg and stops short, which on the elephant (0.49 m
        // of leg against a 0.70 m footwell) is a hind leg pointed rigidly at
        // the rudder bar. Short legs keep the folded pose instead, and the
        // plant fades in over the last few centimetres of reach.
        L.thigh.updateWorldMatrix(true, false);
        const d = _seatH.setFromMatrixPosition(L.thigh.matrixWorld).distanceTo(target);
        // Full plant while the pedal is inside the leg; faded out over the
        // first 10% beyond it, so a leg that nearly makes it still reaches
        // and one that never could (the elephant's, at 0.49 m) stays folded.
        const len = L.l1 + L.l2;
        const w = THREE.MathUtils.clamp((len * 1.10 - d) / (len * 0.11), 0, 1);
        if (!(w > 0.02)) continue;
        // THE KNEE GOES UP AND A LITTLE OUTBOARD. That is how a leg folds
        // into a footwell; pointed anywhere else the two-bone solve puts the
        // knee through the instrument panel or through the seat pan.
        _seatK.set(side === 'L' ? -0.38 : 0.38, 1, -0.12)
          .applyQuaternion(_seatQ).normalize();
        rig.plantFoot(side, target, _seatK, null, w);
      }
      rig.settleFeet?.();
    }
    // ...and the hands: the right on the stick, the left on the throttle.
    const root = rig.obj;
    const chainR = rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
    const chainL = rig._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
    for (const [side, chain, part, hint] of [
      ['R', chainR, parts?.stick, SEAT_HINT_R],
      ['L', chainL, parts?.throttle, SEAT_HINT_L],
    ]) {
      const hand = rig._handPoint?.(side) ?? rig._rollRig?.()?.[side]?.hand;
      if (!chain || !hand || hand === chain.fore) continue;
      const target = grip(part);
      if (!target) continue;
      // No reach gate on an ARM, unlike a leg: the solve straightens the arm
      // and stops short, and an arm stretched toward the stick is what a
      // short-armed pilot's arm looks like. A straightened LEG is a stilt.
      poses.applyReachPose(rig, root, hand, target, null, hint, chain);
    }
    // the head looks where the player is looking
    rig.lookSeated?.(g.look?.yaw ?? 0, -(g.look?.pitch ?? 0));
  }


  // C: duck down or stand back up. Not mid-air and not mid-climb — you cannot
  // crouch off a ledge, and the climb owns the whole body while it runs.
  toggleCrouch() {
    if (!this.active || this._climb || this._roll || this._slide || !this.grounded) return false;
    if (this._cover?.low) return false;   // a low wall keeps you down behind it
    this.crouched = !this.crouched;
    this.game._setHint?.(this.crouched
      ? '🤫 ჩაცუცქებული — ნელა და ჩუმად · C — წამოდგომა'
      : 'წამოდექი · C — ჩაცუცქება');
    return true;
  }

  // Cmd: the gentleman's walk, on and off. A toggle for the same reason the
  // crouch is one -- and for one of its own, which is that holding Cmd down in a
  // browser is how you close the tab (see GENT_SPEED). Everything about it is a
  // way of carrying yourself: the pace, the wide turns, the long slow leg cycle
  // and the hands behind the back. Nothing about it is a combat move, so any
  // move that is one simply writes over it while it runs.
  toggleStroll(on = !this._stroll) {
    if (!this.active) return false;
    this._stroll = !!on;
    this.game._setHint?.(this._stroll
      ? '🎩 გასეირნება — ნელი, დარბაისელი ნაბიჯი · Cmd — გამორთვა'
      : 'ჩვეულებრივი ნაბიჯი · Cmd — გასეირნება');
    return true;
  }

  // X: the dodge roll. A tumble you commit to -- the direction is taken once,
  // here, and the next ROLL_TIME seconds belong to it: no steering, no jump out
  // of it, no crouching in it. What it buys you is ground: ROLL_SPEED off the
  // push is quicker than a sprint, so it breaks a chase, crosses a street or
  // takes you out of someone's line of fire. Grounded only, and on a cooldown,
  // so it stays a move instead of a faster way to travel.
  // `opt` is how the air dive hands over: its own line (`yaw`), permission to
  // start off the ground (`air`), and the fraction of a turn the body has already
  // been carried through (`p0`), so the tumble picks up at the attitude the dive
  // left it in instead of snapping upright first.
  _startRoll(m, opt = {}) {
    if (!this.active || this._roll || this._climb || this._cover || this._flip) return false;
    if (this._dive) this._endDive();   // the dive hands over to the landing roll
    if ((!this.grounded && !opt.air) || this._rollCd > 0) return false;
    // X OUT OF A SLIDE ends the slide then and there -- the same way Space hops
    // out of one. A slide you cannot leave is a slide you have to sit out, and
    // the moment you most want out of it is the moment something arrives in
    // front of you. Three things carry over: the LINE it was on, the SPEED the
    // friction had left (the roll's own floor applies, as from anywhere else),
    // and the ATTITUDE -- the body is already lying back on the road, so the
    // tumble picks it up there instead of standing it up for a frame first.
    const sl = this._slide;
    let from = null;
    if (sl) {
      const act = this.rig?.act;
      if (act) from = { q: act.quaternion.clone(), p: act.position.clone() };
      this._endSlide();
    }
    const g = this.game;
    const armed = !!this.held;
    // Where the keys point, measured the same way the walk measures them:
    // camera-relative with a weapon out, from the hero's own facing without
    // one. Nothing held = straight ahead.
    const camYaw = this.camRef ?? Math.atan2(
      g.camera.position.x - this.pos.x,
      g.camera.position.z - this.pos.z
    );
    const keyed = m.z !== 0 || m.sx !== 0 || (armed && m.x !== 0);
    const dirYaw = armed
      ? camYaw + Math.atan2(-(m.x + m.sx), -m.z)
      : this.yaw + Math.atan2(-m.sx, -m.z);
    // Momentum is CARRIED, never thrown away: a roll out of a sprint is at
    // least as fast as the sprint was. That is the whole difference between
    // AC's roll and a dodge -- it is a way of keeping speed through something,
    // over a wall, out of a fall, through a crowd, not a way of stopping.
    this._roll = {
      t: 0,
      // handed over from a dive: the push already happened in the air, so start
      // in the rolling phase, at the turn the body is already at, and pull the
      // tuck in over a moment instead of snapping into a ball
      phase: opt.p0 ? 1 : 0,   // 0 dive, 1 over the back, 2 up on the feet
      pt: 0,      // progress within the timed phases
      p: opt.p0 ?? 0,          // 0..1 of the full turn
      tuck: 0,    // 0..1 of the curl
      soft: !!opt.p0,          // ramp the curl in rather than snapping it
      R: ROLL_R,  // m — how big a ball this body makes, measured on the way
      yaw: opt.yaw ?? (keyed ? dirYaw : (sl ? sl.yaw : this.yaw)),
      in: from,                // the attitude to ease out of (a slide's recline)
      v0: Math.max(this.runSpeed * ROLL_BOOST, Math.abs(this.speed)),
      // which shoulder goes in: alternate, so a chain of rolls does not look
      // like one animation played twice
      side: (this._rollSide = -(this._rollSide ?? -1)),
    };
    this.crouched = false;   // you come out of a tumble on your feet
    this.speed = this._roll.v0;
    return true;
  }

  // Z: the slide. Feet first, out of a run -- you cannot slide from standing and
  // you cannot slide backwards, because a slide is something done WITH speed you
  // already have, not a way of getting any. It goes down the line the hero is
  // actually travelling, not the one he happens to be facing.
  //
  // `opt` is how the air dive hands over on touchdown: its own line (`yaw`), the
  // leg that was already leading (`side`), how far the body had already laid
  // back in the air (`blend`, so the landing is not a second pose), no kick
  // (`kick`: there is no back foot to push off when you are coming down out of
  // the sky) and `force`, because a landing is never refused for a cooldown or
  // for being a little slow.
  _startSlide(opt = {}) {
    if (!this.active || this._slide || this._roll || this._climb || this._cover) return false;
    if (!this.grounded) return false;
    if (!opt.force && (this._slideCd > 0 || this.speed < this.runSpeed * SLIDE_MIN)) return false;
    if (this._dive) this._endDive();   // the air dive hands over to the slide
    this._slide = {
      t: 0,
      blend: opt.blend ?? 0,
      ending: false,
      yaw: opt.yaw ?? (this._strafing ? (this._moveYaw ?? this.yaw) : this.yaw),
      // which leg leads; alternated so two slides in a row are not one pose twice
      side: opt.side ?? (this._slideSide = -(this._slideSide ?? -1)),
    };
    this.speed = Math.abs(this.speed) * (opt.kick ?? SLIDE_KICK); // off the back foot
    this.crouched = false;
    return true;
  }

  // The gradient of the ground along a direction, + uphill. Sampled a stride
  // ahead and a stride behind rather than read off a surface normal: that is the
  // ground the body actually crosses, and the city's roads are draped on the
  // terrain, so this is the real slope of the real street.
  _slopeAlong(dx, dz) {
    const w = this.game.world;
    const a = w.heightAt(this.pos.x + dx * SLIDE_LOOK, this.pos.z + dz * SLIDE_LOOK);
    const b = w.heightAt(this.pos.x - dx * SLIDE_LOOK, this.pos.z - dz * SLIDE_LOOK);
    if (a === null || b === null) return 0;
    // +-0.9 is 42 deg, steeper than any street and about as steep as the
    // hillside banks get. Past the friction angle (atan(mu), ~40 deg) the slope
    // beats the friction and the slide ACCELERATES -- rare, and it should be.
    return THREE.MathUtils.clamp((a - b) / (2 * SLIDE_LOOK), -0.9, 0.9);
  }

  // The slide pose: the skeleton does the baseball slide (CharacterRig.setSlidePose)
  // and this lies the whole body down on the road at whatever angle it ended up.
  //
  // The blend and the leading leg are arguments, not state, because the AIR DIVE
  // on Z lays the body down with this same function while it is still falling:
  // the pose it hands to the slide at touchdown is then the one the slide
  // carries on with, and the two are a single motion. `lean` is a forward lean
  // written over the top of the recline, which is what the dive is still
  // carrying when the lay-back starts.
  _slidePose(b = this._slide.blend, side = this._slide.side, lean = 0) {
    const rig = this.rig;
    const marks = rig.setSlidePose(b, side);
    // reclined about the body's own left-right axis, feet leading, banked onto
    // the hip the trailing knee is folded under
    const q = this._rollQ.setFromEuler(
      this._slideE.set(SLIDE_RECLINE * b - lean, 0, SLIDE_BANK * side * b));
    rig.act.quaternion.copy(q);
    // NO FOLDABLE LEGS (the astronaut and the robot park their boots outside the
    // leg chain, so there is no knee to bend) -- but the body still has to LIE
    // DOWN. Reclining it about the rig origin instead swings it over like a
    // felled tree: the torso goes through the pavement, the feet stay standing
    // where they were, and the two come apart. Stand in a straight line of marks
    // up the body's own height and lay THAT down by the same rule.
    const m = marks ?? (this._slideFallback ??= {
      pts: [0, 0.45, 0.9, 1.35, HERO_HEIGHT].map((y) => new THREE.Vector3(0, y, 0)),
      hips: new THREE.Vector3(0, HERO_HEIGHT * 0.52, 0),
    });
    // LAY IT ON THE ROAD. Tipping a standing body back drives its heels and its
    // hip through the pavement, so take every point that can touch -- heels,
    // knees, hips, hands, head -- and lift the body until the lowest of them is
    // just resting on it. Then bring the hips back over the hero's own feet, or
    // the body hangs out behind the spot the game thinks he is standing on.
    let lo = Infinity;
    for (const p of m.pts) {
      const y = this._rollV.copy(p).applyQuaternion(q).y;
      if (y < lo) lo = y;
    }
    const h = this._rollV.copy(m.hips).applyQuaternion(q);
    rig.act.position.set(-h.x * b, (SLIDE_CLEAR - lo) * b, -h.z * b);
  }

  // X IN THE AIR: the dive. Mad Max throws himself flat and crosses ground he
  // could never have jumped, and pays for it by landing in a roll instead of on
  // his feet. So: all distance and no height (the lift is a kick, not a second
  // jump), the direction committed the moment it starts, and the landing roll
  // comes with it whether you wanted it or not.
  //
  // `opt.slide` is the Z version: the identical throw, flown the identical way,
  // but it lands in the slide instead of the roll (see the lay-back in the
  // flight, below). It answers to the SLIDE's cooldown, not the roll's -- the
  // two endings are two moves, and neither should be able to eat the other's.
  _startDive(m, opt = {}) {
    if (!this.active || this._dive || this._roll || this._slide || this._climb || this._cover) return false;
    if (this.grounded || (opt.slide ? this._slideCd : this._rollCd) > 0) return false;
    const g = this.game;
    const armed = !!this.held;
    const camYaw = this.camRef ?? Math.atan2(
      g.camera.position.x - this.pos.x,
      g.camera.position.z - this.pos.z
    );
    // IN THE AIR, A/D MEAN SIDEWAYS. On the ground they turn the hero, which is
    // meaningless once his feet are off it -- and Mad Max's dive goes left and
    // right as readily as it goes forward. So here they are a direction, the same
    // as Q/E, and the dive is thrown down whichever line the keys describe.
    const lat = m.x + m.sx;
    const keyed = m.z !== 0 || lat !== 0;
    const dirYaw = armed
      ? camYaw + Math.atan2(-lat, -m.z)
      : this.yaw + Math.atan2(-lat, -m.z);
    this._dive = {
      t: 0,
      spin: 0,
      yaw: keyed ? dirYaw : (this._strafing ? (this._moveYaw ?? this.yaw) : this.yaw),
      side: (this._diveSide = -(this._diveSide ?? -1)),
      slide: !!opt.slide,   // Z: land sliding instead of rolling
      sb: 0,                // 0..1 of the lay-back into the slide pose
    };
    this.speed = Math.max(Math.abs(this.speed), this.runSpeed * DIVE_PUSH);
    this.vy = Math.max(this.vy, DIVE_LIFT);  // thrown out of a fall, it still sails
    this.crouched = false;
    return true;
  }

  // The dive's body. Near upright with a lean on it while it flies, facing the
  // shot, and it only goes over -- head down, reaching -- as the floor comes up.
  // It never tumbles in mid-air: the ROLL does the turning, and it takes over the
  // moment the body is far enough over (see the flight, above).
  _divePose() {
    const rig = this.rig;
    const d = this._dive;
    const w = Math.min(1, d.t / 0.1);
    const near = d.near ?? 0;
    // Through the air this is a LEAP, not a bellyflop: the stretched-out jump
    // pose, gathering as the ground comes up. The reach of a dive is written over
    // the top of it as he goes over, which is the part that is a dive.
    // Z's legs hand over from the FLIGHT to the SLIDE halfway down the approach:
    // the air pose fades out as the floor comes up and the slide's own pose takes
    // the body from there, so the two swap through the body's own standing legs
    // instead of cutting from one fold straight into the other.
    const af = d.slide
      ? Math.max(0, 1 - near / Math.max(1e-3, 1 - SLIDE_LAYBACK)) : 1;
    rig.setAirPose?.(w * af, 0.45 + 0.5 * near, d.side);
    // Z: THE LAST COUPLE OF METRES ARE THE SLIDE, not a dive over the top. Posed
    // by the slide's own function, laid on the road by the slide's own rule, so
    // the touchdown hands over a pose that is already the right one and the
    // slide simply continues it with the ground now under it.
    if (d.slide) {
      if (d.sb > 0.02) {
        this._slidePose(d.sb, d.side, DIVE_LEAN * (1 - d.sb) * w);
        return;
      }
    } else if (near > 0.5) rig.setDivePose?.(w * (near - 0.5) * 2, d.side);
    // Pitched over the axis that lies ACROSS the dive, in the body's own frame --
    // the same axis the roll tumbles about. Diving straight ahead that is a plain
    // forward pitch; diving to the side it lays the body over that shoulder
    // instead, which is what a sideways dive looks like. Using the roll's own
    // formula also means the roll can pick the body up exactly where this leaves
    // it when the flight runs out.
    const ra = d.yaw - this.yaw;
    const pitch = DIVE_LEAN + (DIVE_PITCH - DIVE_LEAN) * (d.slide ? 0 : near);
    const q = this._rollQ.setFromAxisAngle(
      this._rollAxis.set(-Math.cos(ra), 0, Math.sin(ra)), pitch * w);
    const core = ROLL_CORE;
    const c = this._rollV.set(0, core, 0).applyQuaternion(q);
    const baseY = rig.act.position.y;
    rig.act.quaternion.copy(q);
    rig.act.position.set(-c.x, baseY + (core - c.y), -c.z);
  }

  // Down, out of time, or caught on a ledge. The landing roll takes the body from
  // here (it is armed), so hand the layer back and let it.
  _endDive() {
    this._dive = null;
    const act = this.rig?.act;
    if (act) {
      act.quaternion.identity();
      act.position.x = 0;
      act.position.z = 0;
    }
  }

  // THE JUMP'S BODY, and the fall's. Started by the jump itself, but also by
  // simply walking off a kerb -- every way of leaving the ground comes through
  // here, so there is one place that knows the hero is in the air.
  //
  // The pose follows the REAL ARC: progress comes from the vertical speed, so the
  // body pushes as it leaves, stretches at the top where vy passes through zero,
  // and gathers its legs as the fall builds. Land, and the same pose unwinds over
  // AIR_OUT -- which reads as the knees taking the impact and standing up.
  _airPose(dt) {
    const rig = this.rig;
    if (!this._air) {
      // left the ground without jumping: no push phase, start at the stretch
      if (this.grounded || this._climb || this._dive || this._flip) return;
      this._jumpV = JUMP_V;
      this._air = {
        w: 0, p: 0.5, out: AIR_OUT, fall: true,
        lead: (this._airLead = -(this._airLead ?? -1)),
        lean: THREE.MathUtils.clamp(Math.abs(this.speed) / this.runSpeed, 0.15, 1.4),
      };
    }
    const a = this._air;
    const span = Math.max(1, this._jumpV);
    if (this.grounded) {
      // DOWN. The knees take the landing -- and they take it as a CROUCH, not as
      // the air pose unwinding: the air pose folds the legs UP under the body,
      // which in the air is right and on the ground leaves the hero standing on
      // nothing with his feet in the air. setCrouch bends the same knees and drops
      // the body by exactly as much as the fold lifts the ankles (it measured
      // that once), so the soles stay on the road through the whole dip.
      a.out -= dt;
      a.w = Math.max(0, a.out / AIR_OUT);
      // hand every bone the air pose folded back first -- setCrouch only owns the
      // legs and the spine, so the ARMS would otherwise stay frozen mid-reach
      rig.setAirPose(0, 1, a.lead);
      rig.setCrouch(Math.max(this._crouchT, a.w * LAND_DIP));
      rig.act.quaternion.identity();
      if (a.w <= 0) {
        this._air = null;
        rig.act.position.x = 0;
        rig.act.position.z = 0;
      }
      return;
    } else {
      a.p = THREE.MathUtils.clamp(0.5 - 0.5 * (this.vy / span), a.fall ? 0.5 : 0, 1);
      a.w = Math.min(1, a.w + dt / AIR_IN);
    }
    const posed = rig.setAirPose(a.w, a.p, a.lead);
    if (a.w <= 0) {
      // Done: hand the layer back EXACTLY as the crouch expects it. The lean put
      // an offset on x/z and the crouch only ever writes y, so a leftover z would
      // sit the hero a few centimetres behind himself for the rest of the session.
      this._air = null;
      if (posed) {
        rig.act.quaternion.identity();
        rig.act.position.x = 0;
        rig.act.position.z = 0;
      }
      return;
    }
    if (!posed) return;              // no legs to fold on this body
    // LEAN INTO IT, about the body's middle and not about its feet: pivoting at
    // the soles swings the whole hero backwards out of his own jump. The crouch
    // has already written act.position.y this frame, so add to it rather than
    // replacing it.
    const lean = -AIR_LEAN * a.w * a.lean * (1 - 0.35 * a.p);
    const q = this._rollQ.setFromAxisAngle(this._rollAxis.set(1, 0, 0), lean);
    const c = this._rollV.set(0, ROLL_CORE, 0).applyQuaternion(q);
    const baseY = rig.act.position.y;
    rig.act.quaternion.copy(q);
    rig.act.position.set(-c.x, baseY + (ROLL_CORE - c.y), -c.z);
  }

  // ---- THE BACKFLIP -----------------------------------------------------------
  //
  // Off a height, backwards, all the way over and down on his feet. There is no
  // key for it: the reel asks for it (cinema.js), the way it asks for a roll or
  // a slide, and like those it is the GAME'S OWN BODY doing the move -- the air
  // physics fly the arc, the ground probe lands it, the fall is a real fall. The
  // only things owned here are the push and the attitude.
  //
  // The attitude is the roll's, turned the other way and taken off the ground:
  // the body balls up (CharacterRig.setRollTuck, the dodge roll's own tuck),
  // turns a full circle about the axis ACROSS it -- positive about the body's
  // own +X, which lifts the chest and takes the head back -- and opens out
  // again for the landing. Pivoted about the body's middle like every other
  // tumble, never about its feet, or the hero swings round his own soles and
  // leaves the crate sideways.
  //
  // THE ROTATION IS TIMED TO THE ARC, not to a number: how long he is in the air
  // comes out of the push and the drop (`fall`, which the caller knows and the
  // body cannot -- the ground under his feet is the crate, not the floor he is
  // going to land on), and he is upright a moment before the feet arrive. A flip
  // that is still turning when the ground comes is a fall.
  _startFlip(opt = {}) {
    if (!this.active || this._flip || this._climb || this._cover) return false;
    if (!this.grounded && !opt.air) return false;
    if (this._slide) this._endSlide();
    if (this._dive) this._endDive();
    if (this._roll) this._endRoll();
    const yaw = opt.yaw ?? this.yaw;     // what he FACES: the flip goes backward from it
    this.yaw = yaw;
    this.camYaw = opt.camYaw ?? yaw;
    const up = opt.up ?? FLIP_UP;
    const fall = Math.max(0, opt.fall ?? 0);
    const tUp = up / GRAVITY;
    const air = tUp + Math.sqrt(2 * (up * up / (2 * GRAVITY) + fall) / GRAVITY);
    this._jumpV = up;
    this.vy = up;
    this.grounded = false;
    this._coyote = 0;
    this._jumpBuf = 0;
    this._air = null;                    // the flip owns the air pose outright
    this._fallFrom = this.pos.y;
    this.crouched = false;
    this._flip = {
      t: 0,
      dur: Math.max(0.45, air * FLIP_SPIN),
      yaw: yaw + Math.PI,                // he travels BACKWARD, away from his facing
      v: opt.back ?? FLIP_BACK,
      p: 0, tuck: 0,
      lead: (this._airLead = -(this._airLead ?? -1)),
    };
    this.speed = this._flip.v;
    this._strafing = true;
    this._moveYaw = this._flip.yaw;
    return true;
  }

  // The attitude, one frame of it. Runs in place of the air pose for as long as
  // the flip lasts, and hands the body back to the landing dip on touchdown.
  _flipPose(dt) {
    const rig = this.rig;
    const f = this._flip;
    f.t += dt;
    const p = THREE.MathUtils.clamp(f.t / f.dur, 0, 1);
    f.p = p;
    // in a ball for the turn itself, open at both ends: the push leaves on
    // extended legs and the landing arrives on them
    const want = p < FLIP_TUCK_IN ? p / FLIP_TUCK_IN
      : p > FLIP_TUCK_OUT ? Math.max(0, (1 - p) / (1 - FLIP_TUCK_OUT)) : 1;
    f.tuck += (want - f.tuck) * Math.min(1, 14 * dt);
    // A SKIM IS NOT A LANDING. Going over the lip of the thing he jumped off,
    // the ground probe can find it for a single frame -- and that ended the
    // turn two thirds through and dropped the rest of the fall on a plain air
    // pose. A touchdown has to last to count.
    f.down = this.grounded ? (f.down ?? 0) + dt : 0;
    if (f.down > 0.05 && f.t > 0.08) {
      // DOWN. Hand the body to the air pose's own landing branch -- the knees
      // take it as a crouch, which is the one thing the tuck cannot do.
      this._flip = null;
      rig.setRollTuck?.(0);
      rig.act.quaternion.identity();
      rig.act.position.x = 0;
      rig.act.position.z = 0;
      this._air = { w: 1, p: 1, out: AIR_OUT, fall: true, lead: f.lead, lean: 0.3 };
      this._airPose(dt);
      return;
    }
    // past the turn he simply falls, legs down, waiting for the floor
    if (p >= 1) {
      rig.setRollTuck?.(0);
      rig.setAirPose?.(1, 1, f.lead);
      rig.act.quaternion.identity();
      rig.act.position.x = 0;
      rig.act.position.z = 0;
      return;
    }
    const ball = rig.setRollTuck?.(f.tuck) ?? { core: ROLL_CORE, r: ROLL_CORE };
    const core = ROLL_CORE + (ball.core - ROLL_CORE) * f.tuck;
    // Eased at both ends: a flip leaves slowly off the legs, whips through the
    // inversion and opens out. A linear turn reads as a wheel, not a body.
    const q = this._rollQ.setFromAxisAngle(this._rollAxis.set(1, 0, 0),
      2 * Math.PI * smooth01(p));
    const c = this._rollV.set(0, core, 0).applyQuaternion(q);
    rig.act.quaternion.copy(q);
    rig.act.position.set(-c.x, core - c.y, -c.z);
  }

  // THE GUN KEEPS POINTING WHERE THE SHOT GOES -- through a roll, through a
  // slide, in every mode. What it must not do is reach somewhere a shoulder
  // cannot: lying back in a slide, or upside down halfway through a tumble, the
  // aim line can be behind the arm, and solving for it drags the hand back
  // through the ribs and out of the body. So the hands track the aim inside a
  // cone around the CHEST's own forward, and past the edge of the cone they stop
  // at the edge instead of tearing -- Apex's slide, The Division's cover and
  // Assassin's Creed's rolls all hold the weapon this way. The BULLET is not
  // clamped: it still goes down the crosshair. Only the arm is.
  //
  // Reclined 57 degrees in a slide the aim line sits 57 degrees off the chest,
  // inside the cone, so a slide aims EXACTLY where you are shooting.
  _moveAim(aim) {
    const rig = this.rig;
    if (!aim || !rig) return aim;
    rig.act.updateWorldMatrix(true, false);
    const q = rig.act.getWorldQuaternion(this._aimQ);
    // the chest's own basis: where it faces, which way is up out of it, and which
    // way is across it. All three carry the tumble and the recline.
    const fwd = this._aimV.set(0, 0, -1).applyQuaternion(q);
    const up = this._aimA.set(0, 1, 0).applyQuaternion(q);
    const side = this._aimS.set(1, 0, 0).applyQuaternion(q);
    // the aim line in that basis, as an elevation and a swing
    const z = fwd.dot(aim), x = side.dot(aim), y = up.dot(aim);
    let pitch = Math.asin(THREE.MathUtils.clamp(y, -1, 1));
    let yaw = Math.atan2(x, z);
    const cp = THREE.MathUtils.clamp(pitch, AIM_DOWN, AIM_UP);
    // Across the body the reach is the ARM's across the turned chest plus
    // however far the torso has turned toward the shot (this._aimTwist,
    // swingBody): the banana is as deep as it is wide, and an arm that
    // crosses further than that crosses THROUGH it. Capped at the old limit.
    const maxYaw = Math.min(AIM_YAW, AIM_ARM_YAW + Math.abs(this._aimTwist ?? 0));
    const cy = THREE.MathUtils.clamp(yaw, -maxYaw, maxYaw);
    if (cp === pitch && cy === yaw) return aim;   // inside the workspace: as-is
    // rebuild it at the edge of what the shoulder has
    const c = Math.cos(cp);
    return this._aimOut.set(0, 0, 0)
      .addScaledVector(fwd, c * Math.cos(cy))
      .addScaledVector(side, c * Math.sin(cy))
      .addScaledVector(up, Math.sin(cp))
      .normalize();
  }

  // Back on the feet. Same hand-back as the roll: whatever posed the `act` layer
  // last is what you see, so leave it exactly as the crouch and the climb expect.
  _endSlide() {
    // Armed, the mouse moved the AIM and not the body while this ran, so the two
    // have drifted apart. Getting up, the hero turns to face where he has been
    // aiming -- otherwise the camera snaps back onto his old heading as the slide
    // ends and takes the crosshair with it.
    if (this.held) this.yaw = this.camYaw;
    this._slide = null;
    this._slideCd = SLIDE_CD;
    this._strafing = false;
    const act = this.rig?.act;
    if (act) {
      act.quaternion.identity();
      act.position.set(0, 0, 0);
      act.scale.set(1, 1, 1);
    }
  }

  // The tumble, written to the `act` layer: one full turn of the body about its
  // own middle while it curls into a ball and back out again. There is no roll
  // clip on any of these bodies, so the whole move is this function.
  _rollPose() {
    const rig = this.rig;
    const r = this._roll;
    const tuck = r.tuck;
    // THE CURL IS THE SKELETON'S OWN -- hips, knees, spine, neck, arms (see
    // CharacterRig.setRollTuck). It hands back the ball it measured on this
    // particular body: where the curl's centre sits above the soles, and how far
    // the furthest part of it reaches from that centre. The body turns about
    // that centre and rides exactly that radius off the ground, so the ball
    // touches the pavement and never cuts through it.
    const ball = rig.setRollTuck(tuck) ?? { core: ROLL_CORE, r: ROLL_CORE };
    const spin = 2 * Math.PI * r.p;
    const core = ROLL_CORE + (ball.core - ROLL_CORE) * tuck;
    const high = ROLL_CORE + (ball.r - ROLL_CORE) * tuck;
    // WHICH WAY it goes over: the axis lies across the direction of travel,
    // taken in the body's own frame. Bare-handed that is straight forwards by
    // now (the body has swung into the roll); with a weapon out the facing is
    // locked to the crosshair, so the very same line takes it over the shoulder
    // or back over the heels instead -- a sideways dive, for free.
    //
    // And the axis is CANTED, tilted up out of horizontal by ROLL_SHOULDER (the
    // tilt is about the line of travel, which is why one sin/cos does it): the
    // body goes over one shoulder and past the opposite hip instead of over the
    // crown of its own head. That cant is the difference between a parkour roll
    // and a gymnast's somersault. It eases in and out with the tuck, so the dive
    // and the stand-up both stay square.
    const ra = r.yaw - this.yaw;
    const cant = ROLL_SHOULDER * r.side * tuck;
    const cc = Math.cos(cant);
    const q = this._rollQ.setFromAxisAngle(
      this._rollAxis.set(-Math.cos(ra) * cc, Math.sin(cant), Math.sin(ra) * cc),
      spin);
    // Spin about the MIDDLE, not the feet: the rig's origin is under the soles,
    // and turning about that swings the hero over like a felled tree and puts
    // his head a metre underground. Rotate the centre, then put it back where
    // it belongs -- T(centre) . R . T(-centre), by hand.
    const c = this._rollV.set(0, core, 0).applyQuaternion(q);
    const tx = -c.x, ty = high - c.y, tz = -c.z;
    // Eased in from the attitude the body was handed over in, if it was handed
    // over in one at all (a slide: see _startRoll). ROLL_IN is short -- this is
    // a join between two moves, not a transition the player waits through.
    if (r.in) {
      const k = smooth01(THREE.MathUtils.clamp(r.t / ROLL_IN, 0, 1));
      rig.act.quaternion.slerpQuaternions(r.in.q, q, k);
      rig.act.position.set(
        r.in.p.x + (tx - r.in.p.x) * k,
        r.in.p.y + (ty - r.in.p.y) * k,
        r.in.p.z + (tz - r.in.p.z) * k
      );
      if (k >= 1) r.in = null;
    } else {
      rig.act.quaternion.copy(q);
      rig.act.position.set(tx, ty, tz);
    }
    // and NO scale: a body that shrinks and grows again is a zoom, not a tuck
  }

  // Back on the feet: hand the `act` layer back to the crouch and the climb
  // exactly as they left it, or the hero keeps walking around as a ball.
  _endRoll() {
    this._roll = null;
    this._rollCd = ROLL_CD;
    this._strafing = false;
    const act = this.rig?.act;
    if (act) {
      act.quaternion.identity();
      act.position.set(0, 0, 0);
      act.scale.set(1, 1, 1);
    }
  }

  _snapCamera() {
    const g = this.game;
    this.camYaw = this.yaw;
    const off = new THREE.Vector3(0, 2.6, 6.2).applyEuler(new THREE.Euler(0, this.yaw, 0));
    g.camera.position.copy(this.pos).add(off);
    g.camera.lookAt(this.pos.x, this.pos.y + EYE, this.pos.z);
  }

  _readMove() {
    const k = this.game.keys;
    return {
      x: (k.has('KeyD') || k.has('ArrowRight') ? 1 : 0) - (k.has('KeyA') || k.has('ArrowLeft') ? 1 : 0),
      z: (k.has('KeyS') || k.has('ArrowDown') ? 1 : 0) - (k.has('KeyW') || k.has('ArrowUp') ? 1 : 0),
      // WoW's strafe keys: Q/E carry the legs sideways and never turn the body.
      // With a weapon out A/D are locked to the crosshair and do the same.
      sx: (k.has('KeyE') ? 1 : 0) - (k.has('KeyQ') ? 1 : 0),
      // TURN — the same A/D keys read a second way. Bare-handed they swing the
      // body itself instead of carrying it sideways. +1 = round to the right.
      turn: (k.has('KeyD') || k.has('ArrowRight') ? 1 : 0)
          - (k.has('KeyA') || k.has('ArrowLeft') ? 1 : 0),
      sprint: k.has('ShiftLeft') || k.has('ShiftRight'),
      walk: k.has('ControlLeft') || k.has('ControlRight'),
      jump: k.has('Space'),
      roll: k.has(ROLL_KEY),
      slide: k.has(SLIDE_KEY),
    };
  }

  // The plane is not part of the world's collision set -- it moves. While you
  // are on foot near it, though, it is scenery you can climb on and stand on,
  // so it is handed to the ground probe and the ledge probe as an extra
  // target. Only when it is close enough to matter, so every step is not
  // paying for a raycast against an aircraft on the far side of the airport.
  _planeTargets() {
    const g = this.game;
    const p = g.plane;
    if (!p) return null;
    const dx = p.position.x - this.pos.x;
    const dz = p.position.z - this.pos.z;
    if (dx * dx + dz * dz > 900) return null; // 30 m — not worth the raycast
    // ITS BOX SHELL IF IT HAS ONE (planes.js buildPlaneShell): ~190 triangles
    // with the shape of the airframe, instead of up to 120k of real aircraft
    // under six probes a frame.
    //
    // It is handed over HERE and nowhere else. Putting it in the world's
    // collidables instead looked tidier and broke the aeroplane: that set is
    // what the PLANE's own ground probe reads, so the machine found its own
    // shell under its wheels, took it for the road and sat on itself — "my
    // plane back not works ... i am on road".
    const shell = p.userData?.shell;
    if (shell) return [shell];
    // MESHES only. Handing the whole plane to intersectObjects walks into its
    // sprites and empties, and Sprite.raycast throws on a null material --
    // which aborted the entire foot update every frame, so nothing moved.
    if (!this._planeMeshes || this._planeMeshT > 0.5) {
      const out = [];
      p.traverse((o) => { if (o.isMesh && o.material && o.visible) out.push(o); });
      this._planeMeshes = out;
      this._planeMeshT = 0;
    }
    return this._planeMeshes.length ? this._planeMeshes : null;
  }

  // Ground under a point, WITH the surface normal -- the foot solver needs to
  // know which way the ground faces, not just how high it is. Falls back to
  // the analytic terrain when nothing is hit, which is most of the city.
  _probeGround(x, z) {
    const g = this.game;
    const from = this.pos.y + 1.4;
    const targets = g.world.collidablesNear(x, z);
    const pl = this._planeTargets();
    const all = pl ? targets.concat(pl) : targets;
    if (all.length) {
      this._ray.set(new THREE.Vector3(x, from, z), new THREE.Vector3(0, -1, 0));
      this._ray.far = 1.4 + FOOT_PROBE;
      const hits = this._ray.intersectObjects(all, true);
      this._ray.far = Infinity;
      if (hits.length) {
        const h = hits[0];
        const n = h.face
          ? h.face.normal.clone().transformDirection(h.object.matrixWorld)
          : new THREE.Vector3(0, 1, 0);
        if (n.y < 0) n.negate();
        return { y: h.point.y, n };
      }
    }
    const ty = g.world.heightAt(x, z);
    return ty === null ? null : { y: ty, n: new THREE.Vector3(0, 1, 0) };
  }

  // SPACE, at something you can get up. Looks where the hero is FACING (and, if he
  // is moving, where he is going too), and if there is an edge within reach that a
  // body fits on, takes it. Returns false when there is nothing there -- and then
  // Space goes back to being the jump button.
  //
  // Mid-air counts: that is Assassin's Creed's grab, jump at a wall and catch the
  // top of it. What does not count is being busy -- a roll, a slide and a climb
  // already own the body.
  _tryClimb() {
    if (!this.active || this._climb || this.crouched || this._cover || this._flip) return false;
    const f = this._climbDir;
    const dirs = [f.set(-Math.sin(this.yaw), 0, -Math.cos(this.yaw)).clone()];
    if (this._strafing && Math.abs(this.speed) > 0.5 && this._moveYaw != null) {
      dirs.push(f.set(-Math.sin(this._moveYaw), 0, -Math.cos(this._moveYaw)).clone());
    }
    const rig = this.rig;
    const clamp = THREE.MathUtils.clamp;
    for (const d of dirs) {
      const ledge = this._findLedge(d.x, d.z);
      if (!ledge) continue;
      // THE WHOLE CLIMB IS PLANNED HERE, in the wall's frame, from the body's
      // own dimensions -- and nothing in it moves afterwards: the hands, the
      // foot plants and the push point are world points the body is solved
      // onto, which is what makes them read as HOLDING something.
      const met = rig.climbMetrics?.() ?? CLIMB_MET_FALLBACK;
      const rise = ledge.y - this.pos.y;
      // how far under the lip the feet hang: shoulders up, then the arm's
      // reach minus what the hips' standoff from the face takes out of it
      const h = HANG_OFF + HAND_OVER + met.shoulderZ;
      const vert = Math.sqrt(Math.max(0.05, (HANG_ARM * met.armLen) ** 2 - h * h));
      const hangDrop = Math.min(HANG_DROP_MAX, met.shoulderY + vert);
      const catchY = Math.max(this.pos.y, ledge.y - hangDrop); // hop only as far as the hands need
      const { face, catch: catchP } = this._facePoint(d, this.pos, catchY, ledge.y);
      const hop = catchY - this.pos.y;
      const gY = this._gY ?? this.pos.y;
      // a hang is HELD when the feet would dangle, or when the lip is beyond a
      // standing mantle -- a wall you had to jump for is a wall you can hang on
      const hold = catchY > gY + HANG_MIN || ledge.y - gY > CLIMB_MAX * 0.85;
      const grip = this._gripPoint(d, ledge);
      const corner = grip.clone().addScaledVector(d, -HAND_OVER);
      const right = new THREE.Vector3(-d.z, 0, d.x);  // dir x up: the body's right
      const span = Math.max(0.16, met.shoulderHalf + 0.02);
      const push = grip.clone().addScaledVector(d, Math.min(PUSH_IN, 0.3 * met.armLen));
      push.y = ledge.y + 0.04;
      const to = ledge.clone();
      to.x = corner.x + d.x * STAND_IN;
      to.z = corner.z + d.z * STAND_IN;
      // where the pull ends: hip joints just under the lip, chest over it
      const chestY = Math.max(ledge.y - met.hipY + PULL_HIP, ledge.y + CHEST_OVER - met.shoulderY);
      const lift0 = Math.max(0, chestY - catchP.y);
      const high = clamp((rise - STEP_UP) / (CLIMB_MAX - STEP_UP), 0, 1);
      const lead = (this._climbLead = this._climbLead === 'L' ? 'R' : 'L');
      const side = lead === 'R' ? 1 : -1;
      // the wall plant: fixed in the world at the height the hip has when the
      // foot lands (PLANT_AT of the pull), PLANT_REACH0 of the leg under it
      const hipLand = catchP.y + lift0 * pullLift(PLANT_AT) + met.hipY;
      const dz = HANG_OFF + met.hipZ - (met.ankleOff + 0.03);
      const dy = Math.sqrt(Math.max((PLANT_REACH0 * met.legLen) ** 2 - dz * dz, (0.3 * met.legLen) ** 2));
      const plantWall = face.clone().addScaledVector(d, -(met.ankleOff + 0.03))
        .addScaledVector(right, side * (met.hipHalf + 0.03));
      plantWall.y = hipLand - dy;
      // the top plant: just past the corner on the lead side, no further than
      // the leg reaches from a hip PULL_HIP under the lip and HANG_OFF off it
      const dyTop = met.ankleOff + 0.01 - PULL_HIP;
      const fwdMax = Math.sqrt(Math.max(0, (0.75 * met.legLen) ** 2 - dyTop * dyTop)) - (HANG_OFF + met.hipZ);
      const plantTop = corner.clone().addScaledVector(d, clamp(fwdMax, 0.05, PLANT_IN))
        .addScaledVector(right, side * (met.hipHalf + 0.02));
      plantTop.y = ledge.y + met.ankleOff + 0.01;
      // the knee points mostly SIDEWAYS on the wall: with the foot under the
      // hip the perpendicular to the leg is horizontal, and a forward knee is
      // inside the wall
      const kneeWall = _UP.clone().multiplyScalar(0.5).addScaledVector(d, 0.25).addScaledVector(right, side * 0.8).normalize();
      const toeWall = _UP.clone().multiplyScalar(0.85).addScaledVector(d, 0.5).normalize();
      const kneeTop = _UP.clone().addScaledVector(d, 0.4).normalize();
      const toeTop = d.clone();
      this._climb = {
        t: 0, phase: 'reach', from: this.pos.clone(), catch: catchP, hang: hold ? catchP : null,
        to, dir: d.clone(), right, yaw: Math.atan2(-d.x, -d.z), grip, push, span, face,
        plantWall, plantTop, kneeWall, toeWall, kneeTop, toeTop, lead, side, hold, high,
        chestY, lift0, met,
        // standing, the catch is a reach (plus a hop if the lip is above it);
        // airborne it is the swing-in of hands that just found an edge
        reachDur: !this.grounded ? HANG_REACH : CATCH_TIME + CATCH_HOP * hop,
        hung: 0, pullDur: 0, mantleDur: 0,
        plant: 0, plantIn: false, plantOff: false, topOn: false,
      };
      this._fallFrom = null;   // a climb is not a fall, whatever came before it
      this._air = null;
      this.vy = 0;
      return true;
    }
    return false;
  }

  // The wall face ahead, and where the body hangs off it: a chest-height ray
  // from `from` along `d` (kept under the lip so it cannot sail over a low
  // parapet); a miss -- a slab edge seen from a step below -- takes the body's
  // own column as the face.
  _facePoint(d, from, y, lipY) {
    const g = this.game;
    const near = g.world.collidablesNear(from.x, from.z);
    this._ray.set(new THREE.Vector3(from.x, Math.min(y + 1.0, lipY - 0.1), from.z), d);
    this._ray.far = CLIMB_REACH + 0.4;
    const hit = this._ray.intersectObjects(near, true);
    this._ray.far = Infinity;
    const face = hit.length ? hit[0].point.clone() : from.clone().addScaledVector(d, HANG_OFF);
    face.y = y;
    const catchP = face.clone().addScaledVector(d, -HANG_OFF);
    catchP.y = y;
    return { face, catch: catchP };
  }

  // Where the hands go: the LIP of the ledge -- the line where the face ahead
  // meets the top. A ray just under the top finds the face; a parapet with no
  // face (a slab edge seen from a step below) falls back to the probe column.
  _gripPoint(d, ledge) {
    const g = this.game;
    const near = g.world.collidablesNear(this.pos.x, this.pos.z);
    this._ray.set(new THREE.Vector3(this.pos.x, ledge.y - 0.25, this.pos.z), d);
    this._ray.far = CLIMB_REACH + 0.5;
    const hit = this._ray.intersectObjects(near, true);
    this._ray.far = Infinity;
    const p = hit.length
      ? hit[0].point.clone().addScaledVector(d, HAND_OVER)
      : ledge.clone().addScaledVector(d, -0.35 + HAND_OVER);
    p.y = ledge.y + 0.015;
    return p;
  }

  // Both hands onto the edge, weight `w`: the lip (c.grip) while hanging and
  // pulling, the top (c.push) for the press-off, `mix` between them. WORLD
  // LOCKED -- the points never move, the body moves under them, which is the
  // whole difference between holding a wall and miming one. Each arm is a
  // two-bone solve (poses.applyReachPose) written through _boneSet so the
  // release hands it back to the clip; faded by `w` and, on top of that, by
  // how far the shoulder is from the point -- a hand lets go when the arm runs
  // out instead of stretching to a point it cannot reach.
  _handsOnLedge(c, w, hint = HINT_HANG, mix = 0) {
    const rig = this.rig;
    if (!(w > 0.01) || !c.grip) return;
    const root = rig.obj;
    const chainR = rig._armChain !== undefined ? rig._armChain : (rig._armChain = findArmChain(root));
    const chainL = rig._armChainL !== undefined ? rig._armChainL : (rig._armChainL = findArmChainL(root));
    const len = c.met.armLen;
    for (const [chain, side, sgn] of [[chainR, 'R', 1], [chainL, 'L', -1]]) {
      const hand = rig._handPoint?.(side) ?? rig._rollRig?.()?.[side]?.hand;
      if (!chain || !hand || hand === chain.fore) continue;
      const target = this._v4.lerpVectors(c.grip, c.push, mix).addScaledVector(c.right, sgn * c.span);
      chain.upper.updateWorldMatrix(true, false);
      const S = this._v3.setFromMatrixPosition(chain.upper.matrixWorld);
      const dist = S.distanceTo(target);
      const wr = w * THREE.MathUtils.clamp((HAND_REACH_TO * len - dist) / ((HAND_REACH_TO - HAND_REACH_FROM) * len), 0, 1);
      if (!(wr > 0.01)) continue;
      const clipU = this._q3.copy(rig._clipQ ? rig._clipQ(chain.upper) : chain.upper.quaternion);
      const clipF = this._q4.copy(rig._clipQ ? rig._clipQ(chain.fore) : chain.fore.quaternion);
      const preU = this._q1.copy(chain.upper.quaternion);
      const preF = this._q2.copy(chain.fore.quaternion);
      if (!poses.applyReachPose(rig, root, hand, target, null, hint, chain)) continue;
      const fU = chain.upper.quaternion.clone();
      const fF = chain.fore.quaternion.clone();
      if (wr < 0.999) { fU.slerp(clipU, 1 - wr); fF.slerp(clipF, 1 - wr); }
      if (rig._boneSet) {
        chain.upper.quaternion.copy(preU); rig._boneSet(chain.upper, fU);
        chain.fore.quaternion.copy(preF); rig._boneSet(chain.fore, fF);
      } else {
        chain.upper.quaternion.copy(fU); chain.fore.quaternion.copy(fF);
      }
      chain.upper.updateWorldMatrix(false, true);
      // a mitten hand is bent over the edge; a hand with fingers curls them (setGrip)
      const hook = this._v5.copy(c.dir).multiplyScalar(mix < 0.5 ? 0.55 : 0.95)
        .addScaledVector(_UP, mix < 0.5 ? -0.85 : -0.3).normalize();
      rig.setHandHook?.(side, hook, wr);
    }
  }

  // The act layer turned about a point `pivotY` up the body instead of the
  // soles: a lean about the feet, with the feet 0.26 m from a wall, pushes the
  // hips into the face; a lean about the hips folds the chest over the edge.
  _bodyPivot(tx, tz, pivotY) {
    const act = this.rig.act;
    act.rotation.set(tx, 0, tz);
    this._v5.set(0, pivotY, 0).applyQuaternion(act.quaternion);
    act.position.set(0, pivotY, 0).sub(this._v5);
  }

  // The pull begins -- straight from the catch, or out of a hang. How long it
  // takes is decided here: by the lift, and by whether there is momentum left
  // from the catch to spend on it (a dead hang starts from nothing).
  _startPull(c) {
    const dead = c.hung > HANG_DEAD;
    c.pullDur = c.lift0 > 0.02
      ? Math.max(dead ? PULL_MIN_HANG : PULL_MIN, PULL_RATE * c.lift0) * (dead ? 1 : PULL_MOMENTUM)
      : 0;
    c.mantleDur = MANTLE_TIME * (0.7 + 0.3 * c.high) * (dead ? 1 : 0.8);
    c.t = 0;
    c.from.copy(this.pos);
    if (c.pullDur > 0) {
      c.phase = 'pull';
    } else {
      // a kerb: nothing to pull, the hands on the lip at thigh height and the
      // foot stepping straight onto the top -- the quick vault
      c.phase = 'mantle';
      c.topOn = true; c.plantIn = true; c.plantOff = false; c.plant = 0;
    }
  }

  // THE ONE EXIT. Every bone the climb wrote goes back to the clip (setHangPose
  // at weight 0 is _releaseBones: legs, spine, head, the hand hook, both arms,
  // and the astronaut's parked boots), the act layer is squared up, and the
  // feet state is whatever the way out says.
  _endClimb(how) {
    const c = this._climb;
    const rig = this.rig;
    // ...and the mantle's own pose is faded out rather than dropped (see
    // CLIMB_OUT): the body straightens over a fifth of a second instead of
    // over one frame.
    if (how === 'done') this._climbOut = { t: 0, lead: c?.lead ?? 'R' };
    // (the lean spring is NOT touched here: settleBody ran it down to zero
    // over the climb itself -- see the note by the `_accV` reset at the end of
    // this method for what used to happen when it was not.)
    rig.setHangPose?.(0);
    rig.act.rotation.set(0, 0, 0);
    rig.act.position.set(0, 0, 0);
    if (how === 'done') {
      this.speed = 0;
      this.vy = 0;
      this.grounded = true;
    } else if (how === 'letgo') {
      this.grounded = false;
      this.vy = 0;
      this._fallFrom = this.pos.y;
      if (c) this.pos.addScaledVector(c.dir, -0.12); // a push off the wall
      this._gripT = 0;
    }
    this._climb = null;
    // Boarding handed the body to the climb (see _climbStep 'walk'): the
    // mantle has just put it on the aeroplane, so the last beat — lowering
    // into the seat — picks up from wherever it landed. A climb that was
    // let go of or aborted drops the hand-off with it.
    if (this._boardAfter) {
      const b = this._boardAfter;
      this._boardAfter = null;
      if (how === 'done') this._sitFromHere(b.seat, b.noseYaw);
    }
    // THE LEAN SPRING IS HANDED BACK SETTLED, and this has to be the LAST
    // thing: `speed` is carried through a climb untouched (he ran at the wall
    // at 7.8 m/s and the number stays there all the way up) and the lines
    // above set it to 0. Read the acceleration estimate before that and it
    // still holds the sprint, so the next frame the body's lean spring sees
    // -7.8 m/s in one step and pitches the torso from -17 to +21 degrees in
    // three frames -- railed at BODY_MAX. The chest hardly moves; it TURNS,
    // and the rifle strapped across it swings 22 cm down his back. That is
    // the AK "falling" as he tops out. He never decelerated: the climb simply
    // stopped carrying his old speed, and the estimator is told so.
    this._accV = 0;
    this._speedWas = this.speed;
    this._turnV = 0;
    this._yawWas = this.yaw;
  }

  // The chase camera, every frame on foot -- and from _climbFrame, so a held
  // hang does not leave the hero off-screen.
  _chaseCamera(dt, armed) {
    const g = this.game;
    // behind a wall the camera is the cover's (leaning out it is the aim's)
    if (this._cover && this._cover.blend >= 1 && this._cover.peek < 0.01 && this._cover.pop < 0.01
      && this._cover.out < 0.01) {
      return this._coverCamera(dt, armed);
    }
    if (this._covCam) this._covCam.on = false;   // the spring restarts from wherever this camera leaves it
    // Chase camera: behind the character, drag-to-orbit like the plane cam.
    // AIMING pulls it in over the right shoulder, GTA-style, so the reticle sits
    // on what the weapon is pointing at instead of halfway across the street.
    // (...and leaning back IN to cover still counts as aiming for the camera:
    // for the beat the lean takes, the ordinary armed camera -- 5.6 m behind a
    // hero whose behind is a wall -- is where it would otherwise jump)
    const cvx = this._cover;
    const leaning = !!cvx && Math.max(cvx.peek, cvx.pop, cvx.out) > 0.01;
    const aiming = !!g.pistol?.aiming || leaning;
    const baseYaw = armed || leaning ? this.camYaw : this.yaw + g.look.yaw;
    const pitch = THREE.MathUtils.clamp(g.look.pitch, -0.9, 1.1);
    // Hip fire gets the same over-the-shoulder framing as aiming, just looser:
    // pointed AT the hero, the reticle sits on their own chest and the shot
    // goes through them into whatever is behind.
    // AIMING IS GTA IV's CAMERA: hard in over the right shoulder, about two
    // metres back at shoulder height, with the hero shoved into the left of
    // the frame and the reticle looking straight down range past them. Close
    // enough to read the hands on the grip, which is the point of putting the
    // pistol animation on them.
    // WHICH SHOULDER. Counter-Strike's left/right-hand switch, in third person:
    // the camera sits over the right shoulder by default (the hero on the left
    // of the frame, the weapon toward the middle) and the setting mirrors it.
    // (...and leaning out of cover it is the shoulder AWAY FROM THE WALL. Round
    // a right-hand corner the wall is on the hero's RIGHT, so the camera goes
    // over the left shoulder -- over the right one its centre ray clipped the
    // wall just short of the edge, the crosshair sat on the brick and every
    // shot went into it from a metre away.)
    const peeking = this._cover && this._cover.peek > 0.01;
    const sideSign = peeking ? -this._cover.side
      : (g.settings?.state?.side === 'left' ? -1 : 1);
    const off = aiming
      ? new THREE.Vector3(0.58 * sideSign, 1.60, 2.0).applyEuler(new THREE.Euler(0, baseYaw, 0))
      : armed
        ? new THREE.Vector3(0.8 * sideSign, 2.0, 5.6).applyEuler(new THREE.Euler(0, baseYaw, 0))
        : new THREE.Vector3(0, 2.6 + 3.5 * Math.sin(Math.max(0, pitch)), 6.2)
          .applyEuler(new THREE.Euler(0, baseYaw, 0));
    // ducking takes the camera down with it, and the look-at with it -- and so
    // does a slide, or the hero disappears off the bottom of the frame
    const low = Math.max(this._crouchT, this._slide ? this._slide.blend : 0);
    if (low > 0.001) off.y -= 0.55 * low;
    const camTarget = this.pos.clone().add(off);
    // Keep the camera out of the WALL the hero is working from: stepped off a
    // wall to aim, "behind the hero" is inside the building, so the camera is
    // held just in front of the face -- GTA's tight cover-aim shot. BEFORE the
    // ground clamp below: a target still inside the building is over its roof,
    // and the ground probe would lift the camera onto it.
    if (this._cover) {
      const c = this._cover;
      const d = this._aimS.subVectors(camTarget, c.base).dot(c.n) + c.off;
      if (d < COVER_CAM_WALL) {
        const push = COVER_CAM_WALL - d;
        camTarget.addScaledVector(c.n, push);
        // Pushed in close the camera rises a little, looking down over the
        // shoulder -- GTA's tight cover-aim shot. The shoulder offset is kept:
        // slid in behind the body it ended up inside the hero's head.
        const k = THREE.MathUtils.clamp(push / off.length(), 0, 0.7);
        camTarget.y += 0.3 * k;
      }
    }
    // Keep the camera out of the ground on slopes.
    const camGY = g._groundAt(camTarget.x, camTarget.z, camTarget.y, 0.5);
    if (camGY !== null && camTarget.y < camGY + 1.2) camTarget.y = camGY + 1.2;
    // A weapon out means flick shots: the camera has to keep up with the mouse,
    // not swim after it.
    // (coming out of cover the camera has a long way to swing: snap it)
    const camRate = armed ? (peeking && this._cover.peek < 0.999 ? PEEK_CAM : 18) : g.look.dragging ? 14 : 7;
    g.camera.position.lerp(camTarget, 1 - Math.exp(-camRate * dt));
    if (armed) {
      // Look DOWN-RANGE, not at the hero. GTA points the weapon camera along
      // the character's facing and lets the character sit off to one side of
      // the frame; looking straight at them re-centres the body over the
      // reticle and you cannot see what you are shooting at.
      // full pitch, not a fraction of it: the crosshair has to track the mouse
      // up and down exactly as it does left and right
      // ...plus the sway and the shake (shooting.js): the view wanders, and
      // the crosshair, the arms and the round wander with it
      const aimDir = new THREE.Vector3(0, 0, -1)
        .applyEuler(new THREE.Euler(-(pitch + (g.pistol?.swayPitch ?? 0)),
          baseYaw + (g.pistol?.swayYaw ?? 0), 0, 'YXZ'));
      g.camera.lookAt(g.camera.position.clone().addScaledVector(aimDir, 40));
    } else {
      g.camera.lookAt(this.pos.x, this.pos.y + EYE - 0.5 * low, this.pos.z);
    }
    // Aiming narrows the lens, like GTA: the target gets bigger without the
    // camera having to shove itself into the hero's back. Eased, not snapped --
    // an instant FOV change reads as a glitch.
    // a rifle's sights pull the lens in a little tighter than a pistol's
    const wantFov = aiming ? (isRifle(this.held) ? AIM_FOV * 0.84 : AIM_FOV) : FOOT_FOV;
    if (Math.abs(g.camera.fov - wantFov) > 0.05) {
      g.camera.fov += (wantFov - g.camera.fov) * (1 - Math.exp(-10 * dt));
      g.camera.updateProjectionMatrix();
    }
    if (!g.look.dragging && !armed && (g.look.yaw !== 0 || g.look.pitch !== 0)) {
      const back = 1 - Math.exp(-4 * dt);
      g.look.yaw *= 1 - back;
      g.look.pitch *= 1 - back;
    }
  }

  // ONE FRAME OF A CLIMB. The order is the point: the mixer, then the body's
  // place and lean, then the world matrices (the keys and the IK read bone
  // positions), then the keys, then the lead foot's plant over them, then the
  // hands over everything -- so no lean can move a hand off the lip -- then
  // the parked boots, the grip, the camera.
  _climbFrame(dt, m, armed) {
    const rig = this.rig;
    const c = this._climb;
    if (this._roll) this._endRoll();
    if (this._slide) this._endSlide();
    if (this._air) { this._air = null; rig.setAirPose?.(0, 1, 1); }
    rig.play('Idle');
    rig.update(dt);
    // The climb owns the frame and never reaches swingBody, so the lean spring
    // would sit frozen on whatever the run-up stored and then snap flat the
    // frame the climb ends -- 17 deg of chest in one frame, which throws the
    // slung rifle down his back right as he tops out. Keep stepping it, asking
    // for nothing: he arrives upright and the AK stays where it was put.
    rig.settleBody?.(dt);
    // square up to the wall: both hands are placed in its frame
    this.yaw += wrapPi(c.yaw - this.yaw) * Math.min(1, 10 * dt);
    rig.obj.rotation.y = this.yaw;
    rig.act.position.set(0, 0, 0);
    rig.act.rotation.set(0, 0, 0);
    let keys = null;                     // the legs/spine/head keys this frame
    let handW = 0;
    let hint = HINT_HANG;
    let mix = 0;
    let plant = null;                    // { at, knee, toe }
    let exit = null;
    if (c.phase === 'reach') {
      // the hands go to the lip, the body swings in under them
      c.t += dt / Math.max(0.02, c.reachDur);
      const k = Math.min(1, c.t);
      const e = ease01(k);
      this.pos.lerpVectors(c.from, c.catch, e);
      // a lip above the standing reach is JUMPED for: the legs push off and
      // stretch under a body going up, and the hands close on it at the top;
      // a lip within reach is simply taken
      const hop = c.catch.y - c.from.y;
      keys = () => (hop > 0.2 && this.grounded !== false
        ? rig.setAirPose?.(1, 0.3 + 0.4 * e, 1)
        : (c.hold ? rig.setHangPose?.(e, 0) : rig.setPullPose?.(e * 0.6, 0, c.lead)));
      handW = e;
      this._gripT += (1 - this._gripT) * Math.min(1, 18 * dt);
      if (k >= 1) {
        if (c.hold) { c.phase = 'hang'; c.t = 0; } else this._startPull(c);
      }
    } else if (c.phase === 'hang') {
      // held until the player decides. Forward (held) or Space pulls up -- so a
      // free-run that catches an edge with W still down flows straight over it,
      // the way Ezio does, and letting go of the stick is what makes him hang;
      // S or C lets go of the wall.
      c.t += dt;
      c.hung = c.t;
      this.pos.copy(c.catch);
      this._bodyPivot(HANG_SWAY_X * Math.sin(0.9 * c.t + 1), HANG_SWAY_Z * Math.sin(1.3 * c.t), c.grip.y - c.catch.y);
      keys = () => rig.setHangPose?.(1, c.t);
      handW = 1;
      this._gripT = 1;
      const jumpNow = m.jump && !this._jumpWas;
      this._jumpWas = m.jump;
      // A hang is a decision — EXCEPT when this climb is somebody boarding the
      // aeroplane. Enter means "get in", and a player who pressed it has no
      // reason to know the body is now waiting to be told to pull up: it hung
      // off the wing until the end of time.
      if (m.z < 0 || jumpNow || this._boardAfter) this._startPull(c);
      else if (m.z > 0 || this.game.keys.has('KeyC')) exit = 'letgo';
    } else if (c.phase === 'pull') {
      // up BESIDE the wall under hands that do not move; the lead foot lands
      // on the face at PLANT_AT and pushes through the fast part of the lift
      c.t += dt / c.pullDur;
      const u = Math.min(1, c.t);
      this.pos.set(c.catch.x, c.catch.y + c.lift0 * pullLift(u), c.catch.z);
      this._bodyPivot(-LEAN_TOP * ease01((u - 0.55) / 0.45), 0, c.met.hipY);
      keys = () => rig.setPullPose?.(1, u, c.lead);
      handW = 1;
      hint = hintMix(HINT_HANG, HINT_PULL, ease01(u / 0.6));
      mix = ease01((u - 0.85) / 0.15);   // the palms come onto the top at the end
      if (!c.topOn) {
        if (u >= PLANT_AT && !c.plantOff) c.plantIn = true;
        if (u > 0.8) c.plantOff = true;
        if (c.plantOff && c.plant < 0.02 && u >= 0.9) { c.topOn = true; c.plantOff = false; c.plantIn = true; c.plant = 0; }
      }
      plant = c.topOn
        ? { at: c.plantTop, knee: c.kneeTop, toe: c.toeTop }
        : (c.plantIn ? { at: c.plantWall, knee: c.kneeWall, toe: c.toeWall } : null);
      this._gripT = 1;
      if (u >= 1) {
        c.phase = 'mantle';
        c.t = 0;
        c.from.copy(this.pos);
        if (!c.topOn) { c.topOn = true; c.plantOff = false; c.plantIn = true; c.plant = 0; }
      }
    } else {
      // the mantle: up over the hands first, then forward, so the hips clear
      // the corner; the palms push DOWN on the top and leave it as the hips
      // pass over them; the lead foot is on the top until the leg is straight
      c.t += dt / c.mantleDur;
      const u = Math.min(1, c.t);
      const ey = 1 - (1 - u) * (1 - u);
      const exz = ease01((u - 0.12) / 0.88);
      this.pos.set(
        c.from.x + (c.to.x - c.from.x) * exz,
        c.from.y + (c.to.y - c.from.y) * ey,
        c.from.z + (c.to.z - c.from.z) * exz
      );
      this._bodyPivot(-LEAN_TOP * (1 - ease01(u / 0.7)), 0, c.met.hipY);
      keys = () => rig.setMantlePose?.(1, u, c.lead);
      handW = 1 - ease01((u - 0.5) / 0.3);
      hint = HINT_PRESS;
      mix = 1;
      if (u > 0.7) c.plantOff = true;
      plant = { at: c.plantTop, knee: c.kneeTop, toe: c.toeTop };
      if (u >= 1) { this.pos.copy(c.to); exit = 'done'; }
    }
    // the plant's weight ramps in and out over PLANT_FADE
    if (c.plantOff) c.plant = Math.max(0, c.plant - dt / PLANT_FADE);
    else if (c.plantIn) c.plant = Math.min(1, c.plant + dt / PLANT_FADE);
    rig.obj.updateMatrixWorld(true);
    keys?.();
    if (plant && c.plant > 0.001) {
      const reach = rig.plantFoot?.(c.lead, plant.at, plant.knee, plant.toe, c.plant) ?? 0;
      if (reach >= PLANT_RELEASE) c.plantOff = true;   // the leg is straight: the push is spent
    }
    this._handsOnLedge(c, handW, hint, mix);
    rig.settleFeet?.();
    rig.setGrip?.(this._gripT, this.held ? this._handBone : null);
    if (exit) this._endClimb(exit);
    this._chaseCamera(dt, armed);
  }

  // Is there a ledge ahead we could pull ourselves onto? Returns the world
  // point to end up standing on, or null.
  //
  // Two probes, both necessary: cast DOWN from above and in front to find the
  // top surface, then cast UP from that surface to make sure a body fits there.
  // Without the second one the character mantles into the underside of a floor
  // slab and ends up inside the building.
  _findLedge(dirX, dirZ) {
    const g = this.game;
    const feet = this.pos.y;
    const planes = this._planeTargets();
    const near = planes
      ? g.world.collidablesNear(this.pos.x, this.pos.z).concat(planes)
      : g.world.collidablesNear(this.pos.x, this.pos.z);
    if (!near.length) return null;
    for (const reach of [0.55, CLIMB_REACH]) {
      const px = this.pos.x + dirX * reach;
      const pz = this.pos.z + dirZ * reach;
      this._ray.set(new THREE.Vector3(px, feet + CLIMB_MAX + 0.4, pz),
        new THREE.Vector3(0, -1, 0));
      this._ray.far = CLIMB_MAX + 0.6;
      const down = this._ray.intersectObjects(near, true);
      this._ray.far = Infinity;
      if (!down.length) continue;
      // Some surfaces are not for vaulting: a shooting range's firing counter
      // is 1.05 m, right inside the mantle range, and climbing it carries the
      // personage over the furniture and into the lane. If the first thing
      // under the probe says no, there is no ledge here at all.
      if (down[0].object.userData.noClimb) return null;
      const top = down[0].point.y;
      const rise = top - feet;
      if (rise <= STEP_UP || rise > CLIMB_MAX) continue;
      // headroom: a body has to fit standing on it
      this._ray.set(new THREE.Vector3(px, top + 0.12, pz), new THREE.Vector3(0, 1, 0));
      this._ray.far = CLIMB_CLEAR;
      const up = this._ray.intersectObjects(near, true);
      this._ray.far = Infinity;
      if (up.length) continue;
      return new THREE.Vector3(px + dirX * 0.35, top + 0.02, pz + dirZ * 0.35);
    }
    return null;
  }

  // Landing after a drop. GTA does not kill you for a kerb and does kill you
  // for a roof, so the cost ramps between the two.
  _land2(fromY) {
    let drop = fromY - this.pos.y;
    this._fallFrom = null;
    this._land = 0.18;
    this.game.footsteps?.land(this, Math.max(0, drop));   // both feet, and the ground under them
    // ASSASSIN'S CREED's landing roll. Hit the ground already rolling -- or with
    // the key pressed on the way down -- and the drop goes into the roll instead
    // of into your legs: ROLL_BREAK metres of it, which turns a crippling fall
    // into a free one and a fatal one into something you limp away from. This is
    // the move's real job; the speed is a bonus.
    if (this._roll || this._rollBuf > 0) {
      // a fall-break is never refused for being on cooldown
      if (!this._roll) { this._rollCd = 0; this._startRoll(this._lastM ?? { x: 0, z: 0, sx: 0 }); }
      this._rollBuf = 0;
      drop = Math.max(0, drop - ROLL_BREAK);
      if (drop <= 0) this.game._setHint('🤸 გადაგორება — დაცემა უგულებელი!');
    } else if (this._slideBuf > 0) {
      // Z's ending: the feet arrive, the slide starts, and it takes a bite out
      // of the drop too -- a smaller one than the roll's, because a slide is
      // feet and a hip rather than a whole body rolling through a shoulder.
      // The dive is still on the hero this frame, so the slide picks up its
      // line, its leading leg and the lay-back it already has in the body.
      const d = this._dive;
      this._slideCd = 0;              // a fall-break is never refused either
      if (this._startSlide({ force: true, kick: 1, yaw: d?.yaw, side: d?.side,
        blend: d?.sb ?? 0 })) {
        drop = Math.max(0, drop - SLIDE_BREAK);
        if (drop <= 0) this.game._setHint('🛷 სრიალით დაშვება — დაცემა უგულებელი!');
      }
    }
    this._slideBuf = 0;
    if (drop < FALL_SAFE) return;
    const g = this.game;
    const f = Math.min(1, (drop - FALL_SAFE) / (FALL_FATAL - FALL_SAFE));
    g._applyDamage?.(f * 1400, null);
    g._setHint(drop >= FALL_FATAL ? '💀 მაღლიდან ჩამოვარდი!' : '🤕 მძიმე დაცემა');
  }

  update(dt) {
    const g = this.game;
    const rig = this.rig;
    const m = this._readMove();
    const armed = !!this.held;
    // Bare-handed, A/D are the TURN keys, so they carry the feet nowhere — only
    // W/S and the Q/E sidestep do. With a weapon out the body is locked on the
    // crosshair and A/D strafe around it, so there they are movement again.
    const moving = m.z !== 0 || m.sx !== 0 || (armed && m.x !== 0);

    // ---- getting into the aeroplane owns the frame --------------------------
    if (this._boardSeq) {
      this._climbStep(dt);
      return;
    }

    // ---- a climb owns the frame (see _climbFrame) ---------------------------
    if (this._climb) {
      this._climbFrame(dt, m, armed);
      return;
    }

    this._planeMeshT += dt;
    this._planePrompt(dt);

    // ---- jump: edge-triggered, with coyote time and an input buffer ------
    const jumpPressed = m.jump && !this._jumpWas;
    this._jumpWas = m.jump;
    if (jumpPressed) this._jumpBuf = JUMP_BUFFER;
    this._jumpBuf = Math.max(0, this._jumpBuf - dt);
    this._coyote = this.grounded ? COYOTE : Math.max(0, this._coyote - dt);
    // Space while ducked stands you up rather than jumping, like GTA.
    // (behind a low wall it is the vault instead -- _coverFrame takes it)
    if (this._jumpBuf > 0 && this.crouched && !this._cover) {
      this.crouched = false;
      this._jumpBuf = 0;
    }
    // ---- COVER owns the frame's movement (see _coverFrame). It may also end
    // here -- a push out, a roll, a jump -- and the frame then runs on as an
    // ordinary one, which is what lets Space from cover be the jump below.
    if (this._cover) this._coverFrame(dt, m, armed);
    // ---- the stroll: Shift says you are in a hurry after all --------------
    if (this._stroll && m.sprint) this.toggleStroll(false);
    this._strollT += ((this._stroll ? 1 : 0) - this._strollT)
      * Math.min(1, dt / GENT_IN);
    // ---- roll: X, edge-triggered, and it owns the next ROLL_TIME seconds --
    this._lastM = m;      // _land2 rolls out of a fall and needs the input
    this._rollCd = Math.max(0, this._rollCd - dt);
    this._rollBuf = Math.max(0, this._rollBuf - dt);
    if (m.roll && !this._rollWas && !this._startRoll(m)) {
      // Pressed in the air: AC's landing roll. Hold the press and the touchdown
      // itself turns into the roll, which is what takes the drop out of your
      // legs -- see _land2.
      if (!this.grounded) {
        this._startDive(m);
        this._rollBuf = ROLL_BUFFER;
      }
    }
    this._rollWas = m.roll;
    if (this._roll) this._jumpBuf = 0; // no jumping out of a tumble, it finishes

    // ---- slide: Z, and Z again to get back up early ----------------------
    this._slideCd = Math.max(0, this._slideCd - dt);
    this._slideBuf = Math.max(0, this._slideBuf - dt);
    if (m.slide && !this._slideWas) {
      if (this._slide) this._slide.ending = true;
      else if (!this._startSlide() && !this.grounded) {
        // PRESSED IN THE AIR: X's dive with the other ending. Same throw, same
        // flight -- and he comes down feet first into the slide instead of over
        // his own head into the roll. Holding the press through a fall works the
        // way the landing roll does: the touchdown itself becomes the slide.
        this._startDive(m, { slide: true });
        this._slideBuf = SLIDE_BUFFER;
      }
    }
    this._slideWas = m.slide;

    // FREE-RUN -- Assassin's Creed's high profile. Sprinting at something
    // climbable climbs it with no button at all, and holding forward in the
    // air at a wall catches its edge (and hangs there, if it is high). Walking
    // into it still just slides along the face: low profile keeps the feet on
    // the ground, which is what makes a crowded floor walkable.
    const freeRun = m.z < 0 && (m.sprint || !this.grounded);
    if (freeRun && !this._climb && !this._roll && !this._slide && !this._dive
        && !this.crouched && this._tryClimb()) {
      this._jumpBuf = 0;
    }

    // SPACE AT SOMETHING CLIMBABLE CLIMBS IT. The ledge gets first refusal on the
    // key and Space only jumps when there is nothing to pull onto -- which is how
    // the button behaves in GTA, and it is the only way a climb can be a choice
    // instead of something the geometry does to you. It works off the ground too:
    // jump at a wall, press Space again on the way up, and the hands find the edge.
    if (this._jumpBuf > 0 && !this._roll && !this._slide && this._tryClimb()) {
      this._jumpBuf = 0;
      this._jumpWas = true;
    } else if (this._jumpBuf > 0 && this._coyote > 0) {
      // SLIDE-HOP: jumping out of a slide is a push off the trailing leg, and
      // it keeps whatever speed the slide still had. No blend-out -- the body
      // is going up, it has to be back on its feet now.
      if (this._slide) this._endSlide();
      const run = THREE.MathUtils.clamp(Math.abs(this.speed) / this.runSpeed, 0, 1.6);
      this._jumpV = JUMP_V * (1 + JUMP_RUN * run);
      this.vy = this._jumpV;
      // the body that goes with it: which knee drives, and how hard it leans
      this._air = {
        w: 0, p: 0, out: AIR_OUT, fall: false,
        lead: (this._airLead = -(this._airLead ?? -1)),
        lean: THREE.MathUtils.clamp(run, 0.15, 1.4),
      };
      this.grounded = false;
      this._coyote = 0;
      this._jumpBuf = 0;
      this._fallFrom = this.pos.y;
    }

    // WEAPON OUT, COUNTER-STRIKE STYLE: the crosshair never leaves the centre
    // of the screen, the mouse swings the view, and the PERSONAGE turns with it
    // one to one -- so where you look is where the gun points. The movement
    // keys then carry the legs around that heading (strafing, below).
    if (this._cover) {
      // the cover wrote the facing and the camera heading (_coverFrame)
    } else if (this._roll) {
      // Rolling, the turn keys are dead — the direction was committed at the
      // push. Bare-handed the body swings into the roll and goes head over
      // heels down the line it is travelling; with a weapon out the facing
      // stays on the crosshair and the tumble rolls sideways over the shoulder
      // instead, so you come up still looking at what you were shooting at.
      if (armed) {
        this.yaw += g.look.yaw;
        g.look.yaw = 0;
        this.camYaw = this.yaw;
      } else {
        let d = this._roll.yaw - this.yaw;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        this.yaw += d * Math.min(1, 20 * dt);
        this.camYaw = this.yaw + g.look.yaw;
      }
    } else if (this._dive) {
      // DIVING. Bare-handed the hero turns into his own dive and goes head first
      // down it, whichever way it points. With a weapon out he does NOT turn: the
      // body flies sideways while the mouse keeps the aim and the arms follow it
      // -- Max Payne's shootdodge, and the same rule the slide already follows.
      const d = this._dive;
      if (armed) {
        // THE BODY FACES THE SHOT. The mouse moves the aim, and the hero turns to
        // it even though he is travelling down the line he dived along -- so he
        // flies sideways and backwards with his front, and his gun, to the target.
        this.camYaw += g.look.yaw;
        g.look.yaw = 0;
        let da = this.camYaw - this.yaw;
        while (da > Math.PI) da -= 2 * Math.PI;
        while (da < -Math.PI) da += 2 * Math.PI;
        this.yaw += da * Math.min(1, 12 * dt);
      } else {
        let dy = d.yaw - this.yaw;
        while (dy > Math.PI) dy -= 2 * Math.PI;
        while (dy < -Math.PI) dy += 2 * Math.PI;
        this.yaw += dy * Math.min(1, 14 * dt);
        this.camYaw = this.yaw + g.look.yaw;
      }
    } else if (this._slide) {
      // Sliding, A/D shift your weight: the line BENDS, it does not carve, and
      // the body always points down the line it is actually sliding along. With
      // a weapon out the mouse keeps the body, as everywhere else.
      const sl = this._slide;
      // A SLIDING BODY DOES NOT SPIN. Everywhere else a weapon locks the body to
      // the mouse, Counter-Strike style -- but a hero on his back with his feet
      // out in front is committed to the line he is on, and swinging him round it
      // like a turret is the one thing a slide cannot do. So here the mouse moves
      // the AIM and the camera only: the crosshair goes where you point it, the
      // shot goes with it, the arms follow it as far as a shoulder reaches
      // (_moveAim), and the body keeps sliding where it was already sliding.
      if (armed) {
        this.camYaw += g.look.yaw;
        g.look.yaw = 0;
      } else if (m.turn) {
        sl.yaw -= m.turn * SLIDE_STEER * dt;
      }
      let d = sl.yaw - this.yaw;
      while (d > Math.PI) d -= 2 * Math.PI;
      while (d < -Math.PI) d += 2 * Math.PI;
      this.yaw += d * Math.min(1, 12 * dt);
      if (!armed) this.camYaw = this.yaw + g.look.yaw;
    } else if (armed) {
      this.yaw += g.look.yaw;
      g.look.yaw = 0;
      this.camYaw = this.yaw;
    } else {
      // BARE-HANDED — WoW's keyboard turn: A/D held down swing the hero round at
      // a steady rate, standing still or at a full sprint, and the chase camera
      // follows him round. Running plus a held D is a curve; a tap is a course
      // correction. A rate and not a snap, because the turn is something you
      // steer — and a sprint turns wider, so speed costs you agility.
      if (m.turn) {
        const rate = TURN_RATE * (this._stroll ? GENT_TURN
          : m.sprint && m.z < 0 ? TURN_SPRINT : 1);
        this.yaw -= m.turn * rate * dt;   // +1 (D) is clockwise: yaw decreases
      }
      this.camYaw = this.yaw + g.look.yaw;
    }

    // ARMED the body is locked on the crosshair, so movement is camera-relative
    // and A/D strafe around it. BARE-HANDED the hero steers himself with A/D, so
    // everything is measured from his OWN facing instead — W is always straight
    // ahead, whatever the camera is doing.
    const camYaw = this.camRef ?? Math.atan2(
      g.camera.position.x - this.pos.x,
      g.camera.position.z - this.pos.z
    );
    let target = 0;
    if (moving && !this._cover) {
      // ARMED: camYaw is the yaw whose forward (-sin,-cos) points
      // camera→character, i.e. "away from the camera" — exactly where W should
      // run. The input offset rotates around it: D adds -π/2 (screen right),
      // etc. S is NOT a 180° spin: the character keeps facing away from the
      // camera and backpedals toward it, so the chase cam never whips around.
      const back = m.z > 0;
      // Where the FEET go. Kept across frames so a released strafe key
      // decelerates along the same line instead of snapping onto the facing.
      this._moveYaw = armed
        ? camYaw + Math.atan2(-(m.x + m.sx), -m.z)
        : this.yaw + Math.atan2(-m.sx, -m.z);
      // sticky strafe: a weapon out, or Q/E, moves the legs off the facing
      this._strafing = armed || m.sx !== 0;
      // A STROLL IS A PACE, NOT A DIRECTION: it holds going backwards too, and
      // a backpedal is never faster than the gait that is feeding it.
      const pace = this._stroll ? GENT_SPEED
        : m.walk ? WALK_SPEED
          : this.runSpeed * (m.sprint ? SPRINT_MULT : 1);
      target = back ? -Math.min(pace, this.runSpeed * BACK_MULT) : pace;
      // ducked: a creep, and no sprinting out of it
      if (this.crouched) target = Math.sign(target) * Math.min(Math.abs(target), CROUCH_SPEED);
    }
    if (this._cover) {
      // the shuffle along the wall wrote this.speed and moved the base (_coverFrame)
    } else if (this._roll) {
      // The tumble WRITES its own speed and its own progress; the accel ramp
      // stays out of both. Travel runs down the committed line whatever the body
      // is facing, which is what `_strafing` means to the mover below.
      const r = this._roll;
      r.t += dt;
      r.R = rig.tuck?.r ?? ROLL_R;     // measured off this body's own curl
      if (r.phase === 0) {
        // the dive: a push off the legs, so it is timed
        r.pt = Math.min(1, r.pt + dt / ROLL_DIVE);
        r.p = ROLL_P_DIVE * smooth01(r.pt);
        r.tuck = smooth01(r.pt);
        this.speed = r.v0;
        if (r.pt >= 1) { r.phase = 1; r.pt = 0; }
      } else if (r.phase === 1) {
        // over the back: ROLLING, so the turn comes from the ground covered
        r.p += (Math.abs(this.speed) * dt) / (2 * Math.PI * r.R);
        r.tuck = r.soft ? Math.min(1, r.tuck + dt / 0.18) : 1;
        const f = THREE.MathUtils.clamp(
          (r.p - ROLL_P_DIVE) / (ROLL_P_UP - ROLL_P_DIVE), 0, 1);
        this.speed = r.v0 * (1 - (1 - ROLL_DRAG) * f);  // the ground takes some
        if (r.p >= ROLL_P_UP) { r.p = ROLL_P_UP; r.phase = 2; r.pt = 0; }
      } else {
        // feet down, standing up out of it and running on
        r.pt = Math.min(1, r.pt + dt / ROLL_UP);
        r.p = ROLL_P_UP + (1 - ROLL_P_UP) * smooth01(r.pt);
        r.tuck = 1 - smooth01(r.pt);
        this.speed = r.v0 * (ROLL_DRAG + (ROLL_OUT - ROLL_DRAG) * r.pt);
      }
      r.done = (r.phase === 2 && r.pt >= 1) || r.t > ROLL_MAX;
      this._strafing = true;
      this._moveYaw = r.yaw;
    } else if (this._flip) {
      // A flip is committed too: he goes backward off the thing he jumped from,
      // on the line taken at the push, and the air takes a little of it back.
      this.speed = Math.max(0, this.speed - DIVE_DRAG * 0.5 * dt);
      this._strafing = true;
      this._moveYaw = this._flip.yaw;
    } else if (this._dive) {
      // Flying: thrown forward down the line it committed to, and the air barely
      // takes anything back. No steering -- you are a projectile now.
      const d = this._dive;
      d.t += dt;
      this.speed = Math.max(0, this.speed - DIVE_DRAG * dt);
      this._strafing = true;
      this._moveYaw = d.yaw;
      // which ending the ground will be met with, kept armed for the whole
      // flight so a long dive still lands in its own move
      if (d.slide) this._slideBuf = SLIDE_BUFFER;
      else this._rollBuf = ROLL_BUFFER;
      // HOW CLOSE THE FLOOR IS -- that, and nothing else, is what turns the body
      // over. Above DIVE_NEAR he flies as he was; from there down he tips forward
      // until, at DIVE_ROLL_H, he is far enough over for the roll to take him.
      const h = this._gY === null || this._gY === undefined
        ? DIVE_NEAR : this.pos.y - this._gY;
      d.near = THREE.MathUtils.clamp(
        (DIVE_NEAR - h) / (DIVE_NEAR - DIVE_ROLL_H), 0, 1);
      // Z: the same approach, read as a LAY-BACK instead of a turn over the top.
      // It finishes before the floor does, so the feet are already out in front
      // when they get there -- that is the whole difference between landing in a
      // slide and landing on your feet and then dropping into one.
      if (d.slide) {
        d.sb = THREE.MathUtils.clamp(
          (d.near - (1 - SLIDE_LAYBACK)) / SLIDE_LAYBACK, 0, 1);
      }
      // Touchdown normally ends the dive through the armed landing roll, which
      // calls _endDive on its way in; the first branch is belt and braces.
      // A SLIDE WAITS FOR THE ROAD. The roll can be handed the body in mid-air
      // -- it turns about its own middle and needs nothing to turn against --
      // but a slide is friction against the ground and a slope to run down, so
      // Z's dive flies until the feet are actually on something, and _land2
      // starts the slide the moment they are.
      if (this._climb || this.grounded) {
        // Z: the feet are down, so the slide starts HERE if the touchdown itself
        // did not already start it. A gentle landing never goes through _land2 --
        // the ground probe glues down the last few centimetres instead of
        // crossing them -- and that landing is still owed its slide.
        if (d.slide && this.grounded && !this._climb && !this._slide) {
          this._slideCd = 0;
          this._startSlide({ force: true, kick: 1, yaw: d.yaw, side: d.side, blend: d.sb });
        }
        this._endDive();
      } else if (!d.slide && d.near >= 1 && this.vy < 0 && d.t > DIVE_FLY * 0.5) {
        // THE FLOOR IS HERE: over he goes. The roll picks up the dive's line, its
        // speed and the exact attitude the dive has the body in, so the two are
        // one motion -- fly, go over, roll out, run on.
        const yaw = d.yaw;
        const pitch = DIVE_LEAN + (DIVE_PITCH - DIVE_LEAN) * d.near;
        this._endDive();
        this._startRoll(m, { yaw, air: true, p0: pitch / (2 * Math.PI) });
      } else if (d.t > DIVE_MAX) {
        this._endDive();   // no floor coming (a cliff): fall, and land rolling
      }
    } else if (this._slide) {
      // A SLIDE IS NOT A MOVE, IT IS A DECELERATION. Nothing drives it: you go
      // down with the speed you had and the ground takes it back at mu*g --
      // plus or minus the hill, which on these streets is most of the story. A
      // slope steeper than the friction angle (mu ~ 32 deg) wins, and the slide
      // ACCELERATES down it, which is precisely what happens on a real hill.
      const sl = this._slide;
      sl.t += dt;
      sl.blend = THREE.MathUtils.clamp(
        sl.blend + (sl.ending ? -dt / SLIDE_UP : dt / SLIDE_IN), 0, 1);
      const slope = this._slopeAlong(-Math.sin(sl.yaw), -Math.cos(sl.yaw));
      const flat = 1 / Math.sqrt(1 + slope * slope);   // cos of the slope angle
      const decel = SLIDE_MU * SLIDE_G * flat + SLIDE_G * slope * flat;
      this.speed = Math.max(0, this.speed - decel * dt);
      this._strafing = true;
      this._moveYaw = sl.yaw;
      // Up again when it has run out, when the ground goes away under you, or
      // when it has simply gone on too long.
      if (!sl.ending && (this.speed < SLIDE_STOP || !this.grounded || sl.t > SLIDE_MAX)) {
        sl.ending = true;
      }
      if (sl.ending && sl.blend < 0.02) this._endSlide();
    } else {
      // Ground grips, air barely does: you keep the momentum you jumped with.
      const accel = this.grounded ? ACCEL_GROUND : ACCEL_AIR;
      const d2 = target - this.speed;
      this.speed += Math.sign(d2) * Math.min(Math.abs(d2), accel * dt);
      if (Math.abs(this.speed) < 0.01) this.speed = 0;
    }
    rig.obj.rotation.y = this.yaw;

    // Advance, sliding along walls instead of sticking to them.
    // (in cover the frame has already stood the hero where the wall says)
    if (this.speed !== 0 && !this._cover) {
      // Strafing — a weapon out, or Q/E — travel runs along the INPUT direction
      // (the sign is already in it: S aims _moveYaw back at the camera) while
      // the body keeps its facing. Otherwise travel is the body's own facing,
      // forwards or backwards, exactly as it always was.
      const strafing = this._strafing;
      const travelYaw = strafing ? (this._moveYaw ?? this.yaw) : this.yaw;
      const dir = new THREE.Vector3(-Math.sin(travelYaw), 0, -Math.cos(travelYaw))
        .multiplyScalar(strafing ? 1 : Math.sign(this.speed));
      const step = dir.clone().multiplyScalar(Math.abs(this.speed) * dt);
      const pl = this._planeTargets();
      const near = pl
        ? g.world.collidablesNear(this.pos.x, this.pos.z).concat(pl)
        : g.world.collidablesNear(this.pos.x, this.pos.z);
      // TWO heights, not one. A single chest-height probe walks straight into
      // waist-high furniture: the shooting range's firing counters top out at
      // 1.05 m and the ray at 1.1 m passed clean over them. The second height
      // sits just above STEP_UP, so kerbs and stairs are still stepped onto
      // rather than bumped into, and anything taller than that stops you.
      let hits = [];
      for (const h of [1.1, STEP_UP + 0.1]) {
        this._ray.set(this.pos.clone().add(new THREE.Vector3(0, h, 0)), dir);
        this._ray.far = 0.8;
        const got = this._ray.intersectObjects(near, true);
        if (got.length && (!hits.length || got[0].distance < hits[0].distance)) hits = got;
      }
      this._ray.far = Infinity;
      if (hits.length) {
        let n = hits[0].face
          ? hits[0].face.normal.clone().transformDirection(hits[0].object.matrixWorld)
          : null;
        if (n && n.dot(dir) > 0) n.negate();
        if (n && n.y < 0.45) {
          // A wall, and it stays a wall: running into something no longer hauls
          // the hero up it by itself. Climbing is a DECISION now, taken with
          // Space, the way GTA and Assassin's Creed both take it -- see
          // _tryClimb. So all that happens here is the slide along the face.
          n.y = 0;
          n.normalize();
          step.addScaledVector(n, -step.dot(n)); // keep only the along-wall part
        } else if (!n) {
          step.set(0, 0, 0);
        }
      }
      this.pos.add(step);
    }

    // Ground: walk up steps, fall off edges (roofs, walls, the funicular hill).
    // Pass the walker's OWN height — _groundAt defaults to the parked plane's,
    // which made walking under bridges and into underpasses snap to the deck.
    // While airborne the ground probe must NOT reach up and grab a ledge the
    // jump is still rising past -- that teleported the character onto roofs
    // mid-arc. On the ground it keeps the generous reach that walks up stairs.
    const reach = this.grounded && this.vy <= 0 ? STEP_UP : 0.05;
    const gY = g._groundAt(this.pos.x, this.pos.z, this.pos.y, reach, this._planeTargets());
    this._gY = gY;   // the dive reads this to know when the floor is coming up
    if (gY !== null) {
      const airborne = this.pos.y > gY + 0.25 || this.vy > 0;
      if (airborne) {
        if (this.grounded && this._fallFrom === null) this._fallFrom = this.pos.y;
        this.grounded = false;
        // Almost weightless for the first part of a dive -- that moment of
        // hanging in the air is the whole move.
        this.vy -= GRAVITY * (this._dive && this._dive.t < DIVE_FLY ? DIVE_G : 1) * dt;
        this.pos.y += this.vy * dt;
        if (this.pos.y <= gY) {           // touchdown
          this.pos.y = gY;
          this.vy = 0;
          this.grounded = true;
          if (this._fallFrom !== null) this._land2(this._fallFrom);
        } else if (this._fallFrom !== null && this.pos.y > this._fallFrom) {
          this._fallFrom = this.pos.y;    // still rising — measure from the apex
        }
      } else {
        // glued to the ground: follow it up stairs and down slopes. Coming
        // down off a jump lands HERE, not at the touchdown above -- the last
        // quarter metre of any ordinary fall is inside the glue band -- so
        // this is where the feet hit the ground for the ear.
        if (!this.grounded && this._fallFrom !== null) {
          this.game.footsteps?.land(this, Math.max(0, this._fallFrom - this.pos.y));
        }
        this.pos.y += (gY - this.pos.y) * Math.min(1, 18 * dt);
        this.vy = 0;
        this.grounded = true;
        this._fallFrom = null;
      }
    }
    g.boundary.clamp(this.pos, null);

    // Animation: idle ↔ run, run cycle speed tied to actual ground speed.
    // Backpedaling plays the run cycle in reverse — legs step backwards.
    const pace = Math.abs(this.speed);
    // Turning on the spot still moves the feet: a hero who spins in the Idle
    // pose slides like a statue on a turntable. The run cycle at a crawl reads
    // as the shuffle of steps a turn is actually made of.
    const turningInPlace = pace <= 0.4 && m.turn !== 0 && !armed && this.grounded && !this._roll && !this._cover;
    // WHICH WAY THE LEGS ARE ACTUALLY TRAVELLING, as an angle off the facing.
    // Strafing, the sign of a backpedal is already inside _moveYaw (S aims it
    // back at the camera), which is why this does NOT repeat the 180° that the
    // reversed run cycle needs below.
    const travel = this._wrapA((this._strafing
      ? this._moveYaw
      : this.yaw + (this.speed < 0 ? Math.PI : 0)) - this.yaw);
    // The walk set (characters.js WALK_SET): a real sidestep instead of a body
    // turned to face the way it is going, and a real walk on the bodies that
    // have only ever hopped. Cover keeps its own shuffle -- that pose is built
    // bone by bone and a clip underneath it is not an improvement.
    const walk = this.grounded && !this._roll && !this._slide && !this._cover && !this._air
      ? rig.walkClip?.(travel, pace) : null;
    if (this._roll || this._slide) {
      // Both of these are written on top of whatever is playing, and a run cycle
      // flailing its legs inside a ball -- or along the pavement -- is noise.
      rig.play('Idle');
    } else if (!this.grounded && this._air) {
      // NOTHING PEDALS ITS LEGS IN MID-AIR. The air pose owns them.
      rig.play('Idle');
    } else if (pace > 0.4 && walk) {
      rig.play(walk);
      rig.setTimeScale(walk, rig.walkRate(walk, pace));
    } else if (pace > 0.4) {
      rig.play('Run');
      // The clip's floor is 0.6 of its own tempo -- below that a run cycle stops
      // reading as legs at all. A stroll is slower than that floor by design, so
      // it gets its own: long, unhurried steps instead of a jog in slow motion.
      rig.setTimeScale('Run', (this._cover ? 1 : Math.sign(this.speed)) * THREE.MathUtils.clamp(
        pace / this.runSpeed, this._stroll ? GENT_CYCLE : 0.6, 1.7));
    } else if (turningInPlace) {
      rig.play('Run');
      rig.setTimeScale('Run', 0.45);
    } else {
      rig.play('Idle');
    }
    rig.update(dt);
    // bodies with no run cycle of their own hop, lean and squash instead —
    // every personage runs, whatever it was modeled as
    // Step sideways for real: hand the rig the angle between the way the legs
    // are travelling and the way the body faces. A backpedal is already covered
    // by the reversed run cycle, so take that 180° back out.
    let sa = 0, sb = armed ? (isRifle(this.held) ? 0.35 : 0.6) : 1;
    if (this._cover) {
      // the shuffle: the legs a little toward the way they are going, the back
      // still on the wall
      if (Math.abs(this.speed) > 0.3) {
        const t = this._cover.t, s = Math.sign(this.speed);
        sa = Math.atan2(-t.x * s, -t.z * s) - this.yaw;
        while (sa > Math.PI) sa -= 2 * Math.PI;
        while (sa < -Math.PI) sa += 2 * Math.PI;
      }
      sb = COVER_SHUFFLE;
    } else if (this._strafing && !this._roll && !this._slide && Math.abs(this.speed) > 0.3) {
      sa = this._moveYaw - this.yaw;
      if (this.speed < 0) sa += Math.PI;
      while (sa > Math.PI) sa -= 2 * Math.PI;
      while (sa < -Math.PI) sa += 2 * Math.PI;
    }
    // ...less with a rifle: a rifleman strafes with the torso on the target
    // and the legs doing the sidestep, and the chest's turn toward the aim
    // has less to make up.
    // NONE of it while a walk cycle is running: the clip steps sideways on its
    // own, and turning the body as well would take it past where it is going.
    rig.setStrafe(sa, dt, walk ? 0 : sb);
    // the rigless bodies hop instead of stepping, so a turn in place gets a
    // slow hop rather than nothing at all -- and a roll gets none: the hop
    // would bounce the ball off the ground it is rolling along
    // (and none of the hop either, when a real cycle is carrying the legs)
    rig.locomote(dt, this._roll || this._slide || (this._air && !this.grounded) || walk ? 0
      : turningInPlace ? 1.1 : this.speed, this.runSpeed);
    // With a weapon out, the right arm stops following the clip and holds the
    // GTA carry instead. This has to come AFTER rig.update() -- the mixer
    // overwrites every bone it owns, so posing before it has no effect at all.

    // CROUCH. The rig folds its own knees and drops its pelvis (characters.js
    // setCrouch) — the legs are real bones on nearly every body here. Only the
    // legless ones fall back to a squash, and only those need the weapon's
    // height given back, since a squash scales the hand bone it hangs off.
    // The climb owns `act` while it runs, so stay out of its way.
    let crouchWant = this.crouched ? 1 : 0;
    // shooting over a low wall the hero comes most of the way up out of the duck
    if (this._cover) crouchWant += (POP_CROUCH - crouchWant) * smooth01(this._cover.pop);
    this._crouchT += (crouchWant - this._crouchT) * Math.min(1, 10 * dt);
    if (!this._climb && !this._roll && !this._slide && !this._flip) {
      const squashed = rig.setCrouch(this._crouchT);
      const gun = this.held?.obj;
      if (gun) {
        gun.userData.baseScaleY ??= gun.scale.y;
        gun.scale.y = squashed
          ? gun.userData.baseScaleY / (1 - CROUCH_SQUASH * this._crouchT)
          : gun.userData.baseScaleY;
      }
    }

    // The tumble pose goes on LAST: the crouch and the climb write the same
    // `act` layer, and whoever writes it last is what you see.
    // COMING OUT OF A CLIMB: the last of the mantle, easing off.
    if (this._climbOut && !this._climb) {
      this._climbOut.t += dt;
      const w = 1 - Math.min(1, this._climbOut.t / CLIMB_OUT);
      if (w <= 0.02) this._climbOut = null;
      else rig.setMantlePose?.(w, 1, this._climbOut.lead);
    }
    if (this._flip) {
      this._flipPose(dt);
    } else if (this._roll) {
      this._rollPose();
      if (this._roll.done) this._endRoll();
    } else if (this._slide) {
      this._slidePose();
    } else if (this._dive) {
      this._divePose();
    } else {
      this._airPose(dt);
    }

    // ARMS LAST BUT ONE: the physics layer (CharacterRig.swingArms) -- the hand
    // lagging the shoulder, the whip at the end of every swing, the swing itself
    // growing with the pace, the elbows folding as it does, both arms thrown by a
    // hard turn or a hard stop, and the gentleman's hands clasped behind his
    // back. It runs AFTER the roll, the slide, the dive and the air pose because
    // each of those owns the arms outright while it lasts -- they say so with
    // blend 0, the springs keep running, and nothing of theirs is written over.
    //
    // What the body is doing TO the arms, measured here rather than guessed:
    // how fast it is turning and how hard it is changing pace. Both smoothed --
    // a raw per-frame delta is mostly noise, and noise in a shoulder is a twitch.
    let dy = this.yaw - (this._yawWas ?? this.yaw);
    this._yawWas = this.yaw;
    while (dy > Math.PI) dy -= 2 * Math.PI;
    while (dy < -Math.PI) dy += 2 * Math.PI;
    const ease = Math.min(1, 10 * dt);
    if (dt > 0) {
      this._turnV += (dy / dt - this._turnV) * ease;
      this._accV += ((this.speed - (this._speedWas ?? this.speed)) / dt - this._accV) * ease;
    }
    this._speedWas = this.speed;
    // THE TORSO TURNS TOWARD THE SHOT. Standing, the body itself turns onto the
    // crosshair and this is nothing; sliding or rolling it cannot, and a spine
    // turns toward a target off to the side before the arm has to cross the
    // chest to reach it -- which is how anyone shoots across themselves, and
    // the difference between a hand out in front of the body and a hand
    // inside it.
    // THE CHEST FACES THE AIM. The legs go where the strafe, the slide or a
    // flick the body has not caught up with put them; the spine takes up the
    // difference, exactly, up to what a spine can do -- and a rifle blades on
    // top of that. (It used to take a fraction of the sideways component,
    // which left a strafing body 46-67 degrees off the aim with the rifle laid
    // along the aim from a pocket on the body's flank: straight through it.)
    // HOW FAR BEHIND THE WALL the hero is, 0..1, eased: the cover hold, the
    // lean into the brick and the head turned to the corner all hang off it,
    // and it runs down over a beat when the wall is left so none of them cut.
    const cv = this._cover;
    const behind = cv ? cv.blend * (1 - smooth01(Math.max(cv.peek, cv.pop, cv.out))) : 0;
    this._coverW += (behind - this._coverW) * Math.min(1, dt / 0.14);
    if (!cv && this._coverW < 0.002) this._coverW = 0;
    const cw = this._coverW;
    const cside = cv ? (cv.corner || cv.camS) : 0;   // (camS eases, so the head swings, it does not snap)
    const L = (this._leanW ??= new THREE.Vector3()).set(0, 0, 0);
    if (cv) {
      L.addScaledVector(cv.t,
        cv.side * PEEK_BANK * smooth01(Math.min(1, cv.peek * 1.5))        // out round the corner
        + cv.corner * LOOK_BANK * smooth01(cv.look)                        // the look round it
        + cw * (COVER_SWAY * Math.sin(2 * Math.PI * 0.09 * cv.time)        // weight, foot to foot
          + (this.speed / COVER_SPEED) * COVER_MOVE_LEAN                   // into the shuffle
          + this._accV * COVER_ACC_LEAN));                                 // the rock of a stop
      L.addScaledVector(cv.n, -(COVER_LEAN * cw + (cv.slam > 0 ? -COVER_SLAM : 0)));   // back into the brick
    }
    const leanP = L.x * -Math.sin(this.yaw) + L.z * -Math.cos(this.yaw);      // along the facing: pitch
    const leanR = -(L.x * Math.cos(this.yaw) - L.z * Math.sin(this.yaw));     // to the right: roll (negative = right)
    let twistWant = 0;
    if (this.held && !this._stow) {
      const rawAim = g.pistol?.aimDir?.();
      const off = rawAim ? (rig.aimOffFacing?.(rawAim) ?? 0) : 0;
      const blade = isRifle(this.held) ? (this._rifleSide() === 'L' ? -RIFLE_BLADE : RIFLE_BLADE) : 0;
      twistWant = THREE.MathUtils.clamp(off + blade, -AIM_TWIST_MAX, AIM_TWIST_MAX);
    }
    // BEHIND A WALL the chest does not chase the crosshair: the camera is
    // looking along the wall, the weapon is up by the shoulder, and a torso
    // turned toward a shot nobody is taking carried the gun arm round behind
    // the body and into the brick.
    twistWant *= 1 - cw;
    this._aimTwist += (twistWant - this._aimTwist) * Math.min(1, 14 * dt);
    const bodyOpts = {
      speed: this.speed,
      runSpeed: this.runSpeed,
      turn: this._turnV,
      accel: this._accV,
      stroll: this._strollT,
      crouch: this._crouchT,
      blend: this._roll || this._slide || this._dive || this._climb
        || !this.grounded || cw > 0.5 ? 0 : 1,
      // in cover: pressed back into the wall, head and shoulders round to the
      // corner, and the lean out round it while peeking
      // ...plus the hit as the back meets the wall, a breath, a slow shift of
      // weight, the lean into the shuffle and the rock of its stop, the lean
      // out round the corner -- the small motions a body at rest against a
      // wall actually has. All of them are WORLD directions (into the wall,
      // along it, toward the corner) projected onto the body's own axes here,
      // because the body turns in cover: a "roll to the right" leant the hero
      // into the brick once he had turned to face round the corner.
      lean: leanP,
      bank: leanR,
      chestPitch: cv ? -COVER_BREATH * Math.sin(2 * Math.PI * 0.25 * cv.time) * cw : 0,
      // the head looks where the camera looks (round toward the corner, as it happens)
      headYaw: cw * THREE.MathUtils.clamp(this._wrapA(this.camYaw - this.yaw), -COVER_HEAD_MAX, COVER_HEAD_MAX),
      chestYaw: -cside * COVER_CHEST * cw * 0.8,
      gunTwist: this._gunReachEnv() * GUN_TWIST + this._aimTwist,
      // (reaching over the shoulder for a slung rifle the shoulder goes UP)
      gunDip: this._gunReachEnv() * GUN_DIP * ((this._draw ?? this._stow)?.rifle ? -1.2 : 1),
      gunPitch: this.held && isRifle(this.held) ? (g.pistol?.rock ?? 0) : 0,
    };
    // THE EMPTY HANDS. Open and relaxed standing still, loose fists by a jog --
    // nobody sprints with their palms flat open, and a hand that never changes is
    // the first thing that reads as a puppet. A hand that is holding something
    // keeps the grip the weapon gave it.
    const gripWant = THREE.MathUtils.clamp(
      (Math.abs(this.speed) / this.runSpeed) * 1.8, 0, 1) * (this._stroll ? 0.3 : 1);
    this._gripT += (gripWant - this._gripT) * Math.min(1, 7 * dt);
    rig.setGrip?.(this._gripT, this.held || this._stow || this._draw ? this._handBone : null);
    rig.swingArms?.(dt, bodyOpts);
    // ...and the body those arms hang off: the lean, the bank into a turn, the
    // shoulders counter-rotating against the hips, the head held level.
    rig.swingBody?.(dt, bodyOpts);

    // THE GUN GOES LAST, and it goes last on purpose. The tumble and the slide
    // both fold the arms (CharacterRig), and whoever writes a bone last owns it
    // -- so the weapon arm is solved AFTER them and takes its bones back. Running
    // it here also means the solve sees the body's final orientation for this
    // frame, tumble and recline included, instead of last frame's.
    //
    // The arms work to the AIM LINE in every mode: standing, running, mid-roll,
    // mid-slide. Aimed, BOTH hands go to the weapon and the barrel goes exactly
    // down the crosshair; from the hip the gun arm keeps its low-ready dip. Only
    // the reach is limited (see _moveAim) -- never the direction.
    // The weapon on its way to the belt or out of it -- the arm reaching with
    // it -- and the rock as it beds in.
    if (this._stow || this._draw) this._moveArm(dt, rig);
    if (this._settle) this._updateSettle(dt);

    if (this.held && this._handBone) {
      const aiming = !!g.pistol?.aiming;
      const raw = g.pistol?.aimDir?.();
      const aim = this._roll || this._slide || this._dive ? this._moveAim(raw) : raw;
      // The hold is the game's own: applyAimPose for the two-handed sight
      // picture, applyPistolPose for the one-handed carry. Both are solved to
      // the aim line on whatever body this is, and they read right on all 39
      // of them -- the Sketchfab clip laid over the top of them did not.
      const rifle = isRifle(this.held);
      const side = this._rifleSide();
      // the side was switched in the settings with the rifle up: it changes hands
      if (rifle && side !== this._heldSide) {
        const bone = this._gunHand(this.held);
        if (bone && bone !== this._handBone) {
          attachToHand(bone, this.held.obj, this.held, rig.obj);
          this._handBone = bone;
        }
        this._heldSide = side;
      }
      const rReload = rifle && (g.pistol?.reloadBlend ?? 0) > 0.001;
      if (rifle) {
        poses.applyRifleHold(rig, rig.obj, this._handBone, aim, this.held,
          rReload ? 'reload' : aiming ? 'aim' : 'carry', side);
      }
      else if (aiming) poses.applyAimPose(rig, rig.obj, this._handBone, aim);
      else poses.applyPistolPose(rig, rig.obj, this._handBone, aim);
      // THROUGH A ROLL THE GUN COMES IN TO THE CHEST. Solving the arms to the
      // aim line while the body tumbles -- even clamped to the chest's cone --
      // locks both elbows and swings the weapon round on a stiff arm as the
      // chest turns over: a T-pose with a pistol. Max Payne, GTA and Wick all
      // do the same thing here: the pistol is pulled in, both hands on it,
      // muzzle forward and up, and the aim is picked back up on the feet.
      // Weighted by the roll's own curl, so it tucks as the body does.
      const tuck = this._roll && !rifle ? (this._roll.tuck ?? 1) : 0;
      let tuckAim = null;
      if (tuck > 0.001) {
        const q = poses.applyTuckPose(rig, rig.obj, this._handBone, _tq);
        if (q) {
          for (let i = 0; i < q.bones.length; i++) {
            q.bones[i].quaternion.slerp(q.from[i], 1 - tuck);
            q.bones[i].updateWorldMatrix(false, true);
          }
          tuckAim = _tv.copy(aim && aim.lengthSq() > 1e-8 ? aim : q.fwd).lerp(q.fwd, tuck).normalize();
        }
      }
      // THE RELOAD IS THE EXCEPTION, and the only one. Swapping a magazine is
      // a move, not a pose: the gun hand comes down and turns the weapon over,
      // the other hand leaves the grip, goes to the belt and comes back. There
      // is nothing in a pose solver that can invent that, so for the second it
      // lasts the real keyframes are blended over the hold and taken off again
      // -- see src/gunanim.js.
      // ...AND THE SHOT: the clip's recoil for the half second after the
      // trigger, both hands down the sights, the gun hand alone from the hip.
      // (The draw and the holster are not clip moves any more -- see _moveArm.)
      const reloading = (g.pistol?.reloadBlend ?? 0) > 0.001;
      // (the clip is a right-handed man's: over a rifle on the LEFT shoulder it
      // would put the magazine change on the wrong arm, so it stays off there --
      // the magazine still moves on the gun, the hands hold)
      // (and a rifle takes the clip's shot jolt at half strength: the
      // Mixamo man's hands jump back 7 cm on a shot, which on a stock already
      // against the flank is 7 cm INTO the flank -- the body rocks instead)
      // A RIFLE'S MAGAZINE CHANGE IS NOT THE CLIP'S ANY MORE. It is built out
      // of points on the weapon itself (weapons.js applyRifleHold 'reload' +
      // applyRifleReloadHand): the hold brings the rifle out in front of the
      // chest and rolls the magazine well up, and the support hand walks the
      // handguard -> well -> pouch -> well -> handguard. The clip only keeps
      // the SHOT here, at the half strength a stock in the shoulder allows.
      const rw = rifle && side === 'L' ? 0
        : rifle ? (g.pistol?.fireBlend ?? 0) * 0.45
          : Math.max(g.pistol?.reloadBlend ?? 0, g.pistol?.fireBlend ?? 0);
      let gunDir = null;
      if (rw > 0.001 && !this._roll && !this._slide && !this._dive) {
        // a shot is a jolt ADDED to the hold (relative to the clip's own
        // idle); a reload is the clip's pose outright
        const ref = reloading ? null : (aiming ? g.gunAnim?.refAim : g.gunAnim?.refHip) ?? null;
        // ...and the magazine change is laid out in front of the CHEST, not
        // down the crosshair. Standing in the open the two are the same thing
        // -- the body turns to the aim -- but IN COVER the hero faces out of
        // the wall while the camera looks along it, and the clip quoted against
        // that put the whole reload across the body: 45-74% of the gun hand
        // inside the torso (tools/_probe_cornerarm.mjs).
        const frame = reloading ? (chestForward(rig, rig.obj, this._cvFwd ??= new THREE.Vector3()) ?? aim) : aim;
        // ON A RIFLE THE GUN ARM DOES NOT FOLLOW THE CLIP. The clip is a
        // first-person viewmodel and its magazine change brings the weapon
        // BACK TOWARD THE CAMERA -- which on a visible body is back into the
        // chest: measured at a third to three fifths of the AKM's length
        // inside the torso shell, the stock at 0.44 of the body's own radius,
        // against 0% and 1.00 on the carry (tools/_probe_akreload.mjs). A
        // pistol survives that because it is 20 cm long and hangs off the
        // hand; an 88 cm rifle sweeps the torso with it. Taking the delta off
        // the clip's own idle instead was no better -- the swing is in the
        // keyframes, not in the offset.
        //
        // So the clip gets THE SUPPORT ARM ONLY, which is the arm that does
        // the work anyway: off the handguard, down to the belt, back up with
        // a magazine. applyRifleHold keeps the gun hand exactly where a rifle
        // is held, and applyRifleSupport lets its hand go as the blend comes
        // in (1 - rw, below). The result is the real move on the real hold.
        const only = reloading && rifle ? (side === 'L' ? 'r' : 'l')
          : (reloading || aiming || rifle ? null : 'r');
        gunDir = blendReloadPose(rig, rig.obj, this._handBone, frame,
          g.gunAnim?.dirs, rw, g.gunAnim, only, ref);
      }
      // THE WEAPON FOLLOWS THE CLIP TOO while the reload is showing. seatWeapon
      // normally lays the barrel on the crosshair with the slide up, every
      // frame -- which is right for carrying and aiming and wrong for this: the
      // clip turns the gun over in the hand to get at the magazine well, and a
      // pistol held stubbornly level through that is the one thing that gives
      // away that the animation is not being followed. Both the heading and the
      // roll are eased in and out with the pose, so nothing snaps.
      let seatAim = tuckAim ?? aim;
      let seatUp = null;
      // ...and the beat after a draw: the muzzle came round past the line and
      // the wrist takes a third of a second to damp the rock out of it
      let dipExtra = 0;
      if (this._presentT >= 0) {
        this._presentT += dt;
        if (this._presentT > DRAW_SETTLE) this._presentT = -1;
        else dipExtra = -DRAW_SETTLE_AMP * Math.sin(2 * Math.PI * this._presentT / 0.24)
          * Math.exp(-this._presentT / 0.09);
      }
      const dipHold = aiming ? 0 : rifle ? RIFLE_DIP : MUZZLE_DIP;
      let seatDip = dipHold + dipExtra;
      if (gunDir) {
        // ...and the weapon's own heading goes with it -- ALL of it for a
        // pistol, which the clip turns over in the hand to get at the
        // magazine well, and a third of it for a rifle, which in a real
        // magazine change tilts the receiver over and no more. Letting the
        // clip have the rifle's heading outright is the other way the stock
        // ends up in the ribs.
        seatAim = this._seatF.copy(aim && aim.lengthSq() > 1e-8 ? aim : gunDir.f)
          .lerp(gunDir.f, rw).normalize();
        seatUp = this._seatU.set(0, 1, 0).lerp(gunDir.u, rw).normalize();
        seatDip = dipHold * (1 - rw) + dipExtra;
      }
      // ...and through a rifle's magazine change the barrel goes on the line
      // the HOLD just built -- dipped, across, rolled -- not on the crosshair.
      if (rReload && rig._rifleLine) {
        const rb = g.pistol?.reloadBlend ?? 0;
        seatAim = this._seatF.copy(aim && aim.lengthSq() > 1e-8 ? aim : rig._rifleLine)
          .lerp(rig._rifleLine, rb).normalize();
        seatUp = this._seatU.set(0, 1, 0).lerp(rig._rifleUp, rb).normalize();
        seatDip = dipHold * (1 - rb) + dipExtra;
      }
      poses.seatWeapon(this._handBone, this.held.obj, this.held, rig.obj, seatAim,
        seatDip, seatUp);
      // the support hand onto the handguard of the rifle now that it is seated
      // (the clip has the left arm while a reload or a shot is showing)
      if (rifle && rReload) {
        const p = g.pistol;
        const u = p._reloadFrom > 0 ? 1 - p._reload / p._reloadFrom : 0;
        poses.applyRifleReloadHand(rig, rig.obj, this.held.obj, this.held, u,
          p.reloadBlend, aim, side, p._reloadName === 'reloadEmpty');
      } else if (rifle) {
        poses.applyRifleSupport(rig, rig.obj, this.held.obj, this.held, 1 - rw, aim, side);
      }
      // ...and BEHIND THE WALL all of that gives way to the cover hold -- except
      // the magazine change, which is a move the hold has no say in
      const cwHold = cw * (1 - (g.pistol?.reloadBlend ?? 0));
      if (cwHold > 0.001) this._coverHold(rig, cwHold);
    } else if (cw > 0.001 || this._coverArmsOn) {
      this._coverArmsBare(rig, cw);
    }

    // the holster sockets follow the strap the skin has bent
    this._rideSockets();

    this._chaseCamera(dt, armed);

    // HUD: ground speed in km/h, altitude 0 — you're the altitude now.
    g.ui.speed.textContent = Math.round(pace * 3.6);
    g.ui.alt.textContent = 0;
    const f = new THREE.Vector3(-Math.sin(this.yaw), 0, -Math.cos(this.yaw));
    const hRad = Math.atan2(-f.x, f.z);
    let h = hRad * THREE.MathUtils.RAD2DEG;
    if (h < 0) h += 360;
    if (g.ui.compassDial) {
      g.ui.compassDial.style.transform = `rotate(${-h}deg)`;
      g.ui.compassDeg.textContent = `${Math.round(h)}°`;
    }
    g.mapui.update(this.pos.x, this.pos.z, hRad);

    // Standing next to the cockpit: prompt to climb back in.
    const nearPlane =
      this.pos.distanceTo(g.plane.position) < Math.max(BOARD_DIST, g.halfSpan + 3);
    if (nearPlane) g._setHint('Enter — თვითმფრინავში დაბრუნება ✈');
  }

  dispose() {
    // parent, not the scene: while the hero is flying the body hangs off the
    // aircraft, and scene.remove would leave it there
    if (this.rig) this.rig.obj.parent?.remove(this.rig.obj);
  }
}

// ---- live tuning -----------------------------------------------------------
// The whole move set lives in this file, and every number in it wants trying in
// the actual city rather than described. So an update patches the OnFoot the game
// is already running: the hero keeps his position, his weapon, his belt and even
// the roll he is in the middle of, and the next frame uses the new code.
if (import.meta.hot) {
  import.meta.hot.accept((next) => {
    if (!next || !patchClass(OnFoot, next.OnFoot, 'foot.js')) import.meta.hot.invalidate();
  });
}
