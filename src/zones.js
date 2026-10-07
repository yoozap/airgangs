// Zones — the real OSM geography of Tbilisi on two levels: 112 neighborhood
// polygons (Sololaki, Rike, Abanotubani…) inside the 10 raioni. Crossing into
// a new neighborhood slams a WoW-style zone title in at the bottom of the
// screen (subzone big, its raion as the tag line); a small persistent label
// above the minimap always answers "where am I". Outside everything you're
// in the outskirts.

const OUTSKIRTS = { name: 'თბილისის შემოგარენი', tag: 'OUTSKIRTS // GEORGIA' };

export class Zones {
  constructor() {
    this.meta = null;
    this.districts = null; // [{name, tag, rings:[[x,z]...], bbox}] in world coords
    this.subzones = null; // same shape, area-ascending: first hit = most specific
    this.current = undefined; // most specific zone; undefined = not resolved yet
    this.currentDistrict = null; // the raion the player is in (map highlight)

    // a new Game builds a new Zones — drop leftovers from the previous run
    document.getElementById('zone-label')?.remove();
    document.getElementById('zone-announce')?.remove();

    const hud = document.getElementById('hud');
    this.label = document.createElement('div');
    this.label.id = 'zone-label';
    hud.appendChild(this.label);

    this.announce = document.createElement('div');
    this.announce.id = 'zone-announce';
    this.announce.innerHTML = '<div id="za-name"></div><div id="za-tag"></div>';
    hud.appendChild(this.announce);
    this._annTimer = null;

    Promise.all([
      fetch('tbilisi/mapmeta.json').then((r) => (r.ok ? r.json() : null)),
      fetch('tbilisi/districts.json').then((r) => (r.ok ? r.json() : null)),
      fetch('tbilisi/subzones.json').then((r) => (r.ok ? r.json() : null)).catch(() => null),
    ])
      .then(([meta, districts, subzones]) => {
        if (!meta || !districts?.length) return;
        this.meta = meta;
        const toWorld = (d) => {
          const rings = d.rings.map((ring) =>
            ring.map(([lat, lon]) => [
              -(lon - meta.origin.lon) * meta.mlon,
              (lat - meta.origin.lat) * meta.mlat,
            ])
          );
          let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
          for (const ring of rings) {
            for (const [x, z] of ring) {
              if (x < minX) minX = x;
              if (x > maxX) maxX = x;
              if (z < minZ) minZ = z;
              if (z > maxZ) maxZ = z;
            }
          }
          return { name: d.name, tag: d.tag, rings, bbox: [minX, minZ, maxX, maxZ] };
        };
        this.districts = districts.map(toWorld);
        this.subzones = (subzones ?? []).map(toWorld);
      })
      .catch(() => {});
  }

  _inRing(ring, x, z) {
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [xi, zi] = ring[i];
      const [xj, zj] = ring[j];
      if (zi > z !== zj > z && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
    }
    return inside;
  }

  _hit(list, x, z) {
    for (const d of list) {
      const [minX, minZ, maxX, maxZ] = d.bbox;
      if (x < minX || x > maxX || z < minZ || z > maxZ) continue;
      if (d.rings.some((ring) => this._inRing(ring, x, z))) return d;
    }
    return null;
  }

  // {sub, dist} — the smallest neighborhood and the raion containing (x, z)
  zoneAt(x, z) {
    if (!this.districts) return undefined;
    return { sub: this._hit(this.subzones ?? [], x, z), dist: this._hit(this.districts, x, z) };
  }

  update(x, z) {
    const zone = this.zoneAt(x, z);
    if (zone === undefined) return; // still loading
    const { sub, dist } = zone;
    const specific = sub ?? dist;
    this.currentDistrict = dist;
    const name = specific?.name ?? null;
    const curName = this.current?.name ?? null;
    if (this.current !== undefined && name === curName) return;
    const first = this.current === undefined;
    this.current = specific;
    const show = specific
      ? { name: specific.name, tag: sub ? `${sub.tag} // ${dist?.tag ?? 'TBILISI'}` : `${dist.tag} // TBILISI` }
      : OUTSKIRTS;
    this.label.textContent = show.name;
    // Entering the outskirts right at start (spawn glitch) isn't worth a slam.
    if (!(first && !specific)) this._announce(show);
  }

  _announce({ name, tag }) {
    document.getElementById('za-name').textContent = name;
    document.getElementById('za-tag').textContent = `■ ${tag}`;
    this.announce.classList.remove('show');
    void this.announce.offsetWidth; // restart the CSS animation
    this.announce.classList.add('show');
    clearTimeout(this._annTimer);
    this._annTimer = setTimeout(() => this.announce.classList.remove('show'), 5200);
  }
}
