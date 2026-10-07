// Export the hand-modeled Georgian landmark models as real .obj files.
// Run: node tools/export-obj.mjs   ->  public/models/landmarks/*.obj
import { mkdirSync, writeFileSync } from 'fs';

// landmarks.js paints canvas textures; stub the DOM so it runs under Node.
globalThis.document = {
  createElement: () => ({
    width: 0,
    height: 0,
    getContext: () => new Proxy({}, { get: () => () => ({ addColorStop() {} }) }),
  }),
};

const { OBJExporter } = await import('three/examples/jsm/exporters/OBJExporter.js');
const { BUILDERS } = await import('../src/landmarks.js');

mkdirSync('public/models/landmarks', { recursive: true });
const exporter = new OBJExporter();
for (const [name, build] of Object.entries(BUILDERS)) {
  const obj = name === 'ship' ? build('container') : build();
  obj.updateMatrixWorld(true);
  writeFileSync(`public/models/landmarks/${name}.obj`, exporter.parse(obj));
  console.log(`${name}.obj`);
}
