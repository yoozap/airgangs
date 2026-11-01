// Play-mode-only — see FirstPlayModeRun.cs's own header for why the guard is
// here (a PLAYER build compiles this assembly too, and has no TestRunner).
#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TbilisiFly.Character;
using TbilisiFly.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>
    /// Proof file for the "unclaimed remainder" audit of src/foot.js,
    /// src/characters.js and src/anims.js (docs/BUDGET_AUDIT.md,
    /// docs/port_units/character_rest.json). Two prior audits found ZERO
    /// genuinely unported lines in foot.js/characters.js — this session's
    /// independent re-derivation (walking every uncited cluster of both
    /// files against its C# counterpart) agrees, with one correction and one
    /// small real addition:
    ///
    /// 1. characters.js:2847-2867 `makeCharacter`'s own diagnostic half — "say
    ///    out loud what actually got loaded" (bone count / clip list / height
    ///    sanity / a loud "STATIC MESH, limbs cannot move" warning) — had NO
    ///    C# counterpart anywhere (grep for the literal strings found nothing).
    ///    That exact failure class (a body with no usable skeleton/avatar) was
    ///    this port's single most expensive bug
    ///    (.claude/skills/unity/SKILL.md, "IsHumanoid" section), so this is a
    ///    cheap, real, high-value port: Character/CharacterRig.cs's ctor now
    ///    calls LogSpawnDiagnostic() after NormalizeHeight(). SpawnDiagnostic
    ///    ReachedOnRealBody proves it fires on the real shipped banana.fbx,
    ///    not a synthetic fixture.
    /// 2. docs/port_manifest.json carried a 138-line anims.js claim against
    ///    `Assets/Scripts/Character/MoveLibrary.cs` — a file deleted
    ///    2026-10-31 "as a duplicate with no caller" (CharacterRoster.cs's own
    ///    header says so). The methods that claim's note described
    ///    (RegisterClip/GetClip/HasClips/LoadClipsEditorOnly) do not exist
    ///    anywhere in unity/Assets any more (grep confirms). That unit has
    ///    been restated to 0 in docs/port_units/character_rest.json.
    ///    StaleMoveLibraryClaimStaysGone is a standing regression guard: if
    ///    the file ever reappears, re-check the manifest for a reintroduced
    ///    double-dip against the already-excluded SCHEMES/retarget range.
    ///
    ///   Unity -batchmode -projectPath unity -runTests -testPlatform PlayMode
    ///        -testFilter CharacterRestRun -testResults &lt;xml&gt; -logFile &lt;log&gt;
    /// </summary>
    public class CharacterRestRun
    {
        static void Check(List<string> fails, string id, bool cond, string note = null)
        {
            Debug.Log($"PLAYMODE CHARREST_{id}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}");
            if (!cond) fails.Add(id + (note == null ? "" : ": " + note));
        }

        static int CountBones(GameObject root)
        {
            var seen = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var b in smr.bones)
                    if (b != null) seen.Add(b);
            return seen.Count;
        }

        // =====================================================================
        // characters.js:2847-2867's diagnostic, newly ported as
        // CharacterRig.LogSpawnDiagnostic — reached on the real shipped asset,
        // per .claude/skills/unity/SKILL.md's "pair every gated system with
        // one assertion that its gate is actually open on the shipped asset".
        // =====================================================================
        [UnityTest]
        public IEnumerator SpawnDiagnosticReachedOnRealBody()
        {
            LogAssert.ignoreFailingMessages = true;
            yield return null;
            var fails = new List<string>();

            const string path = "Assets/Resources/Art/Characters/banana.fbx";
            var prefab = ArtAssets.Load<GameObject>(path);
            Check(fails, "prefab_found", prefab != null, path + " missing");
            if (prefab == null) { Assert.Fail(string.Join(" | ", fails)); yield break; }

            var instance = ArtAssets.Instantiate(prefab);
            int bonesBefore = CountBones(instance);

            // The exact shape CharacterRig.LogSpawnDiagnostic emits —
            // "personage banana: N bones, clips [...], H.HH m tall" plus an
            // optional " — OFF, ..." / " — STATIC MESH, ..." suffix. Matching
            // the shape (not retyping every field) is the proof that the real
            // ctor path reached the real Debug.Log call, not a mock of it.
            LogAssert.Expect(LogType.Log,
                new System.Text.RegularExpressions.Regex(@"^personage banana: \d+ bones, clips \[.*\], \d+\.\d\d m tall"));

            var rig = new CharacterRig(instance, "banana");

            Check(fails, "rig_built", rig != null);
            Check(fails, "bones_nonzero", bonesBefore > 0,
                "0 bones on the shipped asset would mean the STATIC MESH branch, never the good one, is what gets exercised here");
            Check(fails, "height_in_tolerance",
                Mathf.Abs(rig.BodyHeight - CharacterRoster.HeroHeight) <= 0.02f,
                $"BodyHeight={rig.BodyHeight:F4} HeroHeight={CharacterRoster.HeroHeight:F4} (characters.js's own >0.02 OFF threshold)");

            Object.Destroy(instance);
            Debug.Log(fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (fails.Count > 0) Assert.Fail("CharacterRestRun failures: " + string.Join(" | ", fails));
        }

        // =====================================================================
        // The MoveLibrary.cs correction — a static fact about the shipped
        // source, filed here so one `-testFilter CharacterRestRun` run
        // reproduces the whole audit's evidence in one place.
        // =====================================================================
        [Test]
        public void StaleMoveLibraryClaimStaysGone()
        {
            string p = System.IO.Path.Combine(Application.dataPath, "Scripts", "Character", "MoveLibrary.cs");
            bool exists = System.IO.File.Exists(p);
            Debug.Log($"PLAYMODE CHARREST_movelibrary_file_absent={(!exists).ToString().ToLowerInvariant()}");
            Assert.IsFalse(exists,
                "MoveLibrary.cs reappeared at " + p + " — re-check docs/port_manifest.json's anims.js unit " +
                "for a reintroduced double-dip against the already-excluded SCHEMES/retarget range (116-369)");
        }
    }
}
#endif
