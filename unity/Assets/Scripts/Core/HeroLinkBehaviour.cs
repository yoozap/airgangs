using UnityEngine;

namespace TbilisiFly.Core
{
    // ONE MonoBehaviour PER FILE, NAMED AFTER IT (.claude/skills/unity/SKILL.md):
    // moved out of Core/HeroLink.cs, which is named after a different class.
    // Unity cannot bind a script ASSET to a MonoBehaviour whose file is not
    // named after it, so an AddComponent'd component serialises with NO guid the
    // moment it is saved into a scene a player loads (that is what corrupted
    // level0). Behaviour unchanged.
    /// <summary>
    /// Optional thin MonoBehaviour shim: forwards Unity's lifecycle events to
    /// a HeroLink the way the JS forwards pagehide/beforeunload/visibilitychange
    /// (src/net.js:29-38). Not required — HeroLink itself needs no MonoBehaviour —
    /// but a game loop that already has one can just add this component instead
    /// of duplicating the plumbing.
    /// </summary>
    public class HeroLinkBehaviour : MonoBehaviour
    {
        public HeroLink Link;

        void Update() => Link?.Tick(Time.deltaTime);
        void OnApplicationPause(bool paused) { if (paused) Link?.Flush(leaving: true); }
        void OnApplicationQuit() => Link?.Flush(leaving: true);
    }
}
