using System.Collections.Generic;

namespace TbilisiFly.Character
{
    /// <summary>
    /// Port of src/characters.js's RACES / PERSONAGES / bodiesOf (lines 1-131),
    /// plus the WALK_SET table (lines 111-127) read together with the measured
    /// root-motion speeds in moves.json.
    ///
    /// THE BIG CUT, per .claude/skills/unity/SKILL.md and docs/UNITY_PORT.md:
    /// `PERSONAGES` in the JS lists 13 ids and ships 39 GLBs on disk, because a
    /// hero already saved with (say) the old `anime` or `ghost` body has to keep
    /// it. `RACES[].bodies` is the actual create-screen roster and it is 4:
    ///
    ///     ხილ-ბოსტნეული 🍌  ->  banana, eggplant, toad (სოკო-კაცი, "mushroom-man")
    ///     სათამაშოები  🧸  ->  elephant ONLY
    ///
    /// Those other 35 are NOT ported here — no FBX conversion, no entry in this
    /// roster, nothing. A save-migration layer for an old Three.js hero is a
    /// product decision for whoever owns cross-save compatibility, not this file.
    ///
    /// GEORGIAN ONLY. roleKa carries the exact strings the JS prints on the
    /// create screen ("⚔ მებრძოლი" / "🛡 ფარი") — the user explicitly rejected
    /// English DPS/TANK strings on this screen (src/characters.js:22-25) and
    /// that rule carries over unchanged. TANK/DPS stay where they decide
    /// something: on the planes (Flight/PlaneCatalog.cs), never here.
    /// </summary>
    public static class CharacterRoster
    {
        /// <summary>src/characters.js:88 — every body is normalised to this height, metres.</summary>
        public const float HeroHeight = 1.8f;

        public sealed class Race
        {
            public string Id;
            public string Name;      // Georgian race name
            public string Latin;
            public string RoleKa;    // Georgian-only role string for the create screen
            public string Icon;
            public float RunSpeed;   // m/s on foot
            public int[] Stats;      // [შეტევა, დაცვა, განკურნება, სისწრაფე] 0-100
            public string[] Bodies;  // personage ids this race may wear, in order
            public string DefModel;
            public string Desc;
        }

        public sealed class Personage
        {
            public string Id;
            public string Name;   // Georgian
            public string Icon;
            /// <summary>Generic (non-Humanoid) rig with only one shipped clip —
            /// true for exactly one body here: the elephant. CharacterRig gives
            /// it a procedural run instead of a second clip.</summary>
            public bool ProceduralOnly;
        }

        // src/characters.js:28-69 — only the 4-body subset (see header).
        public static readonly Race[] Races =
        {
            new Race
            {
                Id = "veggie", Name = "ხილ-ბოსტნეული", Latin = "VEGE-FRUIT",
                RoleKa = "⚔ მებრძოლი", Icon = "🍌", RunSpeed = 7.2f,
                Stats = new[] { 88, 46, 22, 84 },
                // 2026-10-31: the race is 3D Everything's "Cartoon - Fruits and Vegetables"
                // pack (15 bodies, limbs + rig added by tools/build_veggies.py);
                // banana/eggplant/toad (Polygonal Mind) are gone at the user's request.
                Bodies = new[] { "apple", "banana", "broccoli", "cherry", "cucumber", "kiwi", "lettuce", "onion", "orange", "pear", "plum", "potato", "red_pepper", "tomato", "strawberry" },
                DefModel = "banana",
                Desc = "ბაზრის რიგიდან აყვანილი რაზმი — მსუბუქი, მწიფე და სწრაფი.",
            },
            new Race
            {
                Id = "toys", Name = "სათამაშოები", Latin = "TOYS",
                RoleKa = "🛡 ფარი", Icon = "🧸", RunSpeed = 5.6f,
                Stats = new[] { 58, 96, 34, 42 },
                Bodies = new[] { "elephant" },
                DefModel = "elephant",
                Desc = "თაროდან ჩამოსული ჯარი — პლასტმასის ჯავშანი და მძიმე მანქანები.",
            },
        };

        // src/characters.js:89-109 — only the 4 ids RACES actually names.
        public static readonly Personage[] Personages =
        {
            new Personage { Id = "apple",      Name = "ვაშლი",       Icon = "🍎" },
            new Personage { Id = "banana",     Name = "ბანანი",      Icon = "🍌" },
            new Personage { Id = "broccoli",   Name = "ბროკოლი",     Icon = "🥦" },
            new Personage { Id = "cherry",     Name = "ალუბალი",     Icon = "🍒" },
            new Personage { Id = "cucumber",   Name = "კიტრი",       Icon = "🥒" },
            new Personage { Id = "kiwi",       Name = "კივი",        Icon = "🥝" },
            new Personage { Id = "lettuce",    Name = "სალათა",      Icon = "🥬" },
            new Personage { Id = "onion",      Name = "ხახვი",       Icon = "🧅" },
            new Personage { Id = "orange",     Name = "ფორთოხალი",   Icon = "🍊" },
            new Personage { Id = "pear",       Name = "მსხალი",      Icon = "🍐" },
            new Personage { Id = "plum",       Name = "ქლიავი",      Icon = "🟣" },
            new Personage { Id = "potato",     Name = "კარტოფილი",   Icon = "🥔" },
            new Personage { Id = "red_pepper", Name = "წიწაკა",      Icon = "🌶" },
            new Personage { Id = "tomato",     Name = "პომიდორი",    Icon = "🍅" },
            new Personage { Id = "strawberry", Name = "მარწყვი",     Icon = "🍓" },
            new Personage { Id = "elephant", Name = "სპილო",       Icon = "🐘", ProceduralOnly = true },
        };

        static readonly Dictionary<string, Personage> ById = Build();

        static Dictionary<string, Personage> Build()
        {
            var d = new Dictionary<string, Personage>();
            foreach (var p in Personages) d[p.Id] = p;
            return d;
        }

        public static Race RaceById(string id)
        {
            foreach (var r in Races) if (r.Id == id) return r;
            return Races[0];
        }

        public static Personage PersonageById(string id)
            => ById.TryGetValue(id, out var p) ? p : Personages[0];

        /// <summary>src/characters.js:75-79 bodiesOf — a race's own body list,
        /// filtered to ids this roster actually has (none are missing here,
        /// since we only ported the 4 the races actually name).</summary>
        public static IReadOnlyList<string> BodiesOf(string raceId)
        {
            var race = RaceById(raceId);
            var list = new List<string>();
            foreach (var id in race.Bodies) if (ById.ContainsKey(id)) list.Add(id);
            if (list.Count == 0) list.Add(race.DefModel);
            return list;
        }

        public static bool IsHumanoidBody(string id) => id != "elephant" && ById.ContainsKey(id); // every fruit/veg body carries the 22-bone humanoid rig
        public static bool IsProceduralBody(string id) => id == "elephant";

        /// <summary>
        /// src/characters.js:111-127 — the eight walk cycles IN COMPASS ORDER,
        /// turning the body's own way: ahead, ahead-left, left, back-left, back,
        /// back-right, right, ahead-right. `AngleDeg` is the same signed angle
        /// walkClip() indexes with (0 ahead, +90 to the body's LEFT, like the JS).
        ///
        /// THIS IS src/anims.js's PORT HOME. A `Character/MoveLibrary.cs` used
        /// to hold a moves.json loader and a clip-metadata table beside this
        /// one; it was deleted 2026-10-31 as a duplicate with no caller. The
        /// reason it had none is the whole point of anims.js: that file exists
        /// because three.js has no retargeter, and its ~300 lines of SCHEMES/
        /// mapBones/buildRig/retarget/sampleQ (anims.js:109-369) solve "play a
        /// UE-mannequin clip on an arbitrary rig" — which Unity's Humanoid
        /// Avatar system IS, built in, and banana/eggplant/toad are all
        /// imported as Humanoid (`animationType: 3` in their .fbx.meta). What
        /// anims.js's metadata half actually DELIVERS in the shipped JS is this
        /// table and nothing else: characters.js:2792-2809 `addMoves` asks
        /// movesFor() for WALK_SET only, and walkClip()/walkRate()
        /// (characters.js:2812-2834) consume just `move`/`dur` — which is
        /// MoveLateral/MoveForward/Dur below, measured off the same
        /// public/models/anims/moves.json. The library's climb/parkour/melee/
        /// idle clips (14/5/3/9 of them) are never played by the JS at all —
        /// grep for ClimbUp/Vault/Slide/Melee across src/*.js hits only
        /// anims.js's own comments; src/foot.js's climb, vault and slide are
        /// procedural, bone-written moves, and so is Character/FootClimb.cs.
        ///
        /// UNITY REPLACEMENT (the big win named in SKILL.md): the JS's walkClip()
        /// does `i = round(a / (pi/4)) mod 8` and hard-switches to a DISCRETE clip
        /// each frame, with its own time-scale (walkRate) to match ground speed.
        /// That whole indexing scheme is what a 2D Freeform Directional BlendTree
        /// is FOR — Unity blends continuously between the surrounding 2-3 clips
        /// instead of snapping, which is strictly smoother. `MoveLateral`/
        /// `MoveForward` below are exactly the per-clip root-motion components
        /// (from moves.json's `move`, read at load the same way WALK_CLIP_SPEED
        /// was: `move.x / dur`, `move.z / dur`) and are meant to be used AS the
        /// child-motion 2D positions in that blend tree — see CharacterRig's
        /// walk-direction notes. They are NOT uniform: the sidesteps cover 0.9 m
        /// per cycle where the forward walk covers 1.35 m (src/characters.js:121),
        /// which is exactly why a single constant speed would skate them.
        ///
        /// SIGN CONVENTION: MoveLateral follows moves.json / the JS's own
        /// convention directly (+ = the body's LEFT), not Unity's +X-is-right
        /// axis-letter convention — see CharacterRig.SetWalkDirection. This
        /// sidesteps a handedness question entirely: the blend tree's 2D space
        /// is OUR OWN abstract space, consistent between the child-motion
        /// positions here and the parameter the mover feeds in, which is all a
        /// BlendTree actually requires.
        /// </summary>
        public struct WalkClip
        {
            public string Name;
            public float AngleDeg;     // compass offset from "ahead", + = left
            public float MoveLateral;  // m per cycle, + = left   (moves.json move[0])
            public float MoveForward;  // m per cycle, + = ahead  (moves.json move[2])
            public float Dur;          // s, one loop
            public float SpeedLateral => Dur > 0 ? MoveLateral / Dur : 0f;
            public float SpeedForward => Dur > 0 ? MoveForward / Dur : 0f;
        }

        // Measured off public/models/anims/moves.json (tag:"walk" clips), cross-
        // checked against src/characters.js's own comment ("the sidesteps cover
        // 0.9 m where the forward walk covers 1.35"): Walk_L_Loop move=[0.9,0,0],
        // Walk_Fwd_Loop move=[0,0,1.35] — matches exactly. dur is identical
        // (1.3333 s) for all eight in the shipped library.
        public static readonly WalkClip[] WalkSet =
        {
            new WalkClip { Name = "Walk_Fwd_Loop",   AngleDeg = 0,    MoveLateral = 0f,    MoveForward = 1.35f, Dur = 1.3333f },
            new WalkClip { Name = "Walk_Fwd_L_Loop",  AngleDeg = 45,  MoveLateral = 1.0f,  MoveForward = 1.0f,  Dur = 1.3333f },
            new WalkClip { Name = "Walk_L_Loop",      AngleDeg = 90,  MoveLateral = 0.9f,  MoveForward = 0f,    Dur = 1.3333f },
            new WalkClip { Name = "Walk_Bwd_L_Loop",  AngleDeg = 135, MoveLateral = 1.3f,  MoveForward = -1.3f, Dur = 1.3333f },
            new WalkClip { Name = "Walk_Bwd_Loop",    AngleDeg = 180, MoveLateral = 0f,    MoveForward = -1.4f, Dur = 1.3333f },
            new WalkClip { Name = "Walk_Bwd_R_Loop",  AngleDeg = -135, MoveLateral = -1.3f, MoveForward = -1.3f, Dur = 1.3333f },
            new WalkClip { Name = "Walk_R_Loop",      AngleDeg = -90, MoveLateral = -0.8f, MoveForward = 0f,    Dur = 1.3333f },
            new WalkClip { Name = "Walk_Fwd_R_Loop",  AngleDeg = -45, MoveLateral = -1.0f, MoveForward = 1.0f,  Dur = 1.3333f },
        };

        /// <summary>src/characters.js:122 WALK_CLIP_SPEED — the literal constant
        /// the JS ships (the forward cycle's own reach, 1.35 m / 1.3333 s, rounds
        /// to 1.0125 — the JS keeps the rounder 1.01 and so does this port,
        /// verbatim, per this port's rule to carry odd-looking measured constants
        /// across exactly rather than "fixing" them).</summary>
        public const float WalkClipSpeed = 1.01f;

        /// <summary>src/characters.js:127 — a walk may be hurried this far (x the
        /// clip's own speed) before the run cycle has to take back over.</summary>
        public const float WalkMaxRate = 2.6f;
    }
}
