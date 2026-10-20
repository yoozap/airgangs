// DOES IT STILL FLY, AND DOES THE PILOT FLY IT?
//   node tools/_probe_flyseat.mjs
//
// Boards, starts, takes off and flies a circuit with the keys held the way a
// player holds them, sampling every quarter second: the stick's and pedals'
// deflection against the keys that asked for them, the hands' distance from
// their grips, the frame time, and anything the page logged as an error.
// The point is that the pose keeps up with a MOVING aeroplane -- the grips
// are in the aircraft's frame and the solve is in the world's.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';

const browser = await chromium.launch({ args:['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport:{width:1280,height:800} });
const errs = [];
page.on('pageerror',(e)=>errs.push(e.message));
page.on('console',(m)=>{ if(m.type()==='error') errs.push(m.text().slice(0,160)); });
await enterGame(page, { user:'flybot', pass:'flybot123' });
await page.waitForFunction(()=>window.__game?.started,null,{timeout:420000});
await page.waitForTimeout(4000);

const start = await page.evaluate(() => {
  const g = window.__game;
  return { seated: !!g.foot?.seated, onFoot: !!g.foot?.active, plane: g.plane?.userData?.id,
    kit: !!g.plane?.userData?.cockpitKit, controls: !!g.plane?.userData?.controls };
});
console.log('at the gate:', JSON.stringify(start));

// hold the throttle, then pull up, then a turn with rudder -- real key holds,
// not press(), which the game loop never sees (tools: harness-keys gotcha)
const hold = async (keys, ms) => {
  for (const k of keys) await page.keyboard.down(k);
  await page.waitForTimeout(ms);
  for (const k of keys) await page.keyboard.up(k);
};
const sample = () => page.evaluate(() => {
  const g = window.__game, THREE = window.__THREE;
  const rig = g.foot?.rig, parts = g.plane?.userData?.controls;
  const W = (o)=> o ? o.getWorldPosition(new THREE.Vector3()) : null;
  const d = (a,b)=> (a&&b) ? +a.distanceTo(b).toFixed(3) : null;
  const ctl = g.foot?._ctl;
  return {
    alt: +g.plane.position.y.toFixed(1), spd: +g.speed.toFixed(1), air: !!g.airborne,
    seated: !!g.foot?.seated, crashed: !!g.crashed,
    stickPitch: parts?.stick ? +parts.stick.rotation.x.toFixed(3) : null,
    stickRoll: parts?.stick ? +parts.stick.rotation.z.toFixed(3) : null,
    pedal: parts?.pedalL ? +parts.pedalL.rotation.x.toFixed(3) : null,
    throttle: parts?.throttle ? +parts.throttle.rotation.x.toFixed(3) : null,
    ctl: ctl ? { p:+ctl.pitch.toFixed(2), r:+ctl.roll.toFixed(2), y:+ctl.rudder.toFixed(2), t:+ctl.throttle.toFixed(2) } : null,
    handStick: d(W(rig?._handPoint?.('R')), W(parts?.stick?.userData?.grip)),
    handThr: d(W(rig?._handPoint?.('L')), W(parts?.throttle?.userData?.grip)),
    fps: +(window.__fps ?? 0).toFixed(0),
  };
});

// a crude fps meter for the run
await page.evaluate(() => {
  let last = performance.now(), n = 0, acc = 0;
  const tick = () => { const t = performance.now(); acc += t - last; last = t; n++;
    if (n >= 30) { window.__fps = 1000 / (acc / n); n = 0; acc = 0; }
    requestAnimationFrame(tick); };
  requestAnimationFrame(tick);
});

const rows = [];
rows.push(['parked', await sample()]);
await hold(['KeyW'], 5000); rows.push(['full throttle', await sample()]);
await page.keyboard.down('KeyW');
await hold(['ArrowUp'], 2500); rows.push(['rotate', await sample()]);
await hold(['ArrowLeft','KeyQ'], 2500); rows.push(['left turn + rudder', await sample()]);
await hold(['ArrowRight','KeyE'], 2000); rows.push(['right turn + rudder', await sample()]);
await page.keyboard.up('KeyW');
await page.waitForTimeout(1500); rows.push(['hands off', await sample()]);

console.log('\nphase                 alt   spd  air  stick(p/r)  pedal  thr   ctl                 handR handL  fps');
for (const [name, s] of rows) {
  const f=(n,w=6)=>String(n===null?'-':n).padStart(w);
  console.log(`${name.padEnd(20)} ${f(s.alt)}${f(s.spd)}  ${s.air?'yes':'no '} ${f(s.stickPitch)}/${f(s.stickRoll)} ${f(s.pedal)} ${f(s.throttle)}  `
    + `${JSON.stringify(s.ctl)}  ${f(s.handStick,5)} ${f(s.handThr,5)} ${f(s.fps,4)}  ${s.seated?'':'NOT SEATED '}${s.crashed?'CRASHED':''}`);
}
console.log(errs.length ? `\n${errs.length} page errors:\n  ` + [...new Set(errs)].slice(0,8).join('\n  ') : '\nno page errors');
await page.screenshot({ path: (process.env.SHOT_DIR || '/tmp') + '/inflight.png' });
await browser.close();
