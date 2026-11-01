using System.Collections.Generic;
using TbilisiFly.Character;
using TbilisiFly.City;
using TbilisiFly.Combat;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEngine;
using TbilisiFly.Core;

namespace TbilisiFly.Props
{
    /// <summary>
    /// THE SCENE COMPONENT for the warehouse INTERIOR — src/airport.js:500-977:
    /// the two shooting ranges (lane carriers, hanging ISSF sheets, swinging
    /// divider gates, the invisible counter fill), the three 9x18 PM ammo
    /// crates at the user's own dev-mode spot, the three work tables, and the
    /// two pistols + the AKM lying on them as ground loot.
    ///
    /// WHY THIS FILE EXISTS — the measured gap, 2026-10-30. Every builder below
    /// already existed and was probe-verified, and NONE of it was in the game:
    ///   * grep -c on Assets/Scenes/Game.unity was 0 for warehouse_range,
    ///     range_template, range_gate, range_paper, ammo_box, shooting_range
    ///     and wooden_table;
    ///   * a live census of the loaded scene read rangeNodes=0 paperNodes=0
    ///     gateNodes=0 tables=0 guns=0;
    ///   * Combat/RangeBootstrap.DiscoverAndAdoptAll() returned
    ///     adoptedRanges=0, so Combat/RangeLane.cs, Combat/PaperTargets.cs,
    ///     City/RangeDoorsPaper.cs and the four new combat UI views
    ///     (RangeLaneView, PaperTargetScoreView, PickupCardsView) were all
    ///     live code with nothing to drive.
    /// The reason was NOT a missing builder: the GameObject-instantiating half
    /// lived as PRIVATE STATICS inside Editor/BuildAirportDetail.cs, a
    /// standalone -executeMethod bake that operates on whatever scene happens
    /// to be open and never saves it. Even calling it from
    /// Editor/BuildPlayableScene.cs would not have helped, because that
    /// script's `stripped_generated` step deletes every scenery root's
    /// children before SaveScene — on purpose (see its own long comment).
    ///
    /// So this is the ninth member of the pattern City/CityRenderer.cs's class
    /// header states and Flight/ShedSceneFill.cs's header spells out at
    /// length: nothing baked, [ExecuteAlways], OnEnable -> EnsureBuilt
    /// regenerates from the asset pipeline on every scene load. The
    /// AIRPORTDETAIL_* lines Editor/BuildAirportDetail.cs used to print moved
    /// here verbatim, so they are now printed on every LOAD and not only
    /// during a bake; BuildAirportDetail.Run() still exists and now delegates
    /// to <see cref="Build"/>, so there is exactly ONE implementation.
    ///
    /// BUILD ORDER — EXPLICIT, NOT HOPED FOR. This fills the warehouse that
    /// Flight/ShedSceneFill.cs builds, so that component must have run first.
    /// Unity delivers every OnEnable in a loading scene before any Start, but
    /// it does NOT define the order of two OnEnables inside one scene load, so
    /// <see cref="EnsureBuilt"/> does not assume one: it finds the
    /// ShedSceneFill and calls its own public, idempotent EnsureBuilt() (the
    /// `_built` guard makes a second call a no-op, exactly the contract
    /// CityRenderer.EnsureBuilt documents). If the ShedSceneFill is missing
    /// or its warehouse came back null (no city data), this component retries
    /// once per LateUpdate instead of silently giving up, and says so in the
    /// log. Everything downstream of that — Core/PlayableSceneBoot.Start ->
    /// CombatWorldAdapter.Install -> GameLoop.BeginSession ->
    /// RangeBootstrap.DiscoverAndAdoptAll — is a Start, so the two ranges are
    /// already in the scene when the adoption sweep runs.
    ///
    /// HANDEDNESS — the one real correctness change this pass makes, and it is
    /// NOT a stylistic preference. City/AirportDetail.cs's own header carries
    /// an earlier pass's explicit assumption that shed-local offsets "need no
    /// additional flip" because the shed Transform is already oriented, and
    /// says in as many words that the sign convention "should be re-verified
    /// once ShedFill.cs lands". It is wrong, and the sibling that lives in the
    /// SAME warehouse root already does it right: Props/WarehouseCraneFill.cs
    /// mirrors every hand-authored crane.js position's X and runs every
    /// hand-authored yaw through GeoFrame.YawToUnity (its own rule (b)), and
    /// proved the mirror direction numerically against the real mesh
    /// (HOOKDERIVE_NEAREST 2.2 cm vs HOOKDERIVE_NEAREST_UNMIRRORED 8.02 m).
    /// The algebra: the warehouse root sits at GeoFrame.ToUnity(p) with yaw
    /// GeoFrame.YawToUnity(yaw), and for M = diag(-1,1,1),
    ///     M * (p_g + Ry(yaw)*L_g) = M*p_g + Ry(-yaw) * (M*L_g)
    /// so a point authored at shed-local L_g in airport.js must be placed at
    /// M*L_g under the Unity root — the shed's Unity local frame is the MIRROR
    /// of airport.js's shed-local frame, not a copy of it. Left unmirrored,
    /// the user's hand-dragged ammo-crate spot (x=+5.21, a dev-mode copy off
    /// the gizmo panel on 2026-10-03) lands against the opposite long wall,
    /// and the three tables hug the wrong side of a 48 m shed.
    ///
    /// Applied EXACTLY ONCE, at the point a number becomes a Transform, and
    /// never inside City/AirportDetail.cs — those pure functions stay
    /// bit-identical to airport.js so Editor/Probes/AirportDetailProbe.cs can
    /// keep comparing them line-for-line against tools/_probe_airportdetail.mjs
    /// (AIRPORTDETAILPROBE_CRATE x=5.2100 etc). Same split as ShedFill.Build's
    /// own "the ONE place position/yaw cross from game-frame math into a real
    /// Unity Transform". Per WarehouseCraneFill's rule (a), anything MEASURED
    /// off a real imported mesh (the table's bounds, the range prop's firingZ/
    /// endZ/pitch, the counter blocks' world boxes, the rifle's own depth) is
    /// already in Unity's convention and is used verbatim, unmirrored — the
    /// range holders' own +/-pitch/2 offsets are a measured, symmetric pair, so
    /// a mirror there is the identity and is deliberately not applied.
    ///
    /// PLAYER-SAFE, AND SAYING SO OUT LOUD: like ShedFill, PlaneHallFill,
    /// WarehouseCraneFill and every scenery renderer in this port, the builders
    /// resolve their art through Core/ArtAssets — AssetDatabase in the editor,
    /// Resources in a player — and every count line below carries
    /// `playerSafe=true`. It used to be the opposite: the builders called
    /// AssetDatabase/PrefabUtility directly behind #if UNITY_EDITOR and this
    /// component logged `playerSafe=false reason=editor-only-AssetDatabase` and
    /// returned, so a built player had no range, no crates and no tables. A
    /// real StandaloneOSX build is what proves it now: WAREHOUSEINTERIOR
    /// ranges=2 lanes=20 cards=20 crates=3 tables=3 guns=3 in the player's own
    /// log, identical to the editor's.
    /// </summary>
    [ExecuteAlways]
    public sealed class WarehouseInteriorFill : MonoBehaviour
    {
        // ---- the art, exactly as Editor/BuildAirportDetail.cs named it ----
        public const string TableFbx = "Assets/Resources/Art/Props/wooden_table.fbx";
        public const string AmmoFbx = "Assets/Resources/Art/Props/ammo_box.fbx";
        public const string MakarovFbx = "Assets/Resources/Art/Props/makarov.fbx";
        public const string BerettaFbx = "Assets/Resources/Art/Props/beretta.fbx"; // the FLAT static one, not beretta_anim.fbx
        public const string AkmFbx = "Assets/Resources/Art/Props/akm_anim.fbx";
        public const string RangeFbx = "Assets/Resources/Art/Props/shooting_range.fbx";

        /// <summary>The ONE holder every generated object hangs under, as a
        /// direct child of the warehouse root — so a rebuild can drop the
        /// previous one wholesale without touching the shed itself.</summary>
        public const string HolderName = "warehouse_interior";

        public struct Result
        {
            public bool Built;
            public Transform Holder;
            public int Tables, Crates, Guns, Ranges;
            public int LanesPerRange, CardsPerRange, GatesPerRange, BlocksPerRange;
            public float Pitch, FiringZ, EndZ;
        }

        public Result Last { get; private set; }
        public bool Built => Last.Built;

        // Private and unserialised ON PURPOSE, same as ShedSceneFill._built /
        // CityRenderer._built: a domain reload or a scene load must come back
        // false so EnsureBuilt runs again from the asset pipeline.
        bool _built;
        int _retries;

        void OnEnable() { _built = false; _retries = 0; EnsureBuilt(); }

        /// <summary>Retry path for the one thing this component cannot control:
        /// whether Flight/ShedSceneFill.cs's warehouse exists yet. EnsureBuilt
        /// drives that itself (see the class header), so in practice this never
        /// fires; it is here so a missing/late shed degrades into "built one
        /// frame later", never into silence.</summary>
        void LateUpdate()
        {
            if (_built || _retries > 120) return;
            _retries++;
            EnsureBuilt();
        }

        /// <summary>Build the interior if it is not built yet — safe to call
        /// repeatedly, same contract as CityRenderer/ShedSceneFill.EnsureBuilt.</summary>
        public void EnsureBuilt()
        {
            if (_built) return;

            // ORDER: make the warehouse exist rather than assume it does.
            var sheds = FindFirstObjectByType<ShedSceneFill>(FindObjectsInactive.Include);
            if (sheds == null)
            {
                if (_retries == 0)
                    Debug.LogWarning("WAREHOUSEINTERIOR waiting: no Flight/ShedSceneFill in the scene " +
                                     "(it builds the warehouse this fills).");
                return;
            }
            sheds.EnsureBuilt(); // idempotent — its own _built guard
            var shed = sheds.Warehouse;
            if (shed.Root == null)
            {
                if (_retries == 0)
                    Debug.LogWarning("WAREHOUSEINTERIOR waiting: ShedSceneFill.Warehouse.Root is null " +
                                     "(no city data?) — nothing to fill yet.");
                return;
            }

            _built = true; // set before building: a throw must not loop every frame
            var size = new AirportDetail.ShedSize
            {
                Width = shed.Shell.Width, Height = shed.Shell.Height, Length = shed.Shell.Length,
            };
            Last = Build(shed.Root.transform, size);

            // This component's own transform stays empty (everything lives
            // under the shed, where airport.js puts it), but clear it anyway:
            // every renderer in this pattern must be able to remove children
            // it does not personally remember creating — a scene load never
            // does. See City/GeneratedChildren.cs's header.
            GeneratedChildren.Clear(transform);

            var r = Last;
            Debug.Log($"WAREHOUSEINTERIOR ranges={r.Ranges} lanes={r.LanesPerRange * r.Ranges} " +
                      $"cards={r.CardsPerRange * r.Ranges} crates={r.Crates} tables={r.Tables} guns={r.Guns} {ArtAssets.Tag}");
            Debug.Log($"WAREHOUSEINTERIOR_DETAIL lanes_per_range={r.LanesPerRange} cards_per_range={r.CardsPerRange} " +
                      $"gates_per_range={r.GatesPerRange} blocks_per_range={r.BlocksPerRange} " +
                      $"pitch={r.Pitch:F3} firingZ={r.FiringZ:F3} endZ={r.EndZ:F3} " +
                      $"{ArtAssets.Tag}");
        }

        /// <summary>
        /// src/airport.js:500-977, instantiated under <paramref name="warehouseRoot"/>.
        /// The single implementation — Editor/BuildAirportDetail.cs's Run()
        /// delegates here rather than keeping a second copy.
        /// <paramref name="size"/> is the warehouse's own measured shell
        /// (ShedFill.ShedBuildResult.Shell, or
        /// AirportDetail.EstimateWarehouseSizeFallback for a standalone run).
        /// </summary>
        public static Result Build(Transform warehouseRoot, AirportDetail.ShedSize size)
        {
            var res = new Result();
            if (warehouseRoot == null) { Debug.LogError("WAREHOUSEINTERIOR no warehouse root"); return res; }

            // Drop a previous interior wholesale (a domain reload in an open
            // editor re-fires OnEnable while the old GameObjects are alive —
            // the duplicate-geometry case GeneratedChildren exists for; every
            // renderer drew TWICE before that was fixed at source).
            for (int i = warehouseRoot.childCount - 1; i >= 0; i--)
            {
                var c = warehouseRoot.GetChild(i);
                if (c.name != HolderName) continue;
                if (Application.isPlaying) Destroy(c.gameObject); else DestroyImmediate(c.gameObject);
            }
            var holder = new GameObject(HolderName).transform;
            holder.SetParent(warehouseRoot, false);
            holder.localPosition = Vector3.zero;
            holder.localRotation = Quaternion.identity;
            holder.localScale = Vector3.one;
            res.Holder = holder;

            // ---- B. three work tables — airport.js:500-549 -----------------
            var tableFbx = LoadFbxForceReadable(TableFbx);
            if (tableFbx == null) { Debug.LogError($"WAREHOUSEINTERIOR missing {TableFbx}"); return res; }

            // NEVER RESET AN FBX's NATURAL IMPORT TRANSFORM — see
            // <see cref="NaturalChild"/>. wooden_table.fbx imports with rot
            // (0,180,180) and scale (254,254,254), and AirportDetail.FlattenProp
            // bakes every mesh relative to the root's OWN LOCAL frame
            // (`root.worldToLocalMatrix * child.localToWorldMatrix`), so
            // flattening the FBX root ITSELF cancels that correction exactly —
            // the merged mesh came out 1/254 scale with its Y and Z negated
            // (rot (0,180,180) == diag(1,-1,-1), i.e. the table upside down and
            // back to front). airport.js:506 flattens `tg.scene`, whose CHILD
            // carries the glTF node transform (scale 2.54, rot -90 about X,
            // wooden_table.glb node 0), so the JS merge DOES include it.
            // Reproduced by flattening a WRAPPER that sits at world identity:
            // rootInv is then the identity and each child's localToWorldMatrix
            // carries the natural correction into the baked mesh, which is what
            // makes AirportDetail.ComputeTableLayout's rawBounds come out in the
            // same 198.868 x 226.057 x 469.866 the JS measures.
            var (tableWrap, _) = NaturalWrapped(tableFbx, "wooden_table_wrap");
            var flatTable = AirportDetail.FlattenProp(tableWrap.transform);
            if (flatTable != tableWrap) DestroyImmediate(tableWrap);

            Bounds rawBounds = MeasureLocalBounds(flatTable.transform);
            var tableLayout = AirportDetail.ComputeTableLayout(size, rawBounds);
            Debug.Log($"AIRPORTDETAIL_TABLE scale={tableLayout.Scale:F4} len={tableLayout.TableLength:F3} " +
                      $"offset={tableLayout.ModelLocalOffset:F3}");

            for (int i = 0; i < tableLayout.Spots.Length; i++)
            {
                var spot = tableLayout.Spots[i];
                var hold = new GameObject("warehouse_table");
                hold.transform.SetParent(holder, false);
                // HANDEDNESS rule (b) — airport.js's own shed-local x, mirrored
                // once here (see the class header's algebra). ry is always 0 for
                // the tables (airport.js:521), so there is no yaw to negate.
                hold.transform.localPosition = GeoFrame.ToUnity(spot.x, spot.y, spot.z);
                hold.transform.localRotation = Quaternion.identity;

                var copy = i == 0 ? flatTable : Instantiate(flatTable);
                copy.transform.SetParent(hold.transform, false);
                // rule (a) — a centring offset measured off the real imported
                // mesh: already Unity-correct, used verbatim, NOT mirrored.
                copy.transform.localPosition = tableLayout.ModelLocalOffset;
                copy.transform.localScale = Vector3.one * tableLayout.Scale;
                copy.transform.localRotation = Quaternion.identity;
                Shadows(copy.transform);

                // Unity-native simplification of airport.js's baked triangle-soup
                // `solidify`: a real Collider on the holder, sized to the model's
                // OWN measured bounds, which — by construction of
                // ModelLocalOffset — sits centred on x/z and from y=0 to
                // y=TABLE_H in the holder's frame.
                var col = hold.AddComponent<BoxCollider>();
                col.center = new Vector3(0f, AirportDetail.TableH / 2f, 0f);
                col.size = new Vector3(rawBounds.size.x * tableLayout.Scale, AirportDetail.TableH,
                    rawBounds.size.z * tableLayout.Scale);

                res.Tables++;
                Debug.Log($"AIRPORTDETAIL_TABLE_SPOT i={i} x={hold.transform.localPosition.x:F3} " +
                          $"y={hold.transform.localPosition.y:F3} z={hold.transform.localPosition.z:F3}");
            }

            // ---- C. three ammo crates — airport.js:752-819 -----------------
            var ammoFbx = ArtAssets.Load<GameObject>(AmmoFbx);
            if (ammoFbx == null) Debug.LogError($"WAREHOUSEINTERIOR missing {AmmoFbx}");
            else
                foreach (var spot in AirportDetail.ComputeCrateSpots())
                {
                    // HOLDER + NATURAL CHILD (see <see cref="NaturalChild"/>):
                    // ammo_box.fbx imports with rot (270,0,0) and scale
                    // (100,100,100), and THAT natural transform is what
                    // reproduces airport.js:754's hand-written
                    // SIZE=(0.97,0.40,0.44) with the origin on the bottom —
                    // measured 0.9684 x 0.3990 x 0.4440 m, centre y=0.1995.
                    // Overwriting it with localScale=5 built a 5 cm crate
                    // (0.9684/100*5 = 0.0484 m), which is what
                    // WarehouseInteriorLayoutProbe read. The authored
                    // pos/yaw/scale go on a holder; the FBX keeps its own
                    // transform underneath, so the two COMPOSE.
                    var crate = new GameObject(spot.Id);
                                          // ammo_box / ammo_box_pair / ammo_box_stack — the GLB's own
                                          // primitives are ammo_box_1..5 inside, which is why the
                                          // holders are named apart from them (the recorded collision)
                    crate.transform.SetParent(holder, false);
                    // rule (b), both halves: the user's own dev-mode x mirrored,
                    // the authored yaw through GeoFrame.YawToUnity.
                    crate.transform.localPosition = GeoFrame.ToUnity(spot.Pos.x, spot.Pos.y, spot.Pos.z);
                    crate.transform.localRotation =
                        Quaternion.Euler(0f, GeoFrame.YawToUnity(spot.YawRad) * Mathf.Rad2Deg, 0f);
                    crate.transform.localScale = Vector3.one * AirportDetail.AmmoScale;
                    NaturalChild(ammoFbx, crate.transform, spot.Id + "_model");
                    Shadows(crate.transform);

                    // One BoxCollider per crate, sized in the crate's OWN
                    // model-space units (AmmoSize, unscaled): localScale
                    // stretches it to the real world size, so the stored values
                    // equal AmmoSize, not AmmoSize*AmmoScale.
                    var col = crate.AddComponent<BoxCollider>();
                    col.center = new Vector3(0f, AirportDetail.AmmoSize.y / 2f, 0f);
                    col.size = AirportDetail.AmmoSize;

                    res.Crates++;
                    Debug.Log($"AIRPORTDETAIL_CRATE id={spot.Id} x={crate.transform.localPosition.x:F3} " +
                              $"y={crate.transform.localPosition.y:F3} z={crate.transform.localPosition.z:F3} " +
                              $"yaw={crate.transform.localEulerAngles.y:F3}");

                    // STILL SKIPPED: citymap.game?.dev?.register (airport.js:813)
                    // — src/devmode.js (the ` -key gizmo editor) has no Unity port.
                }

            // ---- D. two pistols + the rifle — airport.js:913-971 -----------
            foreach (var g in AirportDetail.ComputeGunLayout(tableLayout.Spots[0], tableLayout.Spots[1]))
            {
                string fbxPath = g.Id switch
                {
                    "makarov" => MakarovFbx,
                    "beretta" => BerettaFbx,
                    "akm" => AkmFbx,
                    _ => null,
                };
                var def = WeaponDefs.ById.TryGetValue(g.Id, out var d) ? d : null; // Combat/Weapons.cs
                float gunScale = def?.Scale ?? 1f;
                var fbx = ArtAssets.Load<GameObject>(fbxPath);
                if (fbx == null) { Debug.LogError($"WAREHOUSEINTERIOR missing {fbxPath}"); continue; }

                // HOLDER + NATURAL CHILD, both weapons and rifle (see
                // <see cref="NaturalChild"/>). makarov.fbx and beretta.fbx both
                // import with rot (270,0,0) and scale (100,100,100) — that
                // natural transform is what makes them come out at the REAL
                // weapon lengths tools/import_pistols.py normalised them to
                // (Makarov PM 0.1615 m, Beretta 92FS 0.2170 m, measured
                // 0.1615 x 0.1287 x 0.0303 and 0.2170 x 0.1346 x 0.0320).
                // Overwritten with localScale=1.5 they were 2.4 mm long, which
                // is why WarehouseInteriorLayoutProbe printed
                // bounds_size=(0.00,0.00,0.00) — not a missing mesh, not a
                // skinned-renderer edit-mode quirk, just a 2.4 mm pistol
                // rounding to 0.00 at two decimals. akm_anim.fbx is the one
                // asset here whose ROOT natural transform really is identity
                // (its 100 sits on its inner `akm_anim` node), so it is
                // unchanged by this — its bug was the Icosphere, below.
                var gun = new GameObject(g.Id);
                gun.transform.SetParent(null);
                gun.transform.position = Vector3.zero;
                gun.transform.rotation = Quaternion.identity;
                gun.transform.localScale = Vector3.one * gunScale;  // airport.js:941
                NaturalChild(fbx, gun.transform, g.Id + "_model");
                StripFpsJunk(gun.transform, g.Id);

                float depthZ = 0f;
                if (g.IsRifle)
                    // Measured at identity, UNPARENTED, scale already applied —
                    // airport.js:950-957: the scale is set before the box is
                    // taken and never reset, so the box already reflects it.
                    // rule (a): the depth is a real mesh measurement, verbatim.
                    depthZ = MeasureWorldBoundsAtIdentity(gun.transform).size.z;

                gun.transform.SetParent(holder, false);
                var p = g.IsRifle ? AirportDetail.RifleLocalPos(g, depthZ) : AirportDetail.PistolLocalPos(g);
                gun.transform.localPosition = GeoFrame.ToUnity(p.x, p.y, p.z);               // rule (b)
                // Mirroring Ry(ry)*Rx(-90) gives Ry(-ry)*Rx(-90): for
                // M = diag(-1,1,1), M*Rx(t)*M == Rx(t) (the axis flips AND
                // the handedness flips, so they cancel) while
                // M*Ry(t)*M == Ry(-t). So only the YAW is negated — the
                // roll onto its side is untouched.
                gun.transform.localRotation = g.IsRifle                                      // rule (b)
                    ? AirportDetail.RifleLocalRot(GeoFrame.YawToUnity(g.RyRad))
                    : Quaternion.Euler(0f, GeoFrame.YawToUnity(g.RyRad) * Mathf.Rad2Deg, 0f);
                // localScale untouched — set before measuring, as in the JS.
                Shadows(gun.transform);

                // airport.js:966 `gun.userData.pickup = def` — ground loot.
                // Combat/Pickups.cs's PickupTag is this port's form of that
                // field, and Pickups.Scan(root) sweeps for it.
                if (def != null)
                {
                    var tag = gun.GetComponent<PickupTag>() ?? gun.AddComponent<PickupTag>();
                    tag.Weapon = def;
                    tag.Gear = null;
                }
                else Debug.LogWarning($"WAREHOUSEINTERIOR no WeaponDef for '{g.Id}' — placed without a PickupTag");

                res.Guns++;
                Debug.Log($"AIRPORTDETAIL_GUN id={g.Id} x={gun.transform.localPosition.x:F3} " +
                          $"y={gun.transform.localPosition.y:F3} z={gun.transform.localPosition.z:F3} " +
                          $"ry={GeoFrame.YawToUnity(g.RyRad):F4} scale={gunScale:F2} " +
                          $"rifle={(g.IsRifle ? 1 : 0)} depthZ={depthZ:F3} " +
                          $"pickup={(gun.GetComponent<PickupTag>() != null ? 1 : 0)}");
            }

            // ---- E. the two shooting ranges — airport.js:564-738, 820-911 ---
            BuildShootingRanges(holder, ref res);

            res.Built = true;
            return res;
        }

        // =====================================================================
        // E. airport.js:564-738, 820-911 — City/AirportDetail.cs Section E's own
        // header explains the order dependency this follows verbatim:
        // classify -> gate pivots -> carriers -> paper pivots -> firingZ/endZ
        // and pitch (measured only AFTER doors/paper have LEFT the scene root)
        // -> counter blocks -> FlattenProp -> assemble -> clone for unit two.
        // Every number here is rule (a): a prop-local measurement off
        // shooting_range.fbx's own authored axes, never mirrored.
        // =====================================================================
        static void BuildShootingRanges(Transform holder, ref Result res)
        {
            // isReadable: BuildCounterBlocks reads mesh.vertices/triangles and
            // FlattenProp calls Mesh.CombineMeshes — both need CPU data, the
            // same gotcha as the table (wooden_table.fbx.meta ships
            // isReadable: 0 and CombineMeshes then fails SILENTLY).
            var rangeFbx = LoadFbxForceReadable(RangeFbx);
            if (rangeFbx == null) { Debug.LogError($"WAREHOUSEINTERIOR missing {RangeFbx}"); return; }

            // Fresh, unparented, world identity — the invariant
            // AirportDetail.cs Section E's header requires (it matches
            // airport.js's `rg.scene` fresh out of the GLTFLoader, before
            // anything reparents it: world == local, so every measurement
            // below is in the prop's own authored space).
            // Fresh and at the world ORIGIN, but with its NATURAL IMPORT
            // TRANSFORM KEPT: shooting_range.fbx imports with rot (0,180,180)
            // and scale (100,100,100). Resetting those to identity/one (what
            // this builder did until 2026-10-30) measured the whole range at
            // 1/100 — WarehouseInteriorLayoutProbe read a 24 cm shooting range,
            // and every number derived from it (pitch, firingZ, endZ, the
            // counter blocks' 0.3 m / 0.15 m keep filters) was 100x off with
            // nothing logging an error. Kept, every measurement below is in
            // METRES in the prop's own authored frame, which is exactly what
            // airport.js's `rg.scene` is fresh out of the GLTFLoader: the
            // natural transform reproduces shooting_range.glb's own env box to
            // four decimals (17.0597 x 3.6057 x 16.9885 m, independently
            // recomputed from the GLB accessors), so "world == the asset's own
            // space" — AirportDetail.cs Section E's required invariant — still
            // holds, with the wrapper below supplying the identity root.
            // UNPACK FIRST. BuildGatePivots/BuildCarriers below pull the door
            // and paper shapes OUT of this hierarchy under their own pivots, and
            // in EDIT mode a prefab instance refuses that: "Setting the parent
            // of a transform which resides in a Prefab instance is not possible"
            // - 80 times per scene load (HeroScreenProbe, 2026-10-30), with the
            // gates and cards left where the FBX put them. Completely unpacked,
            // the instance is a plain hierarchy and the reparenting is legal in
            // both modes; nothing here needs the prefab link. (NaturalChild
            // does the unpack.)
            var (rangeWrap, sceneInst) = NaturalWrapped(rangeFbx, "shooting_range_wrap");
            var sceneRoot = sceneInst.transform;

            var classified = AirportDetail.ClassifyRangeMeshes(sceneRoot);
            Debug.Log($"AIRPORTDETAIL_RANGE_CLASSIFY gates={classified.Gates.Count} " +
                      $"laneparts={classified.LaneParts.Count} counters={classified.Counters.Count}");

            var doorsRoot = new GameObject("range_doors").transform;
            var paperRoot = new GameObject("range_paper_root").transform;

            var gatePivots = AirportDetail.BuildGatePivots(classified.Gates, doorsRoot);
            var carriers = AirportDetail.BuildCarriers(classified.LaneParts, paperRoot);
            var paperPivots = AirportDetail.BuildPaperPivots(carriers, classified.LaneParts);

            // measured now, AFTER doors/paper left sceneRoot.
            var (firingZ, endZ) = AirportDetail.RangeFiringEnd(classified.Counters, sceneRoot);
            float pitch = AirportDetail.ComputeRangePitch(sceneRoot);
            var blockSpecs = AirportDetail.BuildCounterBlocks(classified.Counters, pitch);
            Debug.Log($"AIRPORTDETAIL_RANGE_MEASURE firingZ={firingZ:F3} endZ={endZ:F3} pitch={pitch:F3} " +
                      $"doors={gatePivots.Count} papers={paperPivots.Count} blocks={blockSpecs.Count}");

            // airport.js:724 flattenProp(scene) — now that the doors/paper/
            // counter readings are done, collapse what remains (dividers,
            // counters, the rail, the ceiling, the backstop) to one mesh per
            // material.
            // Flattened from the WRAPPER, not from sceneRoot: FlattenProp bakes
            // relative to the root's own LOCAL frame, so flattening sceneRoot
            // would cancel the natural import transform it is standing on and
            // hand back a 1/100-scale, Y/Z-negated mesh (the table has the same
            // trap — see Build's own comment there).
            var flatScene = AirportDetail.FlattenProp(rangeWrap.transform);
            if (flatScene != rangeWrap) DestroyImmediate(rangeWrap);
            Shadows(flatScene.transform);

            // airport.js:822 solidify(rangeHolders, 'warehouse_range') —
            // Unity-native: a real Collider per piece rather than one baked
            // triangle-soup BVH. flatScene's direct children are exactly
            // FlattenProp's one-mesh-per-material output; doors/paper are never
            // among them (they left sceneRoot first), matching the JS's own
            // noCollide skip.
            foreach (var mf in flatScene.GetComponentsInChildren<MeshFilter>(false))
            {
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }

            // airport.js:722-727 — the flattened scene, the invisible counter
            // fill, paperRoot and doorsRoot are four SIBLINGS under the template.
            var tpl = new GameObject("range_template");
            flatScene.transform.SetParent(tpl.transform, true);

            var blocksRoot = new GameObject("range_counter_blocks");
            blocksRoot.transform.SetParent(tpl.transform, true);
            foreach (var spec in blockSpecs)
            {
                var go = new GameObject("range_counter_block");
                go.transform.SetParent(blocksRoot.transform, true);
                go.transform.position = spec.Center; // rule (a): a world box off the real counter triangles
                var col = go.AddComponent<BoxCollider>();
                col.size = spec.Size;
                // airport.js:834 counterCol.userData.noClimb = true
                go.AddComponent<NoClimbMarker>();
                // no Renderer: airport.js:717 box.visible = false (collision only)
            }

            doorsRoot.SetParent(tpl.transform, true);
            paperRoot.SetParent(tpl.transform, true);

            // airport.js:729-737 — two mirrored range units, PITCH apart. The
            // FIRST holder gets the original template; the SECOND a deep clone
            // (airport.js:734 `rangeHolders.length ? tpl.clone(true) : tpl`).
            // +/-pitch/2 off a MEASURED width is a symmetric pair, so the
            // GeoFrame mirror is the identity here and is not applied (it would
            // only swap which unit is called the first one).
            int built = 0;
            foreach (float side in new[] { -0.5f, 0.5f })
            {
                var unit = new GameObject("warehouse_range");
                unit.transform.SetParent(holder, false);
                unit.transform.localPosition = new Vector3(side * pitch, 0f, 0f); // airport.js:733
                var content = built == 0 ? tpl : Instantiate(tpl);
                content.name = "range_template";
                content.transform.SetParent(unit.transform, false); // airport.js:735 holder.add(...)
                built++;
                Debug.Log($"AIRPORTDETAIL_RANGE_HOLDER i={built - 1} x={unit.transform.localPosition.x:F3}");
            }

            res.Ranges = built;
            res.LanesPerRange = carriers.Count;
            res.CardsPerRange = paperPivots.Count;
            res.GatesPerRange = gatePivots.Count;
            res.BlocksPerRange = blockSpecs.Count;
            res.Pitch = pitch; res.FiringZ = firingZ; res.EndZ = endZ;

            // The lane/paper/gate ADOPTION (airport.js:845-851's
            // citymap.game.paper.adopt / citymap.game.lanes.adopt) is NOT done
            // here on purpose: Combat/RangeBootstrap.DiscoverAndAdoptAll()
            // already owns it, walking exactly these baked names
            // ("warehouse_range" -> "range_paper_root"/"range_carrier"/
            // "range_paper", "range_doors"/"range_gate",
            // "range_counter_blocks"), and it is called from
            // Core/GameLoop.BeginSession — a Start, which Unity delivers after
            // every OnEnable in the loading scene, so the two units below are
            // always already there when it sweeps.
            Debug.Log($"AIRPORTDETAIL_RANGE_DONE holders={built}");
        }

        // =====================================================================
        // helpers (moved verbatim from Editor/BuildAirportDetail.cs)
        // =====================================================================

        /// <summary>Forces an FBX's mesh importer to isReadable=true (required
        /// by Mesh.CombineMeshes AND by any mesh.vertices/triangles read) and
        /// reimports it if it was not already — the gotcha documented in
        /// AirportDetail.FlattenProp's header: wooden_table.fbx.meta (and
        /// shooting_range.fbx.meta) ship isReadable: 0 and CombineMeshes then
        /// fails SILENTLY. The flag is persisted to the .meta on the first
        /// run, so a later scene load finds it already true and never
        /// reimports mid-load.</summary>
        public static GameObject LoadFbxForceReadable(string path)
            => ArtAssets.LoadModelReadable(path);

        /// <summary>
        /// NEVER RESET AN FBX's NATURAL IMPORT TRANSFORM TO IDENTITY — the trap
        /// Props/WarehouseCraneFill.cs:252-270 documents (it built the crane and
        /// all three fans ~100x too small and facing the wrong way), and the one
        /// this file was built on top of. Measured per asset, 2026-10-30, by
        /// Editor/Probes/PropNaturalTransformProbe.cs — never assumed:
        ///
        ///   wooden_table.fbx    rot (0,180,180)  scale 254  -> 198.868 x 226.057 x 469.866
        ///   shooting_range.fbx  rot (0,180,180)  scale 100  -> 17.0597 x 3.6057 x 16.9885 m
        ///   ammo_box.fbx        rot (270,0,0)    scale 100  -> 0.9684 x 0.3990 x 0.4440 m
        ///   makarov.fbx         rot (270,0,0)    scale 100  -> 0.1615 x 0.1287 x 0.0303 m
        ///   beretta.fbx         rot (270,0,0)    scale 100  -> 0.2170 x 0.1346 x 0.0320 m
        ///   akm_anim.fbx        rot (0,0,0)      scale 1    (its 100 is on the inner node)
        ///   crane_machine.fbx   rot (0,180,180)  scale 100  (WarehouseCraneFill's own, for reference)
        ///
        /// Three of those sizes are checkable against a number the Unity side
        /// has no way to know: ammo_box's against airport.js:754's hand-written
        /// `SIZE = (0.97, 0.40, 0.44)`, and the two pistols' against
        /// tools/import_pistols.py:32-33's normalisation targets (Makarov PM
        /// 0.1615 m, Beretta 92FS 0.217 m) — the asset-pipeline-constant proof
        /// pattern WarehouseCraneFill used for the 7.9 m hook.
        ///
        /// So: the authored game-frame pos/yaw/scale go on a HOLDER, the FBX
        /// instance keeps its own transform as a CHILD, and the two compose.
        /// </summary>
        static GameObject NaturalChild(GameObject prefab, Transform parent, string name)
        {
            var inst = (GameObject)ArtAssets.Instantiate(prefab);
            // Unpack: an inner node of a prefab instance cannot be reparented in
            // edit mode (the range's gates/cards need exactly that), and nothing
            // here wants the prefab link. Done for every asset, not just the
            // range, so one code path serves all six.
            ArtAssets.UnpackCompletely(inst);
            // CAPTURED, not overwritten. SetParent(..., false) already keeps the
            // local values; re-applying them states the intent and survives an
            // edit that changes the parenting call.
            Vector3 natPos = inst.transform.localPosition;
            Quaternion natRot = inst.transform.localRotation;
            Vector3 natScale = inst.transform.localScale;
            inst.transform.SetParent(parent, false);
            inst.transform.localPosition = natPos;
            inst.transform.localRotation = natRot;
            inst.transform.localScale = natScale;
            inst.name = name; // never the holder's own name: GameObject.Find on
                              // "makarov"/"ammo_box" must not land on the inner
                              // glTF node that shares the prop's name
            Debug.Log($"WHNATURAL asset={ArtAssets.PathOf(prefab)} as={name} " +
                      $"pos={natPos.ToString("F4")} rot={natRot.eulerAngles.ToString("F1")} " +
                      $"scale={natScale.ToString("F4")}");
            return inst;
        }

        /// <summary>An FBX instance with its natural transform intact, under a
        /// fresh WORLD-IDENTITY wrapper. Two things need that: anything measured
        /// in world space (AirportDetail.cs Section E's "world == the asset's own
        /// authored space" invariant) and anything handed to
        /// AirportDetail.FlattenProp, which bakes relative to the root's own
        /// local frame and so must be given a root that contributes nothing.</summary>
        static (GameObject wrap, GameObject inst) NaturalWrapped(GameObject prefab, string wrapName)
        {
            var wrap = new GameObject(wrapName);
            wrap.transform.SetParent(null);
            wrap.transform.position = Vector3.zero;
            wrap.transform.rotation = Quaternion.identity;
            wrap.transform.localScale = Vector3.one;
            var inst = NaturalChild(prefab, wrap.transform, prefab.name + "_natural");
            return (wrap, inst);
        }

        /// <summary>
        /// FPS-DOWNLOAD JUNK. akm_anim.fbx carries a 2 m `Icosphere` next to the
        /// rifle — the very junk tools/import_pistols.py:8 names ("a pair of 2 m
        /// icospheres ... reads as severed arms") and strips at :42, and which
        /// tools/import_akm_anim.py strips for the GLB the JS loads:
        /// public/models/akm_anim.glb has 17 nodes and no Icosphere, while the
        /// FBX beside it has a Model::Icosphere. So the JS spec says it is not
        /// there, and in Unity it was both DRAWN (a 2 m grey ball on the bench)
        /// and MEASURED: MeasureWorldBoundsAtIdentity returned its 2.0 m depth
        /// instead of the rifle's 0.0678 m, which lifted the AKM
        /// (1.2 + 2.0/2 + 0.004) = 2.204 m — a metre above the table — and is
        /// what made WarehouseInteriorLayoutProbe read bounds_size=(3.03,2.20,3.04)
        /// for a 0.9 m rifle.
        /// </summary>
        static void StripFpsJunk(Transform root, string id)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || t == null) continue;
                if (!t.name.StartsWith("Icosphere", System.StringComparison.OrdinalIgnoreCase)) continue;
                DestroyImmediate(t.gameObject);
                n++;
            }
            if (n > 0) Debug.Log($"WHNATURAL stripped_fps_junk id={id} icospheres={n}");
        }

        static void Shadows(Transform root)
        {
            // airport.js:960-964 — every mesh casts and receives.
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
            }
        }

        /// <summary>Union of every MeshFilter's sharedMesh.bounds under `root`,
        /// in root's OWN local space — valid when every descendant sits at
        /// local identity relative to root (true of FlattenProp's output).</summary>
        public static Bounds MeasureLocalBounds(Transform root)
        {
            Bounds? b = null;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var corners = BoundsCorners(mf.sharedMesh.bounds);
                for (int i = 0; i < corners.Length; i++) corners[i] = m.MultiplyPoint3x4(corners[i]);
                var wb = new Bounds(corners[0], Vector3.zero);
                for (int i = 1; i < corners.Length; i++) wb.Encapsulate(corners[i]);
                b = b.HasValue ? Encapsulate(b.Value, wb) : wb;
            }
            return b ?? new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>Union of every Renderer's world bounds under `root`, read
        /// WHILE root sits at world identity — so world bounds == root-local
        /// bounds, matching airport.js:954-957.</summary>
        public static Bounds MeasureWorldBoundsAtIdentity(Transform root)
        {
            Bounds? b = null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
            return b ?? new Bounds(Vector3.zero, Vector3.zero);
        }

        /// <summary>Combined Renderer bounds of `root`'s children in root's OWN
        /// local space — the "warehouse" GameObject duck-typing path
        /// Editor/BuildAirportDetail.cs uses when it runs standalone.</summary>
        public static AirportDetail.ShedSize MeasureWarehouseLocal(Transform root)
        {
            Bounds? b = null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var corners = BoundsCorners(r.bounds); // world-space already
                for (int i = 0; i < corners.Length; i++) corners[i] = root.InverseTransformPoint(corners[i]);
                var lb = new Bounds(corners[0], Vector3.zero);
                for (int i = 1; i < corners.Length; i++) lb.Encapsulate(corners[i]);
                b = b.HasValue ? Encapsulate(b.Value, lb) : lb;
            }
            var size = b?.size ?? Vector3.zero;
            return new AirportDetail.ShedSize { Width = size.x, Height = size.y, Length = size.z };
        }

        static Vector3[] BoundsCorners(Bounds b)
        {
            var c = b.center; var e = b.extents;
            return new[]
            {
                c + new Vector3(-e.x, -e.y, -e.z), c + new Vector3(e.x, -e.y, -e.z),
                c + new Vector3(-e.x, e.y, -e.z),  c + new Vector3(e.x, e.y, -e.z),
                c + new Vector3(-e.x, -e.y, e.z),  c + new Vector3(e.x, -e.y, e.z),
                c + new Vector3(-e.x, e.y, e.z),   c + new Vector3(e.x, e.y, e.z),
            };
        }

        static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }
    }
}
