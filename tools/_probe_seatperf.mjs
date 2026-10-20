// WHAT DOES THE SEATED POSE COST, and does getting in and out still work?
//   node tools/_probe_seatperf.mjs
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args:['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport:{width:1280,height:800} });
const errs = [];
page.on('pageerror',(e)=>errs.push(e.message));
await enterGame(page, { user:'perfbot', pass:'perfbot123' });
await page.waitForFunction(()=>window.__game?.started,null,{timeout:420000});
await page.waitForTimeout(6000);

const cost = await page.evaluate(() => {
  const g = window.__game;
  const n = 400;
  const run = (fn) => { const t0 = performance.now(); for (let i=0;i<n;i++) fn(); return (performance.now()-t0)/n; };
  const full = run(() => g.foot.updateSeated(1/60));
  // the same frame with the control solve switched off
  const real = g.foot._flyControls;
  g.foot._flyControls = () => {};
  const bare = run(() => g.foot.updateSeated(1/60));
  g.foot._flyControls = real;
  return { full:+full.toFixed(3), bare:+bare.toFixed(3), solve:+(full-bare).toFixed(3) };
});
console.log(`one seated frame: ${cost.full} ms total, ${cost.bare} ms placing the body,`
  + ` ${cost.solve} ms solving the hands and feet onto the controls`);
console.log(`  (a 60 fps budget is 16.7 ms, so the solve is ${(cost.solve/16.7*100).toFixed(1)}% of a frame)`);

// ---- getting out and back in ----
const step = async (label) => {
  const s = await page.evaluate(() => {
    const g = window.__game;
    return { seated:!!g.foot.seated, onFoot:!!g.foot.active, climbing:!!g.foot._boardSeq,
      y:+(g.foot.rig?.obj?.position?.y ?? 0).toFixed(2), parent: g.foot.rig?.obj?.parent === g.plane ? 'plane' : 'scene' };
  });
  console.log(`  ${label.padEnd(22)} ${JSON.stringify(s)}`);
  return s;
};
console.log('\nclimbing out and back in (Enter):');
await step('in the seat');
await page.keyboard.down('Enter'); await page.waitForTimeout(140); await page.keyboard.up('Enter');
await page.waitForTimeout(4000); await step('after Enter (out)');
await page.waitForTimeout(2000);
await page.keyboard.down('Enter'); await page.waitForTimeout(140); await page.keyboard.up('Enter');
await page.waitForTimeout(12000); const back = await step('after Enter (back in)');
console.log(errs.length ? `\nerrors: ${[...new Set(errs)].slice(0,5).join(' | ')}` : '\nno page errors');
await browser.close();
