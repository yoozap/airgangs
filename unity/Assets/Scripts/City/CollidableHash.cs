using System.Collections.Generic;
using UnityEngine;

namespace TbilisiFly.City
{
    /// <summary>
    /// Keep-out boxes + the collidables spatial hash — src/citymap.js:1235-1332
    /// (inKeepOut, _addCollidable, _removeCollidable, collidablesNear). Every
    /// physics ray in the game (a wall sweep, wheels, a foot probe, the 1100 m gun
    /// range, the HUD's 3000 m boresight) asks `collidablesNear` for "what's in
    /// this neighbourhood" instead of testing the whole city; this is the index
    /// that makes that cheap.
    ///
    /// PURE DATA CLASS, generic over the collidable type `T` so it has no
    /// dependency on how Unity ends up representing a chunk (a Mesh reference, a
    /// BatchRendererGroup handle, whatever the renderer settles on) — the caller
    /// supplies a world-space <see cref="Bounds"/> getter and this file does the
    /// cell bookkeeping. `T` must have reference identity (a class), matching the
    /// JS's own `indexOf`/`Set` membership tests.
    /// </summary>
    public sealed class CollidableHash<T> where T : class
    {
        public const float Cell = 1500f; // citymap.js:1249, :1287

        readonly List<T> _items = new();
        readonly System.Func<T, Bounds> _worldBounds;
        Dictionary<long, List<T>> _hash;
        Dictionary<long, int> _stamped; // per-cell-query dedup, replaces userData._colStamp
        int _stamp;
        readonly Dictionary<(int rings, long cell), List<T>> _cache = new();

        public CollidableHash(System.Func<T, Bounds> worldBounds) => _worldBounds = worldBounds;

        public IReadOnlyList<T> Items => _items;

        static long Key(int ix, int iz) => (long)ix * 2000003 + iz;

        /// <summary>citymap.js:1246-1260 (_addCollidable). Safe to call before the
        /// hash exists yet — the JS defers indexing until collidablesNear first
        /// builds it (citymap.js:1304-1324); here, the hash is built lazily the
        /// same way, so an Add before the first Near() query just appends to the
        /// list and is picked up on the next rebuild.</summary>
        public void Add(T item)
        {
            _items.Add(item);
            if (_hash == null) return; // not built yet — Near() will pick this up
            IndexOne(item);
            _cache.Clear();
        }

        /// <summary>citymap.js:1263-1277 (_removeCollidable) — for a prop that
        /// moves (devmode.js): comes out of every cell it was indexed under.</summary>
        public void Remove(T item)
        {
            _items.Remove(item);
            if (_hash != null)
                foreach (var cell in _hash.Values) cell.Remove(item);
            _cache.Clear();
        }

        void IndexOne(T item)
        {
            var box = _worldBounds(item);
            int x0 = Mathf.FloorToInt(box.min.x / Cell), x1 = Mathf.FloorToInt(box.max.x / Cell);
            int z0 = Mathf.FloorToInt(box.min.z / Cell), z1 = Mathf.FloorToInt(box.max.z / Cell);
            for (int ix = x0; ix <= x1; ix++)
            for (int iz = z0; iz <= z1; iz++)
            {
                long k = Key(ix, iz);
                if (!_hash.TryGetValue(k, out var cell)) _hash[k] = cell = new List<T>();
                cell.Add(item);
            }
        }

        void Rebuild()
        {
            _hash = new Dictionary<long, List<T>>();
            foreach (var item in _items) IndexOne(item);
            _cache.Clear();
        }

        /// <summary>citymap.js:1286-1330 (collidablesNear). `rings` 1500 m cells out
        /// from the query cell — a 3x3 (rings=1) default, up to rings=2 for long
        /// rays. Memoised per (rings, cell): the result only depends on those two
        /// things and the collidable SET, which Add/Remove invalidate.</summary>
        public IReadOnlyList<T> Near(float x, float z, int rings = 1)
        {
            if (_hash == null) Rebuild();
            int cx = Mathf.FloorToInt(x / Cell), cz = Mathf.FloorToInt(z / Cell);
            var cacheKey = (rings, Key(cx, cz));
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

            _stamp++;
            _stamped ??= new Dictionary<long, int>();
            var outList = new List<T>();
            for (int ix = cx - rings; ix <= cx + rings; ix++)
            for (int iz = cz - rings; iz <= cz + rings; iz++)
            {
                if (!_hash.TryGetValue(Key(ix, iz), out var cell)) continue;
                foreach (var m in cell)
                {
                    // dedup a mesh that spans multiple cells, without per-item
                    // state — key the "seen this query" stamp on object identity
                    int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m);
                    if (_stamped.TryGetValue(id, out int s) && s == _stamp) continue;
                    _stamped[id] = _stamp;
                    outList.Add(m);
                }
            }
            if (_cache.Count > 64) _cache.Clear();
            _cache[cacheKey] = outList;
            return outList;
        }
    }

    /// <summary>One rectangular, arbitrarily-rotated keep-out zone (the shed
    /// footprint, a landmark's forecourt, …) — citymap.js:1235-1244 (inKeepOut).
    /// (ux,uz)/(vx,vz) are the box's own unit axes (not necessarily world X/Z),
    /// halfL/halfW the half-extents along them.</summary>
    public struct KeepOutBox
    {
        public float X, Z;
        public float Ux, Uz, HalfL; // "along" axis
        public float Vx, Vz, HalfW; // "across" axis

        public bool Contains(float x, float z)
        {
            float dx = x - X, dz = z - Z;
            return Mathf.Abs(dx * Vx + dz * Vz) <= HalfW
                && Mathf.Abs(dx * Ux + dz * Uz) <= HalfL;
        }
    }

    public static class KeepOut
    {
        /// <summary>citymap.js:1235-1244 — true if (x,z) falls inside ANY zone.</summary>
        public static bool Any(IReadOnlyList<KeepOutBox> zones, float x, float z)
        {
            if (zones == null) return false;
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Contains(x, z)) return true;
            return false;
        }
    }
}
