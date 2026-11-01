using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.Core
{
    /// <summary>
    /// "I draw with Materials that no <see cref="Renderer"/> in the scene
    /// holds." Implemented by every <c>Graphics.RenderMeshInstanced</c> renderer
    /// in City/ — their Mesh/Material/Matrix4x4[] batches are plain fields, so a
    /// scene traversal (which is what src/game.js:1438's
    /// <c>renderer.compileAsync(this.scene, this.camera)</c> does) cannot see
    /// them at all.
    ///
    /// Core/SceneShaderWarmup is the only consumer: it collects
    /// <see cref="WarmupMaterials"/> from every MonoBehaviour in the loaded
    /// scene that implements this, unions it with every
    /// <c>Renderer.sharedMaterials</c>, and draws each distinct Material once
    /// off-screen. Keep implementations to a yield-loop over the batch list and
    /// nothing else — it is called three times a session and must never build.
    /// </summary>
    public interface IWarmupMaterials
    {
        IEnumerable<Material> WarmupMaterials();
    }
}
