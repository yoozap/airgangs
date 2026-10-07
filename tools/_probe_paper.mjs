// Put a round through EVERY paper card. The crash only bit cards after the
// first of a given shape, so one hit on one card proves nothing.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const DIR = process.env.SHOT_DIR || '/private/tmp/claude-501/-Users-wazzap-tbilisifly/103a16db-c731-4bc9-9a57-d1b191ace42c/scratchpad';
const ctx = await chromium.launchPersistentContext(DIR + '/fps-profile', {
  headless: true, viewport: { width: 900, height: 600 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'] });
const page = ctx.pages()[0] ?? await ctx.newPage();
const errs = [];
page.on('pageerror', (e) => errs.push(e.message.split('\n')[0]));
await enterGame(page, { user: 'fpsbot', pass: 'fpsbot123' });
await page.waitForFunction(() => window.__game?.world?.ready && window.__game?.paper, null, { timeout: 180000 });
await page.waitForTimeout(8000);
const out = await page.evaluate(async () => {
  const g = window.__game, THREE = window.__THREE;
  const { CARTRIDGES } = await import('/src/ballistics.js');
  const cart = CARTRIDGES['9x19'];
  const sheets = [];
  g.scene.traverse((o) => { if (o.isMesh && o.userData.paper) sheets.push(o); });
  let hit = 0; let threw = 0; const msgs = [];
  for (const m of sheets) {
    m.updateWorldMatrix(true, false);
    const c = new THREE.Box3().setFromObject(m).getCenter(new THREE.Vector3());
    const n = new THREE.Vector3(0, 0, 1).transformDirection(m.matrixWorld).normalize();
    try {
      // three rounds through each card, slightly apart
      for (let k = 0; k < 3; k++) {
        const p = c.clone().addScaledVector(new THREE.Vector3(1, 0, 0), (k - 1) * 0.03);
        g.paper.hit(m, p, n, cart);
      }
      hit++;
    } catch (e) { threw++; if (msgs.length < 3) msgs.push(e.message); }
  }
  const cards = [...(g.paper.sheets?.values?.() ?? [])];
  return { sheets: sheets.length, cardsOk: hit, cardsThrew: threw, msgs,
    live: g.paper._live?.length ?? 0, blanks: g.paper._blanks?.size ?? 0,
    scored: cards.filter((c) => c.shots?.length).length };
});
console.log(JSON.stringify(out, null, 1));
console.log('pageerrors:', errs.length ? [...new Set(errs)] : 'none');
await ctx.close();
