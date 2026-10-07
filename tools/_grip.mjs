import { chromium } from 'playwright';
const b = await chromium.launch({ args: ['--use-gl=angle','--use-angle=metal','--ignore-gpu-blocklist'] });
for (const body of ['astronaut', 'banana', 'batman', 'cyberfem']) {
  const p = await b.newPage({ viewport: { width: 400, height: 400 } });
  await p.goto(`http://localhost:5173/_runview.html?body=${body}`);
  await p.waitForFunction(() => window.__ready, null, { timeout: 60000 });
  await p.evaluate(() => window.__step(60));
  console.log(body, JSON.stringify(await p.evaluate(() => window.__grip())));
  await p.close();
}
await b.close();
