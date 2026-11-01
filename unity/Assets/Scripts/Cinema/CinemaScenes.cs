using UnityEngine;
using static TbilisiFly.Cinema.CinemaDirector;

namespace TbilisiFly.Cinema
{
    // =========================================================================
    // Port of src/cinema.js's scene data — SCENES, BEATS1/BEATS2, SHOTS1/
    // SHOTS2, FLIGHT_SHOTS (cinema.js:56-542). THE JS IS THE SPEC: every
    // number below is copied, not re-tuned, and every entry cites its JS line.
    // See CinemaReel.cs's class header for what calls this data and what is
    // deliberately not wired up yet (stage measurement, asset loading).
    // =========================================================================
    public static class CinemaScenes
    {
        // ---- scene 1: the range — cinema.js:59-74 --------------------------
        const float StartZ = -3.0f;

        // ---- scene 1's cards — cinema.js:62-66 CARDS1 ----------------------
        // FOUND UNPORTED in a later pass — see CardSpec's own doc comment.
        static readonly CardSpec[] Cards1Data =
        {
            new CardSpec(0.0f, 11.0f),  // his own lane: the draw, the roll
            new CardSpec(3.3f, 12.4f),  // two booths right: the jump, the slide
            new CardSpec(6.6f, 9.4f),   // four right, where the spin leaves him facing
        };

        // ---- scene 2: the crates — cinema.js:242-250 -----------------------
        const float Approach = 2.0f;
        const float FlipSlow = 0.6f;
        const float ReloadPace = 1.35f;
        const float S2StartA = 1.3f, S2StartB = 2.9f;
        const float S2CornerIn = 0.55f;

        // =====================================================================
        // ---- SCENE 1 — THE SCRIPT (cinema.js:84-126 BEATS1) ----------------
        // =====================================================================
        public static readonly Beat[] Beats1 =
        {
            new Beat(0.00f, c => { c.Hold(ReelKeys.W | ReelKeys.Ctrl); c.AimAt(0); }),
            new Beat(1.30f, c => c.Hold()),
            new Beat(1.44f, c => c.Draw()),
            new Beat(1.84f, c => c.Sight(true)), // cinema.js:96 game.pistol.setAiming(true) directly
            new Beat(2.14f, c => c.Fire(0)),
            new Beat(2.20f, c => c.Slow(0.085f)),
            new Beat(3.00f, c => c.Slow(1f)),
            new Beat(3.20f, c => c.Roll(Mathf.PI)),
            new Beat(3.80f, c => c.Fire()),
            new Beat(4.12f, c => c.Fire()),
            new Beat(4.30f, c => { c.Hold(ReelKeys.D); c.AimAt(1); }),
            new Beat(4.56f, c => c.Hold(ReelKeys.D | ReelKeys.Space)),
            new Beat(4.74f, c => { c.Hold(ReelKeys.D); c.Slow(0.42f); }),
            new Beat(4.88f, c => c.Fire(1)),
            new Beat(5.14f, c => c.Fire(1)),
            new Beat(5.30f, c => { c.Slow(1f); c.Hold(); }),
            new Beat(5.82f, c => c.Spin(1.0f)),
            new Beat(6.34f, c => { c.AimAt(2); c.Fire(2); }),
            new Beat(7.00f, c => { c.Hold(ReelKeys.W); c.AimAt(1); }),
            new Beat(7.36f, c => { c.Hold(); c.Slide(); }),
            new Beat(7.98f, c => c.Fire(1)),
            new Beat(8.30f, c => c.Slow(0.32f)),
            new Beat(8.44f, c => c.Fire(1)),
            new Beat(8.78f, c => c.Slow(1f)),
            new Beat(9.04f, c => c.Fire(1)),
        };

        // ---- SCENE 1 — THE CUT LIST (cinema.js:136-213 SHOTS1) -------------
        public static readonly Shot[] Shots1 =
        {
            // 1. ON THE DECK — cinema.js:140-144.
            new Shot { T = 0f, D = 1.50f, Fov = new Vector2(37, 32), Roll = new Vector2(-0.03f, 0.005f), Shake = 0.25f,
                Cam = (k, S) => S.P(1.5f - 0.25f * Smooth(k), 0.40f, 3.3f - 0.75f * Smooth(k)),
                Look = (k, S) => S.Mix(S.P(0, 0.10f, 0f), S.Actor(0, 1.05f, 0), Smooth(Mathf.Clamp01(k * 1.7f - 0.25f))) },
            // 2. THE INSERT — cinema.js:148-152.
            new Shot { T = 1.50f, D = 0.56f, Fov = new Vector2(31, 29), Roll = new Vector2(0.075f, 0.03f), Shake = 0.5f,
                Cam = (k, S) => S.Actor(1.35f, 0.72f + 0.15f * k, 0.25f),
                Look = (k, S) => S.Mix(S.Hip(), S.Actor(0.30f, 1.18f, 0.30f), Smooth(Mathf.Clamp01(k * 1.5f - 0.5f))) },
            // 3. THE SIGHT PICTURE — cinema.js:156-159.
            new Shot { T = 2.06f, D = 0.30f, Fov = new Vector2(40, 38), Roll = new Vector2(0.01f, 0.02f), Shake = 0.3f,
                Cam = (k, S) => S.Actor(0.62f, 1.62f, -1.15f - 0.1f * k),
                Look = (k, S) => S.Card(0, 0, 0.05f, 0) },
            // 4. THE ROUND — cinema.js:162-166.
            new Shot { T = 2.36f, D = 0.80f, Fov = new Vector2(30, 36), Roll = new Vector2(0.02f, -0.025f), Shake = 0.15f,
                Cam = (k, S) => S.Mix(S.Actor(0.85f, 1.42f, 0.55f), S.Card(0, 0.95f, 0.12f, -1.25f), EaseOut(k)),
                Look = (k, S) => S.Round() ?? S.Card(0, 0, 0.04f, 0) },
            // 5. THE ROLL — cinema.js:170-173.
            new Shot { T = 3.16f, D = 1.30f, Fov = new Vector2(34, 39), Roll = new Vector2(-0.05f, 0.035f), Shake = 0.6f,
                Cam = (k, S) => S.Orbit(-1.25f + 1.0f * Smooth(k), 3.9f - 0.55f * k, 0.75f + 0.95f * Smooth(k)),
                Look = (k, S) => S.Actor(0, 1.0f + 0.1f * k, 0.3f) },
            // 6. THE JUMP — cinema.js:177-180.
            new Shot { T = 4.46f, D = 1.30f, Fov = new Vector2(40, 36), Roll = new Vector2(0.05f, -0.05f), Shake = 0.45f,
                Cam = (k, S) => S.Actor(-4.9f + 0.5f * Smooth(k), 1.25f, 0.8f - 1.1f * Smooth(k)),
                Look = (k, S) => S.Actor(0.1f, 1.15f, 0) },
            // 7. THE SPIN — cinema.js:185-188.
            new Shot { T = 5.76f, D = 1.20f, Fov = new Vector2(48, 44), Roll = new Vector2(-0.06f, 0.06f), Shake = 0.7f,
                Cam = (k, S) => S.Orbit(2.1f + 1.6f * Smooth(k), 2.9f, 1.45f - 0.25f * k),
                Look = (k, S) => S.Actor(0, 1.15f, 0.1f) },
            // 8. THE SLIDE — cinema.js:193-196.
            new Shot { T = 6.96f, D = 1.90f, Fov = new Vector2(32, 38), Roll = new Vector2(0.04f, -0.02f), Shake = 0.55f,
                Cam = (k, S) => S.P(2.0f, 0.30f, 3.3f - 0.4f * Smooth(k)),
                Look = (k, S) => S.Mix(S.Actor(0, 0.95f, 0), S.Actor(0, 0.6f, 0.6f), Smooth(Mathf.Clamp01(k * 1.4f - 0.3f))) },
            // 9. THE PAPER — cinema.js:200-203.
            new Shot { T = 8.86f, D = 0.84f, Fov = new Vector2(36, 33), Roll = new Vector2(-0.02f, 0.015f), Shake = 0.3f,
                Cam = (k, S) => S.Card(1, 0.78f - 0.1f * Smooth(k), 0.22f, -1.15f + 0.22f * Smooth(k)),
                Look = (k, S) => S.Card(1, 0.02f, 0.0f, 0) },
            // 10. HERO OUT — cinema.js:209-212.
            new Shot { T = 9.70f, D = 0.90f, Fov = new Vector2(38, 34), Roll = new Vector2(0.035f, -0.015f), Shake = 0.35f,
                Cam = (k, S) => S.Actor(1.5f - 0.2f * Smooth(k), 0.42f, 1.0f - 0.3f * Smooth(k)),
                Look = (k, S) => S.Actor(0, 1.2f + 0.08f * k, 0) },
        };

        // =====================================================================
        // ---- SCENE 2 — THE CRATES (cinema.js:252-309 BEATS2) ---------------
        // =====================================================================
        public static readonly Beat[] Beats2 =
        {
            new Beat(0.00f, c => { c.Slow(Approach); c.AimAt(null); c.Face(FaceMode.In); c.Hold(ReelKeys.W | ReelKeys.Shift); }),
            new Beat(0.38f, c => c.Hold()),
            new Beat(0.70f, c => c.Slow(1f)),
            new Beat(1.05f, c => c.Hold(ReelKeys.W | ReelKeys.Ctrl)),
            new Beat(1.42f, c => c.Hold()),
            new Beat(1.52f, c => c.Draw()),
            new Beat(2.40f, c => { c.AimAt(0); c.Sight(true); }),
            new Beat(2.80f, c => c.Fire(0)),
            new Beat(2.93f, c => c.Fire(0)),
            new Beat(3.06f, c => c.Fire(0)),
            new Beat(3.19f, c => { c.Fire(0); c.Slow(0.4f); }),
            new Beat(3.45f, c => c.Fire(0)),
            new Beat(3.67f, c => { c.Slow(1f); c.Fire(0); }),
            new Beat(3.82f, c => { c.Sight(false); c.AimAt(null); }),
            // cinema.js:288 `c.flip(c._stage.row.lid)` — the crate's own lid height, not the tower.
            new Beat(3.92f, c => { c.Slow(FlipSlow); c.Flip(c.Stage.Row?.Lid); }),
            new Beat(5.17f, c => c.Slow(1f)),
            new Beat(5.35f, c => {
                c.Face(FaceMode.Along);
                c.Hold(ReelKeys.W);
                float rowLen = c.Stage.Row?.Len ?? 0f;
                c.RunTo(rowLen - S2CornerIn, d => { d.Hold(); d.Cover(); });
            }),
            new Beat(6.55f, c => { c.Slow(ReloadPace); c.Reload(); }),
            new Beat(10.05f, c => c.Slow(1f)),
            new Beat(10.20f, c => { c.Face(FaceMode.None); c.AimAt(1); c.Sight(true); }),
            new Beat(10.65f, c => c.Fire(1)),
            new Beat(10.78f, c => c.Fire(1)),
            new Beat(10.91f, c => c.Fire(1)),
            new Beat(11.04f, c => { c.Fire(1); c.Slow(0.35f); }),
            new Beat(11.30f, c => c.Fire(1)),
            new Beat(11.55f, c => { c.Slow(1f); c.Fire(1); }),
        };

        // ---- SCENE 2 — THE CUT LIST (cinema.js:314-403 SHOTS2) -------------
        public static readonly Shot[] Shots2 =
        {
            // 1. THE RUN IN — cinema.js:322-325.
            new Shot { T = 0f, D = 1.18f, Fov = new Vector2(40, 36), Roll = new Vector2(-0.03f, 0.01f), Shake = 0.45f,
                Cam = (k, S) => S.Row(-2.8f, 5.2f - 0.7f * Smooth(k), 1.2f),
                Look = (k, S) => S.Actor(0, 0.9f, 0) },
            // 2. UP ON THE CRATE — cinema.js:332-335.
            new Shot { T = 1.18f, D = 1.22f, Fov = new Vector2(44, 41), Roll = new Vector2(0.04f, -0.02f), Shake = 0.4f,
                Cam = (k, S) => S.Row(S.HeroA() + 3.3f, 5.5f, 1.8f),
                Look = (k, S) => S.Actor(0, 0.2f, 0) },
            // 3. THE BURST FROM THE CRATE — cinema.js:339-342.
            new Shot { T = 2.40f, D = 1.35f, Fov = new Vector2(44, 40), Roll = new Vector2(-0.03f, 0.03f), Shake = 0.7f,
                Cam = (k, S) => S.Row(S.HeroA() + 4.0f, 6.3f, 1.9f),
                Look = (k, S) => S.Actor(0, 0.3f, 0) },
            // 4. ...and the card taking them — cinema.js:345-348.
            new Shot { T = 3.75f, D = 0.20f, Fov = new Vector2(34, 31), Roll = new Vector2(0.02f, -0.015f), Shake = 0.3f,
                Cam = (k, S) => S.Card(0, 0.45f - 0.05f * Smooth(k), 0.12f, -1.55f + 0.15f * Smooth(k)),
                Look = (k, S) => S.Card(0, 0, 0, 0) },
            // 5. THE BACKFLIP — cinema.js:354-357.
            new Shot { T = 3.95f, D = 1.22f, Fov = new Vector2(40, 44), Roll = new Vector2(0.04f, -0.05f), Shake = 0.4f,
                Cam = (k, S) => S.Row(S.HeroA() + 5.0f, 1.4f, 2.7f - 1.3f * Smooth(k)),
                Look = (k, S) => S.Actor(0, 0.4f, 0) },
            // 6. UP OUT OF IT — cinema.js:361-364.
            new Shot { T = 5.17f, D = 1.43f, Fov = new Vector2(36, 40), Roll = new Vector2(-0.03f, 0.025f), Shake = 0.55f,
                Cam = (k, S) => S.Row(S.HeroA() + 4.6f, 5.8f, 1.0f),
                Look = (k, S) => S.Actor(0, 1.0f, 0) },
            // 7. THE SLAM into cover — cinema.js:369-372.
            new Shot { T = 6.60f, D = 0.60f, Fov = new Vector2(36, 33), Roll = new Vector2(0.06f, 0.02f), Shake = 0.5f,
                Cam = (k, S) => S.Row(12.3f, 3.0f, 1.7f),
                Look = (k, S) => S.Actor(0, 1.1f, 0) },
            // 8. THE MAGAZINE — cinema.js:377-380.
            new Shot { T = 7.20f, D = 1.75f, Fov = new Vector2(32, 30), Roll = new Vector2(-0.05f, -0.02f), Shake = 0.45f,
                Cam = (k, S) => S.Row(S.HeroA() + 2.1f, 2.1f, 1.25f),
                Look = (k, S) => S.Actor(0, 0.86f, 0) },
            // 9. ...out to the whole man for the RACK — cinema.js:385-388.
            new Shot { T = 8.95f, D = 1.25f, Fov = new Vector2(40, 37), Roll = new Vector2(0.03f, -0.02f), Shake = 0.4f,
                Cam = (k, S) => S.Row(13.4f, 3.8f - 0.5f * Smooth(k), 1.8f),
                Look = (k, S) => S.Actor(0, 1.1f, 0) },
            // 10. ROUND THE EDGE — cinema.js:392-395.
            new Shot { T = 10.20f, D = 1.30f, Fov = new Vector2(42, 38), Roll = new Vector2(-0.02f, 0.02f), Shake = 0.6f,
                Cam = (k, S) => S.Actor(0, 1.7f, -2.4f) + S.RowU() * 0.85f,
                Look = (k, S) => S.Card(1, 0, 0.1f, 0) },
            // 11. THE PAPER, as the burst arrives — cinema.js:399-402.
            new Shot { T = 11.50f, D = 0.60f, Fov = new Vector2(34, 31), Roll = new Vector2(0.02f, -0.015f), Shake = 0.3f,
                Cam = (k, S) => S.Card(1, 0.45f - 0.06f * Smooth(k), 0.12f, -1.55f + 0.18f * Smooth(k)),
                Look = (k, S) => S.Card(1, 0, 0, 0) },
        };

        // =====================================================================
        // ---- SCENE 3 — THE FLYPAST (cinema.js:457-519 FLIGHT_SHOTS) --------
        // =====================================================================
        public static readonly Shot[] FlightShots =
        {
            // 1. HE GETS IN — cinema.js:461-464.
            new Shot { T = 0f, D = 2.10f, Fov = new Vector2(34, 31), Roll = new Vector2(0.04f, -0.02f), Shake = 0.4f,
                Cam = (k, S) => S.Plane(-3.4f + 0.5f * Smooth(k), 1.25f, 3.1f - 0.9f * Smooth(k)),
                Look = (k, S) => S.Seat(0, 0.55f, 0) },
            // 2. THE PROPELLER — cinema.js:469-472.
            new Shot { T = 2.10f, D = 2.55f, Fov = new Vector2(26, 23), Roll = new Vector2(-0.03f, 0.02f), Shake = 0.3f,
                Cam = (k, S) => S.Nose(4.8f + 1.3f * Smooth(k), 1.05f, 3.2f - 0.7f * Smooth(k)),
                Look = (k, S) => S.Nose(0.15f, 0.55f, 0.2f) },
            // 3. IT GOES — cinema.js:489-490. Static: the ground point does not move.
            new Shot { T = 4.65f, D = 1.50f, Fov = new Vector2(38, 34), Roll = new Vector2(0.02f, -0.05f), Shake = 0.55f,
                Cam = (k, S) => S.Ground(5.5f, 2.4f, 70f),
                Look = (k, S) => S.Plane(0, 1.2f, 0) },
            // 4. DOWN THE ROOFTOPS — cinema.js:498-499.
            new Shot { T = 6.15f, D = 1.70f, Fov = new Vector2(62, 50), Roll = new Vector2(-0.06f, 0.03f), Shake = 0.45f,
                Cam = (k, S) => S.Plane(-S.Span() * 0.78f, 1.0f, 2.4f + 8.5f * Smooth(k)),
                Look = (k, S) => S.Plane(S.Span() * 0.28f, -3.4f - 1.8f * Smooth(k), -5f) },
            // 5. THE GOD'S EYE — cinema.js:508-510. Up vector is the NOSE, not world up.
            new Shot { T = 7.85f, D = 1.65f, Fov = new Vector2(52, 66), Roll = new Vector2(0f, 0f), Shake = 0.2f,
                Cam = (k, S) => S.Plane(0, 44f + 62f * Smooth(k), 6f + 10f * Smooth(k)),
                Look = (k, S) => S.Plane(0, 0, -2f),
                Up = (k, S) => S.FwdVec() },
            // 6. THE HERO SHOT — cinema.js:516-517.
            new Shot { T = 9.50f, D = 2.10f, Fov = new Vector2(40, 54), Roll = new Vector2(0.05f, -0.07f), Shake = 0.45f,
                Cam = (k, S) => S.Plane(2.0f + 1.5f * Smooth(k), -5.5f + 11f * Smooth(k), -62f + 56f * Smooth(k)),
                Look = (k, S) => S.Plane(0, 0.4f, 0) },
        };

        // =====================================================================
        // ---- THE SCENES (cinema.js:525-542) --------------------------------
        // =====================================================================
        public static readonly SceneDef[] All =
        {
            new SceneDef
            {
                Id = "range", Name = "სცენა 1 — ტირი", Dur = 10.6f, Gun = "beretta", Flight = false,
                Beats = Beats1, Shots = Shots1,
                // frame one: already walking in, on his mark — cinema.js:530.
                Place = (c, s) => (s.O + s.Fwd * StartZ, s.Yaw, 1.9f),
                Cards = s => Cards1Data,   // cinema.js:528 cards: CARDS1
            },
            new SceneDef
            {
                Id = "crates", Name = "სცენა 2 — ყუთები", Dur = 12.1f, Gun = "akm", Flight = false,
                Beats = Beats2, Shots = Shots2,
                Place = (c, s) => (s.Row.HasValue ? s.Row.Value.At(S2StartA, S2StartB, 0f) : s.O,
                                    s.Row?.YawIn ?? s.Yaw, 0f),
                // cinema.js:534 `cards: (s) => [{at:s.row.boxX,dist:7.0},{at:s.row.cardX,dist:7.0}]`.
                Cards = s => s.Row.HasValue
                    ? new[] { CardSpec.FromAt(s.Row.Value.BoxX, 7.0f), CardSpec.FromAt(s.Row.Value.CardX, 7.0f) }
                    : System.Array.Empty<CardSpec>(),
            },
            new SceneDef
            {
                Id = "flight", Name = "სცენა 3 — ფრენა", Dur = 11.6f, Gun = null, Flight = true,
                Beats = System.Array.Empty<Beat>(), Shots = FlightShots, Place = null,
                Cards = s => System.Array.Empty<CardSpec>(),   // cinema.js:541 cards: []
            },
        };
    }
}
