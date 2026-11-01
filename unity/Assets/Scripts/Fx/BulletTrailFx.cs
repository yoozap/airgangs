using System.Collections.Generic;
using UnityEngine;
using TbilisiFly.Combat;

namespace TbilisiFly.Fx
{
    /// <summary>
    /// Port of src/ballistics.js's BulletSim._draw() (ballistics.js:671-687)
    /// and the matching half of dispose() (ballistics.js:689-694) — the
    /// visible round in flight. A 9 mm round is not a glowing tracer (the JS
    /// comment at ballistics.js:460-464 is explicit: "you would never see
    /// one"), so this is a short, dull, non-glowing streak: dark lead with a
    /// faint grey smear behind it, invisible past 60 m. The constructor's
    /// InstancedMesh + its tapered-cylinder geometry (ballistics.js:465-470)
    /// is also ported here rather than in Combat/Ballistics.cs, keeping the
    /// split this port already uses throughout Combat/Fx: BulletSim is the
    /// pure simulation (no Renderer/Transform of its own — see
    /// Combat/Ballistics.cs's own header), this class is its one VFX
    /// consumer, exactly the RocketTrailFx / SmokeTrailFx precedent.
    ///
    /// Reads BulletSim.Live every frame; never mutates a Bullet's Pos/Vel/
    /// Trail (BulletSim.Update already advances Trail at ballistics.js:536 —
    /// this class only turns that number into a drawn streak).
    ///
    /// WIRING: nothing constructs this yet, same as RocketTrailFx/
    /// SmokeTrailFx today (grepped — those two have zero call sites outside
    /// their own file either). The owning MonoBehaviour is whichever Core/
    /// system owns the live BulletSim instance (Core/CombatWorldAdapter.cs
    /// constructs `new BulletSim(...)`, which is Core's file, not this
    /// port's); wiring one `new BulletTrailFx()` + a per-frame
    /// `trail.Update(dt, bulletSim.Live, cam.position)` call there is the
    /// exact, minimal hookup needed.
    /// </summary>
    public sealed class BulletTrailFx
    {
        const int MaxLive = BulletSim.MaxLive; // ballistics.js:437 — same cap as the sim itself
        const float VisibleRangeSq = 60f * 60f; // ballistics.js:676

        public Material Mat;
        static Mesh _cylinder;
        public static Mesh StreakMesh => _cylinder != null ? _cylinder : (_cylinder = BuildCylinder(0.013f, 0.008f, 1f, 5));

        readonly Matrix4x4[] _mScratch = new Matrix4x4[MaxLive];
        bool _hadRounds; // ballistics.js:686 this._hadRounds

        public BulletTrailFx()
        {
            var sh = Shader.Find("TbilisiFly/BulletStreak");
            if (sh == null) return; // shader asset not imported yet in this build — Mat stays null, Update() no-ops
            Mat = new Material(sh) { name = "BulletStreak", enableInstancing = true };
            Mat.SetColor("_Color", new Color(0.7255f, 0.698f, 0.6431f, 0.5f)); // ballistics.js:468 0xb9b2a4 @ 0.5
        }

        /// <summary>One frame: turn every live bullet's Trail length into a drawn
        /// streak behind it. `cam` is the active camera's world position
        /// (ballistics.js:673 this.game.camera.position).</summary>
        public void Update(IReadOnlyList<Bullet> live, Vector3 cam)
        {
            if (Mat == null) return;
            int k = 0;
            for (int i = 0; i < live.Count && k < MaxLive; i++)
            {
                var b = live[i];
                if (b.Trail < 0.05f) continue;                              // ballistics.js:675
                if ((b.Pos - cam).sqrMagnitude > VisibleRangeSq) continue;   // ballistics.js:676 — invisible that far out
                float len = b.Trail;
                Vector3 dir = b.Vel.sqrMagnitude > 1e-10f ? b.Vel.normalized : Vector3.forward; // ballistics.js:678
                Quaternion rot = Quaternion.FromToRotation(Vector3.forward, dir);
                Vector3 scale = new Vector3(1f, 1f, len);                   // ballistics.js:679
                Vector3 pos = b.Pos - dir * (len * 0.5f);                   // ballistics.js:680
                _mScratch[k++] = Matrix4x4.TRS(pos, rot, scale);
            }
            // ballistics.js:684-686 — rounds.count = k; needsUpdate gated on k>0 || _hadRounds.
            // Graphics.DrawMeshInstanced submits fresh state every call, so the gate
            // collapses to "don't bother calling Draw with zero instances this frame".
            if (k > 0 || _hadRounds) InstancedSpriteBatch.Draw(StreakMesh, Mat, _mScratch, null, null, k);
            _hadRounds = k > 0;
        }

        /// <summary>ballistics.js:689-694 dispose() — the Mesh is the shared
        /// static StreakMesh (same convention as InstancedSpriteBatch.UnitQuad),
        /// so only this instance's own Material is released here, matching
        /// RocketTrailFx.Dispose()/SmokeTrailFx's sibling.</summary>
        public void Dispose()
        {
            if (Mat != null) UnityEngine.Object.Destroy(Mat);
        }

        // ballistics.js:466 — THREE.CylinderGeometry(0.013, 0.008, 1, 5, 1).rotateX(PI/2):
        // a closed, capped, 5-sided tapered cylinder whose height axis has been
        // rotated from THREE's default +Y onto +Z (so Quaternion.FromToRotation
        // from Vector3.forward above lines the streak up with the bullet's own
        // direction of travel without any extra axis juggling at the call site).
        // radiusTop (0.013, the wider end) lands at +halfH i.e. +Z — the bullet's
        // own current position in Update() above; radiusBottom (0.008, narrower)
        // trails behind at -Z, matching ballistics.js's "dark lead with a faint
        // grey smear behind it" shape.
        static Mesh BuildCylinder(float radiusTop, float radiusBottom, float height, int radialSegments)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float halfH = height * 0.5f;
            int rs = radialSegments;

            int sideStart = verts.Count;
            for (int i = 0; i <= rs; i++)
            {
                float ang = (float)i / rs * Mathf.PI * 2f;
                float cx = Mathf.Cos(ang), sx = Mathf.Sin(ang);
                verts.Add(new Vector3(cx * radiusTop, sx * radiusTop, halfH));
                verts.Add(new Vector3(cx * radiusBottom, sx * radiusBottom, -halfH));
            }
            for (int i = 0; i < rs; i++)
            {
                int a = sideStart + i * 2, b = a + 1;
                int c = sideStart + (i + 1) * 2, d = c + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }

            int topCenter = verts.Count; verts.Add(new Vector3(0, 0, halfH));
            int topStart = verts.Count;
            for (int i = 0; i <= rs; i++)
            {
                float ang = (float)i / rs * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(ang) * radiusTop, Mathf.Sin(ang) * radiusTop, halfH));
            }
            for (int i = 0; i < rs; i++) { tris.Add(topCenter); tris.Add(topStart + i); tris.Add(topStart + i + 1); }

            int botCenter = verts.Count; verts.Add(new Vector3(0, 0, -halfH));
            int botStart = verts.Count;
            for (int i = 0; i <= rs; i++)
            {
                float ang = (float)i / rs * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(ang) * radiusBottom, Mathf.Sin(ang) * radiusBottom, -halfH));
            }
            for (int i = 0; i < rs; i++) { tris.Add(botCenter); tris.Add(botStart + i + 1); tris.Add(botStart + i); }

            var m = new Mesh { name = "Fx_BulletStreak" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            // Instances are rotated/stretched arbitrarily per-draw via the matrix
            // array, same reasoning InstancedSpriteBatch.UnitQuad's own bounds
            // comment gives — a tight object-space bound would get wrongly culled.
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f);
            return m;
        }
    }
}
