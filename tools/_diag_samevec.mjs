import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const b = await chromium.launch({ args: ['--use-angle=metal', '--ignore-gpu-blocklist'] });
const p = await b.newPage({ viewport: { width: 700, height: 500 } });
await enterGame(p, { user: 'cranebot', pass: 'cranebot123' });
await p.waitForFunction(() => window.__game?.started, null, { timeout: 300000 });
await p.waitForTimeout(2000);
console.log(await p.evaluate(async () => {
  const g = window.__game; await g.foot.ready;
  if (!g.foot.active) { g.airborne = false; g.speed = 0; g.vel.set(0, 0, 0); g.foot.toggle(); }
  g.foot.update(1 / 60);
  return `rig.obj.position === foot.pos : ${g.foot.rig.obj.position === g.foot.pos}`;
}));
await b.close();
