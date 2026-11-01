using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TbilisiFly.Combat
{
    /// <summary>
    /// Port of src/ballistics.js — the real projectile sim. Every round is a
    /// cartridge with real numbers, integrated as an actual projectile instead
    /// of an instant ray: it takes time to arrive and it drops, it loses speed
    /// to drag, and whether it digs in / punches through / skips off is decided
    /// by the energy it still carries, the angle it arrived at and what the
    /// surface is made of. PORTED AS-IS — the constants were measured against
    /// real cartridge ballistic tables (see each cartridge's comment).
    ///
    /// HANDEDNESS: this file has no body-axis math (no local forward/up/right,
    /// no banked-turn yaw). The one geometric operation that could be a trap —
    /// the ricochet reflect() — uses only dot products (v' = v - 2(v.n)n),
    /// which are handedness-invariant, so NOTHING here needed a cross-product
    /// swap or a forward-sign flip. Positions and directions are plain world
    /// vectors; GeoFrame only matters where this plugs into City/Flight data,
    /// which is out of this file's scope.
    ///
    /// UNITY DIFFERENCES FROM THE JS, noted where they bite:
    ///  * Three's THREE.Raycaster needs _faceNormal() to fold an InstancedMesh's
    ///    per-instance matrix into the template-space face normal. Unity's
    ///    Physics.Raycast already returns a world-space RaycastHit.normal for a
    ///    MeshCollider, so that whole function collapses to a sign flip
    ///    (FaceNormal below) — there is no Unity analogue of per-instance
    ///    raycast normals because MeshColliders are not GPU-instanced.
    ///  * `refineProxy`'s object-graph walk (an invisible box standing in for
    ///    real geometry held by the same parent — the crane's fan guards, a
    ///    plane's climb-shell) is ported in ProxyRefine below using
    ///    Renderer.enabled / GameObject.activeSelf as the "shown" test and
    ///    Transform parent/children as the "holder". No per-prop wiring is
    ///    needed beyond that; it is attribute-free.
    ///  * classifySurface's material-name / physical-property fallback is
    ///    ported against Unity's Renderer.sharedMaterial, checking the shader
    ///    properties a URP/Standard Lit material actually has
    ///    (_Metallic, _Surface, _AlphaClip, _BaseColor/_Color). The CITY-side
    ///    structural tags (buildings/shellOf/destr/landmarkRec/isTerrain/road)
    ///    are exposed through IBallisticsWorld — City owns those tags, Combat
    ///    only consumes them, so this file never touches a City script.
    /// </summary>
    public sealed class Cartridge
    {
        public string Id;
        public string Label;          // Georgian — kept byte for byte
        public float BulletMm;        // measured bullet diameter, NOT the nominal name
        public float CaseMm;
        public float MassG;
        public float V0;              // muzzle velocity, m/s
        public string Sample;         // audiofx.js recording key (audio out of Combat scope)
        public float Recoil;          // rad of muzzle rise per shot
        public float RecoilH;         // rad of sideways wander per shot (random walk)
        public float Climb = 1f;      // x on rounds 2-5 of a burst (AKM only)
        public float Recover = 0.75f; // of each rise that comes back on its own
        public float KickBack;        // m the weapon jumps back into the hand
        public float Rock;            // rad the torso rocks back per round (rifles)
        public float Bloom = 0.011f;  // rad the cone opens per shot
        public float Sway = 1f;       // x the aimed sway (long gun wanders more)
        public float Rate;            // s between trigger pulls
        public int Mag;               // magazine capacity
        public bool Auto;             // holds the trigger and keeps going

        // ---- derived, exactly cartridge() in ballistics.js ----
        public float AreaM2;          // m^2 — the hole it has to make
        public float AreaCm2;
        public float MassKg;
        public float K;               // dv/dt = -k*v^2 quadratic-drag constant
        public float Energy;          // J at the muzzle
        public bool Supersonic;
    }

    public static class Cartridges
    {
        const float AIR = 1.225f;   // kg/m^3 sea-level air
        const float CD = 0.30f;     // drag coefficient, round-nose FMJ below Mach 1
        const float SONIC = 343f;   // m/s

        // src/ballistics.js:38 cartridge()
        static Cartridge Make(string id, string label, float bulletMm, float caseMm, float massG,
            float v0, string sample, float recoil, float recoilH, float recover, float kickBack,
            float bloom, float rate, int mag, bool auto = false, float climb = 1f, float rock = 0f,
            float sway = 1f)
        {
            float d = bulletMm / 1000f;
            float area = Mathf.PI * (d / 2f) * (d / 2f);
            float m = massG / 1000f;
            var c = new Cartridge
            {
                Id = id, Label = label, BulletMm = bulletMm, CaseMm = caseMm, MassG = massG, V0 = v0,
                Sample = sample, Recoil = recoil, RecoilH = recoilH, Recover = recover, KickBack = kickBack,
                Bloom = bloom, Rate = rate, Mag = mag, Auto = auto, Climb = climb, Rock = rock, Sway = sway,
                AreaM2 = area, AreaCm2 = area * 1e4f, MassKg = m,
                K = (0.5f * AIR * CD * area) / m,
                Energy = 0.5f * m * v0 * v0,
                Supersonic = v0 > SONIC,
            };
            return c;
        }

        // 9x18 mm Makarov, 94 gr FMJ, 93.5 mm barrel. 303 J, subsonic.
        public static readonly Cartridge Mak9x18 = Make(
            "9x18", "9×18 მმ მაკაროვი", bulletMm: 9.27f, caseMm: 18f, massG: 6.1f, v0: 315f,
            sample: "makarov", recoil: 0.0075f, recoilH: 0.0025f, recover: 0.75f, kickBack: 0.012f,
            bloom: 0.011f, rate: 0.17f, mag: 8);

        // 9x19 mm Parabellum, 124 gr FMJ, 125 mm barrel. 580 J, supersonic.
        public static readonly Cartridge Para9x19 = Make(
            "9x19", "9×19 მმ პარაბელუმი", bulletMm: 9.01f, caseMm: 19f, massG: 8.0f, v0: 381f,
            sample: "beretta", recoil: 0.0115f, recoilH: 0.003f, recover: 0.72f, kickBack: 0.014f,
            bloom: 0.011f, rate: 0.14f, mag: 15);

        // 7.62x39 mm M43, 123 gr FMJ, AKM 415 mm barrel. 2000 J, automatic, 600 rpm.
        public static readonly Cartridge M43_762x39 = Make(
            "7.62x39", "7.62×39 მმ", bulletMm: 7.92f, caseMm: 39f, massG: 7.9f, v0: 715f,
            sample: "ak47", recoil: 0.012f, recoilH: 0.0055f, climb: 1.35f, recover: 0.38f,
            kickBack: 0.006f, rock: 0.028f, bloom: 0.009f, sway: 1.4f, rate: 0.1f, mag: 30, auto: true);

        // The belly gun on the plane. Heavy MG round — does not ricochet off masonry.
        public static readonly Cartridge Heavy127x108 = Make(
            "12.7x108", "12.7×108 მმ", bulletMm: 12.98f, caseMm: 108f, massG: 48.3f, v0: 820f,
            sample: null, recoil: 0f, recoilH: 0f, recover: 0.75f, kickBack: 0f, bloom: 0f,
            rate: 0.083f, mag: 0);

        public static readonly Dictionary<string, Cartridge> ById = new Dictionary<string, Cartridge>
        {
            ["9x18"] = Mak9x18,
            ["9x19"] = Para9x19,
            ["7.62x39"] = M43_762x39,
            ["12.7x108"] = Heavy127x108,
        };

        // src/ballistics.js:147 — set from the 9x19 at the muzzle (580 J -> 34 hp).
        public static readonly float HpPerJoule = 34f / Para9x19.Energy;

        // src/ballistics.js:160 HULL — small arms vs an airframe (see planedamage.js, out of scope).
        public const float Hull = 0.4f;
    }

    public struct SurfaceDust
    {
        public Color32 Tint;
        public int N;
        public float Speed;
        public float Up; // 0 = use the surface's default lift
    }

    public sealed class SurfaceProps
    {
        public string Id;
        public string Label;     // Georgian — kept byte for byte
        public float Resist;     // J/cm^3 — specific penetration energy (Poncelet)
        public float Thick;      // m — nominal thickness of this kind of thing
        public float Graze;      // rad — critical ricochet angle, measured FROM the surface (0 = never)
        public float Spall;      // halo radius multiplier (x bullet diameter)
        public float Hole;       // hole diameter multiplier (x bullet diameter)
        public SurfaceDust Dust;
        public string Sound;
        public float Pit;
        public float Sparks;     // 0 = none
        public bool Plane;       // this surface belongs to an airframe
        public bool NoHole;      // liquids/soil/paper/foliage — nothing to punch a decal into
        public bool Liquid;
        public bool Splinter;    // wood
        public float Hull = 1f;  // damage scale vs an airframe (1 = normal, see Cartridges.Hull)
    }

    public static class Surfaces
    {
        static SurfaceProps S(string id, string label, float resist, float thick, float graze,
            float spall, float hole, SurfaceDust dust, string sound, float pit, float sparks = 0f,
            bool plane = false, bool noHole = false, bool liquid = false, bool splinter = false,
            float hull = 1f)
            => new SurfaceProps
            {
                Id = id, Label = label, Resist = resist, Thick = thick, Graze = graze, Spall = spall,
                Hole = hole, Dust = dust, Sound = sound, Pit = pit, Sparks = sparks, Plane = plane,
                NoHole = noHole, Liquid = liquid, Splinter = splinter, Hull = hull,
            };

        static Color32 C(uint hex) => new Color32(
            (byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF), 255);

        public static readonly Dictionary<string, SurfaceProps> ById = new Dictionary<string, SurfaceProps>
        {
            ["masonry"] = S("masonry", "აგური", 110, 0.30f, 0.35f, 11, 1.25f,
                new SurfaceDust { Tint = C(0xa89d8b), N = 9, Speed = 2.6f }, "concrete", 0.55f),
            ["concrete"] = S("concrete", "ბეტონი", 220, 0.25f, 0.33f, 9.5f, 1.15f,
                new SurfaceDust { Tint = C(0x9e9c96), N = 9, Speed = 2.8f }, "concrete", 0.5f),
            ["stone"] = S("stone", "ქვა", 420, 0.4f, 0.44f, 7.5f, 1.1f,
                new SurfaceDust { Tint = C(0xb4b0a6), N = 7, Speed = 3.2f }, "concrete", 0.35f, sparks: 3),
            ["asphalt"] = S("asphalt", "ასფალტი", 90, 0.12f, 0.30f, 8, 1.3f,
                new SurfaceDust { Tint = C(0x4a4845), N = 8, Speed = 2.4f }, "concrete", 0.7f),
            ["metal"] = S("metal", "ლითონი", 2500, 0.0015f, 0.42f, 3.6f, 1.0f,
                new SurfaceDust { Tint = C(0x8d8f93), N = 3, Speed = 3.5f }, "metal", 1f, sparks: 16),
            ["glass"] = S("glass", "მინა", 20, 0.004f, 0f, 16, 2.2f,
                new SurfaceDust { Tint = C(0xd6ecf8), N = 5, Speed = 3f }, "glass", 1f),
            // ---- an aeroplane (see planedamage.js, out of Combat scope) ----
            ["skin"] = S("skin", "ფიუზელაჟი", 900, 0.0012f, 0.38f, 5.2f, 1.18f,
                new SurfaceDust { Tint = C(0xb6b9bd), N = 5, Speed = 3.4f }, "skin", 1f, sparks: 11,
                plane: true, hull: Cartridges.Hull),
            ["perspex"] = S("perspex", "ფარანი", 45, 0.006f, 0.1f, 1, 1.35f,
                new SurfaceDust { Tint = C(0xe2f1ff), N = 6, Speed = 3.1f }, "perspex", 1f,
                plane: true, noHole: true, hull: Cartridges.Hull),
            ["rubber"] = S("rubber", "საბურავი", 120, 0.035f, 0.06f, 2.8f, 1.1f,
                new SurfaceDust { Tint = C(0x26262a), N = 8, Speed = 2.1f }, "soft", 1f,
                plane: true, hull: Cartridges.Hull),
            ["wood"] = S("wood", "ხე", 30, 0.035f, 0.21f, 5.5f, 1.2f,
                new SurfaceDust { Tint = C(0x7a5a38), N = 7, Speed = 2.2f }, "wood", 1f, splinter: true),
            ["soil"] = S("soil", "მიწა", 25, 1.2f, 0.17f, 7, 2.6f,
                new SurfaceDust { Tint = C(0x6b5a42), N = 12, Speed = 3.4f }, "soft", 1f, noHole: true),
            ["grass"] = S("grass", "ბალახი", 22, 1.2f, 0.17f, 7, 2.6f,
                new SurfaceDust { Tint = C(0x556b35), N = 12, Speed = 3.4f }, "soft", 1f, noHole: true),
            ["water"] = S("water", "წყალი", 10, 3.0f, 0.14f, 1, 1,
                new SurfaceDust { Tint = C(0xc8e4f2), N = 16, Speed = 4.5f, Up = 5f }, "water", 1f,
                noHole: true, liquid: true),
            ["paper"] = S("paper", "ქაღალდი", 1, 0.0002f, 0f, 1, 1,
                new SurfaceDust { Tint = C(0xe8e4da), N = 4, Speed = 1.6f }, "soft", 1f, noHole: true),
            ["foliage"] = S("foliage", "ფოთლები", 2, 0.02f, 0f, 4, 1,
                new SurfaceDust { Tint = C(0x4e7a33), N = 8, Speed = 2f }, "soft", 1f, noHole: true),
        };

        public static SurfaceProps Of(string key)
            => (key != null && ById.TryGetValue(key, out var s)) ? s : ById["concrete"];
    }

    /// <summary>
    /// What City/Flight hand Combat about a hit, so ballistics.js's structural
    /// classification (facade vs window, terrain vs road, landmark) ports
    /// without this file reaching into another system's scripts. Every method
    /// may legitimately do nothing (return false / null) — classifySurface
    /// falls through to the material-based heuristics exactly as the JS does
    /// when `world` is absent.
    /// </summary>
    public interface IBallisticsWorld
    {
        /// <summary>src/ballistics.js:384 — an explicit tag on the hit object or a parent.</summary>
        string ExplicitSurfaceTag(Collider hit);
        /// <summary>A building facade (ud.buildings || ud.shellOf) — and whether this exact
        /// point landed on a window cell (windowAt, falling back to nearWindow).</summary>
        bool TryBuildingFacade(Collider hit, Vector3 point, out bool isWindow);
        /// <summary>A destructible prop already tagged masonry (ud.destr).</summary>
        bool IsDestructible(Collider hit);
        /// <summary>A landmark record anywhere up the parent chain.</summary>
        bool IsLandmark(Collider hit);
        /// <summary>Terrain mesh (road tiles are tagged terrain too — see IsRoadSurface).</summary>
        bool IsTerrain(Collider hit);
        /// <summary>The shared road material — the tell that a terrain tile is actually asphalt.</summary>
        bool IsRoadSurface(Collider hit);
        /// <summary>Ground class grid at a world x/z: 0 soil, 1 grass, 2 water.</summary>
        int GroundClassAt(float x, float z);
    }

    public static class SurfaceClassifier
    {
        static readonly Regex ReGlass = new Regex("glass|window|pane|glazing", RegexOptions.IgnoreCase);
        static readonly Regex ReWater = new Regex("water|river", RegexOptions.IgnoreCase);
        static readonly Regex ReAsphalt = new Regex("asphalt|tarmac|runway|road", RegexOptions.IgnoreCase);
        static readonly Regex ReMetal = new Regex("metal|steel|alu|chrome|iron|brass|tin|rail", RegexOptions.IgnoreCase);
        static readonly Regex ReWood = new Regex("wood|plank|timber|bark|trunk|bench", RegexOptions.IgnoreCase);
        static readonly Regex ReStone = new Regex("granite|marble|ashlar|kerb|curb|basalt|stone|bronze", RegexOptions.IgnoreCase);
        static readonly Regex ReFoliage = new Regex("leaf|leaves|foliage|canopy|hedge", RegexOptions.IgnoreCase);
        static readonly Regex ReMasonry = new Regex("brick|plaster|stucco|facade", RegexOptions.IgnoreCase);
        static readonly Regex ReConcrete = new Regex("concrete|beton|sidewalk|pavement", RegexOptions.IgnoreCase);

        static bool IsGreen(Color c) => c.g > c.r * 1.18f && c.g > c.b * 1.25f;

        /// <summary>src/ballistics.js:382 classifySurface — same decision order.</summary>
        public static string Classify(RaycastHit hit, IBallisticsWorld world, Vector3 point)
        {
            var tag = world?.ExplicitSurfaceTag(hit.collider);
            if (!string.IsNullOrEmpty(tag)) return tag;

            var renderer = hit.collider ? hit.collider.GetComponent<Renderer>() : null;
            var mat = renderer ? renderer.sharedMaterial : null;
            string nm = mat ? (mat.name ?? "").ToLowerInvariant() : "";
            if (nm.Length > 0)
            {
                if (ReGlass.IsMatch(nm)) return "glass";
                if (ReWater.IsMatch(nm)) return "water";
                if (ReAsphalt.IsMatch(nm)) return "asphalt";
                if (ReMetal.IsMatch(nm)) return "metal";
                if (ReWood.IsMatch(nm)) return "wood";
                if (ReStone.IsMatch(nm)) return "stone";
                if (ReFoliage.IsMatch(nm)) return "foliage";
                if (ReMasonry.IsMatch(nm)) return "masonry";
                if (ReConcrete.IsMatch(nm)) return "concrete";
            }

            if (world != null && world.TryBuildingFacade(hit.collider, point, out bool isWindow))
                return isWindow ? "glass" : "masonry";
            if (world != null && world.IsDestructible(hit.collider)) return "masonry";
            if (world != null && world.IsLandmark(hit.collider)) return "concrete";
            if (world != null && world.IsTerrain(hit.collider))
            {
                if (world.IsRoadSurface(hit.collider)) return "asphalt";
                int cl = world.GroundClassAt(point.x, point.z);
                return cl == 2 ? "water" : cl == 1 ? "grass" : "soil";
            }

            if (mat != null)
            {
                float metal = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
                if (metal >= 0.55f) return "metal";
                bool transparent = mat.HasProperty("_Surface") && mat.GetFloat("_Surface") > 0.5f;
                Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor")
                    : mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                if (transparent && baseColor.a < 0.8f) return "glass";
                bool alphaClip = mat.HasProperty("_AlphaClip") && mat.GetFloat("_AlphaClip") > 0.5f;
                if (alphaClip && IsGreen(baseColor)) return "foliage";
                if (IsGreen(baseColor)) return "grass";
            }
            return "concrete";
        }
    }

    /// <summary>
    /// Port of src/ballistics.js refineProxy — a round must never mark something
    /// nobody can see. Half the props on a slab are solid by way of an invisible
    /// stand-in (box colliders a metre proud of wire-frame geometry); a hit on
    /// one of those is re-cast against the real geometry held by the same
    /// parent, and a round that went between the wires returns null.
    ///
    /// Three's "visible" walks userData/.visible; Unity's equivalent is
    /// Renderer.enabled plus GameObject.activeSelf up the hierarchy.
    /// </summary>
    public static class ProxyRefine
    {
        const float PxBack = 0.35f; // m the re-cast starts behind the box face

        sealed class ProxyCache
        {
            public List<Collider> Real = new List<Collider>();
            public float Far = -1f; // lazily measured
        }

        static readonly Dictionary<Transform, ProxyCache> _cache = new Dictionary<Transform, ProxyCache>();

        static bool IsShown(Transform t)
        {
            for (Transform q = t; q != null; q = q.parent)
            {
                if (!q.gameObject.activeSelf) return false;
                var r = q.GetComponent<Renderer>();
                if (r != null && !r.enabled) return false;
            }
            return true;
        }

        /// <summary>Returns the real hit, the original hit (nothing to refine), or null
        /// (the round passed clean through — the proxy lied and there is nothing there).</summary>
        public static RaycastHit? Refine(RaycastHit hit, Vector3 dir)
        {
            var o = hit.collider ? hit.collider.transform : null;
            if (o == null || IsShown(o)) return hit; // you can see it: it is the thing
            var holder = o.parent;
            if (holder == null) return hit;

            if (!_cache.TryGetValue(holder, out var cache))
            {
                cache = new ProxyCache();
                foreach (var col in holder.GetComponentsInChildren<Collider>(true))
                {
                    if (col.transform == o) continue;
                    if (col.GetComponent<PlaneShellTag>() != null) continue; // owned by planedamage.js's port
                    if (Regex.IsMatch(col.name, "_shell$|_collider$")) continue;
                    cache.Real.Add(col);
                }
                _cache[holder] = cache;
            }
            if (cache.Real.Count == 0) return hit; // a baked copy: it IS the shape

            var live = new List<Collider>(cache.Real.Count);
            foreach (var c in cache.Real)
                if (c != null && c.enabled && IsShown(c.transform)) live.Add(c);
            if (live.Count == 0) return hit;

            if (cache.Far < 0f)
            {
                var b = new Bounds(holder.position, Vector3.zero);
                foreach (var c in cache.Real) b.Encapsulate(c.bounds);
                cache.Far = b.size.magnitude * 1.05f;
            }

            Vector3 from = hit.point - dir * PxBack;
            var hits = Physics.RaycastAll(new Ray(from, dir), PxBack + cache.Far);
            float best = float.MaxValue;
            RaycastHit bestHit = default;
            bool found = false;
            foreach (var h in hits)
            {
                if (!live.Contains(h.collider)) continue;
                if (h.distance < best) { best = h.distance; bestHit = h; found = true; }
            }
            return found ? bestHit : (RaycastHit?)null;
        }

        /// <summary>Drop the cache for a holder whose geometry changed (a crumble stage, a
        /// prop toggled off). Cheap enough to call liberally; it just forces a rebuild.</summary>
        public static void Invalidate(Transform holder) => _cache.Remove(holder);
    }

    // PlaneShellTag moved to Combat/PlaneShellTag.cs — a MonoBehaviour must
    // live in a file named after it or it cannot be serialised into a scene a
    // PLAYER can load (see that file's header).

    /// <summary>One round in flight, plus the result of a resolved impact — mirrors the
    /// `info` object src/ballistics.js's _resolve builds for onImpact.</summary>
    public sealed class Bullet
    {
        public Cartridge Cart;
        public Vector3 Pos;
        public Vector3 Vel;
        public string From;
        public float T;
        public float Dist;
        public int Segs;
        public float Trail; // m of visible streak behind the round (VFX hook, not simulated here)
    }

    public sealed class ImpactInfo
    {
        public Bullet Bullet;
        public Cartridge Cart;
        public RaycastHit Hit;
        public Vector3 Point;
        public Vector3 Normal;
        public Vector3 Dir;
        public string Surface;
        public SurfaceProps S;
        public float Energy;
        public float Speed;
        public float Graze;
        public float Depth;
        public float Range;
        public string From;
        public float HoleW;
        public float HoleL;
        public bool Ricochet;
        public bool Through;
        public Vector3? Exit;
        public float ExitEnergy;
        public int Damage;
        public bool Lodged;
    }

    /// <summary>
    /// Port of src/ballistics.js BulletSim. Hand-rolled quadratic-drag
    /// integrator — do NOT replace with Rigidbody/PhysX projectile physics.
    /// </summary>
    public sealed class BulletSim
    {
        public const int MaxLive = 64;
        public const float MinEnergy = 12f;    // J — below this it's a falling lump of lead
        public const int MaxSegments = 4;      // penetrations + ricochets before retiring
        public const float MaxFlight = 3.2f;   // s
        public const float Skin = 0.012f;      // m past a face the next segment starts
        const float G = 9.81f;

        public readonly List<Bullet> Live = new List<Bullet>();
        public LayerMask TargetMask = ~0;
        public IBallisticsWorld World;
        /// <summary>Hook for planedamage.js's eventual port: re-casts a hit on a plane's
        /// box-shell collider onto the real airframe panel. Returning null means the
        /// crate lied and the round passed through (fly on); returning the same hit
        /// (or a different one) means a real surface was found.</summary>
        public Func<RaycastHit, Vector3, RaycastHit?> PlaneRefine;
        public Action<ImpactInfo> OnImpact;
        /// <summary>Injectable in [0,1) — defaults to UnityEngine.Random.value. The port-check
        /// probes swap this for a seeded generator (the same mulberry32 the JS-side probe
        /// seeds Math.random with) so a ricochet/perforation DECISION, not just the
        /// deterministic formulas around it, can be diffed byte-for-byte against the JS.</summary>
        public Func<float> NextRandom = () => UnityEngine.Random.value;

        public Bullet Spawn(Cartridge cart, Vector3 origin, Vector3 dir, string from = "pistol")
        {
            if (Live.Count >= MaxLive) Live.RemoveAt(0);
            var b = new Bullet
            {
                Cart = cart, Pos = origin, Vel = dir.normalized * cart.V0, From = from,
                T = 0f, Dist = 0f, Segs = 0, Trail = 0f,
            };
            Live.Add(b);
            return b;
        }

        public void Update(float dt)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
                if (!Step(Live[i], dt)) Live.RemoveAt(i);
        }

        // src/ballistics.js:505 _step — one frame of flight for one round.
        bool Step(Bullet b, float dt)
        {
            b.T += dt;
            if (b.T > MaxFlight) return false;

            float v0 = b.Vel.magnitude;
            if (v0 < 40f) return false; // tumbling, out of the game

            // Quadratic drag integrated exactly over the step: v = v0/(1 + k*v0*dt).
            float v1 = v0 / (1f + b.Cart.K * v0 * dt);
            Vector3 dPre = b.Vel / v0;        // unit heading, pre-gravity
            b.Vel *= v1 / v0;
            b.Vel.y -= G * dt;

            Vector3 p0 = b.Pos;
            Vector3 p1 = b.Pos + b.Vel * (dt * 0.5f) + dPre * (v0 * dt * 0.5f);
            float seg = Vector3.Distance(p0, p1);
            if (seg < 1e-5f) return true;
            Vector3 d = (p1 - p0) / seg;

            float energy = 0.5f * b.Cart.MassKg * v1 * v1;
            if (energy < MinEnergy) return false;

            bool hasHit = Physics.Raycast(new Ray(p0, d), out RaycastHit hit, seg, TargetMask);
            if (!hasHit)
            {
                b.Pos = p1;
                b.Dist += seg;
                b.Trail = Mathf.Min(2.6f, b.Trail + seg * 0.35f);
                return b.Dist < 600f;
            }

            // AN AEROPLANE'S / A STAND-IN'S COLLIDER IS A CRATE, NOT THE REAL THING.
            RaycastHit? real = PlaneRefine != null ? PlaneRefine(hit, d) : hit;
            RaycastHit? land = real == null ? (RaycastHit?)null : ProxyRefine.Refine(real.Value, d);
            if (land == null)
            {
                b.Pos = hit.point + d * Skin;
                b.Dist += hit.distance + Skin;
                return b.Dist < 600f;
            }
            b.Dist += Vector3.Distance(p0, land.Value.point);
            b.Pos = land.Value.point;
            return Resolve(b, land.Value, d, energy, v1);
        }

        static Vector3 FaceNormal(RaycastHit hit, Vector3 dir)
        {
            Vector3 n = hit.normal;
            if (Vector3.Dot(n, dir) > 0f) n = -n; // always face the shooter
            return n;
        }

        // src/ballistics.js:564 _resolve — dig in, punch through, or skip off.
        // Public (not just internal) so the port-check probe can drive it directly with a
        // synthetic hit, the same way tools/_probe_ballistics.mjs calls sim._resolve(...)
        // directly on the real JS instance (ballistics.js's "_resolve" is a plain method,
        // not a # private field, so the JS probe can reach it too).
        public bool Resolve(Bullet b, RaycastHit hit, Vector3 dir, float energy, float speed)
        {
            string key = SurfaceClassifier.Classify(hit, World, hit.point);
            SurfaceProps S = Surfaces.Of(key);
            Vector3 n = FaceNormal(hit, dir);

            float cosI = Mathf.Min(1f, Mathf.Abs(Vector3.Dot(n, dir)));
            float graze = Mathf.Asin(Mathf.Clamp(cosI, 0f, 1f));
            float pathT = S.Thick / Mathf.Max(0.12f, cosI);
            float cost = S.Resist * 1e6f * b.Cart.AreaM2 * pathT;
            float depth = energy / (S.Resist * 1e6f * b.Cart.AreaM2);

            bool ricochet = false;
            if (S.Graze > 0f && graze < S.Graze && b.Segs < MaxSegments && energy > 70f)
            {
                float p = 1f - graze / S.Graze;
                ricochet = NextRandom() < p * 0.95f;
            }

            var info = new ImpactInfo
            {
                Bullet = b, Cart = b.Cart, Hit = hit, Point = hit.point, Normal = n, Dir = dir,
                Surface = key, S = S, Energy = energy, Speed = speed, Graze = graze, Depth = depth,
                Range = b.Dist, From = b.From,
                HoleW = (b.Cart.BulletMm / 1000f) * S.Hole,
                HoleL = (b.Cart.BulletMm / 1000f) * S.Hole / Mathf.Max(0.3f, Mathf.Sin(Mathf.Max(0.1f, graze))),
                Ricochet = ricochet, Through = false, Exit = null,
                Damage = Mathf.Max(1, Mathf.RoundToInt(energy * Cartridges.HpPerJoule * (S.Hull))),
            };

            if (ricochet)
            {
                float keep = 0.35f + 0.35f * (1f - graze / S.Graze);
                Vector3 outv = Vector3.Reflect(dir, n);
                outv -= n * (0.45f * Vector3.Dot(outv, n)); // flatten against the wall
                outv.x += (NextRandom() - 0.5f) * 0.18f;
                outv.y += (NextRandom() - 0.5f) * 0.14f;
                outv.z += (NextRandom() - 0.5f) * 0.18f;
                outv.Normalize();
                b.Vel = outv * (speed * Mathf.Sqrt(keep));
                b.Pos += outv * Skin;
                b.Segs++;
                b.Trail = 0f;
                OnImpact?.Invoke(info);
                return true;
            }

            if (energy > cost && b.Segs < MaxSegments)
            {
                float left = energy - cost;
                float vOut = Mathf.Sqrt((2f * left) / b.Cart.MassKg);
                info.Through = true;
                info.ExitEnergy = left;
                Vector3 exit = hit.point + dir * pathT;
                info.Exit = exit;
                float wob = Mathf.Min(0.09f, 0.02f + 0.07f * (cost / energy));
                Vector3 outv = dir;
                outv.x += (NextRandom() - 0.5f) * wob;
                outv.y += (NextRandom() - 0.5f) * wob;
                outv.z += (NextRandom() - 0.5f) * wob;
                outv.Normalize();
                b.Vel = outv * vOut;
                b.Pos = exit + outv * Skin;
                b.Segs++;
                b.Trail = 0f;
                OnImpact?.Invoke(info);
                return vOut > 60f;
            }

            info.Lodged = true;
            OnImpact?.Invoke(info);
            return false;
        }
    }
}
