using UnityEngine;
using TbilisiFly.Props;
using TbilisiFly.Cinema;

namespace TbilisiFly.Probes
{
    // Counterpart to tools/_probe_crane5.mjs. Run with:
    //   Unity -batchmode -quit -nographics -projectPath unity
    //        -executeMethod TbilisiFly.Probes.Crane5Probe.Run -logFile /dev/stdout
    //
    // Exercises the port unit recorded in docs/port_units/crane5.json:
    // WarehouseCrane.ScaleToFit/HookLocalInCrane (crane.js:167, 240-241) and
    // Cinema/CinemaRangeMath.CardPlacement/CrateRowBuilder.Apply (cinema.js:
    // 1836-1837,1844, 1716-1720). KEY=value lines use the IDENTICAL names the
    // .mjs script prints (booleans lower-case via ToLowerInvariant — see
    // SKILL.md's own trivia note), so the two outputs line up for a diff.
    public static class Crane5Probe
    {
        public static void Run()
        {
            // ---- WarehouseCrane.ScaleToFit ---------------------------------
            Debug.Log($"CRANE5_SCALE_REAL_SIZE={WarehouseCrane.ScaleToFit(10.0f):F4}");
            Debug.Log($"CRANE5_SCALE_CAPPED={WarehouseCrane.ScaleToFit(27.4f):F4}");

            // ---- WarehouseCrane.HookLocalInCrane ---------------------------
            Vector3 hook = new Vector3(0.3f, 7.9f, 1.65f);
            Vector3 feetCenter = new Vector3(0.1f, 0f, -1.7f);
            Vector3 hookAt = WarehouseCrane.HookLocalInCrane(hook, feetCenter, 0.0f, 0.8f);
            Debug.Log($"CRANE5_HOOKAT_X={hookAt.x:F4}");
            Debug.Log($"CRANE5_HOOKAT_Y={hookAt.y:F4}");
            Debug.Log($"CRANE5_HOOKAT_Z={hookAt.z:F4}");

            // ---- Cinema.CardPlacement ---------------------------------------
            var rangeCard = new CardSpec(3.3f, 12.4f);
            var cratesCard = CardSpec.FromAt(-0.9295f, 7.0f);
            var ownLaneCard = new CardSpec(0f, 11.0f) { X = null }; // neither at nor x set at all

            Debug.Log($"CRANE5_CARD_RANGE_OFFSET={CardPlacement.AcrossOffset(rangeCard):F4}");
            Debug.Log($"CRANE5_CARD_RANGE_OWNLANE={CardPlacement.UsesOwnLane(rangeCard).ToString().ToLowerInvariant()}");
            Debug.Log($"CRANE5_CARD_RANGE_DIST={CardPlacement.Distance(rangeCard, 4.8f):F4}");
            Debug.Log($"CRANE5_CARD_CRATES_OFFSET={CardPlacement.AcrossOffset(cratesCard):F4}");
            Debug.Log($"CRANE5_CARD_CRATES_OWNLANE={CardPlacement.UsesOwnLane(cratesCard).ToString().ToLowerInvariant()}");
            Debug.Log($"CRANE5_CARD_CRATES_DIST={CardPlacement.Distance(cratesCard, 4.8f):F4}");
            Debug.Log($"CRANE5_CARD_OWNLANE_TRUE={CardPlacement.UsesOwnLane(ownLaneCard).ToString().ToLowerInvariant()}");

            // ---- Cinema.CrateRowBuilder.Apply -------------------------------
            var stage = new StageSetup { Cubicle = 2.3f, Counter = 5.8f, O = Vector3.zero };
            var output = new CrateRowBuilder.Output
            {
                Shift = -5.5973f,
                Row = new RowFrame { O = new Vector3(-3.7782f, 0.0f, 49.5973f), Len = 2.6683f },
            };
            CrateRowBuilder.Apply(ref stage, output);
            Debug.Log($"CRANE5_APPLY_CUBICLE={stage.Cubicle:F4}");
            Debug.Log($"CRANE5_APPLY_COUNTER={stage.Counter:F4}");
            Debug.Log($"CRANE5_APPLY_O_X={stage.O.x:F4}");
            Debug.Log($"CRANE5_APPLY_O_Z={stage.O.z:F4}");
            Debug.Log($"CRANE5_APPLY_Y={stage.Y:F4}");

            Debug.Log("CRANE5_PROBE_DONE=1");
        }
    }
}
