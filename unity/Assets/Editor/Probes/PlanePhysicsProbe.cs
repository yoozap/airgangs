using TbilisiFly.Flight;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// Proves PlanePhysics.cs's ContactSolver.Solve/Damage/Restitution
    /// reproduce src/planephys.js's real (if unexported) `_solve`/`_damage`/
    /// `_restitution`. tools/_probe_planephys.mjs instantiates the REAL
    /// `PlanePhysics` class from src/planephys.js and calls those methods
    /// directly against synthetic actors satisfying the same duck-typed
    /// interface IPlanePhysicsActor formalises here — see that script's
    /// header. Same test actors (identity orientation, so inverse-inertia is
    /// a plain diagonal scale, no quaternion needed), same cases, same KEY=
    /// value format; diff with tools/unity_port_check.py.
    ///
    ///   Unity -batchmode -quit -executeMethod TbilisiFly.Probes.PlanePhysicsProbe.Run
    /// </summary>
    public static class PlanePhysicsProbe
    {
        // A minimal IPlanePhysicsActor for the solver alone — identity
        // orientation (body axes == world axes), mutable vel/omega/pos,
        // exactly mirroring the JS probe's makeActor().
        class TestActor : IPlanePhysicsActor
        {
            public Vector3 VelField, OmegaField, PosField;
            readonly Vector3 _iInv;
            readonly float _mass;

            public TestActor(float mass, Vector3 pos, Vector3 vel, float span, float height, float len)
            {
                _mass = mass;
                PosField = pos;
                VelField = vel;
                var i = BodyInertia.Box(mass, span, height, len);
                _iInv = new Vector3(1f / i.x, 1f / i.y, 1f / i.z);
            }

            public bool IsWorld => false;
            public float Mass => _mass;
            public float InvMass => 1f / _mass;
            public Vector3 Pos => PosField;

            public Vector3 VelAt(Vector3 point)
            {
                var r = point - PosField;
                return VelField + Vector3.Cross(OmegaField, r);
            }

            public Vector3 InvInertiaApply(Vector3 v) => new Vector3(v.x * _iInv.x, v.y * _iInv.y, v.z * _iInv.z);

            public void ApplyImpulse(Vector3 j, Vector3 point)
            {
                VelField += j * InvMass;
                var r = point - PosField;
                OmegaField += InvInertiaApply(Vector3.Cross(r, j));
            }

            public void MovePos(Vector3 n, float d) => PosField += n * d;
        }

        static void RunCase(string id, TestActor a, TestActor b, Vector3 point, Vector3 n, float pen, float e, float mu)
        {
            var c = ContactSolver.Solve(a, b, point, n, pen, e, mu);
            if (c == null) { Debug.Log($"SOLVE id={id} null=1"); return; }
            var r = c.Value;
            Debug.Log($"SOLVE id={id} null=0 vn={r.Vn:F6} Jn={r.Jn:F6} Jt={r.Jt:F6} meff={r.Meff:F6} E={r.E:F6} " +
                      $"avx={a.VelField.x:F6} avy={a.VelField.y:F6} avz={a.VelField.z:F6} " +
                      $"bvx={b.VelField.x:F6} bvy={b.VelField.y:F6} bvz={b.VelField.z:F6} " +
                      $"awx={a.OmegaField.x:F6} awy={a.OmegaField.y:F6} awz={a.OmegaField.z:F6} " +
                      $"bwx={b.OmegaField.x:F6} bwy={b.OmegaField.y:F6} bwz={b.OmegaField.z:F6}");
        }

        public static void Run()
        {
            // ---- CASE 1: head-on, equal mass ----
            {
                var a = new TestActor(3400f, new Vector3(0, 0, 0), new Vector3(0, 0, -50), 11f, 3f, 9f);
                var b = new TestActor(3400f, new Vector3(0, 0, -1), new Vector3(0, 0, 50), 11f, 3f, 9f);
                RunCase("equalHeadOn", a, b, new Vector3(0, 0, -0.5f), new Vector3(0, 0, 1), 0.1f, ContactSolver.Restitution(100f), 0.45f);
            }
            // ---- CASE 2: heavy vs light ----
            {
                var a = new TestActor(462500f, new Vector3(0, 0, 0), new Vector3(0, 0, -40), 88.4f, 8f, 84f);
                var b = new TestActor(2500f, new Vector3(0, 0, -1), new Vector3(0, 0, 60), 8f, 2f, 8f);
                RunCase("heavyVsLight", a, b, new Vector3(0, 0, -0.5f), new Vector3(0, 0, 1), 0.2f, ContactSolver.Restitution(100f), 0.45f);
            }
            // ---- CASE 3: off-centre glancing hit ----
            {
                var a = new TestActor(3400f, new Vector3(0, 0, 0), new Vector3(0, 0, -60), 11f, 3f, 9f);
                var b = new TestActor(5200f, new Vector3(4, 0.5f, -1), new Vector3(1, 0, 40), 12f, 3f, 10f);
                RunCase("glancing", a, b, new Vector3(4, 0.5f, -0.5f), new Vector3(0.1f, 0, 0.99f), 0.15f, ContactSolver.Restitution(100f), 0.45f);
            }
            // ---- CASE 4: separating — must be null ----
            {
                var a = new TestActor(3400f, new Vector3(0, 0, 0), new Vector3(0, 0, 50), 11f, 3f, 9f);
                var b = new TestActor(3400f, new Vector3(0, 0, -1), new Vector3(0, 0, -50), 11f, 3f, 9f);
                RunCase("separating", a, b, new Vector3(0, 0, -0.5f), new Vector3(0, 0, 1), 0.1f, 0.2f, 0.45f);
            }
            // ---- CASE 5: tangential (sliding) contact ----
            {
                var a = new TestActor(3400f, new Vector3(0, 0, 0), new Vector3(10, -2, -30), 11f, 3f, 9f);
                var b = new TestActor(3400f, new Vector3(0, 0, -1), new Vector3(-10, 2, 30), 11f, 3f, 9f);
                RunCase("friction", a, b, new Vector3(0, 0, -0.5f), new Vector3(0, 0, 1), 0.1f, 0.2f, 0.6f);
            }

            int di = 0;
            foreach (var (e, mass, colMult) in new (float, float, float)[]
                     { (8e5f, 3400f, 1.138f), (1e4f, 3400f, 1.138f), (8e5f, 462500f, 0.5f), (100f, 3400f, 1.138f) })
            {
                float dmg = ContactSolver.Damage(e, mass, colMult);
                Debug.Log($"DAMAGE id=d{di++} E={e} mass={mass} colMult={colMult} dmg={dmg:F6}");
            }
            int ri = 0;
            foreach (var v in new float[] { 0, 20, 60, 100, 140, 200 })
                Debug.Log($"RESTITUTION id=r{ri++} v={v} e={ContactSolver.Restitution(v):F6}");

            ProbeExit.Exit(0);
        }
    }
}
