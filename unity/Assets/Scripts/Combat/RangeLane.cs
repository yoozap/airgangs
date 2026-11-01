using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/rangelane.js — the target carrier on its overhead rail, and
    /// the F/G control box that drives it. Cited `// src/rangelane.js:NNN`
    /// throughout.
    ///
    /// ================================ NOT PORTED ================================
    /// The JS's `#lane-ctl` DOM control card (rangelane.js:126-196) is a /UI/
    /// concern (hard rule) — `Update` below returns a `LaneHud` snapshot
    /// (distance, direction, which lane, whether to show) for a UI script to
    /// render however it likes, the same decision PaperTargets.cs made for its
    /// own score card. The carrier's own swing (`_sway`, rangelane.js:229-261)
    /// writes `lane.Th`/`Thv` here exactly as the JS writes `lane.th` — the JS
    /// then ALSO pushes that onto `p.userData.sway` for src/airport.js's shared
    /// updater (out of this port's scope, City's territory) to read; this file
    /// does the same half of the job (compute Th) and leaves the "apply it to
    /// the paper's own rotation" half for whichever City-side script owns the
    /// range's baked geometry — exactly the JS's own division of labour.
    ///
    /// ================================ HANDEDNESS =============================
    /// Every direction here is a Transform's own basis column read in world
    /// space (`carrier.right`/`carrier.forward` for rangelane.js:77's
    /// `setFromMatrixColumn(carrier.matrixWorld, col)`) or a plain dot/position
    /// difference — no cross product anywhere in this file, so nothing needs
    /// the operand swap (same `AxisWorld`-style reasoning as PaperTargets.cs's
    /// header, and rangelane.js itself has no cross product to begin with).
    /// </summary>
    public sealed class RangeLanes
    {
        const float Reach = 3f;         // rangelane.js:19 — how close before the controls appear
        const float Speed = 3.4f;       // rangelane.js:23 m/s
        const float Accel = 7.5f;       // rangelane.js:24 m/s^2
        const float NearStop = 0f;      // rangelane.js:25
        const float EndPad = 0.35f;     // rangelane.js:26
        const float Tap = 4f;           // rangelane.js:27 m
        const float Rate = 0.65f;       // rangelane.js:28 s — tap vs. hold

        const float G = 9.81f;
        const float PaperDrop = 0.30f;      // rangelane.js:39 m
        const float PaperZeta = 0.5f;       // rangelane.js:44
        const float PaperDrag = 0.15f;      // rangelane.js:47
        const float PaperAMax = 26f;        // rangelane.js:48 m/s^2
        const float PaperMax = 0.85f;       // rangelane.js:52 rad
        const float PaperFlut = 0.035f;     // rangelane.js:53 rad

        public sealed class Lane
        {
            public Transform Obj;
            public Vector3 Rail, Home;
            public string Axis;
            public float At;          // metres travelled from home
            public int Away;          // sign: travel-along-rail -> the unit's own +Z
            public List<Transform> Papers = new List<Transform>();
            public float Th, Thv, Vz, Ft;
            public float Min, Max, RangeM;
            public Vector3 Stand;
            public float V; // current drive speed, m/s
        }

        public readonly List<Lane> Lanes = new List<Lane>();
        public Lane Near { get; private set; }

        int _dir;      // -1 in, +1 out, 0 stopped
        int _held;
        float _since;
        float? _goal;
        RailMotorVoice _motor;

        public RangeLanes() { }

        // =====================================================================
        // src/rangelane.js:72-118 adopt — called once per carrier as the range
        // is built. `firingZ`/`endZ` are in the range UNIT's own local frame
        // (its parent), since two range units can sit anywhere in the world.
        // =====================================================================
        public Lane Adopt(Transform carrier, float firingZ = 0f, float? endZ = null, string axis = "z")
        {
            if (carrier == null) return null;
            foreach (var l in Lanes) if (l.Obj == carrier) return null;

            int col = axis == "x" ? 0 : 2;
            Vector3 rail = (col == 0 ? carrier.right : carrier.forward).normalized;
            Vector3 home = carrier.position;

            Bounds? b = WorldBoundsOf(carrier);
            Vector3 spot = b.HasValue ? b.Value.center : home;

            Vector3 localSpot = carrier.parent != null ? carrier.parent.InverseTransformPoint(spot) : spot;
            float local = axis == "x" ? localSpot.x : localSpot.z;
            // Mathf.Sign(0) is +1 in Unity (unlike Math.sign(0)===0 in JS), which
            // is exactly rangelane.js:92's `Math.sign(...) || 1` fallback — no
            // extra `|| 1` needed here.
            int away = (int)Mathf.Sign(local - firingZ);
            rail *= away;

            float parked = Mathf.Abs(local - firingZ);
            float depth = endZ.HasValue ? Mathf.Abs(endZ.Value - firingZ) : parked;
            float back = Mathf.Max(0f, parked - NearStop);

            var papers = new List<Transform>();
            foreach (var t in carrier.GetComponentsInChildren<Transform>(true))
                if (t.name == "range_paper") papers.Add(t);

            var lane = new Lane
            {
                Obj = carrier, Rail = rail, Home = home, Axis = axis, At = 0f,
                Away = away, Papers = papers,
                Min = -back, Max = Mathf.Max(0f, depth - EndPad - parked), RangeM = parked,
                Stand = spot + rail * -parked,
            };
            Lanes.Add(lane);
            return lane;
        }

        static Bounds? WorldBoundsOf(Transform root)
        {
            Bounds? b = null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (b.HasValue) { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
                else b = r.bounds;
            }
            return b;
        }

        /// <summary>rangelane.js:120-123 — firing line to target, in metres.</summary>
        public float Distance(Lane lane) => Mathf.Max(0f, lane.RangeM + lane.At);

        // =====================================================================
        // rangelane.js:206-215 key / releaseAll — F sends it out, G brings it
        // in, while standing at a lane (works even while the aim HUD is hiding
        // the control card — rangelane.js's own comment on why these are on
        // the keyboard, not just the card).
        // =====================================================================
        public bool Key(KeyCode code, bool down)
        {
            int way = code == KeyCode.F ? 1 : code == KeyCode.G ? -1 : 0;
            if (way == 0 || Near == null) return false;
            if (down) { if (_held != way) Press(way); }
            else Release(way);
            return true;
        }

        public void ReleaseAll() { if (_held != 0) Release(_held); }

        // =====================================================================
        // rangelane.js:229-261 _sway — one damped pendulum per carrier.
        // =====================================================================
        void Sway(Lane lane, float dt)
        {
            if (lane.Papers.Count == 0) return;
            float vz = lane.V * lane.Away;
            float az = Mathf.Clamp((vz - lane.Vz) / Mathf.Max(1e-4f, dt), -PaperAMax, PaperAMax);
            lane.Vz = vz;
            float aEff = az + PaperDrag * vz * Mathf.Abs(vz);
            float want = Mathf.Atan(aEff / G);
            if (Mathf.Abs(vz) > 0.1f)
            {
                lane.Ft += dt;
                float fAmt = Mathf.Min(1f, vz * vz / 6f);
                want += PaperFlut * fAmt * (Mathf.Sin(lane.Ft * 17.3f) * 0.6f + Mathf.Sin(lane.Ft * 27.7f) * 0.4f);
            }
            float w2 = G / PaperDrop;
            float w = Mathf.Sqrt(w2);
            lane.Thv += (-w2 * (lane.Th - want) - 2f * PaperZeta * w * lane.Thv) * dt;
            lane.Th = Mathf.Clamp(lane.Th + lane.Thv * dt, -PaperMax, PaperMax);
            if (Mathf.Abs(lane.Th) >= PaperMax) lane.Thv = 0f;
            if (vz == 0f && Mathf.Abs(lane.Th) < 2e-4f && Mathf.Abs(lane.Thv) < 2e-3f) { lane.Th = 0f; lane.Thv = 0f; }
        }

        /// <summary>rangelane.js:269-291 hit — a round goes through the sheet.
        /// `hitObject` is whatever the raycast actually hit; `at` the world
        /// point, used to weight the pendulum's leverage by how low on the
        /// sheet the round landed.</summary>
        public bool Hit(Transform hitObject, Vector3? at, float power = 1f)
        {
            foreach (var lane in Lanes)
            {
                if (lane.Papers.Count == 0) continue;
                foreach (var p in lane.Papers)
                {
                    if (hitObject != p && (hitObject == null || !hitObject.IsChildOf(p))) continue;
                    float arm = 1f;
                    if (at.HasValue)
                    {
                        float drop = Mathf.Abs(p.position.y - at.Value.y);
                        arm = Mathf.Clamp(drop / PaperDrop, 0.15f, 1.8f);
                    }
                    lane.Thv += -lane.Away * 2.6f * power * arm;
                    lane.Ft += 0.37f;
                    return true;
                }
            }
            return false;
        }

        // ---- driving it (rangelane.js:293-337) ---------------------------------
        void Press(int way)
        {
            if (Near == null) return;
            _held = way; _since = 0f; _dir = way; _goal = null;
            Sound(true);
        }

        void Release(int way)
        {
            if (_held != way) return;
            _held = 0;
            if (_since < Rate && Near != null)
                _goal = Mathf.Clamp(Near.At + way * Tap, Near.Min, Near.Max);
            else Stop();
        }

        void Stop(bool clunk = false)
        {
            float d = SoundDist();
            _dir = 0; _goal = null;
            Sound(false);
            if (clunk) FxAudio.RailClunk(_volume, d);
        }

        float SoundDist() => Near != null ? Mathf.Max(1f, Distance(Near)) : 2.5f;

        float _volume = 0.7f;
        public void SetVolume(float v) => _volume = v;

        void Sound(bool on)
        {
            if (on)
            {
                if (_motor == null) _motor = new RailMotorVoice();
                if (!_motor.Active) _motor.Start(_volume, SoundDist());
            }
            else
            {
                _motor?.Stop(); // RailMotorVoice.Stop() winds down over ~0.3s — Tick(dt) below drives it
            }
        }

        // =====================================================================
        // rangelane.js:342-411 update — every lane's sheet sways regardless of
        // which one is being driven (walk away mid-run and the target still
        // finishes swinging); only the nearest lane within REACH gets driven.
        // =====================================================================
        public struct LaneHud { public bool Show; public float DistM; public int Dir; public Lane Lane; }

        /// <summary>rangelane.js:407-408 — the hint line's three Georgian strings,
        /// by direction. Pure text selection (not DOM), same convention as
        /// Combat/GunFx.cs's GunToast.Format — kept out of LaneHud itself so a UI
        /// script can call it only when it actually needs the string.</summary>
        public static string HintText(int dir) =>
            dir > 0 ? "მიდის შორს…" : dir < 0 ? "ბრუნდება…" : "F შორს · G ახლოს";

        public LaneHud Update(float dt, Vector3? actorXZ, bool aiming = false)
        {
            // RailMotorVoice has no Unity automation for its spin-down ramp (its
            // own header) — this is the once-a-frame Tick() that class needs.
            if (_motor != null) { _motor.Tick(dt); if (!_motor.Active) _motor = null; }
            foreach (var l in Lanes) Sway(l, dt);
            if (Lanes.Count == 0 || !actorXZ.HasValue) return default;

            Lane best = null;
            float bestD = Reach * Reach;
            foreach (var l in Lanes)
            {
                float dx = l.Stand.x - actorXZ.Value.x;
                float dz = l.Stand.z - actorXZ.Value.z;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = l; }
            }
            if (best != Near)
            {
                if (_dir != 0) Stop();
                Near = best;
                _held = 0;
            }
            if (best == null) return default;

            if (_held != 0) _since += dt;
            int dir = _dir;
            // rangelane.js:380 `Math.sign(x) || 0` — JS's Math.sign(0) is
            // already 0 (the `||0` is stylistic); Unity's Mathf.Sign(0) is +1,
            // so the zero case needs an explicit guard here or the carrier
            // never settles to "arrived" when _goal is hit exactly.
            if (_goal.HasValue)
            {
                float delta = _goal.Value - best.At;
                dir = Mathf.Approximately(delta, 0f) ? 0 : (int)Mathf.Sign(delta);
            }
            if (dir != 0)
            {
                best.V = Mathf.Clamp(best.V + dir * Accel * dt, -Speed, Speed);
                float step = best.V * dt;
                if (_goal.HasValue)
                {
                    float left = _goal.Value - best.At;
                    if (Mathf.Abs(step) > Mathf.Abs(left)) step = left;
                }
                float want = Mathf.Clamp(best.At + step, best.Min, best.Max);
                bool hitEnd = !Mathf.Approximately(want, best.At + step);
                best.At = want;
                best.Obj.position = best.Home + best.Rail * best.At;
                _motor?.Aim(SoundDist());
                if (hitEnd) { best.V = 0f; Stop(true); }
                else if (_goal.HasValue && Mathf.Abs(_goal.Value - best.At) < 1e-4f) { best.V = 0f; Stop(true); }
            }
            else if (best.V != 0f) best.V = 0f;

            bool show = !aiming;
            return new LaneHud { Show = show, DistM = Distance(best), Dir = dir, Lane = best };
        }

        // src/rangelane.js:413-418 dispose — stop the motor and drop every
        // carrier. (The DOM control-card removal, rangelane.js:415-416, has
        // no analogue: this class never built one.)
        public void Dispose()
        {
            Sound(false);
            Lanes.Clear();
        }
    }
}
