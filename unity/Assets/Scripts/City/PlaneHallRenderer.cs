using System;
using TbilisiFly.Data;
using TbilisiFly.Flight;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// CityLoader's "plane hall" scenery stage — the fifth and last of the
    /// no-op cases listed in <see cref="CityLoader.UnportedStages"/>.
    ///
    /// UNLIKE streetlights/road signs, almost everything this stage needs
    /// already existed before this pass, under Scripts/Flight (not City/) —
    /// checked per the task brief before writing anything:
    ///   - ShedLayout.cs / PlaneHallLayout.cs: the analytical siting + the
    ///     lane-aware shelf-packing math (src/shed.js, src/planehall.js),
    ///     pure data, already probe-verified.
    ///   - ShedFill.cs / PlaneHallFill.cs: the EDITOR-TIME bake that turns
    ///     that math into a REAL hall shell GameObject (walls/roof/pad/ramp,
    ///     cut gable-end doors, baked colliders) and REAL parked-plane
    ///     GameObjects (35 FBX/OBJ airframes, measured, packed tank/DPS,
    ///     each with a BoxCollider) under one "planehall_fleet" switch —
    ///     already probe-verified by PlaneHallFillProbe.cs /
    ///     tools/_probe_planehallfill.mjs (or equivalent).
    ///   - PlaneHallVisibility.cs: the runtime fleet-hide-behind-the-wall
    ///     switch (src/planehall.js:146-194 update/_walled), already
    ///     attached to the hall root by PlaneHallFill.Fill itself.
    ///
    /// So THIS file is only the missing call site: nothing in the project
    /// invoked ShedFill.BuildPlaneHall + PlaneHallFill.Fill at all (grep
    /// confirmed zero call sites outside PlaneHallFillProbe.cs) — CityLoader's
    /// "plane hall" case really was a complete no-op, not a partially-wired
    /// one. <see cref="Render"/> is that call site, using the SAME already-
    /// loaded ctx.City (OriginLat/OriginLon/MLat/MLon, Terrain.HeightAtGame)
    /// the probe's synthetic stand-in replaces.
    ///
    /// NOT PURE DATA, ON PURPOSE. CityLoader.cs's own header calls itself
    /// "PURE DATA/ORCHESTRATION — no GameObject, no Instantiate", which every
    /// OTHER scenery stage honours. The plane hall is the one deliberate
    /// exception: ShedFill/PlaneHallFill's entire job (and the reason they
    /// exist as editor-time bake utilities rather than pure functions) is to
    /// build ~37 REAL GameObjects — one hall shell, 35 parked aeroplanes, one
    /// fleet switch — which is cheap enough to do directly (unlike the
    /// thousands of streetlights/signs/kit buildings the "no GameObject per
    /// prop" rule exists for) and is exactly what src/planehall.js itself
    /// does (new THREE.Group() per aeroplane). <see cref="Render"/> is a
    /// void, side-effecting call for this reason, matching ShedFill/
    /// PlaneHallFill's own already-established contract rather than
    /// inventing a pure-data wrapper around something that isn't.
    /// </summary>
    public static class PlaneHallRenderer
    {
        /// <summary>The hall shell + fleet this call produced, so a caller
        /// (a probe, or whichever system owns the scene root) can find it
        /// again without re-deriving the siting math.</summary>
        public struct Result
        {
            public bool Built;
            public ShedFill.ShedBuildResult Hall;
            public PlaneHallFill.FillResult Fleet;
        }

        /// <summary>CityLoader "plane hall" case. src/planehall.js's
        /// addPlaneHall (:116-134) + PlaneHall._fill (:221-312), called with
        /// ctx's ALREADY-LOADED city/terrain — no second CityData.Load().
        /// `parent` is optional (ShedFill.Build accepts null exactly like
        /// every other orchestration call in this file set); pass the city's
        /// scene root once one exists.</summary>
        public static Result Render(CityLoader.Context ctx, Transform parent = null)
        {
            if (ctx?.City?.Terrain == null) return default;
            try
            {
                var meta = new PlaneHallLayout.GeoMeta
                {
                    OriginLat = ctx.City.OriginLat, OriginLon = ctx.City.OriginLon,
                    MLat = ctx.City.MLat, MLon = ctx.City.MLon,
                };
                Func<float, float, float?> heightAt = (x, z) => ctx.City.Terrain.HeightAtGame(x, z);
                var (hx, hz, _, _, _, _) = PlaneHallLayout.Site(meta, ShedLayout.ShedYaw);

                var hall = ShedFill.BuildPlaneHall(parent, hx, hz, ShedLayout.ShedYaw, heightAt);
                var fleet = PlaneHallFill.Fill(hall);
                return new Result { Built = true, Hall = hall, Fleet = fleet };
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return default;
            }
        }
    }
}
