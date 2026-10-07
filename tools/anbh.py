"""
Sampler for Copernicus GHSL GHS-BUILT-H ANBH tiles (average net building
height, 100 m, Mollweide): real satellite-derived building heights used as the
height source for the ~85% of Tbilisi buildings with no OSM height tags.

Loads the R4/R5 C22 tiles lazily and keeps only small windows around the
requested points cached, so memory stays low.
"""
import glob
import math
import os

# ESRI:54009 World Mollweide treats the WGS84 SEMI-MAJOR axis as the sphere
# radius — NOT the authalic radius 6371007 (that one lands ~5 km off and reads
# empty cells under the whole city; verified against 3401 OSM-tagged towers).
R_SPHERE = 6378137.0


def _mollweide(lat, lon):
    phi = math.radians(lat)
    lam = math.radians(lon)
    th = phi
    for _ in range(50):
        f = 2 * th + math.sin(2 * th) - math.pi * math.sin(phi)
        th -= f / (2 + 2 * math.cos(2 * th))
    return (R_SPHERE * (2 * math.sqrt(2) / math.pi) * lam * math.cos(th),
            R_SPHERE * math.sqrt(2) * math.sin(th))


class ANBH:
    def __init__(self, cache_dir):
        import rasterio  # local import: builder must work without it
        self._srcs = [rasterio.open(p) for p in
                      sorted(glob.glob(os.path.join(cache_dir, 'GHS_BUILT_H_ANBH_*.tif')))]
        if not self._srcs:
            raise FileNotFoundError('no GHS_BUILT_H_ANBH tiles in cache')
        self._blocks = {}  # (src_idx, br, bc) -> ndarray block
        self.BS = 512

    def sample(self, lat, lon):
        """Max ANBH (m) in the 3x3 cells around the point, or 0."""
        x, y = _mollweide(lat, lon)
        for si, src in enumerate(self._srcs):
            b = src.bounds
            if not (b.left <= x < b.right and b.bottom < y <= b.top):
                continue
            r, c = src.index(x, y)
            best = 0.0
            for rr in range(r - 1, r + 2):
                for cc in range(c - 1, c + 2):
                    if not (0 <= rr < src.height and 0 <= cc < src.width):
                        continue
                    v = self._read(si, src, rr, cc)
                    if v is not None and v != 255 and v > best:
                        best = float(v)
            return best
        return 0.0

    def _read(self, si, src, r, c):
        br, bc = r // self.BS, c // self.BS
        key = (si, br, bc)
        blk = self._blocks.get(key)
        if blk is None:
            win = ((br * self.BS, min((br + 1) * self.BS, src.height)),
                   (bc * self.BS, min((bc + 1) * self.BS, src.width)))
            blk = src.read(1, window=win)
            self._blocks[key] = blk
        rr, cc = r - br * self.BS, c - bc * self.BS
        if rr < blk.shape[0] and cc < blk.shape[1]:
            return blk[rr, cc]
        return None
