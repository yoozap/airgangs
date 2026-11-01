using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace TbilisiFly.Tools
{
    /// <summary>
    /// THE MEMORY CENSUS. One place that prints `MEMORY key=value` lines for
    /// every byte class that can get big in this project, so a cut can be
    /// proven instead of asserted.
    ///
    /// It runs in three places and prints the SAME keys in all three:
    ///   * the built player, from <see cref="MemoryCensusRunner"/>, which
    ///     <see cref="Install"/> spawns when the env var TF_MEMCENSUS=1 — no
    ///     scene edit, so the census cannot change what the scene builds;
    ///   * an editor probe (Editor/Probes/MemoryCensusProbe.cs) after a real
    ///     Game.unity load;
    ///   * nowhere else. Nothing subscribes, nothing ticks it.
    ///
    /// Why not `ps rss`: this machine swaps and macOS compresses, so RSS read
    /// 216 MB for a process whose `footprint` said 3572 MB. The in-process
    /// Profiler counters and `Profiler.GetRuntimeMemorySizeLong` per object are
    /// the numbers that move when an import setting changes.
    /// </summary>
    public static class MemoryCensus
    {
        const double MB = 1024.0 * 1024.0;
        const double GB = 1024.0 * 1024.0 * 1024.0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Environment.GetEnvironmentVariable("TF_MEMCENSUS") != "1") return;
            var go = new GameObject("MemoryCensusRunner");
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<MemoryCensusRunner>();
            Debug.Log("MEMORY installed=true");
        }

        /// <summary>Prints the whole census. `phase` tags every line so a log
        /// with a startup and a 60 s block can be grepped apart.</summary>
        public static void Print(string phase)
        {
            Totals(phase);
            ByType(phase);
            Textures(phase);
            Meshes(phase);
            Audio(phase);
            Buffers(phase);
        }

        /// <summary>Just the allocator totals, cheap enough to call at every
        /// build stage. THE NUMBER THAT MATTERS FOR THE OS FOOTPRINT IS
        /// native_reserved: Unity's allocator never hands pages back, so the
        /// process footprint is set by the PEAK, not by what is live. Measured
        /// here: a build whose live total fell 1.30 GB left reserved at
        /// 2.611 GB and the OS footprint only 219 MB lower. So the thing to
        /// find is which stage RAISES reserved.</summary>
        public static void Totals(string phase)
        {
            long allocated = Profiler.GetTotalAllocatedMemoryLong();
            long reserved = Profiler.GetTotalReservedMemoryLong();
            long unused = Profiler.GetTotalUnusedReservedMemoryLong();
            long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long mono = Profiler.GetMonoUsedSizeLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long ws = 0;
            try { ws = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; } catch { }

            Debug.Log($"MEMORY phase={phase} native_allocated_gb={allocated / GB:F3} " +
                      $"native_reserved_gb={reserved / GB:F3} native_unused_gb={unused / GB:F3} " +
                      $"graphics_driver_gb={gfx / GB:F3} managed_used_gb={mono / GB:F3} " +
                      $"managed_heap_gb={monoHeap / GB:F3} working_set_gb={ws / GB:F3} " +
                      $"total_resident_gb={(reserved + gfx) / GB:F3}");
        }

        /// <summary>Every loaded UnityEngine.Object grouped by type, summed
        /// with the per-object runtime size. This is the table that says where
        /// the gigabytes are; the specific lists below only name the culprits.</summary>
        static void ByType(string phase)
        {
            var all = Resources.FindObjectsOfTypeAll<Object>();
            var bytes = new Dictionary<string, long>();
            var count = new Dictionary<string, int>();
            foreach (var o in all)
            {
                if (o == null) continue;
                string t = o.GetType().Name;
                long n = 0;
                try { n = Profiler.GetRuntimeMemorySizeLong(o); } catch { }
                bytes.TryGetValue(t, out long b); bytes[t] = b + n;
                count.TryGetValue(t, out int c); count[t] = c + 1;
            }
            long total = 0;
            foreach (var kv in bytes) total += kv.Value;
            Debug.Log($"MEMORY phase={phase} objects={all.Length} object_bytes_gb={total / GB:F3}");
            var keys = new List<string>(bytes.Keys);
            keys.Sort((a, b) => bytes[b].CompareTo(bytes[a]));
            for (int i = 0; i < keys.Count && i < 30; i++)
                Debug.Log($"MEMORY phase={phase} type={keys[i]} n={count[keys[i]]} mb={bytes[keys[i]] / MB:F1}");
        }

        static void Textures(string phase)
        {
            var all = Resources.FindObjectsOfTypeAll<Texture>();
            long total = 0; long readable = 0; int nread = 0;
            var rows = new List<(long sz, string line)>();
            foreach (var t in all)
            {
                if (t == null) continue;
                long n = 0;
                try { n = Profiler.GetRuntimeMemorySizeLong(t); } catch { }
                total += n;
                string fmt = t is Texture2D t2 ? t2.format.ToString() : t.GetType().Name;
                bool rd = false;
                if (t is Texture2D t2b) { rd = t2b.isReadable; }
                if (rd) { readable += n; nread++; }
                rows.Add((n, $"name={Safe(t.name)} mb={n / MB:F2} fmt={fmt} w={t.width} h={t.height} " +
                              $"mips={t.mipmapCount} readable={rd.ToString().ToLowerInvariant()}"));
            }
            rows.Sort((a, b) => b.sz.CompareTo(a.sz));
            Debug.Log($"MEMORY phase={phase} textures={all.Length} textures_gb={total / GB:F3} " +
                      $"readable_textures={nread} readable_textures_mb={readable / MB:F1}");
            for (int i = 0; i < rows.Count && i < 30; i++)
                Debug.Log($"MEMORY phase={phase} tex{i:D2} {rows[i].line}");
        }

        static void Meshes(string phase)
        {
            var all = Resources.FindObjectsOfTypeAll<Mesh>();
            long total = 0, rdBytes = 0; int nread = 0; long verts = 0;
            var rows = new List<(long sz, string line)>();
            foreach (var m in all)
            {
                if (m == null) continue;
                long n = 0;
                try { n = Profiler.GetRuntimeMemorySizeLong(m); } catch { }
                total += n;
                bool rd = false; int vc = 0; int sub = 0;
                try { rd = m.isReadable; vc = m.vertexCount; sub = m.subMeshCount; } catch { }
                verts += vc;
                if (rd) { rdBytes += n; nread++; }
                rows.Add((n, $"name={Safe(m.name)} mb={n / MB:F2} verts={vc} sub={sub} " +
                             $"readable={rd.ToString().ToLowerInvariant()}"));
            }
            rows.Sort((a, b) => b.sz.CompareTo(a.sz));
            Debug.Log($"MEMORY phase={phase} meshes={all.Length} meshes_gb={total / GB:F3} " +
                      $"readable_meshes={nread} readable_meshes_mb={rdBytes / MB:F1} total_verts={verts}");
            for (int i = 0; i < rows.Count && i < 30; i++)
                Debug.Log($"MEMORY phase={phase} mesh{i:D2} {rows[i].line}");

            // ---- the table that says WHICH LAYER owns the gigabytes ---------
            // The top-30 list above is useless for 13,350 road tiles called
            // road_asphalt_12_7: they are each 0.05 MB and none of them makes
            // the list, while together they are the biggest thing in the
            // process. Grouping by the name with its trailing _<digits> stripped
            // is what turned "1,983 MB of meshes" into "four retained monolith
            // ribbons, 667 MB".
            var gBytes = new Dictionary<string, long>();
            var gCount = new Dictionary<string, int>();
            var gVerts = new Dictionary<string, long>();
            var gImported = new Dictionary<string, int>();
            foreach (var m in all)
            {
                if (m == null) continue;
                string key = GroupKey(m.name);
                long n = 0; int vc = 0;
                try { n = Profiler.GetRuntimeMemorySizeLong(m); } catch { }
                try { vc = m.vertexCount; } catch { }
                gBytes.TryGetValue(key, out long b); gBytes[key] = b + n;
                gCount.TryGetValue(key, out int c); gCount[key] = c + 1;
                gVerts.TryGetValue(key, out long v); gVerts[key] = v + vc;
                bool imp = false;
                try { imp = TbilisiFly.Core.ArtAssets.IsPersistent(m); } catch { }
                gImported.TryGetValue(key, out int ic); gImported[key] = ic + (imp ? 1 : 0);
            }
            var gk = new List<string>(gBytes.Keys);
            gk.Sort((a, b) => gBytes[b].CompareTo(gBytes[a]));
            for (int i = 0; i < gk.Count && i < 30; i++)
                Debug.Log($"MEMORY phase={phase} meshgrp{i:D2} name={gk[i]} n={gCount[gk[i]]} " +
                          $"mb={gBytes[gk[i]] / MB:F1} verts={gVerts[gk[i]]} imported={gImported[gk[i]]}");
        }

        /// <summary>"road_asphalt_12_-7" -> "road_asphalt"; "chunk_304" ->
        /// "chunk". Trailing numeric/sign segments only, so "block_6" (a real
        /// distinct kit mesh) collapses with block_5 — which is what we want
        /// for a per-LAYER table.</summary>
        static string GroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "(unnamed)";
            string s = name;
            while (true)
            {
                int u = s.LastIndexOf('_');
                if (u <= 0 || u == s.Length - 1) break;
                bool numeric = true;
                for (int i = u + 1; i < s.Length; i++)
                    if (!char.IsDigit(s[i]) && s[i] != '-') { numeric = false; break; }
                if (!numeric) break;
                s = s.Substring(0, u);
            }
            return s.Replace(' ', '_');
        }

        static void Audio(string phase)
        {
            var all = Resources.FindObjectsOfTypeAll<AudioClip>();
            long total = 0; double secs = 0;
            var rows = new List<(long sz, string line)>();
            foreach (var c in all)
            {
                if (c == null) continue;
                long n = 0;
                try { n = Profiler.GetRuntimeMemorySizeLong(c); } catch { }
                total += n; secs += c.length;
                rows.Add((n, $"name={Safe(c.name)} mb={n / MB:F2} load={c.loadType} " +
                             $"sec={c.length:F2} ch={c.channels} hz={c.frequency} " +
                             $"loaded={(c.loadState == AudioDataLoadState.Loaded).ToString().ToLowerInvariant()}"));
            }
            rows.Sort((a, b) => b.sz.CompareTo(a.sz));
            Debug.Log($"MEMORY phase={phase} audioclips={all.Length} audio_gb={total / GB:F3} audio_seconds={secs:F0}");
            for (int i = 0; i < rows.Count && i < 25; i++)
                Debug.Log($"MEMORY phase={phase} clip{i:D2} {rows[i].line}");
        }

        /// <summary>The CPU-side instance buffers the instanced renderers keep,
        /// plus the physics cook count. These are managed arrays, so they are
        /// inside managed_used_gb and invisible in the per-object table.</summary>
        static void Buffers(string phase)
        {
            int meshColliders = 0, convex = 0;
            long colliderVerts = 0;
            var cols = Resources.FindObjectsOfTypeAll<MeshCollider>();
            foreach (var c in cols)
            {
                if (c == null) continue;
                meshColliders++;
                if (c.convex) convex++;
                var m = c.sharedMesh;
                if (m != null) { try { colliderVerts += m.vertexCount; } catch { } }
            }
            Debug.Log($"MEMORY phase={phase} meshcolliders={meshColliders} convex={convex} " +
                      $"collider_verts={colliderVerts} cooked_est_mb={colliderVerts * 48.0 / MB:F1}");

            // ---- THE PROOF FOR THE FROZEN COLLIDABLE ROAD TILES -------------
            // RoadNetworkRenderer.Surface calls UploadMeshData(true) on the
            // 4,791 collidable asphalt tiles AFTER their MeshCollider has
            // cooked, which saves 148 MB of CPU-side vertices. The claim that
            // PhysX keeps its own cooked copy is worth nothing unless a ray
            // still hits one, and the play-mode suite cannot check it (the
            // freeze is player-only, by design). So: pick MeshColliders whose
            // mesh is NOT readable, fire a ray straight down through the
            // collider's own bounds centre, and report whether it hit THAT
            // collider. frozen_hit must equal frozen_tried.
            int tried = 0, hit = 0, frozen = 0;
            foreach (var c in cols)
            {
                if (c == null || c.sharedMesh == null) continue;
                bool rd = true;
                try { rd = c.sharedMesh.isReadable; } catch { }
                if (rd) continue;
                frozen++;
                if (tried >= 8) continue;
                var b = c.bounds;
                if (b.size.sqrMagnitude <= 0f) continue;
                tried++;
                // An OverlapBox over the collider's OWN bounds, not a ray down
                // through the bounds centre: a 1500 m road tile's centre is
                // almost never on the ribbon, so the first version of this
                // check fired 8 rays into empty sky and read 2/8 — a badly
                // aimed test, not a broken collider. An overlap asks PhysX
                // whether this collider has cooked geometry at all, which is
                // exactly the claim.
                var found = Physics.OverlapBox(b.center, b.extents, Quaternion.identity,
                                               ~0, QueryTriggerInteraction.Ignore);
                foreach (var f in found) if (f == c) { hit++; break; }
            }
            Debug.Log($"MEMORY phase={phase} frozen_collider_meshes={frozen} frozen_tried={tried} " +
                      $"frozen_hit={hit} frozen_ok={(tried > 0 && hit == tried).ToString().ToLowerInvariant()}");

            var sb = new StringBuilder();
            sb.Append($"MEMORY phase={phase} instbuf");
            foreach (var r in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (r == null) continue;
                var tn = r.GetType().Name;
                if (tn != "CityRenderer" && tn != "VegetationRenderer" && tn != "CityFacadeRenderer") continue;
                long n = 0;
                foreach (var f in r.GetType().GetFields(System.Reflection.BindingFlags.Instance |
                                                        System.Reflection.BindingFlags.Public |
                                                        System.Reflection.BindingFlags.NonPublic))
                {
                    object v;
                    try { v = f.GetValue(r); } catch { continue; }
                    if (v is Array a)
                    {
                        var et = f.FieldType.GetElementType();
                        if (et != null && et.IsValueType)
                        {
                            try { n += (long)a.Length * System.Runtime.InteropServices.Marshal.SizeOf(et); } catch { }
                        }
                    }
                }
                sb.Append($" {tn}_mb={n / MB:F1}");
            }
            Debug.Log(sb.ToString());
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(unnamed)";
            return s.Replace(' ', '_');
        }
    }
}
