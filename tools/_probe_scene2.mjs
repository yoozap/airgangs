// Scene 2 of the reel (cinema.js), traced: what the BODY is actually doing at
// each moment of the script -- on the floor, on the lid, on the tower, in the
// air, in cover -- and a still at the middle of every shot in the cut list.
import { chromium } from 'playwright';
import { enterGame } from './enter.mjs';
const OUT = process.env.OUT;
const SHOTS = process.env.SHOTS === '1';
const ctx = await chromium.launchPersistentContext(OUT + '/scene2-profile', {
  headless: true, viewport: { width: 1280, height: 800 },
  args: ['--use-gl=angle', '--use-angle=metal', '--ignore-gpu-blocklist'],
});
const page = ctx.pages()[0] ?? await ctx.newPage();
page.on('console', (m) => { if (/cinema|crate|flip|cover|climb|failed|Error/i.test(m.text())) console.log('  [page]', m.text()); });
page.on('pageerror', (e) => console.log('PAGE ERROR', e.message));
await enterGame(page, { user: 'reelbot', pass: 'reelbot123' });
console.log('in world; waiting for the crates and the range…');
await page.waitForFunction(() => {
  let n = 0; window.__game?.scene?.traverse((o) => { if (/ammo_box.*_shell/.test(o.name)) n++; });
  return n >= 3 && window.__game?.lanes?.lanes?.length;
}, null, { timeout: 240000 });

const ok = await page.evaluate(async () => {
  const g = window.__game;
  if (!g.foot.active) { g.foot.toggle(); await new Promise((r) => setTimeout(r, 400)); }
  return g.cinema.start(1);
});
console.log('scene 2 started:', ok);
if (!ok) { await ctx.close(); process.exit(1); }

// freeze the reel's clock and step it: the trace is then deterministic
const trace = [];
const stepTo = async (t) => page.evaluate((t) => new Promise((done) => {
  const g = window.__game; const c = g.cinema;
  c.paused = false;
  const tick = () => {
    if (c.t >= t || !c.active) {
      const f = g.foot;
      done({
        t: +c.t.toFixed(2), scale: +c.timeScale.toFixed(2),
        y: +f.pos.y.toFixed(2), gy: f._gY === null ? null : +f._gY.toFixed(2),
        a: +c._S.heroA().toFixed(2),
        b: +new window.__THREE.Vector3().subVectors(f.pos, c._stage.row.o).dot(c._stage.row.n).toFixed(2),
        state: f._climb ? 'climb:' + f._climb.phase : f._flip ? 'flip:' + f._flip.p.toFixed(2)
          : f._roll ? 'ROLL:' + f._roll.phase : f._cover ? 'cover' : f.grounded ? 'ground' : 'air',
        rbuf: +(f._rollBuf ?? 0).toFixed(2), rcd: +(f._rollCd ?? 0).toFixed(2),
        want: c._rollLand ? 'rollLand' : null,
        held: f.held?.id ?? null, ammo: g.pistol?.ammo, reload: +(g.pistol?._reload ?? 0).toFixed(2),
        peek: +(f._cover?.peek ?? 0).toFixed(2), near: f._cover?.nearSide ?? 0,
        speed: +f.speed.toFixed(2),
        hits: (() => { let n = 0; for (const c of (g.paper?.sheets?.values() ?? [])) n += c.shots.length; return n; })(),
        live: g.bullets?.live?.length ?? 0,
        canFire: f.coverCanFire?.() ?? null, yaw: +f.yaw.toFixed(2), cam: +f.camYaw.toFixed(2),
        want: c._faceYaw === null || c._faceYaw === undefined ? null : +c._faceYaw.toFixed(2),
        lk: +g.look.yaw.toFixed(3), keys: [...g.keys].join('+'),
      });
      return;
    }
    requestAnimationFrame(tick);
  };
  tick();
}), t);
const T0 = +(process.env.T0 ?? 0.4), T1 = +(process.env.T1 ?? 16.0), TD = +(process.env.TD ?? 0.4);
for (let t = T0; t <= T1; t += TD) {
  const row = await stepTo(t);
  if (!row) break;
  trace.push(row);
  console.log(`t=${String(row.t).padStart(5)}  ${row.state.padEnd(10)} a=${String(row.a).padStart(6)} `
    + `b=${String(row.b).padStart(6)} y=${String(row.y).padStart(6)} v=${String(row.speed).padStart(5)} `
    + `rbuf=${row.rbuf} rcd=${row.rcd} want=${row.want} lk=${row.lk} keys=${row.keys} held=${row.held ?? '-'} rl=${row.reload} peek=${row.peek} hits=${row.hits} live=${row.live} cf=${row.canFire}`);
}
if (SHOTS) {
  const times = await page.evaluate(() => window.__game.cinema.shotTimes());
  const only = process.env.ONLY ? process.env.ONLY.split(',').map(Number) : null;
  for (let i = 0; i < times.length; i++) {
    if (only && !only.includes(i + 1)) continue;
    await page.evaluate(() => { window.__game.cinema._reset(); });
    await stepTo(times[i].t + times[i].d * 0.5);
    // FREEZE BEFORE THE SHUTTER. The last shot's middle is half a second from
    // the loop's end and a headless screenshot takes longer than that -- the
    // film wrapped mid-capture and the still came back off the top of the
    // next loop, on clean paper. (check_cinema.mjs pauses for the same reason.)
    await page.evaluate(() => { window.__game.cinema.paused = true; });
    await page.waitForTimeout(220);
    await page.screenshot({ path: `${OUT}/s2_shot${String(i + 1).padStart(2, '0')}.png` });
    if (process.env.DIAG) {
      console.log('  diag', JSON.stringify(await page.evaluate(() => {
        const T = window.__THREE; const g = window.__game; const c = g.cinema;
        const t0 = c._targets[c._target ?? 0];
        t0.mesh.updateWorldMatrix(true, false);
        const mp = new T.Vector3().setFromMatrixPosition(t0.mesh.matrixWorld);
        return { target: c._target, camToAim: +g.camera.position.distanceTo(t0.aimAt).toFixed(2),
          camToMesh: +g.camera.position.distanceTo(mp).toFixed(2),
          aimToMesh: +t0.aimAt.distanceTo(mp).toFixed(2),
          shots: g.paper?.sheets?.get(t0.mesh)?.shots?.length ?? -1,
          laneAt: +t0.lane.at.toFixed(2), vis: t0.mesh.visible };
      })));
    }
    await page.evaluate(() => { window.__game.cinema.paused = false; });
    console.log(`shot ${i + 1} @ ${(times[i].t + times[i].d * 0.5).toFixed(2)}s`);
  }
}
await ctx.close();
console.log('done');
