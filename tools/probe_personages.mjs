import { chromium } from 'playwright';

// Which personages can actually RUN? Loads every body through the game's own
// CharacterRig and reports the clips it ended up with.
const URL = process.env.URL || 'http://localhost:5174';
const browser = await chromium.launch({ args: ['--use-gl=angle'] });
const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(URL);
await page.waitForTimeout(1200);
await page.evaluate(() => document.getElementById('btn-map').click());
await page.waitForFunction(() => window.__game?.started, null, { timeout: 240000 });
await page.waitForTimeout(2500);

const rows = await page.evaluate(async () => {
  const mod = await import('/src/characters.js');
  const out = [];
  for (const p of mod.PERSONAGES) {
    try {
      const rig = await mod.makeCharacter(p.id);
      const idle = rig.actions.Idle?.getClip().name ?? null;
      const run = rig.actions.Run?.getClip().name ?? null;
      // drive it exactly like OnFoot does and see whether ANYTHING moves
      rig.play('Run');
      const y0 = rig.anim.position.y;
      // clips animate bone ROTATIONS, so compare the whole skinning palette
      const pose = () => {
        let h = 0;
        rig.obj.updateMatrixWorld(true);
        rig.obj.traverse((o) => {
          if (!o.isSkinnedMesh) return;
          o.skeleton.update();
          const m = o.skeleton.boneMatrices;
          for (let i = 0; i < m.length; i += 7) h += m[i] * (i + 1);
        });
        return h;
      };
      const p0 = pose();
      for (let k = 0; k < 12; k++) { rig.update(1 / 30); rig.locomote(1 / 30, 6.5, 6.5); }
      const clipMoved = Math.abs(pose() - p0) > 1e-4;
      const procMoved = Math.abs(rig.anim.position.y - y0) > 1e-3;
      out.push({
        id: p.id,
        run: run ? run.split('|').pop() : null,
        proc: rig.procedural,
        clipMoved,
        procMoved,
        canRun: clipMoved || procMoved,
      });
    } catch (e) {
      out.push({ id: p.id, error: e.message });
    }
  }
  return out;
});
const bad = rows.filter((r) => !r.canRun);
for (const r of rows) console.log(`${r.id.padEnd(14)} run=${String(r.run).padEnd(14)} ${r.proc ? 'procedural' : 'clip      '} moved:${r.clipMoved ? 'bones' : r.procMoved ? 'hop  ' : 'NONE '} ${r.canRun ? 'RUN OK' : '*** NO RUN ***'}`);
console.log(`\n${bad.length}/${rows.length} personages cannot run: ${bad.map((r) => r.id).join(', ')}`);
await browser.close();
