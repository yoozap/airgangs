using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;
using TbilisiFly.Fx; // Fx.PlaneMarkFx — the five mark layers' renderer, below

namespace TbilisiFly.Combat
{
    // =========================================================================
    // SHOOTING AN AEROPLANE — port of src/planedamage.js (1998 lines)
    // =========================================================================
    // Three things had to be true before this looked real, and all three are
    // visible from a metre away (src/planedamage.js:9-49):
    //   1. the round has to actually reach the airframe (Refine, below) — the
    //      thing a gun hits first is a crude climb-shell box, invisible, a foot
    //      proud of the real skin in places;
    //   2. it has to know what it hit (Mark/Trace classify skin/perspex/rubber,
    //      canopy/engine/tyre, by material + name regex, same as the JS);
    //   3. the damage has to stay on the aeroplane in ITS OWN frame, not the
    //      world, or it hangs in the air once the machine has flown off.
    //
    // HANDEDNESS — the local-frame boundary for this whole file:
    // Every position/normal this file stores (PlaneTear.x/y/z/nx/ny/nz, every
    // PlaneMark.Local) lives in the OWNING WRAPPER'S OWN LOCAL SPACE, reached via
    // `wrapper.worldToLocalMatrix` / `wrapper.localToWorldMatrix`. Unity's world
    // frame is already correctly mirrored at its source (every plane's Transform
    // was placed there by GeoFrame-aware code upstream), so converting a WORLD
    // point into a plane's LOCAL frame via its own matrices needs no further
    // mirroring — that matrix multiply is not a cross product and does not
    // reverse under a handedness flip. The one cross product in this file
    // (Basis() below, building a decal's tangent frame from a normal + an
    // in-plane "along" direction) is the SAME operation ImpactFx.cs's
    // DecalLayer.Stamp already had to solve, so it is solved the same way:
    // the JS builds `y = cross(z, x)`; here the operands are SWAPPED,
    // `y = Cross(x, z)`, to keep (x, y, z) a proper, unmirrored frame in
    // Unity's left-handed convention (see Combat/ImpactFx.cs:101-106's note,
    // which this file's Stamp() cites directly rather than re-deriving).
    // =========================================================================

    /// <summary>One opening in an airframe's skin, in the WRAPPER'S OWN local frame.
    /// src/planedamage.js's tear object (`_feed`/`_tear`).</summary>
    public sealed class PlaneTear
    {
        public float X, Y, Z;
        public float Nx, Ny, Nz;
        public int N;           // rounds fed into this opening
        public float Cal;       // largest bullet diameter (m) that has hit it
        public float R;         // current radius (m) — src/planedamage.js:1648 tearRadius
        public float Bridge;    // this opening's own join reach (m) — src/planedamage.js:1655 bridgeOf
        public string Part;     // "fus" | "wingL" | "wingR" | "tail"
        public PlaneMark Mark;
    }

    /// <summary>One live decal, kept in the owner's local frame so it rides the airframe
    /// (src/planedamage.js:1374 _stamp's `mark.local`).</summary>
    public sealed class PlaneMark
    {
        public PlaneMarkLayer Layer;
        public int Index;
        public Matrix4x4 Local;
        public bool Dropped;
    }

    /// <summary>A classified damageable renderer on an airframe (src/planedamage.js:599
    /// _partsOf). `RaycastCollider` is the collider Refine()/exit-raycasts hit-test
    /// against — lazily created, the Unity analogue of the JS's lazily-built BVH
    /// (_bvhLater): the first round on a machine pays for it, the burst that follows
    /// does not.</summary>
    internal struct ClassifiedPart
    {
        public Renderer R;
        public Transform T;
        public string Surface; // "skin" | "perspex" | "rubber"
        public string Part;    // "skin" | "canopy" | "tyre" | "engine"
        public bool Spin;
        public Collider RaycastCollider;
        public bool IsSkinned;
    }

    internal sealed class PlanePartsCache
    {
        public readonly List<ClassifiedPart> Skin = new List<ClassifiedPart>();
        public readonly List<ClassifiedPart> Glass = new List<ClassifiedPart>();
        public readonly List<ClassifiedPart> All = new List<ClassifiedPart>();
        public bool CollidersReady;
    }

    /// <summary>
    /// THE METAL REALLY GOES. Every tear this set holds is written into a small data
    /// texture in the airframe's own frame; a fragment shader (Assets/Shaders/PlaneHull.shader)
    /// walks it and `clip()`s any pixel inside one, so a hole shows daylight (or the
    /// hangar floor) through the skin instead of a painted-on sticker.
    ///
    /// src/planedamage.js's HoleSet patches onBeforeCompile onto a CLONED material per
    /// mesh so thirty-six aeroplanes of the same type don't all show the same holes —
    /// three.js has no stable way to bind per-renderer uniforms to a shared material.
    /// Unity does: materials here are unique Material INSTANCES per renderer (created
    /// once, in Patch(), copying the look of whatever was there before onto
    /// Shader "TbilisiFly/PlaneHull" — see CopyLook below), so "clone first" is honoured
    /// literally, and the per-renderer tear range (Lo/Hi) and the shared tear texture
    /// are just properties on that unique instance — no MaterialPropertyBlock needed.
    /// </summary>
    public sealed class HoleSet
    {
        public const int TearW = 64;
        public const int TearH = 32;
        public const int HoleSlots = TearW * TearH; // 2048 — src/planedamage.js:215-217
        public const float Void = 0.95f;            // src/planedamage.js:223

        public readonly Texture2D Tex;
        readonly Color[] _data = new Color[HoleSlots]; // xyz = local pos, w = r * VOID
        public int TearAll;      // openings belonging to no panel (a severed piece's own)
        public int TearCount;    // tally, for probes
        public Matrix4x4 TearInv = Matrix4x4.identity; // world -> airframe-local

        readonly Dictionary<Renderer, (int lo, int hi)> _ranges = new Dictionary<Renderer, (int, int)>();
        readonly HashSet<Material> _patched = new HashSet<Material>();
        public int Done, Failed; // src/planedamage.js:314/339 — probe-visible hook counters

        public HoleSet()
        {
            Tex = new Texture2D(TearW, TearH, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "PlaneTearMap",
            };
        }

        (int lo, int hi) RangeFor(Renderer mesh)
        {
            if (mesh == null) return (0, 0);
            if (!_ranges.TryGetValue(mesh, out var r)) { r = (0, 0); _ranges[mesh] = r; }
            return r;
        }

        /// <summary>src/planedamage.js:282 keepOnly — a wing came off; its panels stop
        /// being walked for openings on the stump.</summary>
        public void KeepOnly(IEnumerable<Renderer> live)
        {
            var keep = new HashSet<Renderer>(live);
            var dead = new List<Renderer>();
            foreach (var k in _ranges.Keys) if (!keep.Contains(k)) dead.Add(k);
            foreach (var k in dead) _ranges.Remove(k);
        }

        /// <summary>src/planedamage.js:291 patch — give one renderer the ability to lose
        /// pixels. Builds (or reuses) a unique Material on Shader "TbilisiFly/PlaneHull",
        /// copying the original material's look across, and assigns it back.</summary>
        public void Patch(Renderer r)
        {
            if (r == null) return;
            var shader = Shader.Find("TbilisiFly/PlaneHull");
            if (shader == null) { Failed++; return; }
            var mats = r.sharedMaterials;
            bool any = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (_patched.Contains(m)) { any = true; continue; }
                Material nu = m.shader == shader ? m : BuildHullMaterial(m, shader);
                mats[i] = nu;
                _patched.Add(nu);
                any = true;
            }
            if (any)
            {
                r.sharedMaterials = mats;
                Done++;
                _ranges[r] = RangeFor(r); // ensure an entry exists; Write() fills lo/hi
            }
            else Failed++;
        }

        static Material BuildHullMaterial(Material src, Shader hullShader)
        {
            var nu = new Material(hullShader) { name = src.name + "_hull" };
            CopyIfPresent(src, nu, "_BaseMap");
            CopyIfPresent(src, nu, "_BaseColor");
            CopyIfPresent(src, nu, "_Color");
            CopyIfPresent(src, nu, "_BumpMap");
            CopyIfPresent(src, nu, "_BumpScale");
            CopyIfPresent(src, nu, "_MetallicGlossMap");
            CopyIfPresent(src, nu, "_Metallic");
            CopyIfPresent(src, nu, "_Smoothness");
            CopyIfPresent(src, nu, "_Glossiness");
            CopyIfPresent(src, nu, "_OcclusionMap");
            CopyIfPresent(src, nu, "_OcclusionStrength");
            return nu;
        }

        static void CopyIfPresent(Material src, Material dst, string prop)
        {
            if (!src.HasProperty(prop) || !dst.HasProperty(prop)) return;
            var type = src.shader.GetPropertyType(src.shader.FindPropertyIndex(prop));
            switch (type)
            {
                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    dst.SetTexture(prop, src.GetTexture(prop)); break;
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                    dst.SetColor(prop, src.GetColor(prop)); break;
                default:
                    dst.SetFloat(prop, src.GetFloat(prop)); break;
            }
        }

        /// <summary>src/planedamage.js:352 write — every opening, laid into the map; the
        /// biggest first, shared per panel by FRACTION when the map would overflow.</summary>
        public void Write(List<PlaneTear> all, Matrix4x4? inv)
        {
            if (inv.HasValue) TearInv = inv.Value;
            int n = 0;
            int lit = 0;

            void Put(PlaneTear t)
            {
                if (n >= HoleSlots) return;
                _data[n] = new Color(t.X, t.Y, t.Z, t.R * Void);
                n++;
                lit++;
            }

            List<PlaneTear> ordered = all;
            if (all.Count > 1)
            {
                ordered = new List<PlaneTear>(all);
                ordered.Sort((a, b) => b.R.CompareTo(a.R)); // biggest first
            }

            if (_ranges.Count == 0)
            {
                foreach (var t in ordered) Put(t);
                TearAll = n;
            }
            else
            {
                TearAll = 0;
                var per = new List<(Renderer mesh, List<PlaneTear> mine)>();
                int want = 0;
                foreach (var meshKey in new List<Renderer>(_ranges.Keys))
                {
                    Bounds? box = MeshBoxLocal(meshKey, TearInv);
                    var mine = new List<PlaneTear>();
                    foreach (var t in ordered)
                    {
                        if (box.HasValue)
                        {
                            Vector3 p = new Vector3(t.X, t.Y, t.Z);
                            float d = DistanceToBounds(box.Value, p);
                            if (d > t.R) continue;
                        }
                        mine.Add(t);
                    }
                    per.Add((meshKey, mine));
                    want += mine.Count;
                }
                float share = want > HoleSlots ? (float)HoleSlots / want : 1f;
                foreach (var (mesh, mine) in per)
                {
                    int lo = n;
                    int take = share < 1f ? Mathf.Max(1, Mathf.FloorToInt(mine.Count * share)) : mine.Count;
                    for (int i = 0; i < take && i < mine.Count; i++) Put(mine[i]);
                    int hi = n;
                    _ranges[mesh] = (lo, hi);
                }
            }
            TearCount = lit;
            for (int i = n; i < HoleSlots; i++) _data[i] = new Color(0, 0, 0, 0);
            Tex.SetPixels(_data);
            Tex.Apply(false, false);
            // push the per-renderer range + shared state onto each unique material
            foreach (var kv in _ranges)
            {
                var mats = kv.Key.sharedMaterials;
                foreach (var m in mats)
                {
                    if (m == null || !_patched.Contains(m)) continue;
                    m.SetTexture("_TearTex", Tex);
                    m.SetInt("_TearAll", TearAll);
                    m.SetInt("_TearLo", kv.Value.lo);
                    m.SetInt("_TearHi", kv.Value.hi);
                    // ROWS, not a matrix: ShaderLab cannot declare a matrix
                    // property, and an undeclared member of the shader's
                    // UnityPerMaterial block does not get uploaded — which made
                    // the discard that opens a tear depend on how many other
                    // airframes had been hit first. See Shaders/PlaneHull.shader's
                    // Properties block.
                    m.SetVector("_TearInv0", TearInv.GetRow(0));
                    m.SetVector("_TearInv1", TearInv.GetRow(1));
                    m.SetVector("_TearInv2", TearInv.GetRow(2));
                    m.SetVector("_TearInv3", TearInv.GetRow(3));
                }
            }
        }

        static float DistanceToBounds(Bounds b, Vector3 p) => b.SqrDistance(p) <= 0f ? 0f : Mathf.Sqrt(b.SqrDistance(p));

        // src/planedamage.js:407 _meshBox — one panel's reach, in the airframe's frame.
        static Bounds? MeshBoxLocal(Renderer r, Matrix4x4 inv)
        {
            if (r == null || r is SkinnedMeshRenderer) return null; // bind-space bounds aren't measurable this way
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;
            Bounds local = mf.sharedMesh.bounds;
            Matrix4x4 m = inv * r.transform.localToWorldMatrix;
            Bounds? box = null;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3(
                    (i & 1) != 0 ? local.max.x : local.min.x,
                    (i & 2) != 0 ? local.max.y : local.min.y,
                    (i & 4) != 0 ? local.max.z : local.min.z);
                Vector3 p = m.MultiplyPoint3x4(c);
                if (box == null) box = new Bounds(p, Vector3.zero); else { var b = box.Value; b.Encapsulate(p); box = b; }
            }
            return box;
        }

        public void Dispose()
        {
            if (Tex != null) UnityEngine.Object.DestroyImmediate(Tex);
        }
    }

    /// <summary>A ring buffer of instanced marks, one per kind of damage (hole/tear/exit/
    /// scrape/craze), shared across the whole fleet — src/planedamage.js's MarkLayer.
    /// Pure data: `Pose`/`Tint`/`Live` drive whatever draws them (GPU instancing via
    /// Graphics.DrawMeshInstanced, or a VFX Graph bound to this as a GraphicsBuffer) —
    /// consistent with Combat/ImpactFx.cs's DecalLayer, which leaves rendering the same way.</summary>
    public sealed class PlaneMarkLayer
    {
        public readonly int Max;
        public readonly Matrix4x4[] Pose;
        public readonly Color32?[] Tint;
        public readonly bool[] Live;
        readonly PlaneMark[] _owners;
        int _cursor;
        public int Count { get; private set; }

        public PlaneMarkLayer(int max)
        {
            Max = max;
            Pose = new Matrix4x4[max];
            Tint = new Color32?[max];
            Live = new bool[max];
            _owners = new PlaneMark[max];
        }

        /// <summary>src/planedamage.js:465 alloc — the next ring slot; whatever was in it
        /// stops existing (its owning tear drops the mark on the next sync).</summary>
        public int Alloc(PlaneMark mark)
        {
            int i = _cursor;
            _cursor = (_cursor + 1) % Max;
            var prev = _owners[i];
            if (prev != null) prev.Dropped = true;
            _owners[i] = mark;
            Count = Mathf.Max(Count, i + 1);
            return i;
        }

        public void Set(int i, Matrix4x4 m, Color32? tint = null)
        {
            Pose[i] = m; Tint[i] = tint; Live[i] = true;
        }

        public void Clear(int i) { Live[i] = false; _owners[i] = null; }
        public void Hide(int i) { Live[i] = false; }
    }

    /// <summary>
    /// Port of src/planedamage.js's PlaneDamage class — the IPlaneDamageHooks
    /// implementation Combat/CombatWorldHooks.cs's interface was written against.
    /// </summary>
    public sealed class PlaneDamage : IPlaneDamageHooks
    {
        // ---- constants, verbatim from the JS (comments cite the source line) ----
        static readonly Dictionary<string, float> PartMul = new Dictionary<string, float>
        { { "canopy", 1.6f }, { "engine", 1.9f }, { "tyre", 0.45f }, { "skin", 1f } }; // :53
        const int CanopyHitsToShatter = 4;       // :54
        const float Lift = 0.085f, LiftMin = 0.0035f, LiftMax = 0.011f; // :69-71
        const float SmokeFrom = 0.62f, FireFrom = 0.3f;                // :72-73
        const float Seen = 280f;                                        // :74
        const float BackOff = 0.4f;                                     // :75

        const int CutMinTears = 4, CutMinHoles = 6;                    // :125-126
        const float BridgeCal = 11f, BridgeHot = 18f, BridgeE = 4000f, BridgeMin = 0.07f; // :127-130
        const float MergeCal = 3.5f;                                   // :131
        const int TearsMaxPerPart = 480;                               // :132 (budget note; ring layers bound the rest)
        const float CutEdge = 0.17f, CutEdgeMin = 0.28f;               // :139-140
        const float CutHull = 0.4f;                                    // :141
        const float CutMinLen = 0.6f;                                  // :142
        const int CutPieces = 10;                                      // :143

        // src/planedamage.js:1608-1645 "THE TEXTURES" — the design rationale
        // behind TearRadius/the mark-layer atlas, carried over here because it
        // explains constants declared on the next two lines (and because the
        // local SURF copy its own comment at :1600-1602 introduces is already
        // ported inline at this file's `holeS` ternary, cited ":1603-1607"):
        //
        // Drawn so the WHOLE mark fills one tile and the caller sizes the quad
        // in metres. *_RING is how many bullet diameters across the finished
        // mark is, which is also what puts the puncture at the right size
        // inside it: a 9 mm round on a 4.5 cm quad leaves a 9 mm hole.
        //
        // FOUR OF EVERYTHING. One drawing stamped over and over is the single
        // thing that makes damage read as stickers rather than damage — a
        // burst into a wing came out as the same grey splat repeated eight
        // times. So each layer's map is a 2x2 ATLAS of four separately drawn
        // marks and every instance picks one at random (PlaneMarkLayer's own
        // tile count), on top of which the quad is rotated by the flight
        // path, scaled by the angle and tinted by how shot-up the machine is.
        // Nothing within a magazine repeats.
        //
        // ...and nothing in them is CENTRED. A real bullet hole is a small
        // dark puncture with a thin, broken rim of bare metal and the paint
        // let go on ONE SIDE of it — not a tidy bright ring with a dot in the
        // middle, which is what this file drew first and what read as a
        // decal from two metres. HOW BIG THE HOLE IS after `n` rounds have
        // gone into it (see TearRadius below).
        //
        // `TORN` is the part that is not arithmetic. A round through a
        // millimetre of aluminium does not punch a neat hole its own width —
        // the skin TEARS, and it tears wider than the bullet. More to the
        // point, a 9 mm opening drawn at 9 mm is about two pixels across from
        // five paces: true to life, and from where the player stands
        // completely invisible. Three calibres reads as a hole you could put
        // a thumb through, which is what being shot at actually looks like on
        // a light airframe.
        //
        // After that it is arithmetic: area adds up, so the radius goes as
        // the root of the count — four rounds in one place is twice the
        // opening, sixteen is four times. A 9 mm starts at a 2.7 cm tear and
        // is a 12 cm gash by the sixteenth round. ...and a ceiling, or a long
        // burst into one place eats the aeroplane: at TORN_MAX (12) calibres
        // of radius the panel is simply gone (a 9 mm opens 21 cm, the belly
        // gun 31 cm) and further rounds through it change nothing.
        const float Torn = 3f, TornMax = 12f;                          // :1646-1647
        const float HoleRing = 3.0f, TearRingK = 2.4f, ExitRing = 2.6f; // :1661-1663 (quad sizing only — art deferred, see class header)

        static readonly Regex TyreRx = new Regex(
            "tyre|tire|wheel|lastik|rueda|roue|reifen|gomme|tekerlek|llanta", RegexOptions.IgnoreCase); // :152
        static readonly Regex EngineRx = new Regex(
            "engine|motor|turbine|nacelle|exhaust|propeller|pervane|airscrew|blade|h[eé]lice|helice|spinner|intake|jet",
            RegexOptions.IgnoreCase); // :153-154

        public readonly PlaneMarkLayer Holes = new PlaneMarkLayer(3200);   // :60
        public readonly PlaneMarkLayer Tears = new PlaneMarkLayer(1600);  // :133 TEAR_MAX
        public readonly PlaneMarkLayer Exits = new PlaneMarkLayer(1600);  // :61
        public readonly PlaneMarkLayer Scrapes = new PlaneMarkLayer(800); // :62
        public readonly PlaneMarkLayer Craze = new PlaneMarkLayer(800);   // :63
        PlaneMarkLayer[] AllLayers => new[] { Holes, Tears, Exits, Scrapes, Craze };

        // THE RENDERER for the five layers above (Fx/PlaneMarkFx.cs), plus the
        // atlas art Combat/DamageDecals.cs bakes for it. Owned here because in
        // the JS it is THIS class's constructor that builds each layer's mesh,
        // material and texture (src/planedamage.js:523-528 `new MarkLayer(s,
        // atlasTexture(drawSkinHole), ...)`) — and because it is the same
        // self-contained-field pattern Combat/ImpactFx.cs uses for
        // Fx/ImpactDecalFx.cs and Combat/Rockets.cs for Fx/RocketTrailFx.cs, so
        // no Core/ edit is needed to make damage visible: Update() is already
        // ticked every frame from Core/CombatWorldAdapter.cs:564.
        // Without this the five layers were computed and never drawn, which left
        // Shaders/PlaneHull.shader discarding the pixels inside each tear with no
        // hole art around it — the inverse of the rule at :1617-1623.
        // `internal` so Editor/Probes can read its per-layer Drawn* counts —
        // "a count you computed is not evidence anything was drawn".
        internal readonly PlaneMarkFx MarkFx = new PlaneMarkFx();

        public readonly HashSet<PlaneDamageRecord> Hurt = new HashSet<PlaneDamageRecord>();
        public readonly List<AirframeBreakup> Wrecks = new List<AirframeBreakup>();

        public IImpactSink Impacts;              // debris/sparks — see interface below
        public IBreakupWorld BreakupWorld;       // handed to lazily-created AirframeBreakup rigs
        public Func<PlaneDamageRecord, AirframeBreakup> ExternalBreakupLookup; // e.g. planephys' own rig
        public AirframeBreakup HeroBreakup;      // src/planedamage.js:1249 game.breakup
        public Transform HeroPlane;              // rec.wrapper === g.plane check
        public Func<float> Rng = () => UnityEngine.Random.value;
        public Func<Vector3> CameraPosition = () => Vector3.zero; // for _hull/_burn distance gating
        public Func<float> MasterVolume = () => 0.7f;

        readonly Dictionary<Collider, (PlaneDamageRecord rec, ClassifiedPart part)> _colliderInfo =
            new Dictionary<Collider, (PlaneDamageRecord, ClassifiedPart)>();
        readonly ConditionalWeakTable<Bullet, object> _rowHit = new ConditionalWeakTable<Bullet, object>();
        static readonly object RowHitMarker = new object(); // ConditionalWeakTable disallows a null value

        // ---- scratch (avoid per-call allocation in hot paths) ----
        readonly RaycastHit[] _hitBuf = new RaycastHit[32];

        // =====================================================================
        // ---- finding the real aeroplane under the hit (src/planedamage.js:557) ----
        // =====================================================================

        static PlaneDamageRecord RecOf(Collider c) => c == null ? null : c.GetComponentInParent<PlaneDamageRecord>();

        static bool Spins(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
                if (p.GetComponent<SpinningPartTag>() != null) return true;
            return false;
        }

        /// <summary>src/planedamage.js:567 refine. NEVER returns the collider-box hit:
        /// a part list with nothing damageable in it is "nothing here", not "the box".</summary>
        public RaycastHit? Refine(RaycastHit hit, Vector3 dir)
        {
            var rec = RecOf(hit.collider);
            if (rec == null) return hit; // not one of ours — unchanged, per the interface contract
            var parts = PartsOf(rec);
            if (parts.All.Count == 0) return null;
            EnsureColliders(rec, parts);

            Vector3 origin = hit.point - dir * BackOff;
            float far = BackOff + (rec.Size > 0f ? rec.Size : 24f) * 1.15f;

            // THE SKIN YOU SHOT, NOT THE ONE BEHIND IT. In three.js the hull is
            // DoubleSide (src/planes.js lineHullInside, and the hole patch's own
            // `side` note in src/planedamage.js:291) and a Raycaster HONOURS
            // material.side, so the JS always lands on the first surface along
            // the ray. Unity's mesh queries silently drop back-facing triangles
            // unless Physics.queriesHitBackfaces is on, so on an airframe whose
            // outer skin is wound inward the outer surface was INVISIBLE TO THE
            // RAY and the round landed on the next skin down — 0.45 m inside the
            // wing on `h6k`, 0.35 m on `carpet`. Everything downstream then
            // worked perfectly on the wrong surface: the tear was written, the
            // material was patched, the shader discarded its fragments, and not
            // one rendered pixel changed, because the opening was inside the
            // aeroplane. Measured in pixels before the fix (Editor/Probes/
            // RenderShotProbe's HOLEFORCE walk): a forced 0.15 m opening at the
            // hit point changed 0 px, and the same opening moved +0.35 m up the
            // airframe's own Y put it dead centre.
            //
            // Scoped to this one query (saved/restored) rather than set once at
            // boot: Combat/Ballistics.cs's penetration walk and the ground
            // probes both lean on back-face culling to tell "leaving a solid"
            // from "entering one", and Exit() below deliberately starts its ray
            // INSIDE the bay, where a back-face hit would be the entry wound.
            bool savedBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            bool found;
            RaycastHit h;
            try { found = RaycastParts(parts, origin, dir, far, out h); }
            finally { Physics.queriesHitBackfaces = savedBackfaces; }
            if (!found) return null;
            // A back-face hit reports the triangle's own normal, which points
            // AWAY from the incoming round. Every mark/decal/tear downstream
            // orients off this normal (src/planedamage.js stamps the quad along
            // it), so it is turned to face the shooter the way a correctly wound
            // surface already does.
            if (Vector3.Dot(h.normal, dir) > 0f) h.normal = -h.normal;
            return h;
        }

        bool RaycastParts(PlanePartsCache parts, Vector3 origin, Vector3 dir, float far, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            var ray = new Ray(origin, dir);
            foreach (var p in parts.All)
            {
                if (p.RaycastCollider == null || !p.RaycastCollider.enabled) continue;
                if (p.RaycastCollider.Raycast(ray, out RaycastHit h, far) && h.distance < bestDist)
                {
                    bestDist = h.distance; best = h; found = true;
                }
            }
            return found;
        }

        /// <summary>src/planedamage.js:599 _partsOf — the airframe, split by what it is
        /// made of. Box shell, cockpit furniture and the pilot are left out.</summary>
        PlanePartsCache PartsOf(PlaneDamageRecord rec)
        {
            if (rec.PartsCache != null) return rec.PartsCache;
            var cache = new PlanePartsCache();
            var kit = rec.CockpitKit;

            void Visit(Renderer r, bool skinned)
            {
                if (r == null || !r.enabled) return;
                if (r.GetComponent<PlaneShellTag>() != null || r.name == "plane_shell") return;
                if (r.name.StartsWith("cockpit_")) return;
                for (Transform p = r.transform; p != null; p = p.parent) if (p == kit) return;

                var mats = r.sharedMaterials;
                bool clear = false;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    if (IsTransparent(m) && Alpha(m) < 0.95f) { clear = true; break; }
                }
                string nm = r.name + " " + (r.transform.parent != null ? r.transform.parent.name : "");
                bool spin = Spins(r.transform);
                string surface = clear ? "perspex" : (TyreRx.IsMatch(nm) ? "rubber" : "skin");
                string part = clear ? "canopy" : TyreRx.IsMatch(nm) ? "tyre" : EngineRx.IsMatch(nm) ? "engine" : "skin";

                var cp = new ClassifiedPart { R = r, T = r.transform, Surface = surface, Part = part, Spin = spin, IsSkinned = skinned };
                (clear ? cache.Glass : cache.Skin).Add(cp);
                cache.All.Add(cp);
            }

            foreach (var mr in rec.GetComponentsInChildren<MeshRenderer>(true)) Visit(mr, false);
            foreach (var smr in rec.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Visit(smr, true);

            rec.PartsCache = cache;
            return cache;
        }

        static bool IsTransparent(Material m)
        {
            if (m.HasProperty("_Surface")) return m.GetFloat("_Surface") > 0.5f; // URP Lit: 0 opaque, 1 transparent
            return m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        static float Alpha(Material m)
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor").a;
            if (m.HasProperty("_Color")) return m.GetColor("_Color").a;
            return 1f;
        }

        /// <summary>src/planedamage.js:636 _bvhLater, done synchronously (Unity's MeshCollider
        /// bake is the BVH build here) the first time a machine is actually hit rather than
        /// on a setTimeout — same amortisation, simpler to reason about.</summary>
        void EnsureColliders(PlaneDamageRecord rec, PlanePartsCache parts)
        {
            if (parts.CollidersReady) return;
            parts.CollidersReady = true;
            for (int i = 0; i < parts.All.Count; i++)
            {
                var p = parts.All[i];
                var go = p.R.gameObject;
                var col = go.GetComponent<MeshCollider>();
                if (col == null) col = go.AddComponent<MeshCollider>();
                col.convex = false;
                if (p.IsSkinned)
                {
                    var smr = (SkinnedMeshRenderer)p.R;
                    var baked = new Mesh();
                    // useScale: TRUE — a MeshCollider's mesh is transformed by
                    // its own GameObject's localToWorldMatrix, so it needs the
                    // same space Flight/PlaneCockpitMeasurement.WorldVertices
                    // now uses. `carpet` and `neonfly` are the fleet's only
                    // skinned airframes and both import at scale 100: with the
                    // default overload their collision skin came out 100x the
                    // aeroplane and no round could ever land on it. See that
                    // file's note for the measurement.
                    smr.BakeMesh(baked, true);
                    col.sharedMesh = baked;
                }
                else
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null) col.sharedMesh = mf.sharedMesh;
                }
                if (go.GetComponent<PlaneSurfaceTag>() == null) go.AddComponent<PlaneSurfaceTag>();
                p.RaycastCollider = col;
                parts.All[i] = p;
                if (p.Surface == "skin" || p.Part == "canopy") { /* indexing kept in sync below */ }
                _colliderInfo[col] = (rec, p);
            }
            // keep Skin/Glass lists' RaycastCollider in sync with the All list we just filled
            SyncColliderRefs(parts.Skin, parts.All);
            SyncColliderRefs(parts.Glass, parts.All);
        }

        static void SyncColliderRefs(List<ClassifiedPart> list, List<ClassifiedPart> source)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i].R;
                for (int j = 0; j < source.Count; j++)
                {
                    if (source[j].R == r) { var cp = list[i]; cp.RaycastCollider = source[j].RaycastCollider; list[i] = cp; break; }
                }
            }
        }

        // =====================================================================
        // ---- the two ways in (src/planedamage.js:647) ----
        // =====================================================================

        /// <summary>src/planedamage.js:652 mark — the bulleted-sim path (IPlaneDamageHooks).</summary>
        public bool Mark(ImpactInfo info)
        {
            if (info.Hit.collider == null || !_colliderInfo.TryGetValue(info.Hit.collider, out var ci)) return false;
            var rec = ci.rec;
            var part = ci.part;
            float cal = info.Cart.BulletMm / 1000f;
            float dist = Vector3.Distance(CameraPosition(), info.Point);
            float vol = MasterVolume();

            if (part.Spin) Spinner(info, dist, vol);
            else if (part.Part == "canopy") Canopy(info, rec, cal, dist, vol);
            else if (part.Part == "tyre") Tyre(info, rec, cal, dist, vol);
            else Skin(info, rec, part, cal, dist, vol);
            return true;
        }

        /// <summary>src/planedamage.js:670 trace — a hitscan round (the belly gun): assumed
        /// square-on and straight through, like a heavy MG round through an airframe always is.</summary>
        public string Trace(RaycastHit hit, Vector3 point, Vector3 normal, Vector3 dir, Cartridge cart)
        {
            if (hit.collider == null || !_colliderInfo.TryGetValue(hit.collider, out var ci)) return null;
            var rec = ci.rec;
            var part = ci.part;
            float cal = cart.BulletMm / 1000f;
            float cosI = Mathf.Clamp(Mathf.Abs(Vector3.Dot(normal, dir)), 0.12f, 1f);
            Vector3 n = normal;
            if (Vector3.Dot(n, dir) > 0f) n = -n; // facing the shooter — :676-683

            var holeS = part.Surface == "perspex" ? 1.35f : part.Surface == "rubber" ? 1.1f : 1.18f; // :1603-1607 SURF table
            var info = new ImpactInfo
            {
                Hit = hit, Point = point, Normal = n, Dir = dir, Cart = cart,
                Energy = cart.Energy, Graze = Mathf.Asin(cosI), Through = true, Exit = null,
                HoleW = cal * holeS, HoleL = cal * holeS / cosI,
            };
            Mark(info);
            return part.Surface;
        }

        // ---- what each part does (src/planedamage.js:695) ----

        void Skin(ImpactInfo info, PlaneDamageRecord rec, ClassifiedPart part, float cal, float dist, float vol)
        {
            Vector3 point = info.Point, normal = info.Normal, dir = info.Dir;
            if (info.Ricochet)
            {
                float len = cal * (16f + 26f * (1f - info.Graze / 0.38f));
                Stamp(Scrapes, rec, point, normal, dir, len, cal * 3.4f, new Color32(0xd7, 0xda, 0xde, 255), 0.21f, null, part.RaycastCollider);
                Vector3 reflected = Vector3.Reflect(dir, normal);
                Impacts?.Sparks(point, 14, new Color32(0xff, 0xd0, 0x89, 255), reflected, 1.7f, 9f, 0.011f, 0.3f, 16f, 1f);
                Impacts?.HitSkinSound(dist, vol);
                Impacts?.RicochetWhine(dist, vol, info.Energy);
                return;
            }
            Color32 tint = Soot(rec);
            var t = Tear(rec, info, part.Part, tint);
            float heavy = (t != null && t.N > 1) ? 1.6f : 1f;
            Impacts?.Debris(point, Mathf.RoundToInt(5 * heavy), new Color32(0xb6, 0xb9, 0xbd, 255), normal, 0.9f, 3.4f, cal * 1.5f * heavy, 0.5f, 0.6f, 8f, cal * 2f);
            Impacts?.Sparks(point, Mathf.RoundToInt(11 * heavy), new Color32(0xff, 0xd0, 0x89, 255), normal, 0.8f, 8f, 0.011f, 0.3f, 16f, 1.2f);
            Impacts?.HitSkinSound(dist, vol);
            if (info.Through) Exit(info, rec, cal, tint);
            if (t != null) CheckCut(rec, t.Part);
        }

        void Canopy(ImpactInfo info, PlaneDamageRecord rec, float cal, float dist, float vol)
        {
            Vector3 point = info.Point, normal = info.Normal, dir = info.Dir;
            float web = cal * (13f + Rng() * 6f);
            Stamp(Craze, rec, point, normal, null, web, web * (0.85f + Rng() * 0.3f), new Color32(255, 255, 255, 255), 0f, null, null);
            Impacts?.Debris(point, 7, new Color32(0xdc, 0xf0, 0xff, 255), dir, 1.2f, 3.6f, cal * 1.3f, 0.2f, 0.5f, 15f, 0f);
            Impacts?.Sparks(point, 8, new Color32(0xcf, 0xea, 0xff, 255), dir, 1.1f, 4.6f, 0.008f, 0.42f, 17f, 0.8f);
            Impacts?.HitPerspexSound(dist, vol);
            rec.CanopyHits++;
            if (info.Through) Exit(info, rec, cal, new Color32(255, 255, 255, 255));
            if (rec.CanopyHits >= CanopyHitsToShatter) ShatterCanopy(rec, point, dist, vol);
        }

        void Tyre(ImpactInfo info, PlaneDamageRecord rec, float cal, float dist, float vol)
        {
            float w = info.HoleW * 3.4f;
            Stamp(Holes, rec, info.Point, info.Normal, info.Dir,
                w * Mathf.Min(1.7f, info.HoleL / Mathf.Max(1e-6f, info.HoleW)), w, new Color32(0x1b, 0x1b, 0x1e, 255));
            Impacts?.Debris(info.Point, 9, new Color32(0x26, 0x26, 0x2a, 255), info.Normal, 1f, 2.4f, cal * 2f, 0.4f, 0.7f, 10f, cal * 2f);
            Impacts?.HitSoftSound(dist, vol);
        }

        void Spinner(ImpactInfo info, float dist, float vol)
        {
            float cal = info.Cart.BulletMm / 1000f;
            Vector3 reflected = Vector3.Reflect(info.Dir, info.Normal);
            Impacts?.Sparks(info.Point, 16, new Color32(0xff, 0xd0, 0x89, 255), reflected, 1.9f, 11f, 0.012f, 0.32f, 16f, 1f);
            Impacts?.Debris(info.Point, 6, new Color32(0xb6, 0xb9, 0xbd, 255), info.Normal, 1.4f, 5f, cal * 1.6f, 0.5f, 0.5f, 9f, cal * 2.5f);
            Impacts?.HitSkinSound(dist, vol);
        }

        /// <summary>src/planedamage.js:799 _exit — the round crosses the bay and leaves
        /// through the FAR skin, half a metre away; found with one ray from inside the hole.</summary>
        void Exit(ImpactInfo info, PlaneDamageRecord rec, float cal, Color32 tint)
        {
            var parts = rec.PartsCache;
            if (parts == null || parts.All.Count == 0) return;
            Vector3 dir = info.Dir;
            Vector3 origin = info.Point + dir * 0.02f;
            float far = (rec.Size > 0f ? rec.Size : 24f) * 1.1f;
            if (!RaycastParts(parts, origin, dir, far, out RaycastHit h)) return;
            if (!_colliderInfo.TryGetValue(h.collider, out var ci)) return;
            Vector3 n = h.normal;
            if (Vector3.Dot(n, dir) < 0f) n = -n;
            if (ci.part.Spin) return;

            if (ci.part.Surface == "perspex")
            {
                float web = cal * (11f + Rng() * 5f);
                Stamp(Craze, rec, h.point, n, null, web, web, new Color32(255, 255, 255, 255), 0f, null, h.collider);
                return;
            }
            Transform w = rec.transform;
            Vector3 lp = w.InverseTransformPoint(h.point);
            Vector3 ln = w.InverseTransformDirection(n).normalized;
            var t = Feed(rec.Exits, lp, ln, cal * 2.1f, info.Cart, "exit");
            Vector3 at = w.TransformPoint(new Vector3(t.X, t.Y, t.Z));
            Vector3 up = w.TransformDirection(new Vector3(t.Nx, t.Ny, t.Nz)).normalized;
            float size = t.R * 2f * ExitRing;
            t.Mark = Stamp(Exits, rec, at, up, dir, size, size, tint, 0f, t.Mark, h.collider);
            SyncHoles(rec);
            Impacts?.Debris(h.point, 6, new Color32(0xb6, 0xb9, 0xbd, 255), n, 1.3f, 4.2f, cal * 1.7f, 0.3f, 0.55f, 9f, cal * 2.5f);
            Impacts?.Sparks(h.point, 7, new Color32(0xff, 0xc8, 0x79, 255), n, 1.1f, 7f, 0.01f, 0.26f, 16f, 1.2f);
        }

        void ShatterCanopy(PlaneDamageRecord rec, Vector3 at, float dist, float vol)
        {
            var parts = rec.PartsCache;
            if (parts == null || rec.CanopyGone || parts.Glass.Count == 0) return;
            rec.CanopyGone = true;
            foreach (var g in parts.Glass)
            {
                Bounds b = g.R.bounds;
                g.R.enabled = false;
                Impacts?.ShatterGlass(b.center, 0f, 0f, 9, 0.5f);
            }
            parts.Glass.Clear();
            parts.All.Clear();
            parts.All.AddRange(parts.Skin);
            ClearLayer(rec, Craze);
            Impacts?.GlassBreakSound(dist, vol, false);
        }

        // =====================================================================
        // ---- tears, and sawing a wing off (src/planedamage.js:865) ----
        // =====================================================================

        PlaneTear Tear(PlaneDamageRecord rec, ImpactInfo info, string partGuess, Color32 tint)
        {
            if (info.Bullet != null)
            {
                if (_rowHit.TryGetValue(info.Bullet, out _)) return null; // :882 one hole per round
                _rowHit.Add(info.Bullet, RowHitMarker);
            }
            Transform w = rec.transform;
            Vector3 p = w.InverseTransformPoint(info.Point);
            Vector3 n = w.InverseTransformDirection(info.Normal).normalized;
            string part = PartAt(rec, p);
            if (!rec.Tears.TryGetValue(part, out var list)) rec.Tears[part] = list = new List<PlaneTear>();
            var t = Feed(list, p, n, info.Cart.BulletMm / 1000f, info.Cart, part);
            DrawTear(rec, t, info, tint);
            SyncHoles(rec);
            return t;
        }

        /// <summary>src/planedamage.js:903 _feed — fold one more round into a list of
        /// openings. The nearest one close enough takes it and grows; area adds up, so
        /// radius goes as the root of the count.</summary>
        /// <summary>internal (not private) so Editor/Probes/DamageProbe.cs can feed
        /// synthetic rounds into a bare List&lt;PlaneTear&gt; and read back whether N hits
        /// at one spot grew ONE tear rather than collecting N separate openings —
        /// a named verification target in this port's brief.</summary>
        internal PlaneTear Feed(List<PlaneTear> list, Vector3 p, Vector3 n, float cal, Cartridge cart, string part)
        {
            PlaneTear hit = null;
            float near = float.MaxValue;
            foreach (var t in list)
            {
                float dx = t.X - p.x, dy = t.Y - p.y, dz = t.Z - p.z;
                float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < t.R + cal * MergeCal && d < near) { near = d; hit = t; }
            }
            if (hit != null)
            {
                hit.N++;
                float k = 1f / hit.N;
                hit.X += (p.x - hit.X) * k; hit.Y += (p.y - hit.Y) * k; hit.Z += (p.z - hit.Z) * k;
                hit.Nx = n.x; hit.Ny = n.y; hit.Nz = n.z;
                hit.Cal = Mathf.Max(hit.Cal, cal);
                hit.Bridge = Mathf.Max(hit.Bridge, BridgeOf(cart));
                hit.R = TearRadius(hit.Cal, hit.N);
                return hit;
            }
            if (list.Count >= TearsMaxPerPart)
            {
                var old = list[0];
                list.RemoveAt(0);
                if (old.Mark != null && !old.Mark.Dropped) { old.Mark.Dropped = true; old.Mark.Layer.Clear(old.Mark.Index); }
            }
            var nt = new PlaneTear
            {
                X = p.x, Y = p.y, Z = p.z, Nx = n.x, Ny = n.y, Nz = n.z,
                N = 1, Cal = cal, R = TearRadius(cal, 1), Bridge = BridgeOf(cart), Mark = null, Part = part,
            };
            list.Add(nt);
            return nt;
        }

        // src/planedamage.js:1648 tearRadius
        /// <summary>Public for Editor/Probes/DamageProbe.cs and tools/_probe_damage.mjs's
        /// side-by-side comparison — "tear radius for a given calibre" is a named target
        /// in this port's verification brief.</summary>
        public static float TearRadius(float cal, int n) => Mathf.Min(cal * TornMax, 0.5f * cal * Torn * (1f + 0.75f * Mathf.Pow(n - 1, 0.6f)));

        // src/planedamage.js:1655 bridgeOf
        public static float BridgeOf(Cartridge cart)
        {
            float d = (cart != null ? cart.BulletMm : 9f) / 1000f;
            float hot = Mathf.Min(1f, (cart != null ? cart.Energy : 580f) / BridgeE);
            return Mathf.Max(BridgeMin, d * (BridgeCal + BridgeHot * hot));
        }

        /// <summary>src/planedamage.js:1063's own join test, lifted out so
        /// Editor/Probes/DamageProbe.cs can prove "the join distance is the round's
        /// ENERGY" directly: two tears join when the metal between them is within
        /// what either round's own bridge reach can be expected to have ruined.</summary>
        public static bool Joined(PlaneTear a, PlaneTear b)
        {
            float bridge = Mathf.Max(a.Bridge, b.Bridge);
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            float rr = a.R + b.R + bridge;
            return dx * dx + dy * dy + dz * dz <= rr * rr;
        }

        /// <summary>src/planedamage.js:947 _partAt — by fuselage side first, then by how
        /// far aft; the centreline is the only thing the tail owns outright.</summary>
        string PartAt(PlaneDamageRecord rec, Vector3 p)
        {
            var lines = Lines(rec);
            if (lines == null) return "fus";
            var L = lines.Value;
            if (Mathf.Abs(p.x) > L.FusHalf) return p.x < 0 ? "wingL" : "wingR";
            // the JS's `p.z > L.tailZ`, reversed: AirframeLines is in the
            // FLIGHT frame (nose +Z) — see AirframeBreakup's constructor.
            return p.z < L.TailZ ? "tail" : "fus";
        }

        AirframeLines? Lines(PlaneDamageRecord rec)
        {
            var cutter = Cutter(rec);
            if (cutter == null) return null;
            try { return cutter.Lines(); } catch { return null; }
        }

        void DrawTear(PlaneDamageRecord rec, PlaneTear t, ImpactInfo info, Color32 tint)
        {
            Transform w = rec.transform;
            Vector3 at = w.TransformPoint(new Vector3(t.X, t.Y, t.Z));
            Vector3 up = w.TransformDirection(new Vector3(t.Nx, t.Ny, t.Nz)).normalized;
            var layer = t.N > 1 ? Tears : Holes;
            if (t.Mark != null && (t.Mark.Dropped || t.Mark.Layer != layer))
            {
                if (!t.Mark.Dropped) { t.Mark.Dropped = true; t.Mark.Layer.Clear(t.Mark.Index); }
                t.Mark = null;
            }
            float w0, h0;
            if (t.N > 1)
            {
                w0 = t.R * 2f * TearRingK;
                h0 = w0 * (0.85f + ((t.N * 37) % 30) / 100f);
            }
            else
            {
                float baseSize = t.R * 2f * HoleRing;
                float stretch = Mathf.Min(1.9f, info.HoleL / Mathf.Max(1e-6f, info.HoleW));
                w0 = baseSize * stretch;
                h0 = baseSize;
            }
            Collider fit = info.Hit.collider;
            t.Mark = Stamp(layer, rec, at, up, info.Dir, w0, h0, tint, 0f, t.Mark, fit);
        }

        /// <summary>src/planedamage.js:1046 _checkCut — connected runs by union-find; a run
        /// that reaches from one edge of the structure to the other severs it.</summary>
        void CheckCut(PlaneDamageRecord rec, string part)
        {
            if (part == "fus") return;
            if (rec.CutOff.Contains(part)) return;
            if (!rec.Tears.TryGetValue(part, out var list) || list.Count < CutMinTears) return;

            int n = list.Count;
            int[] up = new int[n];
            for (int i = 0; i < n; i++) up[i] = i;
            int Find(int i) { while (up[i] != i) { up[i] = up[up[i]]; i = up[i]; } return i; }

            for (int i = 0; i < n; i++)
            {
                var a = list[i];
                for (int j = i + 1; j < n; j++)
                {
                    var b = list[j];
                    float bridge = Mathf.Max(a.Bridge, b.Bridge);
                    float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                    float rr = a.R + b.R + bridge;
                    if (dx * dx + dy * dy + dz * dz <= rr * rr)
                    {
                        int ra = Find(i), rb = Find(j);
                        if (ra != rb) up[ra] = rb;
                    }
                }
            }
            var runs = new Dictionary<int, List<PlaneTear>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!runs.TryGetValue(r, out var l)) runs[r] = l = new List<PlaneTear>();
                l.Add(list[i]);
            }
            var order = new List<(List<PlaneTear> run, int rounds)>();
            foreach (var r in runs.Values)
            {
                if (r.Count < CutMinTears) continue;
                int rounds = 0; foreach (var t in r) rounds += t.N;
                order.Add((r, rounds));
            }
            order.Sort((a, b) => b.rounds.CompareTo(a.rounds));
            foreach (var (run, rounds) in order)
            {
                if (rounds < CutMinHoles) continue;
                var fit = Spans(rec, run, part);
                if (fit.HasValue && Sever(rec, part, fit.Value)) return;
            }
        }

        struct CutFit
        {
            public float Cx, Cy, Cz, Ux, Uz, Span, Across;
            public string Part;
            public Vector3 N;
            public List<PlaneTear> Row;
        }

        /// <summary>src/planedamage.js:1092 _spans — does this run of tears cross the
        /// structure? Measured on the real airframe from each end of the run's own axis.</summary>
        CutFit? Spans(PlaneDamageRecord rec, List<PlaneTear> run, string part)
        {
            float cx = 0, cy = 0, cz = 0, nx = 0, ny = 0, nz = 0;
            foreach (var t in run) { cx += t.X; cy += t.Y; cz += t.Z; nx += t.Nx; ny += t.Ny; nz += t.Nz; }
            int k = run.Count;
            cx /= k; cy /= k; cz /= k;

            float sxx = 0, sxz = 0, szz = 0;
            foreach (var t in run)
            {
                float dx = t.X - cx, dz = t.Z - cz;
                sxx += dx * dx; sxz += dx * dz; szz += dz * dz;
            }
            float th = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
            float ux = Mathf.Cos(th), uz = Mathf.Sin(th);

            var along = new List<(float t, PlaneTear h)>(k);
            foreach (var t in run) along.Add(((t.X - cx) * ux + (t.Z - cz) * uz, t));
            along.Sort((a, b) => a.t.CompareTo(b.t));
            float span = along[along.Count - 1].t - along[0].t;
            if (span < CutMinLen) { rec.WhyNotCut = $"short {span:F2}m"; return null; }

            float nl = Mathf.Sqrt(nx * nx + ny * ny + nz * nz); if (nl < 1e-6f) nl = 1f;
            Vector3 n3 = new Vector3(nx / nl, ny / nl, nz / nl);
            float ahead = GapTo(rec, along[along.Count - 1].h, n3, ux, uz, 1f);
            float behind = GapTo(rec, along[0].h, n3, ux, uz, -1f);
            float edge = Mathf.Max(CutEdgeMin, (span + ahead + behind) * CutEdge);
            if (ahead > edge || behind > edge)
            {
                rec.WhyNotCut = $"stops short ({ahead:F2}m / {behind:F2}m of {edge:F2}m)";
                return null;
            }
            rec.WhyNotCut = "cut";
            var row = new List<PlaneTear>(k);
            foreach (var a in along) row.Add(a.h);
            return new CutFit { Cx = cx, Cy = cy, Cz = cz, Ux = ux, Uz = uz, Span = span, Part = part, N = n3, Across = span + ahead + behind, Row = row };
        }

        /// <summary>src/planedamage.js:1140 _gapTo — how much material is left beyond a
        /// tear, along the run's own line, probed from just INSIDE the skin.</summary>
        float GapTo(PlaneDamageRecord rec, PlaneTear hole, Vector3 n, float ux, float uz, float s)
        {
            var parts = rec.PartsCache;
            if (parts == null || parts.Skin.Count == 0) return 0f;
            Transform w = rec.transform;
            Vector3 insideLocal = new Vector3(hole.X - n.x * 0.04f, hole.Y - n.y * 0.04f, hole.Z - n.z * 0.04f);
            Vector3 inside = w.TransformPoint(insideLocal);
            Vector3 d = w.TransformDirection(new Vector3(ux * s, 0f, uz * s)).normalized;
            float far = (rec.Size > 0f ? rec.Size : 20f) * 0.6f;
            if (!RaycastSkinOnly(parts, inside, d, far, out RaycastHit h)) return 0f;
            return h.distance;
        }

        bool RaycastSkinOnly(PlanePartsCache parts, Vector3 origin, Vector3 dir, float far, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            var ray = new Ray(origin, dir);
            foreach (var p in parts.Skin)
            {
                if (p.RaycastCollider == null || !p.RaycastCollider.enabled) continue;
                if (p.RaycastCollider.Raycast(ray, out RaycastHit h, far) && h.distance < bestDist)
                { bestDist = h.distance; best = h; found = true; }
            }
            return found;
        }

        /// <summary>src/planedamage.js:1158 _sever — take it off. Hands the half-space on
        /// the tip side of the break to AirframeBreakup.Cut, bounded to this part only.</summary>
        bool Sever(PlaneDamageRecord rec, string part, CutFit fit)
        {
            var cutter = Cutter(rec);
            var linesN = Lines(rec);
            if (cutter == null || !linesN.HasValue) return false;
            var L = linesN.Value;
            float cx = fit.Cx, cz = fit.Cz, ux = fit.Ux, uz = fit.Uz;
            float nx = -uz, nz = ux;
            Transform w = rec.transform;
            float halfSpan = rec.HalfSpan > 0f ? rec.HalfSpan : 6f;
            float tipX = part == "wingL" ? -halfSpan : part == "wingR" ? halfSpan : 0f;
            // planedamage.js:1170-1182, with every Z comparison reversed: these
            // are FLIGHT-frame coordinates (nose +Z) against a JS that measures
            // from a -Z nose — see Combat/AirframeBreakup's constructor. The
            // tail tip is aft, so it is -size, not +size; the "keep everything
            // inboard of the cut" bound becomes a MINIMUM instead of a maximum.
            float tipZ = part == "tail" ? -(rec.Size > 0f ? rec.Size : 12f) : cz;
            float sgn = Mathf.Sign((tipX - cx) * nx + (tipZ - cz) * nz); if (sgn == 0f) sgn = 1f;

            float zMin = L.TailZ;
            foreach (var h in fit.Row) zMin = Mathf.Min(zMin, h.Z - 0.6f);

            Func<Vector3, bool> baseTest = part == "tail"
                ? (Vector3 p) => p.z < L.TailZ
                : part == "wingL"
                    ? (Vector3 p) => p.x < -L.FusHalf && p.z > zMin
                    : (Vector3 p) => p.x > L.FusHalf && p.z > zMin;
            RegionTest region = p => baseTest(p) && sgn * ((p.x - cx) * nx + (p.z - cz) * nz) > 0f;
            float outSign = part == "wingL" ? -1f : part == "wingR" ? 1f : 0f;

            Vector3 impulse = new Vector3(
                outSign * (1.5f + Rng()), 0.8f + Rng(),
                (part == "tail" ? 2.5f : 0f) + (Rng() - 0.5f) * 1.5f);
            var piece = cutter.Cut($"shot:{part}:{Rng():F5}", region, impulse,
                spin: part == "tail" ? 3.5f : 2.2f, burn: false, flutter: true);
            if (piece == null) return false;

            rec.CutOff.Add(part);
            rec.PartsCache = null; // the airframe is a different shape now (:1198)

            bool Gone(Vector3 p) => baseTest(p) && sgn * ((p.x - cx) * nx + (p.z - cz) * nz) > -0.15f;
            PieceHoles(rec, piece, Gone);
            Rehome(rec, Gone, piece);
            foreach (var key in new List<string>(rec.Tears.Keys))
            {
                var l = rec.Tears[key];
                if (l == null) continue;
                l.RemoveAll(t => Gone(new Vector3(t.X, t.Y, t.Z)));
            }
            rec.Exits.RemoveAll(t => Gone(new Vector3(t.X, t.Y, t.Z)));
            SyncHoles(rec);

            Vector3 seam = w.TransformDirection(new Vector3(ux, 0f, uz)).normalized;
            foreach (var h in fit.Row)
            {
                Vector3 at = w.TransformPoint(new Vector3(h.X - sgn * nx * 0.1f, h.Y, h.Z - sgn * nz * 0.1f));
                Vector3 up = w.TransformDirection(new Vector3(h.Nx, h.Ny, h.Nz)).normalized;
                Stamp(Scrapes, rec, at, up, seam, 0.42f, 0.15f, new Color32(0xe7, 0xeb, 0xf0, 255));
            }
            CutFx(rec, fit, part);
            float took = Mathf.Min(1f, Mathf.Abs(tipX - cx) / Mathf.Max(0.5f, halfSpan * 0.75f));
            int damage = Mathf.RoundToInt((rec.MaxHullPoints > 0f ? rec.MaxHullPoints : 1f) * CutHull * took);
            Impacts?.DamagePlane(rec, damage);
            Rebake(rec);
            return true;
        }

        AirframeBreakup Cutter(PlaneDamageRecord rec)
        {
            if (rec.Cutter != null) return rec.Cutter;
            if (rec.IsPlayerOwn || rec.transform == HeroPlane)
            {
                rec.Cutter = HeroBreakup;
                return rec.Cutter;
            }
            var external = ExternalBreakupLookup?.Invoke(rec);
            if (external != null) { rec.Cutter = external; return rec.Cutter; }
            var made = new AirframeBreakup(BreakupWorld, rec.transform, null, false) { SizeHint = rec.Size > 0f ? rec.Size : 11f, Rng = Rng };
            Wrecks.Add(made);
            rec.Cutter = made;
            return made;
        }

        /// <summary>src/planedamage.js:1265 _rehome — the holes/marks go with the wing.</summary>
        void Rehome(PlaneDamageRecord rec, Func<Vector3, bool> region, BreakupPiece piece)
        {
            if (rec.Marks.Count == 0 || piece?.Group == null) return;
            Transform w = rec.transform;
            Matrix4x4 inv = piece.Group.worldToLocalMatrix;
            var keep = new List<PlaneMark>();
            PieceRider rider = null;
            foreach (var mk in rec.Marks)
            {
                if (mk.Dropped) continue;
                Vector3 pLocal = mk.Local.MultiplyPoint3x4(Vector3.zero); // local origin in wrapper frame
                if (!region(pLocal)) { keep.Add(mk); continue; }
                Matrix4x4 worldM = w.localToWorldMatrix * mk.Local;
                mk.Local = inv * worldM;
                rider ??= new PieceRider { Obj = piece.Group };
                rider.Marks.Add(mk);
            }
            rec.Marks.Clear();
            rec.Marks.AddRange(keep);
            if (rider != null) _riders.Add(rider);
        }

        sealed class PieceRider
        {
            public Transform Obj;
            public readonly List<PlaneMark> Marks = new List<PlaneMark>();
            public Matrix4x4? LastWorld;
            public HoleSet HoleSet; // a severed piece's own openings, if any
        }
        readonly List<PieceRider> _riders = new List<PieceRider>();

        /// <summary>src/planedamage.js:1289 _pieceHoles — a severed piece's own openings,
        /// carried into its own frame, on its own cloned/patched materials.</summary>
        void PieceHoles(PlaneDamageRecord rec, BreakupPiece piece, Func<Vector3, bool> gone)
        {
            if (rec.Holes == null || piece?.Group == null) return;
            Matrix4x4 toPiece = piece.Group.worldToLocalMatrix * rec.transform.localToWorldMatrix;
            var mine = new List<PlaneTear>();
            void Collect(List<PlaneTear> list)
            {
                if (list == null) return;
                foreach (var t in list)
                {
                    if (!gone(new Vector3(t.X, t.Y, t.Z))) continue;
                    Vector3 p = toPiece.MultiplyPoint3x4(new Vector3(t.X, t.Y, t.Z));
                    mine.Add(new PlaneTear { X = p.x, Y = p.y, Z = p.z, R = t.R });
                }
            }
            foreach (var l in rec.Tears.Values) Collect(l);
            Collect(rec.Exits);
            if (mine.Count == 0) return;
            var set = new HoleSet();
            foreach (var mr in piece.Group.GetComponentsInChildren<MeshRenderer>(true)) set.Patch(mr);
            set.Write(mine, piece.Group.worldToLocalMatrix);
            var rider = _riders.Find(r => r.Obj == piece.Group);
            if (rider == null) { rider = new PieceRider { Obj = piece.Group }; _riders.Add(rider); }
            rider.HoleSet = set;
        }

        void CutFx(PlaneDamageRecord rec, CutFit fit, string part)
        {
            Transform w = rec.transform;
            Vector3 at = w.TransformPoint(new Vector3(fit.Cx, fit.Cy, fit.Cz));
            Vector3 along = w.TransformDirection(new Vector3(fit.Ux, 0f, fit.Uz)).normalized;
            float half = Mathf.Max(0.4f, fit.Across * 0.5f);
            for (int i = 0; i < 16; i++)
            {
                float t = (i / 15f - 0.5f) * 2f * half;
                Vector3 p = at + along * t;
                Impacts?.Sparks(p, 5, new Color32(0xff, 0xc8, 0x79, 255), Vector3.up, 1.5f, 6f, 0.012f, 0.4f, 15f, 1.1f);
                if (i % 4 == 0) Impacts?.SpawnEmber(p);
            }
            Impacts?.SpawnSmokeWide(at, 3.4f, 0.72f, 2.2f, 1.6f, half);
            Impacts?.MetalTearSound(Vector3.Distance(CameraPosition(), at), MasterVolume(), half);
            Impacts?.ShakeGround(0.12f, at.x, at.z);
            Impacts?.SetHint(part == "tail"
                ? $"✂ {rec.DisplayName} — კუდი მოწყდა!"
                : $"✂ {rec.DisplayName} — ფრთა მოწყდა!");
        }

        /// <summary>src/planedamage.js:1347 _rebake — the collider is a baked copy of the box
        /// shell; rebake it after a cut so the hole the wing left can be walked/flown through.</summary>
        void Rebake(PlaneDamageRecord rec)
        {
            var shellTag = rec.GetComponentInChildren<PlaneShellTag>();
            if (shellTag == null) return;
            var shellFilter = shellTag.GetComponent<MeshFilter>();
            var shellCollider = shellTag.GetComponent<MeshCollider>();
            if (shellFilter == null || shellFilter.sharedMesh == null || shellCollider == null) return;
            // Unity's MeshCollider already reads the CURRENT sharedMesh live; forcing a rebake
            // is a reassign-to-self (triggers PhysX to rebuild its internal representation).
            var m = shellCollider.sharedMesh;
            shellCollider.sharedMesh = null;
            shellCollider.sharedMesh = m;
        }

        // ---- stamping (src/planedamage.js:1360) ----

        /// <summary>src/planedamage.js:1374 _stamp — one mark in the aeroplane's own frame.
        /// Cross product note: y = Cross(x, z), operands SWAPPED from the JS's
        /// cross(z, x) — see Combat/ImpactFx.cs:101-106 and this file's class header.</summary>
        PlaneMark Stamp(PlaneMarkLayer layer, PlaneDamageRecord rec, Vector3 point, Vector3 normal,
            Vector3? along, float w, float h, Color32 tint, float slide = 0f, PlaneMark reuse = null, Collider fit = null)
        {
            Transform owner = rec.transform;
            Vector3 z = normal.normalized;
            Vector3 x;
            if (along.HasValue && along.Value.sqrMagnitude > 1e-8f)
            {
                Vector3 a = along.Value;
                x = a - z * Vector3.Dot(a, z);
                if (x.sqrMagnitude < 1e-8f) x = Vector3.right;
            }
            else
            {
                float a = Rng() * Mathf.PI * 2f;
                x = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                x -= z * Vector3.Dot(x, z);
                if (x.sqrMagnitude < 1e-8f) x = new Vector3(z.y, -z.x, 0f);
            }
            x.Normalize();
            Vector3 y = Vector3.Cross(x, z).normalized; // SWAPPED vs JS cross(z, x) — see header
            if (fit != null)
            {
                float k = DecalGeometry.SurfaceFit(fit, point, x, y, z, w, h);
                w *= k; h *= k;
            }
            float lift = Mathf.Clamp(Mathf.Max(w, h) * Lift, LiftMin, LiftMax);
            Vector3 p = point + z * lift;
            if (slide != 0f) p += x * (slide * w);
            Quaternion rot = Quaternion.LookRotation(z, y);
            Matrix4x4 world = Matrix4x4.TRS(p, rot, new Vector3(w, h, 1f));

            bool keep = reuse != null && !reuse.Dropped && reuse.Layer == layer;
            var mark = keep ? reuse : new PlaneMark { Layer = layer, Index = 0, Local = Matrix4x4.identity, Dropped = false };
            mark.Local = owner.worldToLocalMatrix * world;
            if (!keep)
            {
                mark.Index = layer.Alloc(mark);
                rec.Marks.Add(mark);
            }
            layer.Set(mark.Index, world, tint);
            Hurt.Add(rec);
            return mark;
        }

        void ClearLayer(PlaneDamageRecord rec, PlaneMarkLayer layer)
        {
            foreach (var mk in rec.Marks)
            {
                if (mk.Dropped || mk.Layer != layer) continue;
                mk.Dropped = true;
                layer.Clear(mk.Index);
            }
        }

        Color32 Soot(PlaneDamageRecord rec)
        {
            byte k = (byte)Mathf.Round(255 * Mathf.Clamp(0.55f + Hull(rec) * 0.45f, 0.4f, 1f));
            return new Color32(k, k, k, 255);
        }

        float Hull(PlaneDamageRecord rec)
        {
            if (rec.Dead) return 0f;
            float hp = rec.HpGetter != null ? rec.HpGetter() : rec.HullPoints;
            float max = rec.MaxHullPoints > 0f ? rec.MaxHullPoints : 1f;
            return Mathf.Clamp01(hp / max);
        }

        /// <summary>src/planedamage.js:1440 watch — damage with no bullet mark (a rocket,
        /// another aeroplane) still makes the machine smoke/burn where it stands.</summary>
        public void Watch(PlaneDamageRecord rec)
        {
            if (rec != null) Hurt.Add(rec);
        }

        // =====================================================================
        // ---- holes refresh + every frame (src/planedamage.js:974, 1444) ----
        // =====================================================================

        /// <summary>src/planedamage.js:974 _holes — clone+patch this airframe's skin
        /// materials onto Shader "TbilisiFly/PlaneHull", once, re-walked if the shape changed.</summary>
        HoleSet HolesFor(PlaneDamageRecord rec)
        {
            var parts = PartsOf(rec);
            if (rec.Holes != null && rec.HoleParts == parts) return rec.Holes;
            var set = rec.Holes ?? new HoleSet();
            rec.HoleParts = parts;
            var live = new List<Renderer>();
            foreach (var p in parts.Skin)
            {
                set.Patch(p.R);
                live.Add(p.R);
            }
            set.KeepOnly(live);
            rec.Holes = set;
            return set;
        }

        void SyncHoles(PlaneDamageRecord rec)
        {
            var set = HolesFor(rec);
            var all = new List<PlaneTear>();
            foreach (var l in rec.Tears.Values) if (l != null) all.AddRange(l);
            all.AddRange(rec.Exits);
            Matrix4x4 inv = rec.transform.worldToLocalMatrix;
            set.Write(all, inv);
        }

        /// <summary>src/planedamage.js:1446 update — every frame: marks ride the airframe,
        /// hide when it is switched off, smoke/burn, and tumbling wreck pieces are stepped.</summary>
        public void Update(float dt)
        {
            // Draw the marks. Deliberately ABOVE the early-out: a mark has no
            // life and no per-frame simulation (src/planedamage.js's MarkLayer
            // never ages one out — it lives until its ring slot is reused), so
            // "nothing moved this frame" still has to re-submit every live
            // instance, and a severed piece can keep marks alive after its
            // record has been forgotten.
            MarkFx.Render(this);
            if (Hurt.Count == 0 && _riders.Count == 0 && Wrecks.Count == 0) return;
            Vector3 cam = CameraPosition();
            var dead = new List<PlaneDamageRecord>();
            foreach (var rec in Hurt)
            {
                // src/planedamage.js:1451 `if (!w || (!rec.own && !w.parent))`. Three.js
                // distinguishes "destroyed" from "detached from the scene graph but still
                // alive" (a pooled/hidden object); Unity's closest analogue of the latter
                // is the GameObject being destroyed outright, since a root Transform with
                // no parent is still a perfectly normal, visible scene object here. So this
                // only forgets a record whose wrapper is actually gone — "switched off but
                // still exists" is handled below by the vis/shown/flipped tracking instead,
                // which is the mechanism the quality bar's "marks hide when the hall
                // switches its fleet off" note actually depends on.
                Transform w = rec != null ? rec.transform : null;
                if (w == null) { dead.Add(rec); continue; }

                Matrix4x4 m = w.localToWorldMatrix;
                bool moved = !rec.HasLastWorldMatrix || !MatrixApproxEquals(rec.LastWorldMatrix, m);
                if (moved) { rec.LastWorldMatrix = m; rec.HasLastWorldMatrix = true; }
                if (moved && rec.Holes != null) rec.Holes.TearInv = w.worldToLocalMatrix;

                bool vis = w.gameObject.activeInHierarchy;
                bool shown = rec.Shown;
                bool flipped = vis != shown;
                rec.Shown = vis;

                int n = 0;
                for (int i = 0; i < rec.Marks.Count; i++)
                {
                    var mk = rec.Marks[i];
                    if (mk.Dropped) continue;
                    rec.Marks[n++] = mk;
                    if (!vis) { if (flipped) mk.Layer.Hide(mk.Index); }
                    else if (moved || flipped)
                    {
                        Matrix4x4 world = w.localToWorldMatrix * mk.Local;
                        mk.Layer.Set(mk.Index, world);
                    }
                }
                rec.Marks.RemoveRange(n, rec.Marks.Count - n);
                if (!vis) continue;
                Burn(rec, dt, cam);
            }
            foreach (var rec in dead) { Forget(rec); }

            int loose = 0;
            foreach (var wk in Wrecks) loose += wk.Parts.Count;
            for (int i = 0; i < Wrecks.Count && loose > CutPieces; i++)
            {
                int c = Wrecks[i].Parts.Count;
                if (c == 0) continue;
                Wrecks[i].ClearParts();
                loose -= c;
            }
            for (int i = Wrecks.Count - 1; i >= 0; i--) Wrecks[i].Update(dt);

            for (int i = _riders.Count - 1; i >= 0; i--)
            {
                var r = _riders[i];
                if (r.Obj == null)
                {
                    foreach (var mk in r.Marks) if (!mk.Dropped) { mk.Dropped = true; mk.Layer.Clear(mk.Index); }
                    _riders.RemoveAt(i);
                    continue;
                }
                Matrix4x4 m = r.Obj.localToWorldMatrix;
                bool moved = !r.LastWorld.HasValue || !MatrixApproxEquals(r.LastWorld.Value, m);
                if (!moved) continue;
                r.LastWorld = m;
                if (r.HoleSet != null) r.HoleSet.TearInv = r.Obj.worldToLocalMatrix;
                int n = 0;
                for (int k = 0; k < r.Marks.Count; k++)
                {
                    var mk = r.Marks[k];
                    if (mk.Dropped) continue;
                    r.Marks[n++] = mk;
                    Matrix4x4 world = r.Obj.localToWorldMatrix * mk.Local;
                    mk.Layer.Set(mk.Index, world);
                }
                r.Marks.RemoveRange(n, r.Marks.Count - n);
            }
        }

        static bool MatrixApproxEquals(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>src/planedamage.js:1547 _burn — smoke out of the engine bay once the
        /// hull is down, flame once it is nearly gone, a wreck that burns where it stands.</summary>
        void Burn(PlaneDamageRecord rec, float dt, Vector3 cam)
        {
            // src/planedamage.js:1552 — only the record that owns this airframe NOW smokes
            // for it (the hero climbing into a machine he was shooting at a moment ago).
            if ((rec.transform == HeroPlane) != rec.IsPlayerOwn) return;
            bool dead = rec.Dead;
            float frac = Hull(rec);
            if (!dead && frac > SmokeFrom) return;
            float nose = rec.NoseLen > 0f ? rec.NoseLen : 3f;
            // planedamage.js's `-nose * 0.55` is a station ahead of the pilot
            // in the model frame (nose -Z); rec.transform is the FLIGHT frame,
            // so it is +. Before this, the smoke plume trailed out of the tail.
            Vector3 p = rec.transform.TransformPoint(new Vector3(0f, 0.3f, nose * 0.55f));
            if ((p - cam).sqrMagnitude > Seen * Seen) return;
            bool hot = dead || frac < FireFrom;
            float every = dead ? 0.1f : hot ? 0.14f : 0.27f;
            rec.PuffTimer += dt;
            if (rec.PuffTimer < every) return;
            rec.PuffTimer = 0f;
            Impacts?.SpawnSmokeWide(p, dead ? 5.4f : hot ? 3.1f : 1.8f, dead ? 0.92f : hot ? 0.8f : 0.42f,
                dead ? 4.2f : 2.6f, dead ? 3f : 2.3f, dead ? 1.3f : 0.5f);
            if (hot) Impacts?.SpawnEmber(p);
        }

        void Forget(PlaneDamageRecord rec)
        {
            foreach (var mk in rec.Marks) if (!mk.Dropped) { mk.Dropped = true; mk.Layer.Clear(mk.Index); }
            rec.Marks.Clear();
            Hurt.Remove(rec);
        }

        public void Dispose()
        {
            foreach (var w in Wrecks) w.ClearParts();
            Wrecks.Clear();
            _riders.Clear();
            foreach (var rec in Hurt) { rec.Marks.Clear(); rec.Cutter = null; }
            Hurt.Clear();
            // src/planedamage.js:1595-1596 `for (const l of this.layers) l.dispose()`.
            // This had nothing to release while the layers owned no mesh,
            // material or texture; Fx/PlaneMarkFx.cs now owns all three.
            MarkFx.Dispose();
        }
    }

    /// <summary>The non-shader side-effects PlaneDamage needs (debris/sparks/sound/world
    /// shake/hint text/hull damage) — kept as an interface, same pattern as
    /// Combat/CombatWorldHooks.cs's IDamageWorld, so PlaneDamage.cs stays self-contained
    /// and whoever wires audio/VFX/UI (out of this port's scope) implements it.</summary>
    public interface IImpactSink
    {
        void Debris(Vector3 p, int n, Color32 tint, Vector3 dir, float cone, float speed, float size, float up, float life, float grav, float spread = 0f);
        void Sparks(Vector3 p, int n, Color32 tint, Vector3 dir, float cone, float speed, float size, float life, float grav, float drag);
        void SpawnEmber(Vector3 p);
        void SpawnSmokeWide(Vector3 p, float size, float dark, float dur, float vy, float spread);
        void ShatterGlass(Vector3 at, float nx, float nz, int count, float power);
        void HitSkinSound(float dist, float vol);
        void HitPerspexSound(float dist, float vol);
        void HitSoftSound(float dist, float vol);
        void RicochetWhine(float dist, float vol, float energy);
        void GlassBreakSound(float dist, float vol, bool big);
        void MetalTearSound(float dist, float vol, float half);
        void ShakeGround(float amount, float x, float z);
        void SetHint(string text);
        void DamagePlane(PlaneDamageRecord rec, int amount);
    }
}
