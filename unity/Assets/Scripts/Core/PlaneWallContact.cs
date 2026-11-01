using System;
using System.Collections.Generic;
using TbilisiFly.Combat;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.Core
{
    // =========================================================================
    // PlaneWallContact.cs — port of src/game.js's building/wall/wingtip contact
    // detection, the single biggest remaining slice of the file:
    //   _buildingSmash   game.js:1852-1882
    //   _surfaceUnder    game.js:1883-1895
    //   _touchWall       game.js:1896-2016
    //   _checkCollision  game.js:2017-2177
    //
    // WIRING NOTE: _checkCollision runs on the SAME 0.15 s timer
    // GameLoop.cs's StageFlight already drives CheckStructuralStress from
    // (game.js:2409-2412 — the two calls are adjacent in the JS's own _tick).
    // CheckCollision() below should be called from that same block, right
    // alongside CheckStructuralStress(_collisionTimer).
    //
    // ============================ WHAT THIS CLOSES ============================
    // Flight/PlanePhysics.cs's Ram() method explicitly defers "what it does to
    // US" (src/planephys.js:490-522) to its caller — this file IS that caller
    // (the JS's _touchWall/_checkCollision are the ONLY call sites of
    // `this.planephys.ram()` in the whole game). ApplyRamResult() below is a
    // line-for-line port of planephys.js:490-522, read directly from source
    // for this port (not guessed from the Unity-side RamResult struct alone).
    //
    // ============================== DESIGN ==============================
    // Plain C#, raycasting through IPlaneWallWorld rather than Unity's static
    // Physics class directly, so this file stays engine-call-site-agnostic and
    // unit-probable with a fake world. UNITY-NATIVE SIMPLIFICATION (matching
    // PlanePhysics.cs's own IPlanePhysicsWorld.RaycastWorld): no spatial hash —
    // RaycastAllSorted is just Physics.RaycastAll + a distance sort, since
    // Unity's broadphase already does what src/citymap.js's `collidablesNear`
    // existed to approximate.
    //
    // ============================ HANDEDNESS ============================
    // rotateY calls (yaw) negate; rotateZ calls (roll) do not — FlightModel.cs's
    // rule. The `Quaternion.FromToRotation(dir, slide) * plane.rotation`
    // "steer along the facade" step (game.js:1999-2003's premultiply) needs NO
    // sign fix: both `dir` and `slide` are already-correct WORLD-space vectors
    // in Unity's own (mirrored) frame, and rotating one world vector onto
    // another is a frame-consistent operation — see FlightModel.cs's header
    // for why only JS-CONVENTION axis angles (not world-vector-to-world-vector
    // rotations) need the negate treatment.
    // =========================================================================

    /// <summary>What this class needs from the scene. `RaycastAllSorted`
    /// mirrors `raycaster.intersectObjects(list, true)` (nearest-first; three's
    /// `firstHitOnly` only dedupes hits within one BVH mesh, not across the
    /// whole object list — game.js:1915-1918 still filters a multi-hit array).</summary>
    public interface IPlaneWallWorld
    {
        RaycastHit[] RaycastAllSorted(Vector3 origin, Vector3 dir, float maxDist);
        bool IsTerrain(Collider c);
        /// <summary>True for any collider belonging to a registered plane
        /// (game.js:2061 `hit.object?.userData?.planeRec`) — Unity analogue:
        /// `collider.GetComponentInParent&lt;PlaneDamageRecord&gt;() != null` or
        /// equivalent, whatever tags a plane's shell/skin colliders.</summary>
        bool IsPlaneCollider(Collider c);
        float? BoreFloorAt(float x, float z, float refY);
        /// <summary>Combat's building-damage surface (Combat/CombatWorldHooks.cs,
        /// already implemented by Core/CombatWorldAdapter.cs) — forwarded
        /// here, not re-implemented.</summary>
        IDamageWorld Damage { get; }
        void BurstGlass(float[] blown, float power, float count); // src/glass.js burstList — flat, 5 per pane
        void SpawnEmber(Vector3 at);
        void SpawnSmoke(Vector3 at, float size, float dark, float dur, float vy, float spread);
        void DustBurst(Vector3 at, int count, float radius);
        float CameraDistanceTo(Vector3 point);
        float SoundVolume();
        void HitConcreteSound(float dist, float vol);
        void CollapseRumbleSound(float dist, float vol, bool destroyed);
        void ShowBuildingToast(DamageResult res); // src/guns.js _showToast
    }

    public sealed class PlaneWallContact
    {
        readonly IPlaneWallWorld _world;
        readonly PlaneGroundProbe _ground;
        readonly PlaneHullDamage _hull;
        readonly PlaneGroundRoll _groundRoll;

        public PlaneWallContact(IPlaneWallWorld world, PlaneGroundProbe ground, PlaneHullDamage hull, PlaneGroundRoll groundRoll)
        {
            _world = world; _ground = ground; _hull = hull; _groundRoll = groundRoll;
        }

        public float HalfSpan = 4.5f; // game.js:96 — caller refreshes per airframe

        float _hitCooldown;   // game.js:120 this._hitCooldown
        public float WingHitCooldown; // game.js:119 this._wingHitCooldown

        // ---- promoted literals (tools/_probe_gamerest.mjs / GameRestProbe.cs) ----
        internal const float TouchWallMinSpeed = 3f;        // game.js:1898 `if (sp < 3) return false`
        /// <summary>game.js:1946 (ceiling) and game.js:1974 (facade) — the SAME
        /// `this._hitCooldown = 0.4` literal at both call sites, one field.</summary>
        internal const float HitCooldownDuration = 0.4f;
        internal const float BuildingSmashHeadOnSpeed = 0.7f; // game.js:1962 `headOn > 0.7 && sp > 34`
        internal const float BuildingSmashTotalMult = 20f;    // game.js:1968 `Math.round(sp * 20)`
        internal const float RamSpecUsGate = 60f;             // planephys.js:506 `specUs > 60`
        internal const float WingHitCooldownTick = 0.15f;     // game.js:2076 `this._wingHitCooldown -= 0.15`
        internal const float WingHitMinSpeed = 8f;            // game.js:2077 `Math.abs(this.speed) > 8`
        internal const float WingWallShearSpeed = 55f;        // game.js:2106 `Math.abs(this.speed) > 55`
        internal const float WingWallDamageWingBase = 200f;   // game.js:2107 `200 + Math.abs(this.speed) * 2.5`
        internal const float WingDragMinSpeed = 12f;          // game.js:2124 `Math.abs(this.speed) > 12`
        internal const float WingDragShearSpeed = 50f;        // game.js:2159 `Math.abs(this.speed) > 50`
        internal const float WingDragDamageWingDmg = 180f;    // game.js:2162 `this._damageWing(side, 180)`

        public Action<string> OnHint;      // game.js _setHint
        public Action<float> OnShakeAdd;   // this._shakeAmp += amount

        /// <summary>Another registered aeroplane at this hit, or null if the
        /// hit is a wall/terrain/our-own-parked-machine (game.js:1930
        /// `this.targets?.recOf?.(hit.object)` + the `!pRec.own` guard, which
        /// src/planephys.js's own `ram()` re-checks at line 428 — either side
        /// enforcing it is fine; the caller implementing this delegate only
        /// needs to enforce it once). `part`: "nose"/"left"/"right".</summary>
        public Func<RaycastHit, Vector3, float, string, RamOutcome?> RamOther;

        public Func<string, Transform> WingTipBone; // plane.userData.wings[side].tip

        public struct RamOutcome
        {
            public PlanePhysicsSystem.RamResult Result;
            public string OtherName; // rec.def?.name — for the hint text
        }

        /// <summary>game.js:1883-1895 `_surfaceUnder`.</summary>
        RaycastHit? SurfaceUnder(Vector3 p)
        {
            var hits = _world.RaycastAllSorted(p + new Vector3(0f, 1.2f, 0f), Vector3.down, 4f);
            return hits.Length > 0 ? hits[0] : (RaycastHit?)null;
        }

        /// <summary>game.js:1852-1880 `_buildingSmash`.</summary>
        void BuildingSmash(RaycastHit hit, float headOn, float sp)
        {
            int dmg = Mathf.RoundToInt(sp * (0.3f + 1.7f * headOn));
            DamageResult? res = hit.collider != null ? _world.Damage?.DamageHit(hit, dmg) : null;
            BuildingSmashFx(hit.point, headOn, sp, res);
        }

        /// <summary>Same FX tail as BuildingSmash, for a synthetic (no real
        /// collider) contact point — game.js:1856's own `hit.object ? ... : null`
        /// already degrades to "FX only, no building damage" in that case; this
        /// overload is that degraded path made explicit for callers with no
        /// RaycastHit to offer (the wingtip-drags-on-bare-terrain case).</summary>
        void BuildingSmashTerrain(Vector3 point, float headOn, float sp) => BuildingSmashFx(point, headOn, sp, null);

        void BuildingSmashFx(Vector3 point, float headOn, float sp, DamageResult? res)
        {
            float vol = _world.SoundVolume();
            if (res.HasValue)
            {
                _world.ShowBuildingToast(res.Value);
                float d = _world.CameraDistanceTo(point);
                _world.HitConcreteSound(d, vol);
                if (res.Value.Changed) _world.CollapseRumbleSound(d, vol, res.Value.Destroyed);
                var blown = _world.Damage?.BlastWindows(point.x, point.y, point.z, 3f + headOn * 5f);
                if (blown != null && blown.Length > 0) _world.BurstGlass(blown, 1.1f, 10f);
            }
            _world.DustBurst(point, Mathf.Min(28, 8 + Mathf.RoundToInt(sp / 12f)), 1.2f + headOn * 1.6f);
            int nEmber = Mathf.Min(16, 4 + Mathf.RoundToInt((sp * headOn) / 12f));
            for (int i = 0; i < nEmber; i++) _world.SpawnEmber(point);
            _world.SpawnSmoke(point, 3f + headOn * 5f, 0.7f, 2f, 1.5f, 1.5f);
            OnShakeAdd?.Invoke(0.25f + headOn * 0.9f);
        }

        /// <summary>src/planephys.js:490-522, read directly from source — the
        /// "what it does to US" half of a plane-vs-plane ram that Flight/
        /// PlanePhysics.cs's own header says is this file's job to apply.</summary>
        void ApplyRamResult(RamOutcome outcome, Transform plane, Vector3 prevPos, ref Vector3 vel, ref float speed,
                             bool airborne, float mass, Vector3 hitPoint)
        {
            var res = outcome.Result;
            float energy = res.SpecUs * mass; // c.E recovered: SpecUs := c.E / m1
            // Our OWN airframe's dent (planephys.js:471 `this._dent(A, point, n, c.E*0.5)`
            // — the other plane's dent, B, is already applied inside Ram() itself).
            _hull.OnDentHull?.Invoke(hitPoint, res.ImpactNormal, energy * 0.5f, _hull.ColMult);

            vel = res.PilotVelocity;
            plane.position = res.PlowThrough
                ? prevPos + vel * (1f / 60f)
                : plane.position + res.PilotPositionDelta;
            if (airborne) speed = vel.magnitude;

            if (res.Part != "nose" && res.SpecUs > RamSpecUsGate)
            {
                string side = res.Part == "left" ? "left" : "right";
                if (res.SpecUs > PlanePhysicsConst.TearSpec)
                    _hull.ShearWing(side, $"ფრთა {outcome.OtherName}-ს მოხვდა და მოწყდა");
                else
                    _hull.DamageWing(side, Mathf.Max(0f, (res.SpecUs - PlanePhysicsConst.Deadzone) / PlanePhysicsConst.KPlane));
                if (_hull.Crashed) return;
            }
            else if (res.DamageToUs > 0f)
            {
                _hull.ApplyDamage(res.DamageToUs, res.ImpactNormal);
                if (_hull.Crashed) return;
            }
            OnHint?.Invoke(res.HitPlaneDied
                ? (res.PlowThrough ? $"\U0001F4A5 {outcome.OtherName} გასრესე!" : $"\U0001F4A5 {outcome.OtherName} განადგურდა!")
                : $"\U0001F4A5 შეჯახება: {outcome.OtherName}");
        }

        /// <summary>game.js:1896-2016 `_touchWall`. `prevPos` is the frame-start
        /// position (game.js's `this._prevPos`) — the caller tracks it exactly
        /// as the JS field does, copying it right before integrating position
        /// each frame. Returns true on ANY contact (wall, plane, ceiling) —
        /// check `_hull.Crashed` afterward exactly as the JS checks
        /// `this.crashed`.</summary>
        /// <summary>Per-contact diagnostic, null in the game. Set by the
        /// play-mode run to name what a taxiing aeroplane keeps hitting.</summary>
        public static System.Action<string> Diag;

        public bool TouchWall(float dt, Transform plane, bool airborne, ref float speed, ref Vector3 vel,
                               Vector3 prevPos, float noseLen, FlightModel aero)
        {
            if (_hitCooldown > 0f) _hitCooldown -= dt; // game.js:1485

            float sp = airborne ? vel.magnitude : Mathf.Abs(speed);
            if (sp < TouchWallMinSpeed) return false;
            Vector3 forward = plane.forward;
            Vector3 dir = airborne ? vel / Mathf.Max(sp, 1e-6f) : (speed >= 0f ? forward : -forward);

            float maxDist = sp * dt + noseLen;
            var hits = _world.RaycastAllSorted(prevPos, dir, maxDist);
            if (hits.Length > 0 && _world.BoreFloorAt(prevPos.x, prevPos.z, prevPos.y).HasValue)
            {
                var filtered = new List<RaycastHit>(hits.Length);
                foreach (var h in hits) if (!_world.IsTerrain(h.collider)) filtered.Add(h);
                hits = filtered.ToArray();
            }
            if (hits.Length == 0) return false;
            RaycastHit hit = hits[0];

            // ANOTHER AEROPLANE — must come before the relief/ceiling tests
            // below (game.js:1922-1931).
            var ram = RamOther?.Invoke(hit, dir, sp, "nose");
            if (ram.HasValue)
            {
                ApplyRamResult(ram.Value, plane, prevPos, ref vel, ref speed, airborne, aero.Mass, hit.point);
                return true;
            }

            Vector3? n = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : (Vector3?)null;
            if (n.HasValue && Vector3.Dot(n.Value, dir) > 0f) n = -n.Value;
            if (n.HasValue && n.Value.y > 0.45f) return false; // upward-facing = relief/roof

            if (n.HasValue && n.Value.y < -0.4f && airborne && dir.y < 0.35f)
            {
                plane.position = prevPos - new Vector3(0f, 0.25f, 0f);
                if (vel.y > 0f) vel.y = 0f;
                if (_hitCooldown <= 0f && sp > 12f)
                {
                    _hitCooldown = HitCooldownDuration;
                    _hull.CrashDamage(sp * 0.7f, n);
                    _hull.DentHull(hit.point, n.Value, sp, -Vector3.Dot(dir, n.Value), aero.Mass);
                    _world.SpawnEmber(hit.point);
                    _world.DustBurst(hit.point, 4, 0.8f);
                    if (_hull.Crashed) return true;
                    OnHint?.Invoke("\U0001F4A5 ჭერს შეეხე — კორპუსი დაზიანდა!");
                }
                return true;
            }

            float headOn = n.HasValue ? Mathf.Clamp(-Vector3.Dot(dir, n.Value), 0f, 1f) : 1f;
            if (Diag != null)
                Diag($"airborne={airborne.ToString().ToLowerInvariant()} sp={sp:0.##} dist={hit.distance:0.###} " +
                     $"maxDist={maxDist:0.###} noseLen={noseLen:0.##} headOn={headOn:0.###} " +
                     $"n=({(n.HasValue ? $"{n.Value.x:0.##},{n.Value.y:0.##},{n.Value.z:0.##}" : "null")}) " +
                     $"dir=({dir.x:0.##},{dir.y:0.##},{dir.z:0.##}) rayY={prevPos.y:0.##} hitY={hit.point.y:0.##} " +
                     $"col={(hit.collider != null ? hit.collider.name : "null")} " +
                     $"terrain={_world.IsTerrain(hit.collider).ToString().ToLowerInvariant()} hits={hits.Length}");

            // Flying square into a facade at speed: total demolition, plow on through.
            if (airborne && headOn > BuildingSmashHeadOnSpeed && sp > 34f)
            {
                BuildingSmash(hit, headOn, sp);
                if (n.HasValue) _hull.DentHull(hit.point, n.Value, sp, headOn, aero.Mass);
                if (hit.collider != null) _world.Damage?.DamageHit(hit, Mathf.RoundToInt(sp * BuildingSmashTotalMult));
                _hull.Explode(null, airborne: true, vel: vel);
                return true;
            }

            if (_hitCooldown <= 0f)
            {
                _hitCooldown = HitCooldownDuration;
                BuildingSmash(hit, headOn, sp);
                if (n.HasValue) _hull.DentHull(hit.point, n.Value, sp, headOn, aero.Mass);
                _hull.CrashDamage(sp * (1.2f + 5.5f * headOn), n);
                if (_hull.Crashed) return true;
                OnHint?.Invoke("\U0001F4A5 შენობას შეეხე — კორპუსი დაზიანდა!");
            }
            else
            {
                _world.SpawnEmber(hit.point);
                if (UnityEngine.Random.value < 0.4f) _world.DustBurst(hit.point, 3, 0.7f);
            }

            if (n.HasValue)
            {
                Vector3 nn = n.Value;
                Vector3 outv = new Vector3(nn.x, 0f, nn.z);
                outv = outv.sqrMagnitude < 1e-4f ? nn : outv.normalized;
                plane.position = prevPos + outv * 0.6f;
                if (airborne)
                {
                    float into = Vector3.Dot(vel, outv);
                    if (into < 0f) vel += outv * (-into * 1.05f);
                    vel *= 1f - 0.45f * headOn;
                    speed = vel.magnitude;
                    if (headOn < 0.97f)
                    {
                        Vector3 slide = (dir + nn * headOn).normalized;
                        plane.rotation = Quaternion.FromToRotation(dir, slide) * plane.rotation;
                    }
                }
                else
                {
                    speed *= 1f - 0.7f * headOn;
                }
            }
            else
            {
                plane.position = prevPos;
                if (airborne) vel *= 0.2f;
                speed *= 0.2f;
            }
            if (!airborne) speed *= 0.3f;
            return true;
        }

        /// <summary>game.js:2017-2177 `_checkCollision`, run on GameLoop's
        /// existing 0.15 s collision timer (see this file's header).
        /// `prevPos` is the same frame-tracked field TouchWall takes. Returns
        /// true the moment either of this method's own two `_groundRoll.
        /// Touchdown()` calls actually grounds the plane (game.js:2179 `_land`
        /// sets `this.airborne = false` directly) — PlaneController.cs's
        /// Airborne bool has no other way to learn this, since this method
        /// previously returned void and the caller had to guess.</summary>
        public bool CheckCollision(Transform plane, bool airborne, ref float speed, ref Vector3 vel, ref Vector3 rates,
                                    ref float lastGroundDistance, Vector3 prevPos, FlightModel aero)
        {
            var down = _world.RaycastAllSorted(plane.position, Vector3.down, float.PositiveInfinity);
            bool haveDown = down.Length > 0;
            float downY = 0f;
            if (haveDown) { lastGroundDistance = down[0].distance; downY = down[0].point.y; }
            else
            {
                var h = _ground.HeightAt(plane.position.x, plane.position.z);
                if (h.HasValue)
                {
                    haveDown = true;
                    lastGroundDistance = plane.position.y - h.Value;
                    downY = h.Value;
                }
            }

            Vector3 forward = plane.forward;
            bool backing = !airborne && speed < 0f;
            Vector3 moveDir = (airborne && vel.sqrMagnitude > 1f) ? vel.normalized : (backing ? -forward : forward);
            Vector3 rayOrigin = airborne ? plane.position : plane.position + Vector3.up * 2f;
            float farDist = airborne ? Mathf.Max(12f, speed * 0.2f) : 8f;
            var ahead = _world.RaycastAllSorted(rayOrigin, moveDir, farDist);

            bool hitAhead = ahead.Length > 0 && (airborne || Mathf.Abs(speed) > 5f);
            if (hitAhead)
            {
                var hit = ahead[0];
                if (_world.IsPlaneCollider(hit.collider)) return false; // another plane is never "ground"
                Vector3? n = hit.normal.sqrMagnitude > 1e-8f ? hit.normal : (Vector3?)null;
                bool isRelief = n.HasValue && n.Value.y > 0.45f;
                if (isRelief && airborne && !backing)
                {
                    float gy = _ground.SampleStreetY(plane.position) ?? hit.point.y;
                    return _groundRoll.Touchdown(plane, ref speed, ref vel, ref rates, gy, aero);
                }
                return false;
            }

            // The nose is clear — but a wingtip may still catch a wall.
            if (WingHitCooldown > 0f) WingHitCooldown -= WingHitCooldownTick; // fixed tick interval, not dt (game.js:2076)
            else if (Mathf.Abs(speed) > WingHitMinSpeed)
            {
                Vector3 wdir = backing ? -forward : forward;
                foreach (var side in SIDES)
                {
                    float cur = side == "left" ? _hull.Struct.Air.WingL : _hull.Struct.Air.WingR;
                    if (cur == 0f) continue;
                    Vector3 tip = _hull.WingTip(side, plane, HalfSpan, WingTipBone);
                    float farD = Mathf.Max(2.5f, Mathf.Abs(speed) * 0.17f);
                    var hits = _world.RaycastAllSorted(tip, wdir, farD);
                    if (hits.Length == 0) continue;

                    var ramW = RamOther?.Invoke(hits[0], wdir, Mathf.Abs(speed), side);
                    if (ramW.HasValue)
                    {
                        ApplyRamResult(ramW.Value, plane, prevPos, ref vel, ref speed, airborne, aero.Mass, hits[0].point);
                        WingHitCooldown = 0.8f;
                        if (_hull.Crashed) return true;
                        break;
                    }

                    Vector3? wn = hits[0].normal.sqrMagnitude > 1e-8f ? hits[0].normal : (Vector3?)null;
                    if (wn.HasValue && wn.Value.y > 0.45f) continue; // relief sloping up, not a wall

                    BuildingSmash(hits[0], 0.6f, Mathf.Abs(speed));
                    _hull.DentHull(hits[0].point, wn ?? -wdir, speed, 0.6f, aero.Mass);
                    if (Mathf.Abs(speed) > WingWallShearSpeed) _hull.ShearWing(side, "ფრთა კედელს მოხვდა და მოწყდა");
                    else _hull.DamageWing(side, WingWallDamageWingBase + Mathf.Abs(speed) * 2.5f);
                    if (_hull.Crashed) return true;

                    speed *= 0.6f;
                    if (airborne) vel *= 0.6f;
                    float yawCmd = side == "right" ? 0.3f : -0.3f; // rotateY: YAW, NEGATE
                    plane.Rotate(0f, -yawCmd * Mathf.Rad2Deg, 0f, Space.Self);
                    if (wn.HasValue)
                    {
                        Vector3 push = new Vector3(wn.Value.x, 0f, wn.Value.z).normalized;
                        plane.position += push * 2f;
                    }
                    WingHitCooldown = 0.8f;
                    break;
                }
            }

            // Banked flying drags a wingtip across any surface.
            if (airborne && WingHitCooldown <= 0f && Mathf.Abs(speed) > WingDragMinSpeed)
            {
                foreach (var side in SIDES)
                {
                    float cur = side == "left" ? _hull.Struct.Air.WingL : _hull.Struct.Air.WingR;
                    if (cur == 0f) continue;
                    Vector3 tip = _hull.WingTip(side, plane, HalfSpan, WingTipBone);
                    RaycastHit? under = SurfaceUnder(tip);
                    float? g = _ground.GroundAt(tip.x, tip.z, tip.y, maxAbove: 0.3f);
                    bool underValid = under.HasValue && under.Value.point.y <= tip.y + 0.3f;
                    float surfY = Mathf.Max(underValid ? under.Value.point.y : float.NegativeInfinity,
                                             g ?? float.NegativeInfinity);
                    if (float.IsNegativeInfinity(surfY) || tip.y > surfY + 0.25f) continue;

                    bool hitIsMesh = under.HasValue && under.Value.point.y >= surfY - 0.01f;
                    Vector3 hitPoint = hitIsMesh ? under.Value.point : new Vector3(tip.x, surfY, tip.z);

                    if (hitIsMesh)
                    {
                        Vector3 dd = vel.sqrMagnitude > 1f ? vel.normalized : forward;
                        var ramD = RamOther?.Invoke(under.Value, dd, Mathf.Abs(speed), side);
                        if (ramD.HasValue)
                        {
                            ApplyRamResult(ramD.Value, plane, prevPos, ref vel, ref speed, airborne, aero.Mass, hitPoint);
                            WingHitCooldown = 0.8f;
                            if (_hull.Crashed) return true;
                            break;
                        }
                    }

                    if (hitIsMesh) BuildingSmash(under.Value, 0.35f, Mathf.Abs(speed));
                    else BuildingSmashTerrain(hitPoint, 0.35f, Mathf.Abs(speed));
                    _hull.DentHull(hitPoint, Vector3.up, speed, 0.35f, aero.Mass);

                    if (Mathf.Abs(speed) > WingDragShearSpeed)
                    {
                        _hull.ShearWing(side, "ფრთამ სახურავს გაჰკრა");
                    }
                    else
                    {
                        _hull.DamageWing(side, WingDragDamageWingDmg);
                        float rollCmd = side == "right" ? 0.18f : -0.18f; // rotateZ: ROLL, KEEP
                        plane.Rotate(0f, 0f, rollCmd * Mathf.Rad2Deg, Space.Self);
                    }
                    if (_hull.Crashed) return true;
                    WingHitCooldown = 0.8f;
                    break;
                }
            }

            // Backup floor check between per-frame ground tests.
            if (airborne && haveDown && lastGroundDistance < 3f)
                return _groundRoll.Touchdown(plane, ref speed, ref vel, ref rates, downY, aero);
            return false;
        }

        static readonly string[] SIDES = { "left", "right" };
    }
}
