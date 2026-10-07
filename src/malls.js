// Real Tbilisi malls & market halls: Blender-built on their exact OSM
// footprints (tools/build_malls.py -> models/malls.glb, one node per mall
// named mall_<bid>), placed at their true coordinates with the REAL name on
// a lit sign over the entrance. Each mall is a destructible landmark — same
// HP/crumble path as parliament or the hotels. The OSM box extrusion for
// these bids is suppressed in _buildBuildings via citymap._mallBids.
import * as THREE from 'three';
import { newGltfLoader } from './gltf.js';

const label = (text, w, h, bg, fg, size = 48) => {
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const g = c.getContext('2d');
  g.fillStyle = bg;
  g.fillRect(0, 0, w, h);
  g.fillStyle = fg;
  let s = size;
  g.font = `700 ${s}px "Noto Sans Georgian", sans-serif`;
  while (g.measureText(text).width > w - 30 && s > 18) {
    s -= 4;
    g.font = `700 ${s}px "Noto Sans Georgian", sans-serif`;
  }
  g.textAlign = 'center';
  g.textBaseline = 'middle';
  g.fillText(text, w / 2, h / 2 + 2);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return t;
};

export async function addMalls(citymap) {
  const list = citymap._mallList;
  if (!list?.length) return;
  let gltf;
  try {
    gltf = await new Promise((ok, bad) =>
      newGltfLoader().load('models/malls.glb', ok, undefined, bad));
  } catch (e) {
    console.warn('malls.glb failed:', e?.message ?? e);
    return;
  }
  gltf.scene.updateMatrixWorld(true);
  const nodes = new Map(); // bid -> node (Group or Mesh, one per mall)
  gltf.scene.traverse((o) => {
    const m = o.name.match(/^mall_(\d+)$/);
    if (m) nodes.set(+m[1], o);
  });
  const dupes = new Map();
  let placed = 0;
  for (const e of list) {
    const node = nodes.get(e.bid);
    if (!node) continue;
    const root = new THREE.Group();
    root.add(node); // node transforms are identity — geometry is world-fitted
    node.traverse((o) => {
      if (o.isMesh) {
        o.castShadow = true;
        o.receiveShadow = true;
        (Array.isArray(o.material) ? o.material : [o.material]).forEach((mm) => {
          mm.metalness = Math.min(mm.metalness ?? 0, 0.4);
          mm.roughness = Math.max(mm.roughness ?? 0.8, 0.3);
        });
      }
    });
    const gy = citymap.heightAt(e.x, e.z) ?? 450;
    root.position.set(e.x, gy + 0.35, e.z);
    // the REAL mall name on the entrance sign plate (anchor baked in extras)
    const sign = node.userData?.sign;
    if (sign) {
      const [sx, sz, sy, yaw] = sign;
      const plane = new THREE.Mesh(
        new THREE.PlaneGeometry(Math.min(19, 3 + e.name.length * 0.85), 2.1),
        new THREE.MeshBasicMaterial({ map: label(e.name, 1024, 128, '#14181d', '#f2f4f6', 72) })
      );
      plane.position.set(sx, sy, sz);
      plane.rotation.y = yaw;
      root.add(plane);
    }
    citymap.group.add(root);
    const cnt = (dupes.get(e.name) ?? 0) + 1;
    dupes.set(e.name, cnt);
    citymap.registerLandmark(root, cnt > 1 ? `${e.name} ${cnt}` : e.name);
    placed++;
  }
  console.log(`malls: ${placed}/${list.length} placed as real destructible buildings`);
}
