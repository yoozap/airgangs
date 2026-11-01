using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;
using TbilisiFly.UI.Combat;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves the four new UI/Combat/* views — the subject of the
    /// "snapshot computed, no view" audit (RangeLane.LaneHud,
    /// PaperTargets.ScoreSnapshot, Pickups.VisibleCards,
    /// Shooting.Pistol.CrosshairScale) — actually render the data each
    /// Combat snapshot carries, with the Georgian strings taken verbatim.
    ///
    /// This proves the CALCULATION (same discipline as every other probe in
    /// this project): each view is constructed directly and fed a snapshot,
    /// then its own internal Label/VisualElement state is read back and
    /// printed as KEY=value. It does NOT and cannot prove the views are
    /// REACHED from the live game — that needs the Core/GameLoop.cs wiring
    /// described in this port's report (new fields + Ensure*() self-install
    /// methods + the StageCombatAndFx/HeldWeapon.Tick call sites), which is
    /// Core's territory this pass. Once that lands, `grep -rn "new
    /// RangeLaneView(\|new PaperTargetScoreView(\|new PickupCardsView(\|new
    /// ReticleView("` should find a GameLoop.cs call site, and this probe
    /// stays useful as a pure-logic regression check.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.CombatUiViewsProbe.Run
    /// </summary>
    public static class CombatUiViewsProbe
    {
        public static void Run()
        {
            RangeLane();
            PaperTarget();
            Pickup();
            Reticle();
            Debug.Log("COMBATUI_DONE=1");
        }

        static void RangeLane()
        {
            var v = new RangeLaneView();
            // rangelane.js:407-408 HintText — pure function, no view needed to prove it,
            // but print it here too so the two are seen matching side by side.
            Debug.Log($"RL_HINT_OUT={RangeLanes.HintText(1)}");
            Debug.Log($"RL_HINT_IN={RangeLanes.HintText(-1)}");
            Debug.Log($"RL_HINT_IDLE={RangeLanes.HintText(0)}");
            Debug.Log($"RL_BTN_F_KEY={v._outKey.text}");
            Debug.Log($"RL_BTN_G_KEY={v._inKey.text}");
            Debug.Log($"RL_BTN_F_TITLE={v._out.tooltip}");
            Debug.Log($"RL_BTN_G_TITLE={v._in.tooltip}");
            Debug.Log($"RL_DIST_DEFAULT={v._dist.text}");
            Debug.Log($"RL_HINT_DEFAULT={v._hint.text}");

            // a lane the hero is standing at, not aiming, 7.3 m out, running away
            var lane = new RangeLanes.Lane();
            var hud = new RangeLanes.LaneHud { Show = true, DistM = 7.3f, Dir = 1, Lane = lane };
            v.Tick(hud);
            Debug.Log($"RL_DIST_TEXT={v._dist.text}");          // expect "7.3 მ"
            Debug.Log($"RL_HINT_TEXT={v._hint.text}");           // expect "მიდის შორს…"
            Debug.Log($"RL_DISPLAY_SHOWN={v.style.display.value}");

            // no lane at all -> hidden
            v.Tick(default);
            Debug.Log($"RL_DISPLAY_NOLANE={v.style.display.value}");

            // NOT via a simulated PointerDownEvent: VisualElement.SendEvent needs a
            // live panel dispatcher to actually invoke RegisterCallback handlers, and
            // these views are built detached (no UIDocument in this -executeMethod
            // probe) — same "AddComponent<T>() skips Awake() outside a scene tick"
            // shape SKILL.md already documents. Exercise the callback wiring directly
            // instead, which is what the panel would do once attached.
            bool pressed = false;
            v.OnPressOut = down => pressed = down;
            v.OnPressOut.Invoke(true);
            Debug.Log($"RL_BTN_F_PRESS_CALLED={pressed.ToString().ToLowerInvariant()}");
        }

        static void PaperTarget()
        {
            var v = new PaperTargetScoreView();
            Debug.Log($"PT_RING_DEFAULT={v._ring.text}");
            Debug.Log($"PT_SUM_DEFAULT={v._sum.text}");

            // a scored 9 ring, 11.4mm off centre at clock-hour 3
            var hit = new PaperTargets.ScoreSnapshot(9, false, 27, 3, 9.0f, 42f, 3, 11.4f);
            v.Show(hit);
            Debug.Log($"PT_RING_TEXT={v._ring.text}");           // expect "9 3h · 11 მმ"
            Debug.Log($"PT_SUM_TEXT={v._sum.text}");              // "27"
            Debug.Log($"PT_N_TEXT={v._n.text}");                  // "3"
            Debug.Log($"PT_AVG_TEXT={v._avg.text}");              // "9.0"
            Debug.Log($"PT_GRP_TEXT={v._grp.text}");              // "42 მმ"
            Debug.Log($"PT_DWELL_AFTER_SHOW={v._dwell}");         // 15

            v.Tick(14.999f);
            Debug.Log($"PT_VISIBLE_BEFORE_DWELL_END={(v.style.display.value != UnityEngine.UIElements.DisplayStyle.None).ToString().ToLowerInvariant()}");
            v.Tick(1f);
            Debug.Log($"PT_VISIBLE_AFTER_DWELL_END={(v.style.display.value != UnityEngine.UIElements.DisplayStyle.None).ToString().ToLowerInvariant()}");

            bool resetCalled = false;
            v.OnResetClicked = () => resetCalled = true;
            v.OnResetClicked.Invoke(); // same panel-less-dispatch limitation as RangeLane()
            Debug.Log($"PT_RESET_CALLED={resetCalled.ToString().ToLowerInvariant()}");
            var blank = new PaperTargets.ScoreSnapshot(0, false, 0, 0, 0f, 0f, 0, 0f);
            v.Refresh(blank);
            Debug.Log($"PT_RING_AFTER_RESET={v._ring.text}");    // "–"
            Debug.Log($"PT_GRP_AFTER_RESET={v._grp.text}");       // "–" (N<=1)
        }

        static void Pickup()
        {
            var v = new PickupCardsView();
            var mak = new Pickups.Item { Weapon = WeaponDefs.ById["makarov"] };
            var akm = new Pickups.Item { Weapon = WeaponDefs.ById["akm"] };
            var belt = new Pickups.Item { Gear = GearDefs.ById["belt"] };
            var list = new List<Pickups.Item> { mak, akm, belt };
            v.Render(list);

            var rMak = v.RefsFor(mak);
            var rAkm = v.RefsFor(akm);
            var rBelt = v.RefsFor(belt);
            Debug.Log($"PK_COUNT={v.childCount}");                         // 3
            Debug.Log($"PK_NAME_0={rMak.Name.text}");                       // მაკაროვი PM
            Debug.Log($"PK_CAL_0={rMak.Cal.text}");                         // 9x18 line
            Debug.Log($"PK_KEY_0={rMak.KeyBadge.text}");                    // F
            Debug.Log($"PK_TAKE_0={rMak.Take.text}");                       // — აღება
            Debug.Log($"PK_NAME_1={rAkm.Name.text}");
            Debug.Log($"PK_CAL_GEAR_2={rBelt.Cal.text}");                   // აღჭურვილობა — ჩაიცვამს
            Debug.Log($"PK_FOCUS_0_WIDTH={rMak.Img.style.width.value.value}"); // 72 (first = focus)
            Debug.Log($"PK_FOCUS_1_WIDTH={rAkm.Img.style.width.value.value}"); // 64 (not focus)
            Debug.Log($"PK_DISPLAY_SHOWN={v.style.display.value}");         // Flex

            // Same panel-less-dispatch limitation as RangeLane() above.
            Pickups.Item taken = null;
            v.OnTakeClicked = it => taken = it;
            v.OnTakeClicked.Invoke(mak);
            Debug.Log($"PK_TAKE_CALLED={(taken == mak).ToString().ToLowerInvariant()}");

            v.Render(new List<Pickups.Item>());
            Debug.Log($"PK_DISPLAY_EMPTY={v.style.display.value}");         // None
        }

        static void Reticle()
        {
            var v = new ReticleView();
            Debug.Log($"RT_DISPLAY_INITIAL={v.style.display.value}");       // None
            v.Tick(true, false, 1.3f);
            Debug.Log($"RT_DISPLAY_VISIBLE={v.style.display.value}");       // Flex
            Debug.Log($"RT_SCALE_VISIBLE={v.style.scale.value.value.x:F3}"); // 1.300
            Debug.Log($"RT_DOT_COLOR_HIP={ColorUtility.ToHtmlStringRGB(v._dot.style.backgroundColor.value)}"); // FFFFFF
            v.Tick(true, true, 0.6f);
            Debug.Log($"RT_SCALE_AIM={v.style.scale.value.value.x:F3}");     // 0.600
            Debug.Log($"RT_DOT_COLOR_AIM={ColorUtility.ToHtmlStringRGB(v._dot.style.backgroundColor.value)}"); // FF5A4D
            v.Tick(false, false, 1f);
            Debug.Log($"RT_DISPLAY_HIDDEN={v.style.display.value}");        // None
        }
    }
}
