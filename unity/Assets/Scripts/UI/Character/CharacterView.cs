using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.Core;
using TbilisiFly.Character;
using TbilisiFly.Flight;

namespace TbilisiFly.UI.Character
{
    /// <summary>src/main.js:363 `charMode` — the create/roster sub-state of
    /// the character screen. Boot.cs's own header is explicit that it owns
    /// FLOW/DATA only and NOT this: "every screen-specific action main.js
    /// wires to a DOM button is exposed here as a public method instead ...
    /// Building screen-login/screen-character is real, separate work for
    /// whoever next holds the editor lock." This enum is that missing half,
    /// owned by the view, same as the JS's own module-level `charMode`.</summary>
    public enum CharUiMode { Roster, Create }

    /// <summary>
    /// Port of src/main.js's WoW-style character create/roster screen DOM:
    ///   renderRaceColumn   main.js:390-412
    ///   renderCharList     main.js:428-458
    ///   renderCharInfo     main.js:460-504
    ///   applyCharLayout    main.js:418-426
    ///   selectBody/selectHero/enterCreateMode/enterRosterMode/openCharacter
    ///                      main.js:371-381, 506-557
    ///   the btn-char-create / btn-char-delete / char-name handlers
    ///                      main.js:559-615
    ///
    /// Everything else about this screen (the auth round-trip, the hero
    /// roster, which race/body is selected, actually creating/deleting a
    /// hero, entering the world) is already ported and verified in
    /// Core/Boot.cs — this view only builds the DOM-equivalent VisualElement
    /// tree and calls Boot's public methods, the same split SettingsView.cs
    /// keeps from the systems it toggles (see that file's own header).
    ///
    /// HARD CONSTRAINTS (user decisions, enforced before — see
    /// .claude/skills/unity/SKILL.md and src/characters.js's own comments):
    ///   - Every race/role/body/description string is Georgian, read
    ///     straight off Character/CharacterRoster.cs (itself ported
    ///     byte-for-byte from src/characters.js). Neither English role tag
    ///     is ever built by this file — those live only on PlaneDef.Role.
    ///   - The roster is EXACTLY the 4 bodies CharacterRoster.BodiesOf
    ///     returns per race (banana/eggplant/toad, elephant) — never the
    ///     wider PERSONAGES list.
    ///   - NO plane picking anywhere on this screen. The starter plane is
    ///     named (read-only, via <see cref="StarterPlaneFor"/>) but never
    ///     choosable — the old ◄ ► cycler and preview plane stay cut.
    ///
    /// WIRED, as of 2026-10-30. This view and Core/CharacterPreviewRig.cs were
    /// both PROBE_ONLY (tools/_test_no_orphans.py): finished, verified, and
    /// constructed by nothing but Editor/Probes/*. UI/Shell/ShellUi.cs now
    /// builds both — a real UIDocument, a real preview rig, a real
    /// SettingsView on SettingsRequested — and Core/Boot.cs owns that shell's
    /// lifetime. It is still a plain VisualElement, not a MonoBehaviour: it
    /// does not drive its own loop, so <see cref="Tick"/> must be called once
    /// a frame by its owner (ShellUi.Tick, from Boot.Update) — the same
    /// externally-driven pattern as HeroLink.cs. Pointer events from the
    /// preview Image are wired internally (see the constructor).
    /// </summary>
    public class CharacterView : VisualElement
    {
        // ---- byte-for-byte strings, src/main.js ---------------------------------
        const string SubRoster = "შენი გმირები — აირჩიე ან შექმენი ახალი";          // main.js:532/542/578
        const string SubCreateFirst = "შექმენი შენი პირველი გმირი — აირჩიე მხარე და სხეული"; // main.js:549
        const string SubCreateNew = "ახალი გმირი — აირჩიე მხარე და სხეული, მერე დაარქვი სახელი"; // main.js:517
        const string NameRequiredWarning = "⚠ დაარქვი გმირს სახელი!"; // main.js:564 / Boot.CreateHero
        // internal (not private): CharCreateProbe.cs (Assembly-CSharp-Editor)
        // reads these directly rather than re-hardcoding a duplicate that
        // could drift — AssemblyInfo.cs already grants that assembly access
        // via InternalsVisibleTo, same pattern as the rest of this port.
        internal const string BtnCreateText = "შექმნა";                 // main.js:498
        internal const string BtnEnterText = "▶ ამ გმირით შესვლა";      // main.js:501
        internal const string NewHeroRowText = "+ ახალი გმირი";          // main.js:454
        const string ActiveTagText = "ACTIVE";                  // main.js:446 — Latin tech-chrome, like "RACE //"; not a role string.
        const string DefaultCity = "თბილისი";                  // main.js:39

        // src/main.js:261 RACE_STAT_LABELS.
        internal static readonly string[] RaceStatLabels = { "შეტევა", "დაცვა", "განკურნება", "სისწრაფე" };

        // src/main.js:32 MODE_LABEL.
        static string ModeLabel(bool onFoot, bool airborne)
            => onFoot ? "🚶 ფეხით" : airborne ? "✈ ჰაერში" : "🛬 გაჩერებული";

        // This view needs the starter plane's NAME for display (cb-line/ci-code/
        // ci-role). It used to carry its own copy of Boot's race->role map and
        // lookup, with a note asking whoever held the editor lock to make
        // Boot.StarterPlaneFor public and delete the copy. It IS public
        // (Core/Boot.cs:118), so the copy is gone and there is one lookup again.
        // Display only: Boot.CreateHero still computes the saved plane itself,
        // so what this screen shows can never decide what gets written.
        static string StarterPlaneFor(string raceId) => Boot.StarterPlaneFor(raceId);

        readonly Boot _boot;
        readonly CharacterPreviewRig _preview; // may be null (see class header) — every call below is guarded.

        public CharUiMode Mode { get; private set; } = CharUiMode.Roster;
        int _selectedHero; // roster-mode cursor, main.js:364 `selectedHero` — Boot's own cursor is private.

        // ---- queried elements ---------------------------------------------------
        readonly Button _btnBack, _btnSettings, _btnCreate, _btnDelete;
        readonly Label _charSub;
        readonly ScrollView _raceList, _raceList2, _hangarInfo;
        readonly VisualElement _createBar, _ciStats, _charNameFld;
        readonly Label _ciCode, _ciName, _ciRole, _ciSpot, _ciDesc, _cbLine;
        readonly Image _previewImg;
        readonly TextField _nameField;
        readonly Label _namePlaceholder;

        bool _dragging;
        float _lastPointerX;

        /// <summary>Raised when "⚙ პარამეტრები" is pressed — settings live on
        /// this screen's head (project memory, 2026-08) but the SettingsView
        /// instance belongs to whoever attaches the UI root, not this
        /// file.</summary>
        public event Action SettingsRequested;

        public CharacterView(VisualTreeAsset uxml, Boot boot, CharacterPreviewRig preview = null)
        {
            _boot = boot;
            _preview = preview;
            uxml.CloneTree(this);
            Common.UiBootstrap.FillPanel(this); // .hangar is inset:0 — see that method's header

            _btnBack = this.Q<Button>("btn-char-back");
            _btnSettings = this.Q<Button>("btn-char-settings");
            _btnCreate = this.Q<Button>("btn-char-create");
            _btnDelete = this.Q<Button>("btn-char-delete");
            _charSub = this.Q<Label>("char-sub");
            _raceList = this.Q<ScrollView>("race-list");
            _raceList2 = this.Q<ScrollView>("race-list-2");
            _hangarInfo = this.Q<ScrollView>("hangar-info");
            _createBar = this.Q("create-bar");
            _ciStats = this.Q("ci-stats");
            _charNameFld = this.Q("char-name-fld");
            _ciCode = this.Q<Label>("ci-code");
            _ciName = this.Q<Label>("ci-name");
            _ciRole = this.Q<Label>("ci-role");
            _ciSpot = this.Q<Label>("ci-spot");
            _ciDesc = this.Q<Label>("ci-desc");
            _cbLine = this.Q<Label>("cb-line");
            _previewImg = this.Q<Image>("char-preview");
            _nameField = this.Q<TextField>("char-name");
            _namePlaceholder = this.Q<Label>("char-name-placeholder");

            _nameField.maxLength = 16; // belt-and-braces alongside the UXML max-length attribute.
            _namePlaceholder.pickingMode = PickingMode.Ignore; // set in code, not UXML — established project convention (LabelPool.cs/HudView.cs/WeaponBarView.cs etc.)

            Bind();
            style.display = DisplayStyle.None; // main.js's #screen-character starts inactive.
        }

        Action _onRosterChanged;

        /// <summary>
        /// Takes this view back off Boot's events. REQUIRED when the shell is
        /// torn down, and found the hard way: Boot keeps living across the whole
        /// session, so a disposed view stayed SUBSCRIBED and kept handling
        /// ScreenChanged after its own preview camera and UIDocument had been
        /// destroyed. Coming back out of the world (main.js:779-782 btn-back)
        /// then threw MissingReferenceException ("the object of type
        /// 'UnityEngine.Camera' has been destroyed but you are still trying to
        /// access it") from the OLD view, while the new one worked fine — which
        /// reads like a camera bug and is a subscription-lifetime bug.
        /// Editor/Probes/HeroScreenProbe.cs is what caught it.
        /// </summary>
        public void Unbind()
        {
            _boot.ScreenChanged -= OnScreenChanged;
            if (_onRosterChanged != null) _boot.RosterChanged -= _onRosterChanged;
            _boot.AuthError -= OnAuthError;
            _onRosterChanged = null;
        }

        void Bind()
        {
            _boot.ScreenChanged += OnScreenChanged;
            // src/main.js:23-29 heroes.onStates — re-render whatever is showing,
            // without touching charMode. Boot.cs reuses this ONE event for both
            // that role and every local roster mutation (SelectBody/Create/
            // Delete all end in SaveRoster -> RosterChanged too); the mode-
            // changing handlers below always finish with their own explicit
            // Refresh(), which is the authoritative render for that click no
            // matter how many times this guard already fired mid-call.
            // held in a field, not written inline, so Unbind() below can take it
            // off again — see that method for why that matters.
            _onRosterChanged = () => { if (_boot.Screen == BootScreen.Character) Refresh(); };
            _boot.RosterChanged += _onRosterChanged;
            _boot.AuthError += OnAuthError;

            _btnBack.clicked += EnterRosterMode;             // main.js:615
            _btnSettings.clicked += () => SettingsRequested?.Invoke();
            _btnCreate.clicked += OnCreateOrEnterClicked;    // main.js:559-592
            _btnDelete.clicked += OnDeleteClicked;           // main.js:594-609

            _nameField.RegisterValueChangedCallback(_ => UpdateNamePlaceholder());
            _nameField.RegisterCallback<KeyDownEvent>(evt => // main.js:611-613
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    OnCreateOrEnterClicked();
            });
            UpdateNamePlaceholder();

            // main.js:323-350 wireCharDrag — raw pointer delta-X into the rig.
            _previewImg.RegisterCallback<PointerDownEvent>(evt =>
            {
                _dragging = true;
                _lastPointerX = evt.position.x;
                _previewImg.CapturePointer(evt.pointerId);
            });
            _previewImg.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!_dragging) return;
                float dx = evt.position.x - _lastPointerX;
                _lastPointerX = evt.position.x;
                _preview?.Drag(dx);
            });
            _previewImg.RegisterCallback<PointerUpEvent>(evt =>
            {
                _dragging = false;
                _previewImg.ReleasePointer(evt.pointerId);
            });
            // main.js's own `window.addEventListener('resize', sizeCharPreview)`.
            _previewImg.RegisterCallback<GeometryChangedEvent>(_ => ResizePreview());
        }

        /// <summary>Call once a frame while this view may be visible — this is
        /// a plain class, not a MonoBehaviour (see class header); it does not
        /// drive its own per-frame loop. main.js:313-316's rAF tick.</summary>
        public void Tick(float dt)
        {
            _preview?.Tick(dt);
        }

        // =========================================================================
        // ---- screen lifecycle — main.js:538-557 openCharacter ------------------
        // =========================================================================
        void OnScreenChanged(BootScreen s)
        {
            if (s != BootScreen.Character)
            {
                style.display = DisplayStyle.None;
                _preview?.Stop(); // main.js:590,798 stopCharPreview
                return;
            }

            bool hasRoster = _boot.Roster.Count > 0;
            Mode = hasRoster ? CharUiMode.Roster : CharUiMode.Create;
            _selectedHero = _boot.ActiveIndex >= 0 ? _boot.ActiveIndex : 0;
            _charSub.text = hasRoster ? SubRoster : SubCreateFirst;
            if (!hasRoster) _nameField.SetValueWithoutNotify("");

            style.display = DisplayStyle.Flex;
            string initialBody = hasRoster ? _boot.Roster[_selectedHero].Model : _boot.SelectedModelId;
            int w = Mathf.Max(1, (int)_previewImg.resolvedStyle.width);
            int h = Mathf.Max(1, (int)_previewImg.resolvedStyle.height);
            _preview?.Start(w, h, initialBody); // main.js:554 startCharPreview
            // "canvas has real dimensions only after the screen is visible" —
            // Boot.cs's own comment, main.js:555. `schedule` needs a live
            // panel; this view may be asked to show before it is ever
            // attached to one (nothing wires it into a scene yet — see class
            // header), so fall back to an immediate (possibly zero-sized,
            // self-correcting via the GeometryChangedEvent handler) resize.
            if (panel != null) schedule.Execute(ResizePreview);
            else ResizePreview();
            Refresh();
        }

        void ResizePreview()
        {
            if (_preview == null) return;
            int w = Mathf.Max(1, (int)_previewImg.resolvedStyle.width);
            int h = Mathf.Max(1, (int)_previewImg.resolvedStyle.height);
            _preview.Resize(w, h);
            _previewImg.image = _preview.Target;
        }

        void UpdateNamePlaceholder()
            => _namePlaceholder.style.display = string.IsNullOrEmpty(_nameField.value) ? DisplayStyle.Flex : DisplayStyle.None;

        void OnAuthError(string msg)
        {
            // main.js:562-565 — the only AuthError that fires while this
            // screen is showing is the empty-name guard, reusing char-sub as
            // the inline warning exactly like the JS does.
            if (_boot.Screen != BootScreen.Character) return;
            _charSub.text = msg;
            if (Mode == CharUiMode.Create) _nameField.Focus();
        }

        // =========================================================================
        // ---- mode-changing actions ---------------------------------------------
        // =========================================================================

        /// <summary>main.js:515-525 enterCreateMode — the "+ ახალი გმირი" row.</summary>
        void EnterCreateMode()
        {
            Mode = CharUiMode.Create;
            _charSub.text = SubCreateNew;
            _nameField.SetValueWithoutNotify("");
            var race = CharacterRoster.RaceById(_boot.SelectedRaceId);
            _boot.SelectBody(race.Id, race.DefModel); // resets selectedModel to the race's own default
            Refresh();
        }

        /// <summary>main.js:528-536 enterRosterMode — the head's ◄ უკან.</summary>
        void EnterRosterMode()
        {
            if (_boot.Roster.Count == 0) return;
            Mode = CharUiMode.Roster;
            _selectedHero = _boot.ActiveIndex >= 0 ? _boot.ActiveIndex : 0;
            _charSub.text = SubRoster;
            Refresh();
        }

        void SelectHeroClicked(int i)
        {
            if (i < 0 || i >= _boot.Roster.Count) return;
            _selectedHero = i;
            _boot.SelectHero(i); // main.js:506-513 selectHero — keeps Boot's own cursor in step
            Refresh();
        }

        void SelectBodyClicked(string raceId, string bodyId)
        {
            _boot.SelectBody(raceId, bodyId); // main.js:371-381 selectBody
            Refresh();
        }

        /// <summary>main.js:559-592 the btn-char-create handler, both branches.</summary>
        void OnCreateOrEnterClicked()
        {
            if (Mode == CharUiMode.Create)
            {
                string name = (_nameField.value ?? "").Trim();
                int before = _boot.Roster.Count;
                _boot.CreateHero(name); // empty name -> AuthError via OnAuthError above, no mutation
                if (_boot.Roster.Count > before)
                {
                    Mode = CharUiMode.Roster;
                    _selectedHero = _boot.ActiveIndex;
                    _charSub.text = SubRoster;
                    Refresh();
                }
                return;
            }
            if (_boot.Roster.Count == 0) return;
            _boot.SelectHero(_selectedHero);
            _boot.EnterWorldWithSelectedHero(); // fires ScreenChanged(Game) -> OnScreenChanged stops the preview
        }

        /// <summary>main.js:594-609 the btn-char-delete handler.</summary>
        void OnDeleteClicked()
        {
            if (Mode != CharUiMode.Roster || _boot.Roster.Count == 0) return;
            int i = _selectedHero;
            _boot.DeleteHero(i);
            _selectedHero = Mathf.Max(0, Mathf.Min(i, _boot.Roster.Count - 1));
            if (_boot.Roster.Count == 0) EnterCreateMode();
            else Refresh();
        }

        // =========================================================================
        // ---- rendering — main.js:390-504 ---------------------------------------
        // =========================================================================

        void Refresh()
        {
            RenderCharList();
            RenderCharInfo();
            string bodyId = Mode == CharUiMode.Create
                ? _boot.SelectedModelId
                : (_boot.Roster.Count > 0 ? _boot.Roster[Mathf.Clamp(_selectedHero, 0, _boot.Roster.Count - 1)].Model : null);
            if (bodyId != null) _preview?.SetModel(bodyId); // main.js:378/511/524/535 setCharPreviewModel
        }

        /// <summary>main.js:418-426 applyCharLayout.</summary>
        void ApplyCharLayout()
        {
            bool create = Mode == CharUiMode.Create;
            _hangarInfo.EnableInClassList("hidden", create);
            _createBar.EnableInClassList("hidden", !create);
            _raceList2.EnableInClassList("hidden", !create);
            var target = create ? _createBar : (VisualElement)_hangarInfo;
            _charNameFld.RemoveFromHierarchy();
            _btnCreate.RemoveFromHierarchy();
            target.Add(_charNameFld);
            target.Add(_btnCreate);
        }

        /// <summary>main.js:428-458 renderCharList.</summary>
        void RenderCharList()
        {
            ApplyCharLayout();
            _raceList.Clear();

            if (Mode == CharUiMode.Create)
            {
                RenderRaceColumn(_raceList, CharacterRoster.Races[0]);
                RenderRaceColumn(_raceList2, CharacterRoster.Races.Length > 1 ? CharacterRoster.Races[1] : CharacterRoster.Races[0]);
                return;
            }

            for (int i = 0; i < _boot.Roster.Count; i++)
            {
                var hero = _boot.Roster[i];
                var def = CharacterRoster.RaceById(hero.Race);
                var body = CharacterRoster.PersonageById(hero.Model);
                var plane = Planes.PlaneById(hero.Plane);

                var item = new VisualElement();
                item.AddToClassList("pl-item");
                if (i == _selectedHero) item.AddToClassList("selected");

                var row1 = new VisualElement(); row1.AddToClassList("pl-row1");
                var nameRow = new VisualElement(); nameRow.AddToClassList("pl-name");
                var raceIcon = new Label(def.Icon); raceIcon.AddToClassList("race-icon");
                var heroName = new Label(hero.Name);
                nameRow.Add(raceIcon); nameRow.Add(heroName);
                var code = new Label($"{body.Icon} ✈ {plane.Code}"); code.AddToClassList("pl-code");
                row1.Add(nameRow); row1.Add(code);

                var row2 = new VisualElement(); row2.AddToClassList("pl-row2");
                var cls = new Label(def.RoleKa); cls.AddToClassList("pl-cls");
                row2.Add(cls);
                if (i == _boot.ActiveIndex)
                {
                    var tag = new Label(ActiveTagText);
                    tag.AddToClassList("pl-tag"); tag.AddToClassList("equipped");
                    row2.Add(tag);
                }
                item.Add(row1); item.Add(row2);

                var spot = HeroSpot(hero);
                if (spot.HasValue)
                {
                    var row3 = new Label($"📍 {spot.Value.place} · {spot.Value.mode}");
                    row3.AddToClassList("pl-row3");
                    item.Add(row3);
                }

                int idx = i;
                item.RegisterCallback<ClickEvent>(_ => SelectHeroClicked(idx));
                _raceList.Add(item);
            }

            if (_boot.Roster.Count < Boot.MaxHeroes)
            {
                var add = new VisualElement();
                add.AddToClassList("pl-item"); add.AddToClassList("new-hero");
                add.Add(new Label(NewHeroRowText));
                add.RegisterCallback<ClickEvent>(_ => EnterCreateMode());
                _raceList.Add(add);
            }
        }

        /// <summary>main.js:390-412 renderRaceColumn — one race down a column.</summary>
        void RenderRaceColumn(VisualElement el, CharacterRoster.Race race)
        {
            el.Clear();

            var head = new VisualElement();
            head.AddToClassList("race-head");
            if (race.Id == _boot.SelectedRaceId) head.AddToClassList("on");
            var top = new VisualElement(); top.AddToClassList("rh-top");
            var icon = new Label(race.Icon); icon.AddToClassList("rh-icon");
            var name = new Label(race.Name); name.AddToClassList("rh-name");
            var latin = new Label(race.Latin); latin.AddToClassList("rh-latin");
            top.Add(icon); top.Add(name); top.Add(latin);
            var role = new Label(race.RoleKa); role.AddToClassList("rh-role");
            var desc = new Label(race.Desc); desc.AddToClassList("rh-desc");
            head.Add(top); head.Add(role); head.Add(desc);
            head.RegisterCallback<ClickEvent>(_ => SelectBodyClicked(race.Id, race.DefModel));
            el.Add(head);

            var grid = new VisualElement(); grid.AddToClassList("body-grid");
            foreach (var id in CharacterRoster.BodiesOf(race.Id))
            {
                var body = CharacterRoster.PersonageById(id);
                var card = new VisualElement(); card.AddToClassList("body-card");
                if (race.Id == _boot.SelectedRaceId && id == _boot.SelectedModelId) card.AddToClassList("selected");
                var bi = new Label(body.Icon); bi.AddToClassList("bc-icon");
                var bn = new Label(body.Name); bn.AddToClassList("bc-name");
                card.Add(bi); card.Add(bn);
                string bodyId = id;
                card.RegisterCallback<ClickEvent>(_ => SelectBodyClicked(race.Id, bodyId));
                grid.Add(card);
            }
            el.Add(grid);
        }

        /// <summary>main.js:460-504 renderCharInfo.</summary>
        void RenderCharInfo()
        {
            HeroRecord hero = null;
            if (Mode == CharUiMode.Roster && _boot.Roster.Count > 0)
                hero = _boot.Roster[Mathf.Clamp(_selectedHero, 0, _boot.Roster.Count - 1)];

            var def = CharacterRoster.RaceById(hero != null ? hero.Race : _boot.SelectedRaceId);
            string planeId = hero != null ? hero.Plane : StarterPlaneFor(_boot.SelectedRaceId);
            var plane = Planes.PlaneById(planeId);

            _ciCode.text = $"RACE // {def.Latin} // {plane.Code}";
            _ciName.text = hero != null ? hero.Name : $"{def.Icon} {def.Name}";
            _ciRole.text = hero != null
                ? $"{def.Icon} {def.Name} — {def.RoleKa} · ✈ {plane.Name}"
                : $"{def.RoleKa} · ✈ {plane.Name}";
            _ciDesc.text = def.Desc;

            var spot = hero != null ? HeroSpot(hero) : null;
            _ciSpot.EnableInClassList("hidden", !spot.HasValue);
            if (spot.HasValue)
                _ciSpot.text = $"📍 ბოლო ადგილი: {spot.Value.place} · {spot.Value.mode}\n{AgoKa(spot.Value.at)} — აქ გამოჩნდები";

            _ciStats.Clear();
            for (int i = 0; i < def.Stats.Length && i < RaceStatLabels.Length; i++)
            {
                var row = new VisualElement();
                var line = new VisualElement(); line.AddToClassList("hs-row");
                var label = new Label("• " + RaceStatLabels[i]); label.AddToClassList("hs-label"); // CSS ::before bullet, see Character.uss header
                var val = new Label(def.Stats[i].ToString("00")); val.AddToClassList("hs-val");
                line.Add(label); line.Add(val);
                var track = new VisualElement(); track.AddToClassList("hs-track");
                var fill = new VisualElement(); fill.AddToClassList("hs-fill");
                fill.style.width = new Length(Mathf.Clamp(def.Stats[i], 0, 100), LengthUnit.Percent);
                track.Add(fill);
                row.Add(line); row.Add(track);
                _ciStats.Add(row);
            }

            if (Mode == CharUiMode.Create)
            {
                var body = CharacterRoster.PersonageById(_boot.SelectedModelId);
                var starter = Planes.PlaneById(StarterPlaneFor(_boot.SelectedRaceId));
                _cbLine.text = $"{def.Icon} {def.Name} · {body.Icon} {body.Name}\n{def.RoleKa} · ✈ {starter.Name}";
            }

            _charNameFld.EnableInClassList("hidden", Mode != CharUiMode.Create);
            _btnDelete.EnableInClassList("hidden", Mode != CharUiMode.Roster || hero == null);
            _btnBack.EnableInClassList("hidden", Mode != CharUiMode.Create || _boot.Roster.Count == 0);
            _btnCreate.text = Mode == CharUiMode.Create ? BtnCreateText : BtnEnterText;
        }

        // =========================================================================
        // ---- helpers — main.js:33-54 heroSpot/agoKa ----------------------------
        // =========================================================================

        (string place, string mode, long? at)? HeroSpot(HeroRecord hero)
        {
            var st = _boot.Hero.StateOf(hero?.Hid);
            if (!(st?["pos"] is JArray)) return null; // main.js:37 — a row with no position is a LOADOUT, not a spot
            bool onFoot = st["onFoot"]?.ToObject<bool>() ?? false;
            bool airborne = st["airborne"]?.ToObject<bool>() ?? false;
            string place = st["place"]?.ToString();
            long? at = st["at"]?.ToObject<long?>();
            return (string.IsNullOrEmpty(place) ? DefaultCity : place, ModeLabel(onFoot, airborne), at);
        }

        /// <summary>main.js:46-54 agoKa. Display text — a .5 tie here is
        /// invisible to a reader, so this uses plain Math.Round rather than
        /// JsMath.Round (see .claude/skills/unity/SKILL.md's own note).</summary>
        static string AgoKa(long? atMs)
        {
            if (!atMs.HasValue || atMs.Value == 0) return "";
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long m = Math.Max(0, (long)Math.Round((nowMs - atMs.Value) / 60000.0, MidpointRounding.AwayFromZero));
            if (m < 1) return "ახლახან";
            if (m < 60) return $"{m} წთ წინ";
            long h = (long)Math.Round(m / 60.0, MidpointRounding.AwayFromZero);
            if (h < 24) return $"{h} სთ წინ";
            return $"{(long)Math.Round(h / 24.0, MidpointRounding.AwayFromZero)} დღის წინ";
        }
    }
}
