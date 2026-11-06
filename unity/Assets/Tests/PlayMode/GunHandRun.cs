#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using TbilisiFly.Character;
using TbilisiFly.Combat;
using TbilisiFly.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TbilisiFly.PlayModeTests
{
    /// <summary>Drives the user's own complaint end to end — "i dont see my gun
    /// in hand, and on body" — and reads the weapon's REAL state from code
    /// (parent, world position vs the hand bone, scale, layer, renderers,
    /// camera frustum, animator state, bar index) plus screenshots, instead of
    /// asking the user to be the tester. 2026-11-02.</summary>
    public class GunHandRun
    {
        static GameLoop _game; static Boot _boot; static Keyboard _kb; static Mouse _mouse; static bool _booted;
        static void Fact(string k, object v) => Debug.Log($"PLAYMODE GUNHAND_{k}={v}");
        static void FactB(string k, bool v) => Debug.Log($"PLAYMODE GUNHAND_{k}={v.ToString().ToLowerInvariant()}");
        static void Check(List<string> fails, string id, bool cond, string note = null)
        { Debug.Log($"PLAYMODE GHCHECK_{id}={(cond ? "pass" : "FAIL")}{(cond || note == null ? "" : " (" + note + ")")}"); if (!cond) fails.Add(id + (note != null ? ": " + note : "")); }
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";
        static void Press(params Key[] keys) => InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
        static IEnumerator Hold(int frames, params Key[] keys) { for (int i = 0; i < frames; i++) { Press(keys); yield return null; } Press(); yield return null; }
        static IEnumerator Tap(Key k) { yield return Hold(4, k); }
        static void PressMouse(bool l, bool r) { var s = new MouseState(); if (l) s.WithButton(MouseButton.Left); if (r) s.WithButton(MouseButton.Right); InputSystem.QueueStateEvent(_mouse, s); }
        static IEnumerator HoldMouse(int frames, bool l, bool r) { for (int i = 0; i < frames; i++) { PressMouse(l, r); yield return null; } PressMouse(false, false); yield return null; }
        // The pack's aim toggle is a coroutine of WaitForSeconds(0.05)+(0.1) on WALL time;
        // batch frames can be ~2 ms, so hold by real seconds, not frames.
        static IEnumerator HoldMouseReal(float seconds, bool l, bool r) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < seconds) { PressMouse(l, r); yield return null; } }
        static IEnumerator Idle(int n) { for (int i = 0; i < n; i++) yield return null; }
        static IEnumerator WaitReal(float seconds) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < seconds) yield return null; }
        static void CamFacts(string tag)
        {
            var cam = _game.MainCamera; var rig = _game.Rig; if (cam == null || rig == null) return;
            Transform head = rig.Anim != null ? rig.Anim.GetBoneTransform(HumanBodyBones.Head) : null;
            Fact(tag + "_camToRoot", F(Vector3.Distance(cam.transform.position, rig.Root.position)));
            Fact(tag + "_camToHead", head != null ? F(Vector3.Distance(cam.transform.position, head.position)) : "-");
            Fact(tag + "_camFov", F(cam.fieldOfView));
            Fact(tag + "_camPos", V(cam.transform.position)); Fact(tag + "_rootPos", V(rig.Root.position));
        }
        static object Invoke(object t, string name, params object[] a) => t.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(t, a);
        // WaitForEndOfFrame never fires in -batchmode (the first run of this file
        // hung forever on it), so render the game camera into a RenderTexture by
        // hand and write the PNG — works headless with graphics.
        static IEnumerator Shot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("TF_SHOTS"); if (string.IsNullOrEmpty(dir)) yield break;
            yield return null;
            var cam = _game != null ? _game.MainCamera : Camera.main;
            if (cam == null) { Debug.Log("PLAYMODE GUNHAND_SHOT_" + name + "=<no camera>"); yield break; }
            System.IO.Directory.CreateDirectory(dir);
            int w = 1280, h = 800;
            var rt = new RenderTexture(w, h, 24); var prev = cam.targetTexture; var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                string p = System.IO.Path.Combine(dir, name + ".png");
                System.IO.File.WriteAllBytes(p, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                Debug.Log("PLAYMODE GUNHAND_SHOT_" + name + "=" + p);
            }
            finally { cam.targetTexture = prev; RenderTexture.active = prevActive; rt.Release(); UnityEngine.Object.Destroy(rt); }
        }
        static string State(Animator a, int layer)
        {
            if (a == null || a.runtimeAnimatorController == null || layer >= a.layerCount) return "<none>";
            var ci = a.GetCurrentAnimatorClipInfo(layer); return ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "<noclip>";
        }

        static IEnumerator EnsureBooted()
        {
            if (_booted) yield break; _booted = true;
            _kb = InputSystem.GetDevice<Keyboard>() ?? InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.GetDevice<Mouse>() ?? InputSystem.AddDevice<Mouse>();
            InputSystem.EnableDevice(_kb); InputSystem.EnableDevice(_mouse);
            Debug.Log("PLAYMODE GUNHAND_IN_BRIDGE=" + HarnessInput.Install());
            Debug.Log("PLAYMODE GUNHAND_IN_BRIDGE_UI=" + HarnessInput.InstallUiPointer());
            yield return null;
            TokenStore.ClearToken();
            var load = SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            while (load != null && !load.isDone) yield return null;
            yield return Idle(5);
            _boot = UnityEngine.Object.FindAnyObjectByType<Boot>(); _game = UnityEngine.Object.FindAnyObjectByType<GameLoop>();
            if (_boot == null || _game == null) yield break;
            GameLoop.UsePurchasedShooter = true;
            var user = new PublicUser { username = "gunhandrun", level = 3, stars = 1, activeChar = 0,
                characters = new List<CharacterDef> { new CharacterDef { hid = "h-gunhand-" + DateTime.UtcNow.Ticks, name = "ვაშლი", race = "veggie", model = "apple" } } };
            Invoke(_boot, "SetUser", user); _boot.OpenCharacter(); yield return Idle(3);
            _boot.EnterWorldWithSelectedHero();
            for (int i = 0; i < 1200 && !_game.Started; i++) yield return Idle(1);
            yield return Idle(5); _game.SetFixedDt(1f / 60f);
            for (int i = 0; i < 600 && _game.Mode == GameMode.Boarding; i++) yield return Idle(1);
        }
        static IEnumerator EnsureOnFoot()
        {
            if (_game.Mode != GameMode.Flying) yield break;
            var p = _game.ActivePlane; if (p != null) { p.Velocity = Vector3.zero; p.Rates = Vector3.zero; p.Airborne = false; }
            yield return Idle(5); yield return Hold(8, Key.Enter);
            for (int i = 0; i < 900 && _game.Mode != GameMode.OnFoot; i++) yield return Idle(1);
            if (_game.Mode != GameMode.OnFoot) { _game.ToggleBoard(); for (int i = 0; i < 900 && _game.Mode != GameMode.OnFoot; i++) yield return Idle(1); }
        }

        static Transform FindLoot(string id)
        {
            foreach (var t in UnityEngine.Object.FindObjectsByType<PickupTag>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t != null && t.Weapon != null && t.Weapon.Id == id && t.transform.parent != null) return t.transform;
            return null;
        }
        static IEnumerator PinAt(Transform target, float standoff)
        {
            Vector3 g = target.position; Vector3 from = _game.Foot.Pos; Vector3 d = g - from; d.y = 0f;
            if (d.sqrMagnitude < 1e-4f) d = Vector3.forward;
            Vector3 at = g - d.normalized * standoff;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out var h, 6f)) at.y = h.point.y;
            _game.Foot.Pos = at; _game.Foot.Speed = 0f; _game.Foot.Vy = 0f;
            _game.Foot.Yaw = Mathf.Atan2(-d.x, -d.z); _game.Foot.CamYaw = _game.Foot.Yaw;
            for (int i = 0; i < 45; i++) { _game.Foot.Pos = at; _game.Foot.Speed = 0f; yield return null; }
            Fact("PIN_target", $"{target.name} at {V(g)} hero={V(_game.Foot.Pos)} dist={F(Vector3.Distance(_game.Foot.Pos, g))}");
        }

        /// <summary>Which transform turns a baked skinned mesh into WORLD space on this
        /// rig? Compare Renderer.bounds (authoritative) with the baked-vertex bbox
        /// under useScale=false/true × localToWorldMatrix, and with TRS(pos,rot,1).</summary>
        static void BodyScan()
        {
            var rig = _game.Rig; if (rig == null) return;
            Fact("BODY_model", $"{rig.Model.name} localScale={V(rig.Model.localScale)} lossy={V(rig.Model.lossyScale)} root={V(rig.Root.position)}");
            foreach (var r in rig.Model.GetComponentsInChildren<Renderer>(true))
            {
                string kind = r is SkinnedMeshRenderer ? "SMR" : r.GetType().Name;
                Fact("BODY_renderer", $"{r.name} kind={kind} parent={(r.transform.parent != null ? r.transform.parent.name : "-")} localScale={V(r.transform.localScale)} lossy={V(r.transform.lossyScale)} worldBounds={V(r.bounds.size)} center={V(r.bounds.center - rig.Root.position)}");
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    Bounds BB(Vector3[] vs, Matrix4x4 m) { var b = new Bounds(m.MultiplyPoint3x4(vs[0]), Vector3.zero); for (int i = 1; i < vs.Length; i++) b.Encapsulate(m.MultiplyPoint3x4(vs[i])); return b; }
                    var mf = new Mesh(); smr.BakeMesh(mf, false); var vf = mf.vertices;
                    var mt = new Mesh(); smr.BakeMesh(mt, true); var vt = mt.vertices;
                    Matrix4x4 l2w = smr.transform.localToWorldMatrix;
                    Matrix4x4 trs1 = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                    Matrix4x4 rootBone = smr.rootBone != null ? smr.rootBone.localToWorldMatrix : l2w;
                    Fact("BODY_bake", $"{r.name} verts={vf.Length} rootBone={(smr.rootBone != null ? smr.rootBone.name : "-")} rootBoneLossy={(smr.rootBone != null ? V(smr.rootBone.lossyScale) : "-")} "
                        + $"false×l2w={V(BB(vf, l2w).size)} true×l2w={V(BB(vt, l2w).size)} true×trs1={V(BB(vt, trs1).size)} false×rootBone={V(BB(vf, rootBone).size)} rawFalse={V(BB(vf, Matrix4x4.identity).size)} rawTrue={V(BB(vt, Matrix4x4.identity).size)}");
                    UnityEngine.Object.Destroy(mf); UnityEngine.Object.Destroy(mt);
                }
            }
        }
        /// <summary>Does this weapon put ANY pixels on screen? Render the game camera
        /// twice (renderers on / off) and count pixels that differ. Ends the "the
        /// numbers say it is there but the picture does not" argument.</summary>
        static void PixelWitness(string tag, InteractiveWeapon iw)
        {
            var cam = _game != null ? _game.MainCamera : null; if (cam == null || iw == null) return;
            var rs = iw.GetComponentsInChildren<Renderer>(true);
            foreach (var r in rs) Fact(tag + "_rend", $"{r.name} type={r.GetType().Name} goActive={r.gameObject.activeInHierarchy.ToString().ToLowerInvariant()} enabled={r.enabled.ToString().ToLowerInvariant()} isVisible={r.isVisible.ToString().ToLowerInvariant()} bounds={V(r.bounds.size)} layer={LayerMask.LayerToName(r.gameObject.layer)} mat={(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "<none>")}");
            Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds(iw.transform.position, Vector3.zero); for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            Vector3 sp = cam.WorldToScreenPoint(b.center);
            int w = 1280, h = 800;
            var rt = new RenderTexture(w, h, 24); var prev = cam.targetTexture; var prevA = RenderTexture.active;
            Color32[] A, B;
            try
            {
                cam.targetTexture = rt;
                cam.Render(); RenderTexture.active = rt;
                var t1 = new Texture2D(w, h, TextureFormat.RGB24, false); t1.ReadPixels(new Rect(0, 0, w, h), 0, 0); t1.Apply(); A = t1.GetPixels32(); UnityEngine.Object.Destroy(t1);
                bool[] was = new bool[rs.Length]; for (int i = 0; i < rs.Length; i++) { was[i] = rs[i].enabled; rs[i].enabled = false; }
                cam.Render(); RenderTexture.active = rt;
                var t2 = new Texture2D(w, h, TextureFormat.RGB24, false); t2.ReadPixels(new Rect(0, 0, w, h), 0, 0); t2.Apply(); B = t2.GetPixels32(); UnityEngine.Object.Destroy(t2);
                for (int i = 0; i < rs.Length; i++) rs[i].enabled = was[i];
            }
            finally { cam.targetTexture = prev; RenderTexture.active = prevA; rt.Release(); UnityEngine.Object.Destroy(rt); }
            int diff = 0, minX = w, maxX = -1, minY = h, maxY = -1;
            for (int i = 0; i < A.Length; i++)
            {
                int d = Mathf.Abs(A[i].r - B[i].r) + Mathf.Abs(A[i].g - B[i].g) + Mathf.Abs(A[i].b - B[i].b);
                if (d > 24) { diff++; int x = i % w, y = i / w; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            }
            Fact(tag + "_pixels", $"changed={diff} screenCenter=({sp.x:F0},{sp.y:F0},depth={sp.z:F2}) diffBox=x{minX}..{maxX} y{minY}..{maxY} (y up from bottom)");
        }
        static void Describe(string tag, InteractiveWeapon iw)
        {
            var rig = _game.Rig; var cam = _game.MainCamera;
            if (iw == null) { Fact(tag + "_weapon", "<null>"); return; }
            var rs = iw.GetComponentsInChildren<Renderer>(true);
            Bounds b = new Bounds(iw.transform.position, Vector3.zero); bool any = false;
            foreach (var r in rs) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            Transform hand = rig != null && rig.Anim != null ? rig.Anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
            Transform chest = rig != null && rig.Anim != null ? (rig.Anim.GetBoneTransform(HumanBodyBones.Chest) ?? rig.Anim.GetBoneTransform(HumanBodyBones.Spine)) : null;
            bool inFrustum = cam != null && any && GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(cam), b);
            Fact(tag + "_weapon", iw.label);
            Fact(tag + "_active", iw.gameObject.activeInHierarchy.ToString().ToLowerInvariant());
            Fact(tag + "_parent", iw.transform.parent != null ? iw.transform.parent.name : "<none>");
            Fact(tag + "_layer", LayerMask.LayerToName(iw.gameObject.layer));
            Fact(tag + "_worldPos", V(iw.transform.position));
            Fact(tag + "_lossyScale", V(iw.transform.lossyScale));
            Fact(tag + "_distToRoot", rig != null ? F(Vector3.Distance(iw.transform.position, rig.Root.position)) : "-");
            Fact(tag + "_distToHand", hand != null ? F(Vector3.Distance(iw.transform.position, hand.position)) : "<no hand>");
            Fact(tag + "_distToChest", chest != null ? F(Vector3.Distance(iw.transform.position, chest.position)) : "<no chest>");
            Fact(tag + "_handLossy", hand != null ? V(hand.lossyScale) : "-");
            Fact(tag + "_renderers", $"{rs.Length} enabled={rs.Count(r => r.enabled)} visible={rs.Count(r => r.isVisible)}");
            Fact(tag + "_boundsSize", any ? V(b.size) : "-");
            Fact(tag + "_boundsCenter", any ? V(b.center) : "-");
            FactB(tag + "_inCameraFrustum", inFrustum);
            Fact(tag + "_camDist", cam != null ? F(Vector3.Distance(cam.transform.position, iw.transform.position)) : "-");
            Fact(tag + "_mats", string.Join("|", rs.Take(3).Select(r => r.sharedMaterial != null ? (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "<noshader>") : "<nomat>")));
            if (rig != null)
            {
                Vector3 f = iw.transform.forward, rr = rig.Root.right, rf = rig.Root.forward;
                Fact(tag + "_axisDots", $"fwd·bodyFwd={F(Vector3.Dot(f, rf))} fwd·bodyRight={F(Vector3.Dot(f, rr))} fwd·up={F(f.y)} right·bodyFwd={F(Vector3.Dot(iw.transform.right, rf))}");
                Vector3 rel = Quaternion.Inverse(rig.Root.rotation) * (b.center - rig.Root.position);
                Fact(tag + "_centerInBody", $"right={F(rel.x)} up={F(rel.y)} fwd={F(rel.z)} bodyWidth={F(rig.Aspect * rig.BodyHeight)}");
            }
        }
        static void DescribeAnim(string tag)
        {
            var a = _game.Rig?.Anim; var hero = CoverShootHero.Instance;
            Fact(tag + "_ctrl", a != null && a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "<none>");
            Fact(tag + "_state0", State(a, 0)); Fact(tag + "_state1", State(a, 1)); Fact(tag + "_state2", State(a, 2));
            Fact(tag + "_animWeapon", a != null && a.runtimeAnimatorController != null ? a.GetInteger("Weapon").ToString() : "-");
            Fact(tag + "_animAim", a != null && a.runtimeAnimatorController != null ? a.GetBool("Aim").ToString().ToLowerInvariant() : "-");
            Fact(tag + "_animSpeed", a != null && a.runtimeAnimatorController != null ? F(a.GetFloat("Speed")) : "-");
            Fact(tag + "_shootEnabled", hero?.Shoot != null ? hero.Shoot.enabled.ToString().ToLowerInvariant() : "<no hero>");
            Fact(tag + "_aimEnabled", hero?.Aim != null ? hero.Aim.enabled.ToString().ToLowerInvariant() : "-");
            Fact(tag + "_isAiming", hero?.Aim != null ? hero.Aim.IsAiming.ToString().ToLowerInvariant() : "-");
            Fact(tag + "_hasWeapon", hero?.Shoot != null ? hero.Shoot.HasWeapon.ToString().ToLowerInvariant() : "-");
            Fact(tag + "_bar", _game.WeaponBar != null ? $"index={_game.WeaponBar.Index} current={(_game.WeaponBar.Current.HasValue ? _game.WeaponBar.Current.Value.Name : "<none>")}" : "<no bar>");
            Fact(tag + "_footArmed", _game.Foot.Armed.ToString().ToLowerInvariant());
            Fact(tag + "_clipsPlaying", $"{_game.Foot.Clips.IsPlaying.ToString().ToLowerInvariant()} loco={_game.Foot.Clips.LocoClip ?? "<none>"} move={_game.Foot.Clips.CurrentClip ?? "<none>"}");
        }

        [UnityTest, Timeout(1500000)]
        public IEnumerator GunInHandAndOnBody()
        {
            LogAssert.ignoreFailingMessages = true;
            var fails = new List<string>();
            yield return EnsureBooted();
            if (_game == null) { Check(fails, "booted", false, "no GameLoop"); Assert.Fail(string.Join(" | ", fails)); yield break; }
            yield return EnsureOnFoot();
            Check(fails, "onFoot", _game.Mode == GameMode.OnFoot, $"mode={_game.Mode}");
            yield return Idle(30);
            var hero = CoverShootHero.Instance;
            Check(fails, "packAttached", hero != null && hero.Shoot != null, "CoverShootHero.Instance/Shoot null");
            DescribeAnim("BOOT"); BodyScan();

            // ---- AK: F ----
            var ak = FindLoot("akm");
            Check(fails, "akLootFound", ak != null, "no PickupTag akm in scene");
            if (ak == null) { Assert.Fail(string.Join(" | ", fails)); yield break; }
            yield return PinAt(ak, 0.9f);
            yield return Shot("00_before_F");
            yield return Tap(Key.F); yield return Idle(20);
            var iw = hero?.Shoot?.ActiveWeapon;
            if (iw == null) { Fact("F_keyPathTook", "false — invoking OnTakePickup directly"); _game.OnTakePickup?.Invoke(); yield return Idle(20); iw = hero?.Shoot?.ActiveWeapon; }
            else Fact("F_keyPathTook", "true");
            Check(fails, "ak_inPack", iw != null, "pack ActiveWeapon null after F");
            Describe("AK_HAND", iw); DescribeAnim("AK_HAND"); CamFacts("AK_HAND"); PixelWitness("AK_HAND", iw);
            yield return Shot("01_ak_in_hand");
            if (iw != null)
            {
                Check(fails, "ak_parentIsHand", iw.transform.parent != null && iw.transform.parent.name.ToLowerInvariant().Contains("hand"), $"parent={(iw.transform.parent != null ? iw.transform.parent.name : "<none>")}");
                var rs = iw.GetComponentsInChildren<Renderer>(true);
                Check(fails, "ak_renderersEnabled", rs.Length > 0 && rs.All(r => r.enabled), $"renderers={rs.Length}");
                Check(fails, "ak_scaleSane", iw.transform.lossyScale.x > 0.3f && iw.transform.lossyScale.x < 3f, $"lossy={V(iw.transform.lossyScale)}");
                Check(fails, "ak_nearRoot", _game.Rig != null && Vector3.Distance(iw.transform.position, _game.Rig.Root.position) < 2.5f, "weapon far from hero");
                Check(fails, "ak_barShows", _game.WeaponBar != null && _game.WeaponBar.Index == 4, $"bar index={_game.WeaponBar?.Index}");
            }

            // ---- aim ----
            yield return HoldMouseReal(0.8f, false, true);
            PressMouse(false, true); yield return null;
            DescribeAnim("AK_AIM"); CamFacts("AK_AIM");
            Check(fails, "ak_aims", hero?.Aim != null && hero.Aim.IsAiming, "Aim.IsAiming false while RMB held");
            yield return Shot("02_ak_aim");

            // ---- aim PITCH: the pack's OnAnimatorIK (relayed by CoverShootIkRelay) must
            // pitch the torso with the camera, so the gun in the hand rises when the
            // camera looks up and drops when it looks down. Measured off the
            // weapon's height over the hips, not off a bone's local axis.
            {
                var rig = _game.Rig; Transform hipsB = rig.Anim.GetBoneTransform(HumanBodyBones.Hips);
                float GunRise() => iw != null && hipsB != null ? iw.transform.position.y - hipsB.position.y : 0f;
                float fwdUp = 0f, fwdDown = 0f;
                // FootLookPitch is positive looking DOWN (GameLoop.TickChaseCamera's aimDir) — proven by frame, not assumed
                _game.FootLookPitch = -0.6f;
                for (int i = 0; i < 12; i++) { PressMouse(false, true); yield return null; }
                float camUpY = _game.MainCamera.transform.forward.y;
                float riseUp = GunRise(); fwdUp = iw != null ? iw.transform.forward.y : 0f;
                yield return Shot("02b_ak_aim_up");
                _game.FootLookPitch = 0.6f;
                for (int i = 0; i < 12; i++) { PressMouse(false, true); yield return null; }
                float camDownY = _game.MainCamera.transform.forward.y;
                float riseDown = GunRise(); fwdDown = iw != null ? iw.transform.forward.y : 0f;
                yield return Shot("02c_ak_aim_down");
                Check(fails, "cam_pitch_sign", camUpY > 0.2f && camDownY < -0.2f, $"camera forward.y up={F(camUpY)} down={F(camDownY)}");
                _game.FootLookPitch = 0f;
                for (int i = 0; i < 6; i++) { PressMouse(false, true); yield return null; }
                Fact("AK_PITCH", $"ikRelayCalls={hero?.IkRelay?.Calls ?? -1} ikApplied={hero?.Shoot?.IkApplied ?? -1} aimBlocked={(hero?.Shoot != null ? hero.Shoot.IsAimBlocked.ToString().ToLowerInvariant() : "-")} riseUp={F(riseUp)} riseDown={F(riseDown)} fwdUp={F(fwdUp)} fwdDown={F(fwdDown)} camPitchDeg={F(_game.MainCamera.transform.eulerAngles.x)}");
                Check(fails, "ak_ik_relayed", hero?.IkRelay != null && hero.IkRelay.Calls > 0, "CoverShootIkRelay.OnAnimatorIK never ran — IK pass off or relay not on the Animator's object");
                Check(fails, "ak_ik_applied", hero?.Shoot != null && hero.Shoot.IkApplied > 0, "ShootBehaviour.OnAnimatorIK ran but never applied (aim off, no weapon, or blocked)");
                Check(fails, "ak_aim_pitch_follows", riseUp - riseDown > 0.08f, $"gun height over hips up={F(riseUp)} down={F(riseDown)} — the torso does not pitch with the camera");
            }

            // ---- shoot (RMB held + LMB) ----
            int magBefore = iw != null ? iw.Mag : -1; bool flash = false; float pitchBeforeShots = _game.FootLookPitch;
            { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < 0.6f) { PressMouse(true, true); yield return null; if (hero?.Shoot != null && hero.Shoot.muzzleFlash != null && hero.Shoot.muzzleFlash.activeSelf) flash = true; } }
            PressMouse(false, true); yield return Idle(5);
            int magAfter = iw != null ? iw.Mag : -1;
            Fact("AK_SHOOT_mag", $"{magBefore}->{magAfter} flash={flash.ToString().ToLowerInvariant()}");
            DescribeAnim("AK_SHOOT");
            Check(fails, "ak_shoots", magAfter >= 0 && magAfter < magBefore, $"mag {magBefore}->{magAfter}");
            Fact("AK_RECOIL", $"pitchBefore={F(pitchBeforeShots)} pitchAfter={F(_game.FootLookPitch)} recoilLeft={F(_game.CsRecoilDeg)}");
            Check(fails, "ak_recoil_kicks_camera", _game.FootLookPitch < pitchBeforeShots - 0.01f, $"look pitch {F(pitchBeforeShots)}->{F(_game.FootLookPitch)} (positive = down) — the pack's recoilAngle never lifted the camera (CoverShootBridge.CameraKick)");
            yield return Shot("03_ak_shoot");
            PressMouse(false, false); yield return Idle(10);

            // ---- holster AK to the back (digit 5 = rifle category) ----
            yield return WaitReal(0.4f); yield return Tap(Key.Digit5); yield return Idle(25);
            Describe("AK_BACK", iw); DescribeAnim("AK_BACK"); PixelWitness("AK_BACK", iw);
            Check(fails, "ak_holstered", hero?.Shoot != null && hero.Shoot.ActiveWeapon == null, "still active after 5");
            if (iw != null)
            {
                Check(fails, "ak_onBody", iw.gameObject.activeInHierarchy && iw.GetComponentsInChildren<Renderer>(true).All(r => r.enabled) && _game.Rig != null && Vector3.Distance(iw.transform.position, _game.Rig.Root.position) < 2.5f, "AK hidden or away from the body after holster");
                Check(fails, "ak_onBack", iw.transform.parent != null && iw.transform.parent.name.ToLowerInvariant().Contains("back"), $"parent={(iw.transform.parent != null ? iw.transform.parent.name : "<none>")}");
            }
            yield return Shot("04_ak_on_back");
            yield return WaitReal(0.4f); yield return Tap(Key.Digit5); yield return Idle(20);
            Describe("AK_REDRAW", iw);
            Check(fails, "ak_redraw", hero?.Shoot != null && hero.Shoot.ActiveWeapon == iw, "5 again did not redraw");

            // ---- pistol: F, then 2 holsters it to the belt ----
            var pistolLoot = FindLoot("beretta") ?? FindLoot("makarov");
            Check(fails, "pistolLootFound", pistolLoot != null, "no pistol PickupTag");
            InteractiveWeapon pw = null;
            if (pistolLoot != null)
            {
                yield return PinAt(pistolLoot, 0.9f);
                yield return Tap(Key.F); yield return Idle(20);
                pw = hero?.Shoot?.ActiveWeapon;
                if (pw == null || pw == iw) { _game.OnTakePickup?.Invoke(); yield return Idle(20); pw = hero?.Shoot?.ActiveWeapon; }
                Describe("PISTOL_HAND", pw); DescribeAnim("PISTOL_HAND"); PixelWitness("PISTOL_HAND", pw);
                Check(fails, "pistol_inPack", pw != null && pw != iw, "pistol not active after F");
                Describe("AK_WHILE_PISTOL", iw);
                yield return Shot("05_pistol_in_hand");
                yield return WaitReal(0.4f); yield return Tap(Key.Digit2); yield return Idle(25);
                Describe("PISTOL_BELT", pw); DescribeAnim("PISTOL_BELT"); PixelWitness("PISTOL_BELT", pw);
                Check(fails, "pistol_onBelt", pw != null && pw.transform.parent != null && pw.transform.parent.name.ToLowerInvariant().Contains("holster"), $"parent={(pw != null && pw.transform.parent != null ? pw.transform.parent.name : "<none>")}");
                yield return Shot("06_pistol_on_belt");
            }
            Debug.Log(fails.Count == 0 ? "PLAYMODE RESULT=PASS" : "PLAYMODE RESULT=FAIL");
            if (fails.Count > 0) Assert.Fail("GunHandRun failures: " + string.Join(" | ", fails));
        }
    }
}
#endif
