import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { DRACOLoader } from 'three/addons/loaders/DRACOLoader.js';

// ONE place that builds a GLTFLoader, with Draco wired in.
//
// This exists because of a trap that has already cost this project time: the
// loader has no Draco support unless a DRACOLoader is attached, and a
// draco-compressed GLB then fails **silently** -- the load resolves to nothing
// and the model never appears. With two dozen separate loader constructions
// dotted through the source, attaching it in one place is the only way that
// stays true as files get added.
//
// Geometry is 79% of the 235 MB of models here, so Draco is where the download
// actually shrinks; the decoder is ~290 KB of wasm, fetched once and shared.
const draco = new DRACOLoader().setDecoderPath('/draco/');

export function newGltfLoader() {
  const loader = new GLTFLoader();
  loader.setDRACOLoader(draco);
  return loader;
}

export function disposeGltf() {
  draco.dispose();
}
