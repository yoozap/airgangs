using System;
using System.Collections.Generic;
using System.Linq;
using TbilisiFly.Data;   // JsMath.Round -- JS's round-half-UP, not Mathf's round-half-to-even
using UnityEngine;

namespace TbilisiFly.Flight
{
    // Port of src/planes.js. SCOPE, per the porting brief: "the 36-plane
    // definition table + planeRole()" — i.e. this file, not the rest of
    // planes.js. Here is what that means concretely:
    //
    // PORTED: the PLANES table itself (35 entries — src/planes.js currently
    // ships 35, not 36; see the mismatch note at the bottom of this header),
    // planeRole/isTankPlane, planeStamina, planeColMult, planeFuel,
    // planeSpecSheet, planeMass, planeTopSpeed, planeCrashDmg, and the
    // mass-derivation rule (empty + half the useful load).
    //
    // DELIBERATELY NOT PORTED (and why): everything in planes.js from
    // loadPlane() down — loadSpitfireModel, glazeHood, lineHullInside,
    // findCockpit (the whole hull-profile/fuselage-axis measurement rig),
    // fitCockpitKit, buildPlaneShell, hingeHood, makeGameplayPlane. All of
    // that is RUNTIME MESH MEASUREMENT: it walks a loaded Three.js model's
    // own vertex buffers to find where the cockpit is, how big to grow the
    // airframe, where the wing meshes are, etc. There is no Unity analogue
    // for "scan the imported mesh at runtime" yet — the project's own stated
    // direction is to bake this kind of thing once (the way `propHubs`,
    // `cockpit: {...}` and `yaw`/`roll` overrides in THIS table already are:
    // numbers measured once with a tool and pinned as data, not recomputed
    // every load). Building that successor (a Blender/Python bake step or a
    // Unity-side one-time importer) is its own unit of work, belongs with
    // whichever system builds the actual flyable airframe wrapper, and is
    // out of this file's scope. CONTROLS/PILOT_* (the cockpit-kit seat and
    // stick offsets) are part of that same unbuilt system and are likewise
    // left out here.
    //
    // The 36-vs-35 note: docs/UNITY_PORT.md and this project's own memory
    // say "36 planes" / "the whole fleet... 36" in several places, and the
    // user's task brief for this port also says 36. But `grep -c "^    id:"
    // src/planes.js` on 2026-10-26 counts 35 — the memory log itself records
    // a C-17 Globemaster removal "2026-10-19" that evidently took the count
    // from 36 to 35, and the stray "36" in a few places is just not updated.
    // This file ports exactly what src/planes.js contains today: 35.
    public enum PlaneRole { DPS, TANK }

    /// <summary>cockpit: {...} override in a plane def — src/planes.js:1010 findCockpit's
    /// `override` parameter. Carried here as DATA ONLY: nothing in this port
    /// reads it yet (see the header above). null = "measure it" in the JS;
    /// here it simply means no override was authored for this airframe.</summary>
    public class CockpitOverride
    {
        public float? Z;        // station along the fuselage, model-local metres
        public bool? Closed;    // force glass-canopy fit rules rather than open-well
        public float? Y;        // seat pan height, outright
        public float? Room;     // head room, outright
        public float? Grow;     // whole-airframe growth factor, outright
    }

    /// <summary>propMake: {...} — "no propeller in the file at all; build one where
    /// the spinner is" (src/planes.js:239, src/propeller.js:645-709). Only the
    /// P-51 uses it. Consumed by PropellerSystem.cs.</summary>
    public class PropMake
    {
        public Vector3 Hub;      // (r, u, f) — model-local: starboard, up, nose
        public float Radius;
        public int Blades;
    }

    /// <summary>One row of src/planes.js PLANES. Field names mirror the JS keys
    /// (camelCase -> PascalCase) so a diff against the source is mechanical.
    /// Nullable numeric fields (`float?`, `int?`) are nullable for exactly the
    /// JS reason: the field was omitted from that literal, not zero.</summary>
    public class PlaneDef
    {
        public string Id, Name, Tagline, File, Thumb;
        public float Scale = 1f;

        // The ARCADE speed cap aero.js flies with (m/s) — not the real vmax.
        public float MaxSpeed;

        // Orientation fix. RAW JS VALUES, exactly as src/planes.js wrote them —
        // NOT yet converted for Unity. See the handedness note below.
        public float Yaw;   // rotation about the model's local Y (up)
        public float Roll;  // rotation about the model's local Z (nose axis)

        // ===================== HANDEDNESS — read before using Yaw/Roll =====================
        // These two fields are Euler angles applied to a plain wrapper Group
        // around the raw model (src/planes.js:834 `fixed.rotation.set(0,
        // def.yaw ?? 0, def.roll ?? 0)`), straightening a file that was not
        // authored nose-toward-forward. They rotate about the SAME two axes
        // FlightModel.cs's header already classifies:
        //   Yaw  is a rotation about local Y (up)    -> a YAW  -> NEGATE for Unity
        //   Roll is a rotation about local Z (nose)   -> a ROLL -> NEGATE for Unity
        // (pitch about local X never appears in this table — no def needs it.)
        // Use YawUnity / RollUnity below rather than these raw fields when
        // building a Unity Transform's rotation.
        //
        // ROLL'S SIGN WAS WRONG, AND THE REASON IS WORTH KEEPING. It used to
        // read "a ROLL -> KEEP", taken from FlightModel.cs's header, where
        // "roll (local Z): KEEP" is derived from the FLIGHT frame flipping
        // forward, i.e. from Z being the mirrored axis. Measured on all 35
        // shipped FBX (Editor/Probes/PlanePropsProbe.Axis), the import mirrors
        // **X**, not Z — see FlightModel.cs's second handedness block for the
        // measurement and the pipeline constant that pins it. Under an X
        // mirror a rotation about Y negates (unchanged, which is why YawUnity
        // was right by luck as well as by argument) and so does a rotation
        // about Z, because a mirror negates every rotation whose axis is
        // PERPENDICULAR to the mirror plane's normal. So Roll negates too.
        //
        // Zero consumers today (YawUnity/RollUnity are both unconsumed — see
        // FlightModel.cs's header on the missing model-child rotation), so
        // this changes no behaviour; it stops the next pass inheriting a sign
        // derived from an assumption the measurement refuted. `neonfly` is the
        // only def with a Roll, and in the Three.js build it "flew INVERTED
        // before the correct sign was found" — a 90-degree roll with the wrong
        // sign puts a plane on the opposite wing, not upright, which is
        // exactly the class of bug this would have reproduced in Unity.
        //
        // No def in this table sets BOTH Yaw and Roll non-zero at once (only
        // `neonfly` uses Roll, and only on its own; everything else uses Yaw
        // alone), so there is no Euler-composition-order question to resolve
        // here — negating one axis and keeping the other is unambiguous for
        // every entry that exists today. If a future def ever combined both,
        // the composition order would need re-deriving against whatever the
        // actual wrapper rotation is applied as.
        public float YawUnity => Data.GeoFrame.YawToUnity(Yaw);
        public float RollUnity => -Roll;
        // ===================================================================================

        public float? Empty, Mtow;   // kg — real published figures, where known
        public float Mass;            // kg — OPERATING mass, ALWAYS populated (see DeriveMasses)
        public float Tw;              // thrust/weight, static
        public float GBreak;          // load factor (g) that breaks the airframe
        public float ClMax;
        public bool Prop;             // propeller-driven, vs jet
        public int Props = 1;         // how many propellers the real aircraft has

        public float? Span, Len, Vmax, Ceil, Climb;
        public int? Crew, Year;

        public PlaneRole Role;
        public string Code, Cls, Tag;
        public int[] Stats;           // [სიჩქარე, მანევრი, ჯავშანი, ცეცხლის ძალა], 0..100
        public string Desc;           // Georgian, byte-for-byte from src/planes.js

        public CockpitOverride Cockpit;     // data only — see file header
        public Vector3[] PropHubs;           // (r, u, f) per hub, model-local metres
        public float PropRadius, PropDepth;  // model-local metres
        public PropMake PropMake;            // data only — consumed by PropellerSystem.cs

        public float? ColMult;   // explicit planeColMult() override
        public float? Fuel;      // explicit planeFuel() override, kg
    }

    public static class Planes
    {
        // ---- the table -------------------------------------------------------
        // src/planes.js:67-503. One entry per `export const PLANES` element, in
        // the same order, so a line-by-line diff against the source is direct.
        public static readonly PlaneDef[] All =
        {
            new PlaneDef { // src/planes.js:69
                Id = "spitfire", Name = "Spitfire",
                Tagline = "ლეგენდარული გამანადგურებელი · 650 კმ/სთ",
                File = "/models/spitfire.obj", Thumb = "/textures/planes/spitfire.png",
                Scale = 0.34f, MaxSpeed = 190f,
                Empty = 2545f, Mtow = 4309f, Tw = 2.8f, GBreak = 11f, ClMax = 1.6f, Prop = true,
                Span = 11.23f, Len = 9.47f, Vmax = 650f, Ceil = 13100f, Climb = 21.3f, Crew = 1, Year = 1942,
                Role = PlaneRole.DPS,
                Code = "MK-IX", Cls = "მებრძოლი", Tag = "FIGHTER", Stats = new[] { 58, 88, 42, 55 },
                Desc = "ლეგენდა, რომელიც ნეონის ეპოქასაც გადაურჩა. მოქნილი და მომტევებელი — ძველი თბილისის ვიწრო ქუჩებში მანევრირებისთვის იდეალურია.",
            },
            new PlaneDef { // src/planes.js:83
                Id = "kawasaki", Name = "Kawasaki Racer",
                Tagline = "სარბოლო თვითმფრინავი · 580 კმ/სთ",
                File = "/models/planes/kawasaki.glb", Thumb = "/textures/planes/kawasaki.png",
                Scale = 1f, MaxSpeed = 185f,
                Mass = 2500f, Tw = 2.6f, GBreak = 9f, ClMax = 1.5f, Prop = true, // fictional racer — mass estimated
                Vmax = 580f, Crew = 1,
                Role = PlaneRole.DPS,
                Code = "KW-R", Cls = "მზვერავი", Tag = "SCOUT", Stats = new[] { 55, 92, 28, 38 },
                Desc = "მსუბუქი სარბოლო აპარატი. თითქმის უჯავშნოა, მაგრამ მტკვრის ხიდების ქვეშ გაძრომისას მას ვერავინ დაეწევა.",
            },
            new PlaneDef { // src/planes.js:97
                Id = "p100", Name = "P-100 Avenger",
                Tagline = "შემტევი გამანადგურებელი · 700 კმ/სთ",
                File = "/models/planes/p100.glb", Thumb = "/textures/planes/p100.png",
                Scale = 1f, MaxSpeed = 220f,
                Mass = 5200f, Tw = 3.0f, GBreak = 10f, ClMax = 1.5f, Prop = true, // fictional type — mass estimated
                Vmax = 700f, Crew = 1,
                Role = PlaneRole.DPS,
                Code = "P-100", Cls = "მოიერიშე", Tag = "STRIKER", Stats = new[] { 66, 72, 58, 74 },
                Desc = "შემტევი გამანადგურებელი ორმაგი ქვემეხით. დაბალანსებული ბრძოლისთვის ნებისმიერ სიმაღლეზე — ნარიყალადან ტელეანძამდე.",
            },
            new PlaneDef { // src/planes.js:111
                Id = "f104", Name = "F-104 Starfighter",
                Tagline = "ზებგერითი გამანადგურებელი · 2137 კმ/სთ",
                File = "/models/planes/f104.glb", Thumb = "/textures/planes/f104.png",
                Scale = 1f, MaxSpeed = 420f,
                Empty = 6350f, Mtow = 13170f, Tw = 3.6f, GBreak = 8.5f, ClMax = 1.25f,
                Span = 6.68f, Len = 16.69f, Vmax = 2137f, Ceil = 15000f, Climb = 244f, Crew = 1, Year = 1954,
                Role = PlaneRole.DPS,
                Code = "F-104", Cls = "ჩამჭრელი", Tag = "INTERCEPT", Stats = new[] { 98, 40, 45, 68 },
                Desc = "«ქვრივების მჭედელი». სამართებელივით ბასრი ფრთა და ზებგერითი სისწრაფე — ერთი შეცდომა და კედელი შენია.",
            },
            new PlaneDef { // src/planes.js:126
                Id = "a7", Name = "A-7 Corsair II",
                Tagline = "მოიერიშე თვითმფრინავი · 1123 კმ/სთ",
                File = "/models/planes/a7.glb", Thumb = "/textures/planes/a7.png",
                Scale = 0.892f, MaxSpeed = 260f,
                Cockpit = new CockpitOverride { Z = -4.46f },
                Empty = 8676f, Mtow = 19050f, Tw = 3.0f, GBreak = 9.5f, ClMax = 1.7f,
                Span = 11.8f, Len = 14.06f, Vmax = 1123f, Ceil = 12800f, Climb = 38f, Crew = 1, Year = 1965,
                Role = PlaneRole.DPS,
                Code = "A-7", Cls = "მოიერიშე", Tag = "STRIKER", Stats = new[] { 74, 58, 72, 86 },
                Desc = "მიწასთან მუშაობის ოსტატი. სქელი ჯავშანი და მძიმე დანადგარი — ბანდების საყვარელი იარაღი სახურავების დასაშლელად.",
            },
            new PlaneDef { // src/planes.js:146
                Id = "b58", Name = "B-58 Hustler",
                Tagline = "ზებგერითი ბომბდამშენი · 2120 კმ/სთ",
                File = "/models/planes/b58.glb", Thumb = "/textures/planes/b58.png",
                Scale = 1f, MaxSpeed = 400f,
                Empty = 25201f, Mtow = 80240f, Tw = 3.2f, GBreak = 4.5f, ClMax = 1.3f,
                Span = 17.32f, Len = 29.49f, Vmax = 2120f, Ceil = 19300f, Climb = 88f, Crew = 3, Year = 1956,
                Role = PlaneRole.TANK,
                Code = "B-58", Cls = "ბომბდამშენი", Tag = "BOMBER", Stats = new[] { 95, 26, 60, 92 },
                Desc = "ზებგერითი დელტა-ბომბდამშენი. ფრთხილად მოუხვიე — მაგრამ როცა გაჩერდება მიზანზე, მთელი უბანი ინგრევა.",
            },
            new PlaneDef { // src/planes.js:161
                Id = "rq180", Name = "RQ-180",
                Tagline = "სტელს დრონი · 900 კმ/სთ",
                File = "/models/planes/rq180.glb", Thumb = "/textures/planes/rq180.png",
                Scale = 1f, MaxSpeed = 260f,
                Mass = 14000f, Tw = 2.8f, GBreak = 6f, ClMax = 1.4f, // classified — mass estimated
                Vmax = 900f,
                Role = PlaneRole.TANK,
                Code = "RQ-180", Cls = "პროტოტიპი", Tag = "STEALTH", Stats = new[] { 80, 62, 34, 48 },
                Desc = "ექსპერიმენტული სტელს-ფრთა. რადარები მას ვერ ხედავენ — მოწინააღმდეგე მხოლოდ აფეთქებას გაიგებს.",
            },
            // ---- tools/build_planes.py wave: fighters & trainers (DPS) ----
            new PlaneDef { // src/planes.js:176
                Id = "aerobatic", Name = "Extra 330", Tagline = "აკრობატიული · 407 კმ/სთ",
                File = "/models/planes/aerobatic.glb", Thumb = "/textures/planes/aerobatic.png", Scale = 1f, MaxSpeed = 170f,
                Empty = 670f, Mtow = 950f, Tw = 3.4f, GBreak = 12f, ClMax = 1.8f, Prop = true,
                Span = 7.5f, Len = 6.95f, Vmax = 407f, Ceil = 4900f, Climb = 16f, Crew = 1, Year = 1995,
                Role = PlaneRole.DPS,
                Code = "EX-330", Cls = "აკრობატი", Tag = "ACRO", Stats = new[] { 40, 100, 10, 8 },
                Desc = "საჰაერო ცირკის ვარსკვლავი. ±10G და უსასრულო ტრიალი — ვიწრო ეზოებში მას ვერავინ დაიჭერს.",
            },
            new PlaneDef { // src/planes.js:185
                Id = "cessna310", Name = "Cessna 310", Tagline = "მსუბუქი ორძრავა · 381 კმ/სთ",
                File = "/models/planes/cessna310.glb", Thumb = "/textures/planes/cessna310.png", Scale = 1f, MaxSpeed = 160f,
                Yaw = -Mathf.PI / 2f, // orientation fix — GLB stored nose-west (tools/probe_props.mjs)
                Empty = 1633f, Mtow = 2495f, Tw = 1.8f, GBreak = 6f, ClMax = 1.6f, Prop = true, Props = 2,
                Span = 11.25f, Len = 9.74f, Vmax = 381f, Ceil = 5760f, Climb = 8.2f, Crew = 1, Year = 1953,
                // Blades welded into the hull: measured with tools/_probe_hub.mjs.
                PropHubs = new[] { new Vector3(1.94f, 1.50f, 3.06f), new Vector3(-1.94f, 1.50f, 3.06f) },
                PropRadius = 0.78f, PropDepth = 0.34f,
                Role = PlaneRole.DPS,
                Code = "C-310", Cls = "სამოქალაქო", Tag = "CIVIL", Stats = new[] { 35, 55, 25, 10 },
                Desc = "მშვიდობიანი ამხანაგი ფრთის წვერის ავზებით. იარაღი თითქმის არ აქვს — სამაგიეროდ ბენზინზე იაფად დადის.",
            },
            new PlaneDef { // src/planes.js:197
                Id = "t28", Name = "T-28 Trojan", Tagline = "სასწავლო-საბრძოლო · 552 კმ/სთ",
                File = "/models/planes/t28.glb", Thumb = "/textures/planes/t28.png", Scale = 1f, MaxSpeed = 185f,
                Empty = 2914f, Mtow = 3856f, Tw = 2.4f, GBreak = 9f, ClMax = 1.7f, Prop = true,
                Span = 12.22f, Len = 10.06f, Vmax = 552f, Ceil = 10820f, Climb = 19.1f, Crew = 2, Year = 1949,
                Role = PlaneRole.DPS,
                Code = "T-28", Cls = "სასწავლო", Tag = "TRAINER", Stats = new[] { 48, 70, 35, 40 },
                Desc = "სკოლის მერხი, რომელიც ვიეტნამში ომში წავიდა. პატიოსანი, გამძლე და მოთმინებით სავსე — დამწყები გმირის საუკეთესო მეგობარი.",
            },
            new PlaneDef { // src/planes.js:206
                Id = "f4f", Name = "F4F Wildcat", Tagline = "გემბანის მებრძოლი · 533 კმ/სთ",
                File = "/models/planes/f4f.glb", Thumb = "/textures/planes/f4f.png", Scale = 1f, MaxSpeed = 180f,
                Yaw = -Mathf.PI / 2f, // orientation fix — GLB stored nose-west (tools/probe_props.mjs)
                Cockpit = new CockpitOverride { Z = -1.8f },
                Empty = 2612f, Mtow = 3607f, Tw = 2.3f, GBreak = 9.5f, ClMax = 1.8f, Prop = true,
                Span = 11.58f, Len = 8.76f, Vmax = 533f, Ceil = 10400f, Climb = 10.7f, Crew = 1, Year = 1937,
                Role = PlaneRole.DPS,
                Code = "F4F", Cls = "მებრძოლი", Tag = "FIGHTER", Stats = new[] { 45, 68, 48, 50 },
                Desc = "წყნარი ოკეანის ველური კატა. მოკლე ფრთები, ჯიუტი ხასიათი — და ბრძოლას არასდროს გაურბის.",
            },
            new PlaneDef { // src/planes.js:219
                Id = "p47", Name = "P-47 Thunderbolt", Tagline = "მძიმე მებრძოლი · 697 კმ/სთ",
                File = "/models/planes/p47.glb", Thumb = "/textures/planes/p47.png", Scale = 1f, MaxSpeed = 205f,
                // No orientation fix — tools/build_p47.py rebuilt this one levelled.
                Empty = 4536f, Mtow = 7938f, Tw = 2.5f, GBreak = 10f, ClMax = 1.6f, Prop = true,
                Span = 12.42f, Len = 11.02f, Vmax = 697f, Ceil = 12800f, Climb = 16.2f, Crew = 1, Year = 1941,
                Role = PlaneRole.DPS,
                Code = "P-47", Cls = "მებრძოლი", Tag = "FIGHTER", Stats = new[] { 60, 62, 70, 72 },
                Desc = "«ქილა» — რვა ტყვიამფრქვევი და ჯავშანი, რომელსაც პიკირებაში ვერავინ დაეწევა. ძირს დაცემაც კი ეშინია მისი.",
            },
            new PlaneDef { // src/planes.js:231
                Id = "p51", Name = "P-51 Mustang", Tagline = "სადაზვერვო-მებრძოლი · 703 კმ/სთ",
                File = "/models/planes/p51.glb", Thumb = "/textures/planes/p51.png", Scale = 1f, MaxSpeed = 210f,
                Empty = 3465f, Mtow = 5490f, Tw = 2.6f, GBreak = 10f, ClMax = 1.65f, Prop = true,
                Span = 11.28f, Len = 9.83f, Vmax = 703f, Ceil = 12800f, Climb = 16.3f, Crew = 1, Year = 1940,
                PropMake = new PropMake { Hub = new Vector3(0f, 1.57f, 4.95f), Radius = 1.70f, Blades = 4 },
                Role = PlaneRole.DPS,
                Code = "P-51", Cls = "მებრძოლი", Tag = "FIGHTER", Stats = new[] { 66, 78, 45, 60 },
                Desc = "კადილაკი ცაში. სწრაფი, ლამაზი და შორსმფრენი — ესკორტის მეფე, რომელიც მტკვრის ხეობასაც ბოლომდე გაჰყვება.",
            },
            new PlaneDef { // src/planes.js:244
                Id = "hsp75", Name = "Henschel Hs P.75", Tagline = "ქანარდიანი პროტოტიპი · 780 კმ/სთ",
                File = "/models/planes/hsp75.glb", Thumb = "/textures/planes/hsp75.png", Scale = 1f, MaxSpeed = 220f,
                Yaw = Mathf.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
                Mass = 5200f, Tw = 2.7f, GBreak = 8.5f, ClMax = 1.5f, Prop = true, // paper project — mass estimated
                Span = 18.0f, Len = 11.0f, Vmax = 780f, Crew = 1,
                Role = PlaneRole.DPS,
                Code = "P.75", Cls = "პროტოტიპი", Tag = "PROTO", Stats = new[] { 58, 66, 40, 45 },
                Desc = "ქაღალდიდან გამოქცეული ნახაზი: ფრთა უკან, საჭე წინ, ხრახნი კუდში. არავინ იცის როგორ დაფრინავს — მაგრამ დაფრინავს.",
            },
            new PlaneDef { // src/planes.js:254
                Id = "neonfly", Name = "El NeonFly", Tagline = "სარბოლო ჯეტი · 800 კმ/სთ",
                File = "/models/planes/neonfly.glb", Thumb = "/textures/planes/neonfly.png", Scale = 1f, MaxSpeed = 240f,
                Roll = -Mathf.PI / 2f, // orientation fix — flew INVERTED before the correct sign was found
                Mass = 3100f, Tw = 3.2f, GBreak = 11f, ClMax = 1.6f, // fictional type — mass estimated
                Vmax = 800f, Crew = 1,
                Role = PlaneRole.DPS,
                Code = "NEON", Cls = "სარბოლო", Tag = "RACER", Stats = new[] { 72, 95, 20, 15 },
                Desc = "შეჯიბრის ჯეტი ნეონის ქალაქისთვის. მსუბუქი, ავხორცი და თავხედი — ჰაერშივე იცვლის მიმართულებას, როგორც ფიქრს.",
            },
            new PlaneDef { // src/planes.js:268
                Id = "javelin", Name = "Gloster Javelin", Tagline = "ღამის ჩამჭრელი · 1140 კმ/სთ",
                File = "/models/planes/javelin.glb", Thumb = "/textures/planes/javelin.png", Scale = 1f, MaxSpeed = 265f,
                Yaw = Mathf.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
                Empty = 11580f, Mtow = 19580f, Tw = 2.9f, GBreak = 7.5f, ClMax = 1.45f,
                Span = 15.85f, Len = 17.15f, Vmax = 1140f, Ceil = 15900f, Climb = 51f, Crew = 2, Year = 1951,
                Role = PlaneRole.DPS,
                Code = "FAW-9", Cls = "ჩამჭრელი", Tag = "INTERCEPT", Stats = new[] { 70, 55, 60, 62 },
                Desc = "ბრიტანული დელტა ღამის ცისთვის. ორი ძრავა, ორი კაცი და ერთი მიზანი — რომ ღამით ბომბდამშენი არ გაგეპაროს.",
            },
            new PlaneDef { // src/planes.js:278
                Id = "mig21", Name = "MiG-21 Fishbed", Tagline = "ზებგერითი გამანადგურებელი · 2175 კმ/სთ",
                File = "/models/planes/mig21.glb", Thumb = "/textures/planes/mig21.png",
                Scale = 0.935f, MaxSpeed = 415f,
                Cockpit = new CockpitOverride { Z = -3.65f, Closed = true },
                Empty = 5846f, Mtow = 10400f, Tw = 3.4f, GBreak = 8.5f, ClMax = 1.3f,
                Span = 7.15f, Len = 14.7f, Vmax = 2175f, Ceil = 17800f, Climb = 225f, Crew = 1, Year = 1955,
                Role = PlaneRole.DPS,
                Code = "МиГ-21", Cls = "გამანადგურებელი", Tag = "FIGHTER", Stats = new[] { 92, 60, 40, 58 },
                Desc = "მფრინავი კალაშნიკოვი — მარტივი, იაფი და ულმობელი. ნახევარი მსოფლიო ამით სწავლობდა ზებგერით ფრენას.",
            },
            new PlaneDef { // src/planes.js:294
                Id = "mig21nk", Name = "MiG-21PFM", Tagline = "ზებგერითი გამანადგურებელი · 2175 კმ/სთ",
                File = "/models/planes/mig21nk.glb", Thumb = "/textures/planes/mig21nk.png", Scale = 1f, MaxSpeed = 410f,
                Empty = 5350f, Mtow = 9080f, Tw = 3.4f, GBreak = 8.5f, ClMax = 1.3f,
                Span = 7.15f, Len = 14.1f, Vmax = 2175f, Ceil = 18000f, Climb = 120f, Crew = 1, Year = 1964,
                Role = PlaneRole.DPS,
                Code = "ПФМ-415", Cls = "გამანადგურებელი", Tag = "FIGHTER", Stats = new[] { 90, 58, 42, 56 },
                Desc = "ჩრდილოეთის ნაცრისფერი ძმა, ბორტნომერი 415. იგივე რაკეტა ფრთებით — ოღონდ სხვისი დროშით და მკაცრი ხასიათით.",
            },
            new PlaneDef { // src/planes.js:302
                Id = "tornado", Name = "Panavia Tornado", Tagline = "ცვალებადი ფრთა · 2400 კმ/სთ",
                File = "/models/planes/tornado.glb", Thumb = "/textures/planes/tornado.png",
                Scale = 0.919f, MaxSpeed = 400f,
                Yaw = -0.658f, // orientation fix — GLB sits 37.7deg off the fuselage axis
                Empty = 13890f, Mtow = 28000f, Tw = 3.1f, GBreak = 7.5f, ClMax = 1.7f,
                Span = 13.91f, Len = 16.72f, Vmax = 2400f, Ceil = 15240f, Climb = 77f, Crew = 2, Year = 1974,
                Role = PlaneRole.DPS,
                Code = "GR-1", Cls = "მოიერიშე", Tag = "STRIKER", Stats = new[] { 88, 64, 62, 80 },
                Desc = "ევროპული ქარიშხალი საზეიმო შეფერილობით. ფრთებს კეცავს და მიწასთან აკრული მიქრის — რადარი ვერც ამჩნევს.",
            },
            new PlaneDef { // src/planes.js:313
                Id = "su33", Name = "Su-33 Flanker-D", Tagline = "გემბანის გამანადგურებელი · 2300 კმ/სთ",
                File = "/models/planes/su33.glb", Thumb = "/textures/planes/su33.png", Scale = 1f, MaxSpeed = 420f,
                Empty = 18400f, Mtow = 33000f, Tw = 3.5f, GBreak = 9f, ClMax = 1.9f,
                Span = 14.7f, Len = 21.19f, Vmax = 2300f, Ceil = 17000f, Climb = 246f, Crew = 1, Year = 1987,
                Role = PlaneRole.DPS,
                Code = "Су-33", Cls = "გამანადგურებელი", Tag = "FIGHTER", Stats = new[] { 94, 86, 58, 78 },
                Desc = "ზღვის ფლანკერი ქანარდებით. მძიმეა, მაგრამ კობრასავით იხრება — ჰაერში მასთან ჭიდაობა ცუდი იდეაა.",
            },
            new PlaneDef { // src/planes.js:322
                Id = "f15", Name = "F-15C Eagle", Tagline = "უზენაესობის მებრძოლი · 2655 კმ/სთ",
                File = "/models/planes/f15.glb", Thumb = "/textures/planes/f15.png", Scale = 1f, MaxSpeed = 440f,
                Empty = 12700f, Mtow = 30845f, Tw = 3.6f, GBreak = 9f, ClMax = 1.6f,
                Span = 13.05f, Len = 19.43f, Vmax = 2655f, Ceil = 20000f, Climb = 254f, Crew = 1, Year = 1972,
                Role = PlaneRole.DPS,
                Code = "F-15C", Cls = "გამანადგურებელი", Tag = "FIGHTER", Stats = new[] { 97, 80, 60, 84 },
                Desc = "104:0 — ეს მისი ანგარიშია საჰაერო ბრძოლებში. არწივი, რომელიც ჯერ არავის ჩამოუგდია.",
            },
            new PlaneDef { // src/planes.js:331
                Id = "f117", Name = "F-117 Nighthawk", Tagline = "სტელს-მოიერიშე · 993 კმ/სთ",
                File = "/models/planes/f117.glb", Thumb = "/textures/planes/f117.png", Scale = 1f, MaxSpeed = 250f,
                Empty = 13380f, Mtow = 23814f, Tw = 2.4f, GBreak = 6f, ClMax = 1.1f,
                Span = 13.2f, Len = 20.09f, Vmax = 993f, Ceil = 13700f, Crew = 1, Year = 1981,
                Role = PlaneRole.DPS,
                Code = "F-117", Cls = "სტელსი", Tag = "STEALTH", Stats = new[] { 70, 35, 45, 66 },
                Desc = "ღამის შავი დანა. რადარისთვის უჩინარია, თვალისთვის — კოშმარი. ფრთხილად: ეს დანა ორივე მხარეს ჭრის.",
            },
            // ---- tools/build_planes.py wave: big machines (TANK) ----
            new PlaneDef { // src/planes.js:341
                Id = "lancaster", Name = "Avro Lancaster", Tagline = "მძიმე ბომბდამშენი · 454 კმ/სთ",
                File = "/models/planes/lancaster.glb", Thumb = "/textures/planes/lancaster.png", Scale = 1f, MaxSpeed = 175f,
                Empty = 16738f, Mtow = 31751f, Tw = 2.0f, GBreak = 5f, ClMax = 1.9f, Prop = true, Props = 4,
                Span = 31.09f, Len = 21.18f, Vmax = 454f, Ceil = 6500f, Climb = 4.7f, Crew = 7, Year = 1941,
                PropHubs = new[]
                {
                    new Vector3(3.88f, 4.16f, 7.44f), new Vector3(-3.88f, 4.16f, 7.44f),
                    new Vector3(8.09f, 4.01f, 6.91f), new Vector3(-8.09f, 4.01f, 6.91f),
                },
                Cockpit = new CockpitOverride { Z = -8.4f },
                PropRadius = 2.30f, PropDepth = 0.95f,
                Role = PlaneRole.TANK,
                Code = "LANC", Cls = "ბომბდამშენი", Tag = "BOMBER", Stats = new[] { 40, 25, 70, 85 },
                Desc = "ღამის ლეგენდა ოთხი «მერლინით». კუპოლები წრიპინებენ, ბომბსაცავი კი სავსეა — ძველი სკოლის მძიმე არგუმენტი.",
            },
            new PlaneDef { // src/planes.js:359
                Id = "h6k", Name = "Xian H-6K", Tagline = "სარაკეტო ბომბდამშენი · 1050 კმ/სთ",
                File = "/models/planes/h6k.glb", Thumb = "/textures/planes/h6k.png", Scale = 1f, MaxSpeed = 250f,
                Yaw = Mathf.PI, // orientation fix — GLB stored tail-first (tools/probe_props.mjs)
                Empty = 38530f, Mtow = 79000f, Tw = 2.5f, GBreak = 4.5f, ClMax = 1.5f,
                Span = 34.19f, Len = 34.8f, Vmax = 1050f, Ceil = 12800f, Climb = 15f, Crew = 4, Year = 2007,
                Role = PlaneRole.TANK,
                Code = "H-6K", Cls = "ბომბდამშენი", Tag = "BOMBER", Stats = new[] { 72, 22, 68, 90 },
                Desc = "ძველი Ту-16-ის აღმოსავლური რეინკარნაცია. ექვსი ფრთისქვეშა რაკეტა — და უბანი აღარ არის უბანი.",
            },
            new PlaneDef { // src/planes.js:369
                Id = "tu144", Name = "Tu-144", Tagline = "ზებგერითი ლაინერი · 2500 კმ/სთ",
                File = "/models/planes/tu144.glb", Thumb = "/textures/planes/tu144.png",
                Scale = 0.776f, MaxSpeed = 430f,
                Yaw = -0.702f, // orientation fix — GLB sits 40.2deg off the fuselage axis
                Cockpit = new CockpitOverride { Z = -21.7f },
                Empty = 85000f, Mtow = 180000f, Tw = 3.0f, GBreak = 4f, ClMax = 1.4f,
                Span = 28.8f, Len = 65.7f, Vmax = 2500f, Ceil = 18000f, Crew = 3, Year = 1968,
                Role = PlaneRole.TANK,
                Code = "Ту-144", Cls = "ზებგერითი", Tag = "SUPERSONIC", Stats = new[] { 99, 15, 55, 20 },
                Desc = "«კონკორდსკი» — სამგზავრო თვითმფრინავი, რომელიც ტყვიაზე სწრაფად დაფრინავს. მოხვევა? ჯერ ბათუმამდე გაფრინდი, მერე მოუხვიე.",
            },
            new PlaneDef { // src/planes.js:386
                Id = "xb35", Name = "Northrop XB-35", Tagline = "მფრინავი ფრთა · 630 კმ/სთ",
                File = "/models/planes/xb35.glb", Thumb = "/textures/planes/xb35.png", Scale = 1f, MaxSpeed = 200f,
                Empty = 40590f, Mtow = 94347f, Tw = 2.2f, GBreak = 4.5f, ClMax = 1.8f, Prop = true, Props = 4,
                Span = 52.43f, Len = 16.18f, Vmax = 630f, Ceil = 12100f, Climb = 3.6f, Crew = 9, Year = 1946,
                Role = PlaneRole.TANK,
                Code = "XB-35", Cls = "პროტოტიპი", Tag = "PROTO", Stats = new[] { 55, 30, 75, 82 },
                Desc = "ფუსელაჟი? კუდი? ზედმეტი დეტალებია. მხოლოდ ფრთა, ოთხი მბრუნავი ხრახნი და 52 მეტრი ჩრდილი ქალაქზე.",
            },
            new PlaneDef { // src/planes.js:396
                Id = "carpet", Name = "Carpet Bomber", Tagline = "ხალიჩა-ბომბდამშენი · 480 კმ/სთ",
                File = "/models/planes/carpet.glb", Thumb = "/textures/planes/carpet.png", Scale = 1f, MaxSpeed = 190f,
                Cockpit = new CockpitOverride { Z = -11.5f, Closed = true }, // the measurement found the fin, not the nose
                Mass = 30000f, Tw = 2.1f, GBreak = 4.8f, ClMax = 1.8f, Prop = true, Props = 4, // fictional type — mass estimated
                Vmax = 480f, Crew = 4,
                Role = PlaneRole.TANK,
                Code = "LCB", Cls = "ბომბდამშენი", Tag = "BOMBER", Stats = new[] { 45, 28, 65, 88 },
                Desc = "სახელი თავად ამბობს: სადაც ეს ჩაივლის, იქ ბალახიც ხელახლა ითესება. ფრთხილად შენს უბანთან.",
            },
            new PlaneDef { // src/planes.js:406
                Id = "b737", Name = "Boeing 737-300", Tagline = "ავიალაინერი · 880 კმ/სთ",
                File = "/models/planes/b737.glb", Thumb = "/textures/planes/b737.png", Scale = 1f, MaxSpeed = 235f,
                Empty = 32820f, Mtow = 56472f, Tw = 2.3f, GBreak = 4f, ClMax = 2.0f,
                Span = 28.88f, Len = 33.4f, Vmax = 880f, Ceil = 11300f, Climb = 12.7f, Crew = 2, Year = 1984,
                Role = PlaneRole.TANK,
                Code = "B-737", Cls = "ავიალაინერი", Tag = "LINER", Stats = new[] { 66, 30, 62, 10 },
                Desc = "სამოქალაქო კლასიკა. 148 სავარძელი, უფასო წვენი და გმირი შტურვალთან — რას იზამ, ომია.",
            },
            new PlaneDef { // src/planes.js:414
                Id = "c130", Name = "C-130J Hercules", Tagline = "ტაქტიკური სატრანსპორტო · 670 კმ/სთ",
                File = "/models/planes/c130.glb", Thumb = "/textures/planes/c130.png", Scale = 1f, MaxSpeed = 205f,
                Empty = 34274f, Mtow = 70307f, Tw = 2.2f, GBreak = 4.2f, ClMax = 2.1f, Prop = true, Props = 4,
                Span = 40.41f, Len = 29.79f, Vmax = 670f, Ceil = 8615f, Climb = 10.9f, Crew = 3, Year = 1996,
                PropHubs = new[]
                {
                    new Vector3(5.11f, 5.99f, 6.09f), new Vector3(-5.11f, 5.99f, 6.09f),
                    new Vector3(10.31f, 6.11f, 6.00f), new Vector3(-10.31f, 6.11f, 6.00f),
                },
                PropRadius = 2.20f, PropDepth = 0.80f,
                Role = PlaneRole.TANK,
                Code = "C-130J", Cls = "სატრანსპორტო", Tag = "TRANSPORT", Stats = new[] { 52, 34, 80, 25 },
                Desc = "ჰერკულესი — სახელი ავალდებულებს. ზის იქ, სადაც ბილიკიც არ არის, და ოთხი ხრახნით ისევ ცაში ადის.",
            },
            new PlaneDef { // src/planes.js:426
                Id = "yc130", Name = "YC-130 Prototype", Tagline = "ჰერკულესის წინაპარი · 640 კმ/სთ",
                File = "/models/planes/yc130.glb", Thumb = "/textures/planes/yc130.png", Scale = 1f, MaxSpeed = 200f,
                Empty = 27000f, Mtow = 56336f, Tw = 2.2f, GBreak = 4.2f, ClMax = 2.1f, Prop = true, Props = 4,
                Span = 40.41f, Len = 29.79f, Vmax = 640f, Ceil = 7000f, Climb = 9f, Crew = 4, Year = 1954,
                PropHubs = new[]
                {
                    new Vector3(5.19f, 4.27f, 6.73f), new Vector3(-5.19f, 4.27f, 6.73f),
                    new Vector3(10.35f, 4.57f, 6.73f), new Vector3(-10.35f, 4.57f, 6.73f),
                },
                Cockpit = new CockpitOverride { Z = -12.9f },
                PropRadius = 2.08f, PropDepth = 0.88f,
                Role = PlaneRole.TANK,
                Code = "YC-130", Cls = "პროტოტიპი", Tag = "PROTO", Stats = new[] { 50, 36, 74, 22 },
                Desc = "პირველი ჰერკულესი — ჯერ კიდევ ქარხნული სუნით. ისტორია ფრთებზე: 1954 წლის 23 აგვისტო, პირველი აფრენა.",
            },
            new PlaneDef { // src/planes.js:442
                Id = "cargobro", Name = "Cargo Bro", Tagline = "სადესანტო ტრანსპორტი · 830 კმ/სთ",
                File = "/models/planes/cargobro.glb", Thumb = "/textures/planes/cargobro.png",
                Scale = 1.3125f, MaxSpeed = 210f,
                Empty = 128100f, Mtow = 265352f, Tw = 2.3f, GBreak = 4.5f, ClMax = 2.0f, Props = 0, // four turbofans, not props
                Span = 51.75f, Len = 53.04f, Vmax = 830f, Ceil = 13700f, Crew = 3, Year = 1991,
                Role = PlaneRole.TANK,
                Code = "CARGO", Cls = "სატრანსპორტო", Tag = "TRANSPORT", Stats = new[] { 55, 32, 85, 30 },
                Desc = "სადესანტო ძმაკაცი. უკანა რამპა იღება, ასი მებრძოლი გადმოდის — შენ კი უბრალოდ წრეებს ხაზავ ქალაქზე.",
            },
            new PlaneDef { // src/planes.js:454
                Id = "il76", Name = "Ilyushin Il-76", Tagline = "მძიმე სატრანსპორტო · 900 კმ/სთ",
                File = "/models/planes/il76.glb", Thumb = "/textures/planes/il76.png", Scale = 1f, MaxSpeed = 235f,
                Yaw = Mathf.PI, // THIS MODEL IS STORED TAIL-FIRST — used to fly backwards
                Empty = 92500f, Mtow = 190000f, Tw = 2.3f, GBreak = 4f, ClMax = 2.2f,
                Span = 50.5f, Len = 46.59f, Vmax = 900f, Ceil = 13000f, Climb = 14f, Crew = 5, Year = 1971,
                Role = PlaneRole.TANK,
                Code = "Ил-76", Cls = "სატრანსპორტო", Tag = "TRANSPORT", Stats = new[] { 64, 24, 88, 28 },
                Desc = "საბჭოთა სამუშაო ცხენი სრული ინტერიერით. კაბინაში შუშის ცხვირია — ჩამოჯექი და უყურე, როგორ მიცურავს ქალაქი.",
            },
            new PlaneDef { // src/planes.js:470
                Id = "b767", Name = "Boeing 767", Tagline = "შორეული ლაინერი · 913 კმ/სთ",
                File = "/models/planes/b767.glb", Thumb = "/textures/planes/b767.png", Scale = 1f, MaxSpeed = 240f,
                Cockpit = new CockpitOverride { Z = -24.4f },
                Empty = 80130f, Mtow = 142880f, Tw = 2.3f, GBreak = 3.8f, ClMax = 2.1f,
                Span = 47.57f, Len = 48.51f, Vmax = 913f, Ceil = 13100f, Climb = 15f, Crew = 2, Year = 1981,
                Role = PlaneRole.TANK,
                Code = "B-767", Cls = "ავიალაინერი", Tag = "LINER", Stats = new[] { 68, 22, 70, 5 },
                Desc = "ორმილიანი ტრანსატლანტიკური დარბაისელი. დინჯად მოხვევს, დინჯად დაჯდება — და მთელი უბანი ჩრდილში ეხვევა.",
            },
            new PlaneDef { // src/planes.js:482
                Id = "awacs", Name = "Air Defense Sentinel", Tagline = "სადარაჯო თვითმფრინავი · 853 კმ/სთ",
                File = "/models/planes/awacs.glb", Thumb = "/textures/planes/awacs.png", Scale = 1f, MaxSpeed = 230f,
                Empty = 73480f, Mtow = 147400f, Tw = 2.3f, GBreak = 4f, ClMax = 2.0f,
                Span = 44.42f, Len = 46.61f, Vmax = 853f, Ceil = 12500f, Crew = 17, Year = 1975,
                Role = PlaneRole.TANK,
                Code = "AWACS", Cls = "სადარაჯო", Tag = "RADAR", Stats = new[] { 60, 26, 78, 35 },
                Desc = "ცის დიდი ძმა. ყველას ხედავს, ყველას ისმენს — შენს უბანში ბეღურაც ვერ აფრინდება მისი ნებართვის გარეშე.",
            },
            new PlaneDef { // src/planes.js:491
                Id = "an225", Name = "Antonov An-225 Mriya", Tagline = "მსოფლიოში უდიდესი · 850 კმ/სთ",
                File = "/models/planes/an225.glb", Thumb = "/textures/planes/an225.png", Scale = 1f, MaxSpeed = 235f,
                Cockpit = new CockpitOverride { Z = -32.5f },
                Empty = 285000f, Mtow = 640000f, Tw = 2.1f, GBreak = 3.2f, ClMax = 2.3f,
                Span = 88.4f, Len = 84.0f, Vmax = 850f, Ceil = 11000f, Crew = 6, Year = 1988,
                Role = PlaneRole.TANK,
                Code = "Ан-225", Cls = "გიგანტი", Tag = "GIANT", Stats = new[] { 62, 8, 100, 15 },
                Desc = "«მრია» — ოცნება. ექვსი ძრავა, 32 ბორბალი, 88 მეტრი ფრთა. როცა ეს დაჯდება, აეროპორტი ორ დღეს ივსება ხალხით.",
            },
        };

        static readonly Dictionary<string, PlaneDef> ById =
            All.ToDictionary(p => p.Id, p => p);

        static Planes()
        {
            // src/planes.js:507-512 — "Operating mass, from the two real numbers
            // above... every mass in the game is either a published pair or an
            // explicit, labelled estimate." A def with an explicit Mass (the
            // fictions/estimates) keeps it; everything else derives empty +
            // half the useful load.
            foreach (var p in All)
            {
                if (p.Mass <= 0f && p.Empty.HasValue && p.Mtow.HasValue)
                    // JsMath.Round in DOUBLE, not Mathf.Round in float: planes.js:511's
                    // Math.round always goes UP on a .5 tie, Mathf.Round goes to EVEN,
                    // and `empty + 0.5*(mtow-empty)` lands exactly on .5 whenever the
                    // useful load is odd -- wrong for 5 of 35 planes (b58, c130, f15,
                    // lancaster, xb35), each off by exactly 1 kg.
                    p.Mass = JsMath.Round((double)p.Empty.Value
                                          + 0.5 * ((double)p.Mtow.Value - p.Empty.Value));
            }
        }

        /// <summary>src/planes.js:505 planeById — falls back to PLANES[0] (spitfire),
        /// exactly like the JS `?? PLANES[0]`.</summary>
        public static PlaneDef PlaneById(string id)
            => (id != null && ById.TryGetValue(id, out var p)) ? p : All[0];

        // ---- planeRole() — explicitly named in the porting brief ----
        // src/planes.js:49-51
        public static PlaneRole PlaneRoleOf(PlaneDef p) => p?.Role ?? PlaneRole.DPS;
        public static PlaneRole PlaneRoleOf(string id) => PlaneRoleOf(PlaneById(id));
        public static bool IsTankPlane(PlaneDef p) => PlaneRoleOf(p) == PlaneRole.TANK;
        public static bool IsTankPlane(string id) => PlaneRoleOf(id) == PlaneRole.TANK;

        // ---- planeStamina() — src/planes.js:62-65 ----
        public static int PlaneStamina(PlaneDef def)
        {
            int armour = def?.Stats != null && def.Stats.Length > 2 ? def.Stats[2] : 40;
            float mass = def?.Mass ?? 3000f;
            // planes.js:64 Math.round, in double. See PlaneMass above for the tie rule.
            return JsMath.Round(300.0 + armour * 8.0 + System.Math.Sqrt(mass) * 6.0);
        }
        public static int PlaneStamina(string id) => PlaneStamina(PlaneById(id));

        // ---- planeColMult() — src/planes.js:521-525 ----
        public static float PlaneColMult(PlaneDef def)
        {
            if (def?.ColMult.HasValue == true) return def.ColMult.Value;
            int armour = def?.Stats != null && def.Stats.Length > 2 ? def.Stats[2] : 40;
            return 1.6f - 0.011f * armour;
        }
        public static float PlaneColMult(string id) => PlaneColMult(PlaneById(id));

        // ---- planeFuel() — src/planes.js:534-539 ----
        public static float PlaneFuel(PlaneDef def)
        {
            if (def?.Fuel.HasValue == true) return def.Fuel.Value;
            if (def?.Empty.HasValue == true && def.Mtow.HasValue)
                // planes.js:537 Math.round in double. `0.25*(mtow-empty)` is an exact
                // .5 tie whenever (mtow-empty) % 4 == 2 -- wrong for 4 of 35 planes
                // (f117, mig21, mig21nk, p47; p47 is 850.5 -> 851, not 850).
                return JsMath.Round(0.25 * ((double)def.Mtow.Value - def.Empty.Value));
            return JsMath.Round((def?.Mass ?? 3000f) * 0.12);
        }
        public static float PlaneFuel(string id) => PlaneFuel(PlaneById(id));

        // ---- planeSpecSheet() — src/planes.js:545-559 ----
        public static string PlaneSpecSheet(PlaneDef def)
        {
            if (def == null) return "";
            var bits = new List<string> { def.Name };
            if (!string.IsNullOrEmpty(def.Cls)) bits.Add(def.Cls);
            if (def.Span.HasValue && def.Len.HasValue)
                bits.Add($"ფრთები {FmtG(def.Span.Value)} მ · სიგრძე {FmtG(def.Len.Value)} მ");
            if (def.Empty.HasValue && def.Mtow.HasValue)
                // JsMath.ToFixed, not ToString("0.0"): planes.js formats with toFixed(1), which
                // rounds the EXACT IEEE value; .NET "0.0" rounds the shortest round-trip
                // decimal, so 6350/1000 came out "6.4" (f104), 950 "1.0" (aerobatic), 5350
                // "5.4" (mig21nk) where the JS shows 6.3 / 0.9 / 5.3. Double would not fix it.
                bits.Add($"ცარიელი {JsMath.ToFixed(def.Empty.Value / 1000.0, 1)} ტ · მაქს. {JsMath.ToFixed(def.Mtow.Value / 1000.0, 1)} ტ");
            if (def.Ceil.HasValue) bits.Add($"ჭერი {FmtG(def.Ceil.Value)} მ");
            if (def.Climb.HasValue) bits.Add($"აღმასვლა {FmtG(def.Climb.Value)} მ/წმ");
            if (def.Crew.HasValue) bits.Add($"ეკიპაჟი {def.Crew.Value}");
            if (def.Year.HasValue) bits.Add($"{def.Year.Value} წ.");
            return string.Join(" · ", bits);
        }
        public static string PlaneSpecSheet(string id) => PlaneSpecSheet(PlaneById(id));

        // JS template-literal number formatting (`${def.span}`) prints an integer
        // with no decimal point and a non-integer with its natural digits (no
        // fixed precision). This reproduces that for the values this table uses.
        static string FmtG(float v) =>
            (v == Mathf.Floor(v)) ? v.ToString("0") : v.ToString("0.####");

        // ---- planeMass() — src/planes.js:562-565 ----
        public static float PlaneMass(PlaneDef def) => def?.Mass ?? 3000f;
        public static float PlaneMass(string id) => PlaneMass(PlaneById(id));

        // ---- planeTopSpeed() — src/planes.js:570-573 ----
        public static float PlaneTopSpeed(PlaneDef def)
            // planes.js:572 Math.round in double -- same tie rule as PlaneMass.
            => def?.Vmax ?? JsMath.Round((def?.MaxSpeed ?? 190f) * 3.6);
        public static float PlaneTopSpeed(string id) => PlaneTopSpeed(PlaneById(id));

        // ---- planeCrashDmg() — src/planes.js:589-596 ----
        // "a 3 400 kg Spitfire met head-on at 100 m/s closing" is the fixed
        // reference; this is the collision solver's OWN arithmetic
        // (PlanePhysics.cs / src/planephys.js), not an independent number, so
        // it will drift if that solver's K_PLANE (2.2) ever changes without
        // this changing too. K_PLANE is replicated here (it is not exported
        // from planephys.js) — see PlanePhysics.cs's own K_PLANE constant.
        const float RefMass = 3400f;     // kg — a loaded Spitfire
        const float RefCol = 1.138f;     // its own collision multiplier (armour 42)
        const float KPlane = 2.2f;       // src/planephys.js:74 — replicated, see note above
        public static int PlaneCrashDmg(PlaneDef def)
        {
            float m1 = PlaneMass(def);
            float mu = (m1 * RefMass) / (m1 + RefMass);
            float e = 0.5f * mu * 100f * 100f * (1f - 0.05f * 0.05f);
            // planes.js:595 Math.round in double -- same tie rule as PlaneMass.
            return Mathf.Max(0, JsMath.Round(((e / (double)RefMass - 8.0) / KPlane) * RefCol));
        }
        public static int PlaneCrashDmg(string id) => PlaneCrashDmg(PlaneById(id));
    }
}
