using UnityEngine;

namespace TbilisiFly.Tools
{
    /// <summary>
    /// Attach to any scene object to opt it into Assets/Editor/DevPlacement.cs —
    /// the direct analogue of src/devmode.js's <c>game.dev.register(obj, id, opts)</c>
    /// call sites. <c>bakedCollider</c> mirrors the JS's own opt-OUT rule: set it
    /// true for anything whose collider is baked into a world-space soup (the
    /// JS's own examples: "the tables, the ranges") so this component refuses to
    /// register rather than let a drag silently desync the picture from the wall.
    ///
    /// Lives OUTSIDE Assets/Editor/ on purpose — a MonoBehaviour meant to be
    /// attached to a scene GameObject cannot live in a folder Unity treats as
    /// editor-only (DevPlacement.cs's own header explains why). The actual
    /// registry/persistence logic this calls into IS editor-only, hence the
    /// #if UNITY_EDITOR guard around every call: outside the editor this
    /// component compiles to an inert tag with no behaviour.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class DevPlacementTag : MonoBehaviour
    {
        [Tooltip("Unique id this prop is saved/restored under. Must be stable across reloads — changing it starts a fresh override, orphaning the old one.")]
        public string id;

        [Tooltip("Set true for props whose COLLIDER is baked into a world-space soup keyed by where the code placed the mesh (e.g. the range tables) — those must NOT register, or dragging the visual leaves the collider behind.")]
        public bool bakedCollider;

        void OnEnable()
        {
// (registration is pulled by the editor side — see the note below)
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            // likewise: the editor side drops registrations it can no longer find.
#endif
        }
    }
}
