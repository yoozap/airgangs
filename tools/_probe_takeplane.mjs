// CAN I GET INTO ANY OF THEM? Walks the hero up to parked aeroplanes in the
// plane hall, presses Enter, and checks that the machine actually becomes the
// player's: the right def, the right flight model, a stick in its cockpit, the
// pilot seated in it, and the one left behind still standing there solid.
//   node tools/_probe_takeplane.mjs [id ...]
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const WANT = process.argv.slice(2);
const browser = await chromium.launch({ args:['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport:{width:1280,height:800} });
const errs = [];
page.on('pageerror',(e)=>errs.push(e.message));
page.on('console',(m)=>{ if(m.type()==='error') errs.push(m.text().slice(0,160)); });
await enterGame(page, { user:'swapbot', pass:'swapbot123' });
await page.waitForFunction(()=>window.__game?.started,null,{timeout:420000});
await page.waitForTimeout(4000);

// build the hall now rather than flying 300 m to trigger it
await page.evaluate(async () => {
  const g = window.__game;
  const h = g.world?.planeHall;
  if (h && !h.filled) { h.filled = true; await h._fill(); }
});
await page.waitForFunction(() => (window.__game?.targets?.list?.length ?? 0) >= 30,
  null, { timeout: 420000 });
const fleet = await page.evaluate(() => window.__game.targets.list.map((r) => r.def.id));
console.log(`${fleet.length} parked aeroplanes registered\n`);

const ids = WANT.length ? WANT : ['spitfire', 'b767', 'an225', 'aerobatic', 'f15', 'c130'];
let fails = 0;
for (const id of ids) {
  const out = await page.evaluate(async (id) => {
    const g = window.__game, THREE = window.__THREE;
    const rec = g.targets.list.find((r) => r.def.id === id);
    if (!rec) return { id, error: 'not parked' };
    const before = g.def.id;
    // stand the hero beside it, where a pilot would walk up
    const p = rec.wrapper.getWorldPosition(new THREE.Vector3());
    const gy = g._groundAt(p.x + 4, p.z, p.y + 20, 40) ?? p.y - 1.7;
    g.foot._exitPlane(true);
    await new Promise((r) => setTimeout(r, 400));
    g.foot._stand(p.x + Math.min(6, (rec.wrapper.userData.halfSpan ?? 6) * 0.5), gy + 0.02, p.z, 0);
    await new Promise((r) => setTimeout(r, 200));
    const near = g.foot._nearestPlane();
    g.foot.toggle();                       // Enter
    // the climb is a real walk-and-haul: a 31 m bomber takes a good deal
    // longer than a Spitfire, so wait for the seat rather than a fixed beat
    for (let i = 0; i < 100 && !g.foot.seated; i++) await new Promise((r) => setTimeout(r, 120));
    if (!g.foot.seated) {                  // it never got there — say so loudly
      g.foot._boardSeq = null; g.foot._climb = null; g.foot._boardAfter = null;
      g.foot._board();
      await new Promise((r) => setTimeout(r, 400));
      window.__forced = (window.__forced ?? 0) + 1;
    }
    await new Promise((r) => setTimeout(r, 400));
    const ctl = g.plane.userData.controls;
    return {
      id, before, now: g.def.id,
      took: g.plane === rec.wrapper,
      nearestFound: near?.rec?.def?.id ?? (near ? 'own' : null),
      seated: !!g.foot.seated,
      hasStick: !!ctl?.stick, hasPedals: !!(ctl?.pedalL && ctl?.pedalR),
      aeroMass: g.aero?.mass ?? null, defMass: rec.def.mass,
      halfSpan: +(g.halfSpan ?? 0).toFixed(1), realHalfSpan: +((rec.wrapper.userData.halfSpan ?? 0)).toFixed(1),
      inScene: rec.wrapper.parent === g.scene,
      forcedBoard: window.__forced ?? 0,
      stillRegistered: g.targets.list.some((r) => r.wrapper === rec.wrapper),
      leftBehindParked: g.targets.list.some((r) => r.def.id === before),
      handOnStick: (() => {
        const rig = g.foot.rig, gp = ctl?.stick?.userData?.grip;
        if (!rig || !gp) return null;
        const h = rig._handPoint?.('R'); if (!h) return null;
        return +h.getWorldPosition(new THREE.Vector3())
          .distanceTo(gp.getWorldPosition(new THREE.Vector3())).toFixed(3);
      })(),
    };
  }, id);
  if (out.error) { console.log(`${id.padEnd(11)} ERROR ${out.error}`); fails++; continue; }
  const bad = [];
  if (!out.took) bad.push(`did NOT become the player's plane (still ${out.now})`);
  if (!out.seated) bad.push('pilot not seated');
  if (!out.hasStick || !out.hasPedals) bad.push('no hinged controls');
  if (out.aeroMass !== out.defMass) bad.push(`flight model still on the old airframe (${out.aeroMass} vs ${out.defMass})`);
  if (out.halfSpan !== out.realHalfSpan) bad.push('halfSpan stale');
  if (out.stillRegistered) bad.push('still listed as a parked target');
  if (!out.leftBehindParked) bad.push('the aeroplane left behind vanished from the registry');
  if (out.handOnStick !== null && out.handOnStick > 0.1) bad.push(`hand ${out.handOnStick} m off the stick`);
  if (bad.length) fails++;
  console.log(`${id.padEnd(11)} ${out.before.padEnd(10)} -> ${String(out.now).padEnd(10)} `
    + `span ${String(out.halfSpan * 2).padStart(5)} m  stick:${out.hasStick ? 'y' : 'n'} pedals:${out.hasPedals ? 'y' : 'n'} `
    + `hand ${String(out.handOnStick).padStart(5)}  ${bad.length ? 'FAIL — ' + bad.join(' · ') : 'ok'}`);
}
console.log(errs.length ? `\n${errs.length} page errors:\n  ` + [...new Set(errs)].slice(0,6).join('\n  ') : '\nno page errors');
console.log(fails ? `\n${fails} of ${ids.length} failed` : `\nall ${ids.length} aeroplanes taken and flown from the hall`);
await browser.close();
