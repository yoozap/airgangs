// THE AKM ANIMATION, off the Sketchfab viewmodel (same author, same layout as the pistol one).
//
//   node tools/import_akm_arms.mjs
//
// Same download as tools/import_pistols.py, kept for the opposite reason. That
// one wants a static gun to lie on a bench, so it throws the rig away; this one
// throws the gun's SHAPE away and keeps the rig: the arms-only Mixamo skeleton
// (44 bones with full fingers) and the one 16.8 s clip that holds all seven
// moves end to end -- draw, idle, fire, inspect, two reloads, holster.
//
// Nothing of the demo man is drawn in the game. The personage you are playing
// has its own arms -- a banana's are yellow and bend in its own places -- and
// src/gunanim.js reads the clip's joint positions each frame and lays the
// hero's own bones along the same lines. So the ARMS MESH goes: it is 4 of the
// 5 textures and most of the 8 MB, for geometry that never reaches the screen.
//
// The GUN mesh stays, small and at 256px, and it is not for drawing either:
// src/gunanim.js measures the barrel off it once at load to find which way the
// clip points, which is what lets the hero's arms be squared to the player's
// own aim line rather than to the artist's world axes.
//
// No Blender round trip on purpose -- that is what keeps the skinning and the
// 132 animation channels bit-exact.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { textureCompress, prune, dedup } from '@gltf-transform/functions';
import sharp from 'sharp';

const SRC = path.join(os.homedir(), 'Downloads', 'akm_fps_animation.glb');
const OUT = 'public/models/akm_arms.glb';
const DROP = /ch22|body|textura|magazine|barrel|stock|sight|handgaurd|handguard|bullet|casing/i;   // the demo man and the heavy rifle parts: rig kept, skins dropped (the grip stays, small)
const MAX_TEX = 256;

if (!fs.existsSync(SRC)) {
  console.error(`[anim] ${SRC} not found -- re-download the Sketchfab model:\n`
    + '       https://sketchfab.com/3d-models/akm-fps-animation-a0e1e5205129');
  process.exit(1);
}

const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);
const doc = await io.read(SRC);
const root = doc.getRoot();
const before = fs.statSync(SRC).size;

const anim = root.listAnimations()[0];
console.log(`[anim] in : ${root.listMeshes().map((m) => m.getName()).join(', ')} | `
  + `${root.listSkins().map((s) => s.listJoints().length).join(' + ')} joints | `
  + `"${anim?.getName()}" ${anim?.listChannels().length} channels`);

// Drop the arms MESH only. The nodes stay: they are the skeleton the clip
// animates, and detaching a joint would silently kill half the channels.
let dropped = 0;
for (const mesh of root.listMeshes()) {
  if (!DROP.test(mesh.getName() || '')) continue;
  for (const node of root.listNodes()) {
    if (node.getMesh() === mesh) { node.setMesh(null); node.setSkin(null); }
  }
  mesh.dispose();
  dropped++;
}

await doc.transform(
  textureCompress({ encoder: sharp, targetFormat: 'jpeg', quality: 80, resize: [MAX_TEX, MAX_TEX] }),
  dedup(),
  // keepLeaves, or prune walks off with the finger bones -- they carry no mesh
  // and it cannot see that the animation is pointing at them.
  prune({ keepLeaves: true, keepAttributes: true }),
);

await io.write(OUT, doc);
const out = await io.read(OUT);
const kept = out.getRoot();
const after = fs.statSync(OUT).size;
console.log(`[anim] out: ${OUT}  ${(before / 1048576).toFixed(1)} MB -> ${(after / 1024).toFixed(0)} KB`);
console.log(`[anim]      dropped ${dropped} mesh, kept ${kept.listMeshes().length} `
  + `(${kept.listMeshes().map((m) => m.getName()).join(', ')}), `
  + `${kept.listNodes().length} nodes, `
  + `"${kept.listAnimations()[0]?.getName()}" ${kept.listAnimations()[0]?.listChannels().length} channels`);
