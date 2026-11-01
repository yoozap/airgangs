using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using TbilisiFly.Flight;
using TbilisiFly.Character;

namespace TbilisiFly.Core
{
    // =========================================================================
    // Boot.cs — port of src/main.js (856 lines): STARTUP FLOW. WoW's flow and
    // nothing between: login -> your personages -> the world. There is no
    // start menu and no map page (both were deleted 2026-10-19, see project
    // memory) — "▶ ამ გმირით შესვლა" on the personage screen loads the city,
    // and settings live on that screen too.
    //
    // ================================ SCOPE ================================
    // This file owns the FLOW and the DATA (auth state, hero roster, which
    // screen is showing, the handoff into GameLoop) — exactly what main.js
    // owns. Every screen-specific action main.js wires to a DOM button is a
    // public method here (SubmitAuth, SelectHero, CreateHero, ...) and the
    // screens bind to its events (ScreenChanged, RosterChanged, AuthError) —
    // the same "feed an existing view" pattern GameLoop.cs uses for HUD/
    // Target/SkillBar.
    //
    // The VIEWS are UI/Shell/ShellView.cs (index.html's #screen-login and
    // #chrome) and UI/Character/CharacterView.cs (#screen-character), owned by
    // UI/Shell/ShellUi.cs, which this file builds in Awake and tears down on
    // the way into the world. Until 2026-10-30 none of that existed and all
    // three events above had ZERO subscribers: the flow ran correctly and
    // nothing was ever drawn. See ShellUi.cs's header for the UIDocument
    // lifetime rule and StandDownNoLoginEntry() below for why
    // PlayableSceneBoot no longer races it.
    //
    // What IS already a verified, direct port and simply called here:
    //   - TokenStore (Core/NetClient.cs)   — src/main.js:67-101 getToken/
    //                                        setToken/clearToken, byte-for-byte
    //   - NetClient.{Register,Login,Me,Logout} — server/index.js's contract
    //   - HeroLink                        — src/net.js, the hero-state socket
    //   - CharacterRoster.{Races,Personages,BodiesOf} — src/characters.js's
    //                                        static data (RACES/PERSONAGES)
    //   - Flight.Planes.{All,PlaneById,PlaneRoleOf} — src/planes.js's data
    // =========================================================================

    /// <summary>src/main.js:14-18 `screens` + :56-61 `showScreen`.</summary>
    public enum BootScreen { Login, Character, Game }

    /// <summary>Mirrors main.js's roster entry shape 1:1 (it IS NetClient's
    /// CharacterDef — no separate type needed, same as the server contract).</summary>
    public sealed class HeroRecord
    {
        public string Hid, Name, Race, Model, Plane;
    }

    [DisallowMultipleComponent]
    public sealed class Boot : MonoBehaviour
    {
        // ---- probe-readable facts (tools/_probe_gameloop.mjs cross-checks
        // these against src/main.js's own text) --------------------------------
        public const int MaxHeroes = 9;                 // main.js:128 MAX_HEROES
        public const string AutostartArg = "-autostart"; // stands in for `?auto=1` — see Autostart()
        public const string NoLoginArg = "-nologin";     // keeps the pre-shell entry point — see StandDownNoLoginEntry()
        public const string RememberNote =
            "reload keeps you logged in (remember unticked -> session-only, never logs out on reload)"; // main.js:78-101

        // =========================================================================
        // ---- scene wiring ------------------------------------------------------
        // =========================================================================
        [Header("Handoff target")]
        public GameLoop Game;
        public Camera MainCamera;

        [Header("Body prefabs — one GLB-imported prefab per CharacterRoster body id")]
        public List<string> BodyIds = new List<string>();
        public List<GameObject> BodyPrefabs = new List<GameObject>(); // parallel array (Inspector-friendly)

        [Header("Plane prefabs — one PlaneController per flyable plane id, pre-placed in the scene")]
        public List<PlaneController> PlaneControllers = new List<PlaneController>();

        public HeroLink Hero = new HeroLink();

        // =========================================================================
        // ---- events for a future UI (see class header) -------------------------
        // =========================================================================
        public event Action<BootScreen> ScreenChanged;
        public event Action RosterChanged;
        public event Action<string> AuthError;     // Georgian error strings, byte-for-byte from the server
        public event Action<string> HintChanged;   // loading-text equivalent

        /// <summary>The last string pushed through <see cref="HintChanged"/>.
        /// UI/Common/LoadingOverlayView.cs is created by
        /// Core/CombatWorldAdapter.InstallUi, which runs LATER in
        /// StartFlightAsync than the hint does (the shell panel is gone by
        /// then — see SetScreen), so the overlay replays this instead of
        /// losing main.js:671-673's first and only loading string.</summary>
        public string LastHint { get; private set; }

        /// <summary>One place the invoke happens, so <see cref="LastHint"/>
        /// cannot drift from what was raised. main.js:671-673.</summary>
        void RaiseHint(string georgian)
        {
            LastHint = georgian;
            HintChanged?.Invoke(georgian);
        }

        public BootScreen Screen { get; private set; } = BootScreen.Login;
        public PublicUser CurrentUser { get; private set; }
        public List<HeroRecord> Roster { get; private set; } = new List<HeroRecord>();
        public int ActiveIndex { get; private set; } = -1;
        public string AuthMode { get; private set; } = "login"; // main.js:209 — "login" | "register"

        // create-mode selection (main.js:258-365's selectedRace/selectedModel)
        public string SelectedRaceId { get; private set; } = CharacterRoster.Races[0].Id;
        public string SelectedModelId { get; private set; }
        int _selectedHeroRosterIdx; // roster-mode cursor, main.js:364 selectedHero

        // src/main.js:146 RACE_MOVED — retired races map onto the current two.
        static readonly Dictionary<string, string> RaceMoved = new Dictionary<string, string>
        {
            ["kori"] = "veggie", ["artsivi"] = "veggie", ["devi"] = "toys",
        };

        // Race -> starter PLANE ROLE. There is no plane-picking on the personage
        // screen (project memory, 2026-10-19): a race owns a role, a role owns a
        // starter machine. RoleKa strings carry the same split (veggie = "⚔
        // მებრძოლი" i.e. DPS; toys = "🛡 ფარი" i.e. TANK) — see
        // CharacterRoster.Races.
        static readonly Dictionary<string, PlaneRole> RaceRole = new Dictionary<string, PlaneRole>
        {
            ["veggie"] = PlaneRole.DPS, ["toys"] = PlaneRole.TANK,
        };

        /// <summary>public so UI/Character/CharacterView.cs can SHOW the starter
        /// plane's name without duplicating the lookup. Visibility only — CreateHero
        /// still computes it independently, so what is saved never depends on what
        /// the screen displays.</summary>
        public static string StarterPlaneFor(string raceId)
        {
            var role = RaceRole.TryGetValue(raceId, out var r) ? r : PlaneRole.DPS;
            var p = Planes.All.FirstOrDefault(pd => Planes.PlaneRoleOf(pd.Id) == role);
            return (p ?? Planes.All[0]).Id;
        }

        void Awake()
        {
            // A Development build pops Unity's red "Development Console" over
            // the game on the first logged error (the user's 2026-10-31 21:30
            // screenshot: six animation-sampling lines across the HUD). The JS
            // build never drew its console on screen; errors go to Player.log,
            // which is where they are read. Editor behaviour is unchanged.
            if (!Application.isEditor) { Debug.developerConsoleEnabled = false; Debug.developerConsoleVisible = false; }
            SelectedModelId = CharacterRoster.RaceById(SelectedRaceId)?.DefModel;
            Hero.OnStates = (_) => RosterChanged?.Invoke(); // main.js:23-29 heroes.onStates
            EnsureShell();
            if (_shell != null) StandDownNoLoginEntry();
        }

        void Start()
        {
            BootSequence();
        }

        void Update() => Hero.Tick(Time.deltaTime); // drains the WS queue — src/net.js's own per-frame drain

        /// <summary>main.js:313-316's rAF tick for the hero preview. LateUpdate,
        /// not Update: the tick ends in CharacterRig.Locomote, i.e. BONE WRITES,
        /// and this project's rule is that bone writes happen after the Animator
        /// has run — Core/CharacterPreviewRig.Tick's own header says so in as
        /// many words ("Must run in LateUpdate if the caller is a
        /// MonoBehaviour"). Hero.Tick stays in Update because it only drains a
        /// socket queue.</summary>
        void LateUpdate() => _shell?.Tick(Time.deltaTime);

        void OnDestroy() => DisposeShell();

        void SetScreen(BootScreen s)
        {
            Screen = s;
            // Rebuild on the way BACK OUT of the world (Logout, main.js:815;
            // BackToCharacterSelect, main.js:779-782) — see DisposeShell().
            if (s != BootScreen.Game) EnsureShell();
            ScreenChanged?.Invoke(s);
            // ...and tear it down on the way IN, before StartFlightAsync reaches
            // CombatWorldAdapter.Install, which creates the game's own
            // UIDocument. Order matters; UI/Shell/ShellUi.cs's header has why.
            if (s == BootScreen.Game) DisposeShell();
        }

        // =========================================================================
        // ---- the shell UI — login + personages (UI/Shell/ShellUi.cs) ----------
        // =========================================================================
        // This file's own header used to end "Building screen-login/
        // screen-character is real, separate work for whoever next holds the
        // editor lock", and until that work landed every event below
        // (ScreenChanged/RosterChanged/AuthError) had exactly zero subscribers:
        // the flow ran, set Screen = Login, and nothing drew anything.
        UI.Shell.ShellUi _shell;

        /// <summary>Probe-readable (Editor/Probes/HeroScreenProbe.cs): the live
        /// shell, or null when its UXML assets could not be resolved (a player
        /// build — see ShellUi.Create).</summary>
        public UI.Shell.ShellUi ShellUi => _shell;

        void EnsureShell()
        {
            if (_shell != null) return;
            try { _shell = UI.Shell.ShellUi.Create(this); }
            catch (Exception e) { Debug.LogException(e); _shell = null; } // a broken menu must not take the game down
        }

        void DisposeShell()
        {
            if (_shell == null) return;
            try { _shell.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
            _shell = null;
        }

        /// <summary>
        /// src/main.js has exactly ONE way into the world and it starts at the
        /// login screen. Assets/Editor/BuildPlayableScene.cs also puts a
        /// <c>PlayableSceneBoot</c> in Game.unity — "the no-login entry point",
        /// added back when no login UI existed — whose Start() calls
        /// CombatWorldAdapter.Install + GameLoop.BeginSession immediately. With
        /// both alive the shell paints over a world that is ALREADY RUNNING and
        /// "▶ ამ გმირით შესვლა" has nothing left to do, which is precisely how
        /// the personage screen stayed unreachable even once it existed.
        ///
        /// Boot owns the flow (this file's header), so it stands the other entry
        /// point down — but only when the shell actually got built, and never
        /// when <c>-nologin</c> asks for the old behaviour. Disabling the
        /// component is what suppresses its Start(); an explicit
        /// <c>sceneBoot.Run()</c> (Editor/Probes/UiWireProbe.cs, PlayableProbe)
        /// still works exactly as before, so no probe changes meaning.
        ///
        /// If PlayableSceneBoot should own this test itself, this is the one
        /// line to move — it lives here because Boot.cs is the file that knows
        /// whether a login screen exists this session.
        /// </summary>
        void StandDownNoLoginEntry()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), NoLoginArg) >= 0) return;
            // FindAnyObjectByType, not FindFirstObjectByType — the latter is
            // obsolete in Unity 6 (CS0618) because it depends on instance-ID
            // ordering, and there is only ever one of these in the scene.
            var noLogin = FindAnyObjectByType<PlayableSceneBoot>();
            if (noLogin == null || !noLogin.enabled) return;
            noLogin.enabled = false; // Awake runs before any Start(), so its Start() never fires
            Debug.Log("BOOT shell=1 noLoginEntry=disabled (login -> personages -> ▶ owns startup)");
        }

        // =========================================================================
        // ---- boot sequence — src/main.js:838-856 (the trailing IIFE) ----------
        // =========================================================================
        async void BootSequence()
        {
            if (TokenStore.GetToken() != null)
            {
                bool ok = false;
                await CallbackAsTask<ApiResult<MeResponse>>(cb => NetClient.Me(cb), r => { ok = r.Ok; if (r.Ok) SetUser(r.Data.user); });
                if (ok)
                {
                    if (AutostartWanted() && ActiveHero() != null)
                    {
                        Debug.Log($"autostart: straight in on {ActiveHero().Name}");
                        await StartFlightAsync();
                        return;
                    }
                    OpenCharacter();
                    return;
                }
                TokenStore.ClearToken(); // stale/revoked — ask again, main.js:852
            }
            SetScreen(BootScreen.Login);
        }

        /// <summary>Stands in for `?auto=1` (main.js:825-828) — there is no URL
        /// in a compiled build, so this reads a `-autostart` command-line flag
        /// instead. Same contract: ONLY skips the screens on THIS launch, never
        /// remembered (main.js:821-824's own comment about why it must not be).</summary>
        public static bool AutostartWanted()
            => Array.IndexOf(Environment.GetCommandLineArgs(), AutostartArg) >= 0;

        // =========================================================================
        // ---- auth — src/main.js:208-251, delegates to the verified NetClient ---
        // =========================================================================
        public void SetAuthMode(string mode) { AuthMode = mode == "register" ? "register" : "login"; }

        public async void SubmitAuth(string username, string password, string password2, bool remember)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            { AuthError?.Invoke("შეავსე ორივე ველი"); return; }
            if (AuthMode == "register" && password != password2)
            { AuthError?.Invoke("პაროლები არ ემთხვევა"); return; }

            bool ok = false;
            ApiResult<AuthResponse> result = default;
            await CallbackAsTask<ApiResult<AuthResponse>>(
                cb => { if (AuthMode == "register") NetClient.Register(username, password, remember, cb); else NetClient.Login(username, password, remember, cb); },
                r => { result = r; ok = r.Ok; });

            if (!ok) { AuthError?.Invoke(result.RawError ?? "შეცდომა — სცადე თავიდან"); return; }
            TokenStore.LastUser = result.Data.user?.username ?? username;
            SetUser(result.Data.user);
            OpenCharacter(); // main.js:246 — WoW-style: character select comes right after login
        }

        void SetUser(PublicUser user)
        {
            CurrentUser = user;
            Roster = ValidHeroes(user?.characters);
            ActiveIndex = Roster.Count > 0 ? Mathf.Clamp(user?.activeChar ?? 0, 0, Roster.Count - 1) : -1;
            if (Roster.Count == 0 && user != null)
            {
                // profile has no heroes (older backend / fresh account) — fall
                // back to the local mirror, main.js:189-196.
                var local = LoadLocalRoster(user.username);
                Roster = ValidHeroes(local.list);
                ActiveIndex = Roster.Count > 0 ? Mathf.Clamp(local.active, 0, Roster.Count - 1) : -1;
            }
            if (user != null) Hero.Open(TokenStore.GetToken(), user.username); // main.js:203 heroes.open
            RosterChanged?.Invoke();
        }

        /// <summary>main.js:148-170 `validHeroes` — race migration, hid minting
        /// for pre-socket heroes, body/plane fallback for older rosters.</summary>
        List<HeroRecord> ValidHeroes(List<CharacterDef> list)
        {
            var result = new List<HeroRecord>();
            if (list == null) return result;
            foreach (var c in list)
            {
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                string race = RaceMoved.TryGetValue(c.race ?? "", out var moved) ? moved : c.race;
                if (CharacterRoster.RaceById(race) == null) continue;
                var raceDef = CharacterRoster.RaceById(race);
                string model = CharacterRoster.PersonageById(c.model) != null ? c.model : raceDef.DefModel;
                string plane = c.plane; // no per-race plane allow-list ported yet (see class header) — kept as-is
                result.Add(new HeroRecord
                {
                    Hid = string.IsNullOrEmpty(c.hid) ? NewHid() : c.hid.Substring(0, Mathf.Min(32, c.hid.Length)),
                    Name = c.name, Race = race, Model = model,
                    Plane = string.IsNullOrEmpty(plane) ? StarterPlaneFor(race) : plane,
                });
                if (result.Count >= MaxHeroes) break;
            }
            return result;
        }

        static string NewHid() => "h" + DateTime.UtcNow.Ticks.ToString("x") + UnityEngine.Random.Range(0, int.MaxValue).ToString("x");

        public HeroRecord ActiveHero() => ActiveIndex >= 0 && ActiveIndex < Roster.Count ? Roster[ActiveIndex] : null;

        // =========================================================================
        // ---- pilot badge — main.js:132-139 `updateBadge` -----------------------
        // =========================================================================
        // Pure string computation; UI/Shell/ShellView.RefreshBadge is what writes
        // it to #pb-name/#pb-sub (same split as every other view in this port).
        // It honours the null rule below exactly. Fired, in
        // the JS, from the exact two call sites RosterChanged ALREADY fires from
        // (the end of SetUser and the end of SaveRoster below) — no new event was
        // needed, only the two formatted strings themselves, which is the genuine
        // gap: main.js:133 `if (!currentUser) return;` is why both getters answer
        // null instead of an empty string when there is nothing to show — the JS
        // leaves whatever the badge last said untouched rather than blanking it,
        // and a future view should do the same (only write the label when non-null).
        /// <summary>main.js:135 `pb-name`.</summary>
        public string BadgeName => CurrentUser != null ? CurrentUser.username.ToUpperInvariant() : null;

        /// <summary>main.js:136-138 `pb-sub`.</summary>
        public string BadgeSub
        {
            get
            {
                if (CurrentUser == null) return null;
                var hero = ActiveHero();
                if (hero == null) return $"LVL {CurrentUser.level} • {CurrentUser.stars} ★";
                var race = CharacterRoster.RaceById(hero.Race);
                var plane = Planes.PlaneById(hero.Plane);
                return $"{hero.Name} • {race.Name} • {plane.Code} • LVL {CurrentUser.level}";
            }
        }

        // =========================================================================
        // ---- plane weight class — main.js:618-627 ------------------------------
        // =========================================================================
        // main.js's `chosenPlane` module variable is not ported as separate state:
        // every read of it this port needs (StartFlightAsync, below) already
        // recomputes the same value from ActiveHero()/Planes.All[0] independently,
        // which is the stronger form of the JS's own `let chosenPlane = 'spitfire'`
        // default (both resolve to "spitfire" — Planes.All[0].Id — verified).
        /// <summary>main.js:622-625 `planeAllowed`. Reuses StarterPlaneFor's own
        /// race-&gt;role map rather than porting src/characters.js's per-race
        /// `planes` id list as separate data: every id in RACES[].planes carries
        /// exactly the role its race is assigned (checked by direct comparison
        /// against src/planes.js's own `role` field — all 20 veggie ids are DPS,
        /// all 15 toys ids are TANK), so the role tag already on PlaneDef IS that
        /// list, per src/characters.js's own comment ("TANK and DPS now live
        /// where they decide something, on the PLANES").</summary>
        public bool PlaneAllowed(string planeId)
        {
            var hero = ActiveHero();
            if (hero == null) return true; // main.js:624 `!hero ||`
            var role = RaceRole.TryGetValue(hero.Race, out var r) ? r : PlaneRole.DPS;
            return Planes.PlaneRoleOf(planeId) == role;
        }

        /// <summary>main.js:626-627 `firstAllowedPlane`.</summary>
        public string FirstAllowedPlane()
        {
            foreach (var p in Planes.All) if (PlaneAllowed(p.Id)) return p.Id;
            return Planes.All[0].Id;
        }

        // =========================================================================
        // ---- spawn table — main.js:636-657 `selectedCity` / `SPAWN_CITIES` -----
        // =========================================================================
        /// <summary>main.js:636 `selectedCity`. See Core/SpawnCities.cs's own
        /// header for where this table lives and why, and for the boundary of
        /// what is wired beyond this property.</summary>
        public string SelectedCityId { get; private set; } = SpawnCities.Default;

        /// <summary>main.js:692 `SPAWN_CITIES[selectedCity] ?? SPAWN_CITIES.tbilisi`.</summary>
        public SpawnPoint CurrentSpawn => SpawnCities.Resolve(SelectedCityId);

        // ---- local roster mirror — main.js:172-180 saveRoster / :126-129 charKey ----
        string CharKey(string username) => $"tf-chars-{username ?? ""}";

        void SaveRoster()
        {
            PlayerPrefs.SetString(CharKey(CurrentUser?.username), JsonConvert.SerializeObject(
                new { list = Roster.Select(h => new CharacterDef { hid = h.Hid, name = h.Name, race = h.Race, model = h.Model, plane = h.Plane }).ToList(), active = ActiveIndex }));
            PlayerPrefs.Save();
            if (CurrentUser != null)
            {
                var chars = Roster.Select(h => new CharacterDef { hid = h.Hid, name = h.Name, race = h.Race, model = h.Model, plane = h.Plane }).ToList();
                NetClient.PatchMe(null, null, null, chars, ActiveIndex, _ => { });
            }
            RosterChanged?.Invoke();
        }

        (List<CharacterDef> list, int active) LoadLocalRoster(string username)
        {
            try
            {
                string raw = PlayerPrefs.GetString(CharKey(username), "");
                if (string.IsNullOrEmpty(raw)) return (null, -1);
                var obj = JsonConvert.DeserializeObject<JObject>(raw);
                var list = obj["list"]?.ToObject<List<CharacterDef>>();
                int active = obj["active"]?.ToObject<int>() ?? -1;
                return (list, active);
            }
            catch { return (null, -1); } // corrupt local copy — start fresh, main.js:195
        }

        // =========================================================================
        // ---- character screen — src/main.js:360-609 (roster + create modes) ---
        // =========================================================================
        public void OpenCharacter()
        {
            _selectedHeroRosterIdx = ActiveIndex >= 0 ? ActiveIndex : 0;
            SetScreen(BootScreen.Character);
        }

        public void SelectHero(int i) { if (i >= 0 && i < Roster.Count) { _selectedHeroRosterIdx = i; RosterChanged?.Invoke(); } }

        public void SelectBody(string raceId, string bodyId) // main.js:371-381
        {
            SelectedRaceId = raceId;
            SelectedModelId = bodyId;
            RosterChanged?.Invoke();
        }

        /// <summary>main.js:559-592 `btn-char-create` handler, BOTH of its
        /// branches: create-mode commits a new hero, roster-mode enters the world.</summary>
        public void CreateHero(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { AuthError?.Invoke("⚠ დაარქვი გმირს სახელი!"); return; }
            var hero = new HeroRecord
            {
                Hid = NewHid(), Name = name.Trim(), Race = SelectedRaceId, Model = SelectedModelId,
                Plane = StarterPlaneFor(SelectedRaceId),
            };
            Roster.Add(hero);
            ActiveIndex = Roster.Count - 1;
            _selectedHeroRosterIdx = ActiveIndex;
            SaveRoster();
        }

        public void DeleteHero(int i)
        {
            if (i < 0 || i >= Roster.Count) return;
            Hero.Save(Roster[i].Hid, null); // main.js:596 — the hero's spot goes with it
            Roster.RemoveAt(i);
            if (ActiveIndex == i) ActiveIndex = Roster.Count > 0 ? 0 : -1;
            else if (ActiveIndex > i) ActiveIndex--;
            _selectedHeroRosterIdx = Mathf.Clamp(_selectedHeroRosterIdx, 0, Mathf.Max(0, Roster.Count - 1));
            SaveRoster();
        }

        /// <summary>Roster-mode "▶ ამ გმირით შესვლა" — main.js:584-592.</summary>
        public async void EnterWorldWithSelectedHero()
        {
            if (_selectedHeroRosterIdx < 0 || _selectedHeroRosterIdx >= Roster.Count) return;
            ActiveIndex = _selectedHeroRosterIdx;
            SaveRoster();
            await StartFlightAsync();
        }

        // =========================================================================
        // ---- entering the world — src/main.js:661-720 `startFlight` ------------
        // =========================================================================
        async System.Threading.Tasks.Task StartFlightAsync()
        {
            SetScreen(BootScreen.Game);
            var hero = ActiveHero();
            string planeId = hero?.Plane ?? Planes.All[0].Id; // main.js:618/663 chosenPlane
            if (!PlaneAllowed(planeId)) planeId = FirstAllowedPlane(); // main.js:664
            RaiseHint(Hero.StateOf(hero?.Hid) != null ? "ბრუნდები იქ, სადაც გაჩერდი…" : "იტვირთება თბილისი…");

            var plane = PlaneControllers.FirstOrDefault(p => p != null && p.PlaneId == planeId) ?? PlaneControllers.FirstOrDefault();
            if (plane == null) { Debug.LogError("Boot: no PlaneController wired for " + planeId); return; }
            // main.js:672-674 `loadPlane`/`makeGameplayPlane` — the JS BUILDS the
            // machine here, so this path must not depend on something else
            // having done it. PlaneController.Awake() calls Init() in play mode
            // and never fires in an Editor-opened scene (that file's own
            // header), which is why PlayableSceneBoot calls it too; Init is
            // documented idempotent, so doing it here makes this entry point
            // self-sufficient instead of quietly relying on the other one.
            plane.Init();

            // THE HERO'S OWN MACHINE. `FirstOrDefault(... ) ?? FirstOrDefault()`
            // above means the `??` arm fires whenever the scene has no
            // controller for `planeId` — and it has exactly ONE (p51), so every
            // hero flew the Mustang no matter what StarterPlaneFor said
            // (docs/open_items.json `starter-plane`). Rather than bake 35 flight
            // rigs into the scene, the one controller is re-skinned to the
            // hero's catalog row: model, cockpit, shell, damage record and the
            // whole flight rig, from Core/StarterPlaneSkin.cs. Done BEFORE
            // BeginSession, whose WirePlaneCombat/ResetPlane then run against
            // the new airframe (its own belly offset, its own stamina).
            // A failure keeps the baked machine and logs why, so a missing asset
            // costs the skin, not the session.
            if (plane.PlaneId != planeId) StarterPlaneSkin.Apply(plane, planeId);

            Game.ActivePlane = plane;
            Game.MainCamera = MainCamera;
            Game.Hero = Hero;
            Game.ActiveHeroId = hero?.Hid;

            // Assigns GameLoop's remaining named hooks (Terrain/Boundary/Zones,
            // CombatSystems, CrashFactory, OnToggleAim/OnTakePickup/OnGoAirport,
            // the UI Toolkit panel) — see Core/CombatWorldAdapter.cs. Idempotent:
            // safe to call again if this scene also drives Game through
            // PlayableSceneBoot (Assets/Editor/BuildPlayableScene.cs's own entry
            // point, used where no login UI/server exists yet to reach this point).
            CombatWorldAdapter.Install(Game);

            SpawnHeroBody(hero);

            // Resume: position/heading/camera/on-foot/velocity come back from
            // the hero socket, same contract as game.js:_applyResume. The body
            // (Rig) is already spawned above, so GameLoop.ApplyResume's own
            // on-foot restore runs synchronously — see that method's header.
            var resume = Hero.StateOf(hero?.Hid);
            Game.BeginSession();
            Game.ApplyResume(resume, resume?["city"]?.Value<string>(), Game.CityId);

            Hero.Provider = () => { if (Game.Started && !Game.Crashed) Game.SaveState(true); }; // main.js:717-719
            await System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>Spawns the hero's body. The retry-and-then-tell-them ladder
        /// is src/foot.js:1041-1048 `resume()`: the JS re-runs `_loadBody()` once
        /// when `rig` came back null, and if the second try also fails it says so
        /// in Georgian rather than leaving the player bodyless and silent. A
        /// literal re-run is meaningless here — the body is a wired prefab
        /// reference, so looking the SAME id up twice cannot change answer — so
        /// the second try is what actually differs between the two attempts in
        /// the JS: fall back off the saved id (a body that has been retired, or a
        /// hero saved on a build that had it) onto the race default, then onto
        /// banana. Only when all three miss does the hint fire.</summary>
        void SpawnHeroBody(HeroRecord hero)
        {
            string bodyId = hero?.Model ?? CharacterRoster.RaceById(SelectedRaceId)?.DefModel ?? "banana";
            GameObject prefab = BodyPrefabFor(bodyId);
            if (prefab == null)
            {
                string fallback = CharacterRoster.RaceById(SelectedRaceId)?.DefModel ?? "banana";
                if (fallback != bodyId && (prefab = BodyPrefabFor(fallback)) != null)
                    Debug.LogWarning($"Boot: no body prefab for '{bodyId}' — fell back to '{fallback}'");
                else if ((prefab = BodyPrefabFor("banana")) != null)
                    Debug.LogWarning($"Boot: no body prefab for '{bodyId}' — fell back to 'banana'");
            }
            if (prefab == null)
            {
                // foot.js:1047 verbatim. Georgian, because every string in this
                // game is (there is no English fallback anywhere by requirement).
                // Mode is deliberately NOT switched to OnFoot: ApplyResume's own
                // `Game.Rig != null` test then leaves the hero in the aeroplane,
                // which is the only place they can still play from.
                Debug.LogWarning("Boot: no body prefab wired for " + bodyId);
                Game.OnHint?.Invoke("⚠ გმირის მოდელი ვერ ჩაიტვირთა — დაბრუნდი თვითმფრინავში");
                return;
            }
            var instance = Instantiate(prefab);
            Vector3 spawnPos = Game.ActivePlane != null ? Game.ActivePlane.transform.position + Game.ActivePlane.transform.right * 4f : Vector3.zero;
            Game.SpawnBody(instance, bodyId, spawnPos, 0f);
        }

        GameObject BodyPrefabFor(string id)
        {
            int i = BodyIds.IndexOf(id);
            var wired = i >= 0 && i < BodyPrefabs.Count ? BodyPrefabs[i] : null;
            if (wired != null) return wired;
            // Assets/Editor/BuildPlayableScene.cs wires exactly ONE body into
            // this component (`BodyIds = { "banana" }`), so a hero saved on
            // eggplant/toad/elephant used to silently fall back to banana and
            // the create screen's other three tiles had no preview at all.
            // The roster is 4 and all four FBXs are on disk; the id is checked
            // against CharacterRoster (PersonageById falls back to Personages[0]
            // on a miss, so comparing the answer's Id rejects anything that is
            // not one of the four) rather than pasted into a path.
            if (!string.IsNullOrEmpty(id) && CharacterRoster.PersonageById(id).Id == id)
                return ArtAssets.Load<GameObject>($"Assets/Resources/Art/Characters/{id}.fbx");
            return null;
        }

        /// <summary>A FRESH instance of one roster body, or null if that body
        /// has no asset. This is the Unity shape of main.js:263-272's
        /// <c>makeCharacter(raceId)</c> — Core/CharacterPreviewRig.cs takes it
        /// as its <c>spawnBody</c> resolver, which its own header left as "a
        /// hook for whoever builds it". Public because UI/Shell/ShellUi.cs is
        /// the thing that builds the rig.</summary>
        public GameObject InstantiateBody(string bodyId)
        {
            var prefab = BodyPrefabFor(bodyId);
            return prefab != null ? Instantiate(prefab) : null;
        }

        // =========================================================================
        // ---- leaving / logout — src/main.js:760-818 -----------------------------
        // =========================================================================
        public void LeaveGame() // main.js:762-777 leaveGame()
        {
            try { if (Game != null && Game.Started) Game.SaveState(true); }
            catch (Exception e) { Debug.LogException(e); }
            Hero.Flush();
            Hero.Provider = null;
        }

        public void BackToCharacterSelect() // main.js:779-782 btn-back
        {
            LeaveGame();
            OpenCharacter();
        }

        /// <summary>main.js:796-815 `doLogout`. Ends the SESSION only — the
        /// hero, its spawn place, its kit and the roster all survive, exactly
        /// per the JS's own comment: "dont broke login with my personage auto
        /// login".</summary>
        public void Logout()
        {
            if (Game != null && Game.Started) LeaveGame();
            NetClient.Logout(_ => { });
            Hero.Close();
            if (!string.IsNullOrEmpty(CurrentUser?.username)) TokenStore.LastUser = CurrentUser.username;
            TokenStore.ClearToken(); // the ONE thing logout owns — main.js:807
            CurrentUser = null;
            Roster = new List<HeroRecord>();
            ActiveIndex = -1;
            SetAuthMode("login");
            SetScreen(BootScreen.Login);
        }

        // ---- small async/callback bridge (NetClient is callback-style; Boot's
        // flow reads better linearly) --------------------------------------------
        static System.Threading.Tasks.Task CallbackAsTask<T>(Action<Action<T>> call, Action<T> onResult)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            call(r => { onResult(r); tcs.TrySetResult(true); });
            return tcs.Task;
        }
    }
}
