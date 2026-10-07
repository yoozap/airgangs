// Shared Playwright entry for the AIR GANGS shell:
// login/register -> character screen (create hero if fresh) -> enter world ->
// menu -> play -> hangar -> fly. Resolves when world.ready.
export async function enterGame(page, { user = 'shotbot', pass = 'shotbot123' } = {}) {
  // NO HOT RELOADS. Vite's client reloads the page whenever any source file
  // changes, and with an editor (or another session) touching src/ while a
  // capture is running, the game vanishes from under the harness halfway
  // through -- `window.__game` undefined, or a login that never lands. The
  // page is loaded once and left alone: the HMR client is replaced with a
  // stub that exports what the injected code imports and never connects.
  // HMR_LIVE=1 puts the real client back.
  if (!process.env.HMR_LIVE) {
    await page.route('**/@vite/client', (route) => route.fulfill({
      contentType: 'application/javascript',
      body: 'export function createHotContext() { return { accept() {}, acceptExports() {}, dispose() {}, prune() {}, on() {}, off() {}, send() {}, invalidate() {}, data: {} }; }\n'
        + 'export function injectQuery(u) { return u; }\nexport function updateStyle() {}\nexport function removeStyle() {}\n',
    }));
  }
  await page.goto(process.env.GAME_URL || 'http://localhost:5173/');
  // a saved token auto-logs into the character screen; else the login form
  await page.waitForSelector('#screen-login.active, #screen-character.active', { timeout: 30000 });
  const onLogin = await page.$eval('#screen-login', (el) => el.classList.contains('active'));
  if (onLogin) {
    // A saved token can resolve a moment after the login screen paints and swap
    // it out from under us — then the fields are in a hidden screen and the
    // fill waits forever. Try briefly; if they are gone we are already in.
    try {
      await page.fill('#auth-user', user, { timeout: 5000 });
      await page.fill('#auth-pass', pass, { timeout: 5000 });
      await page.click('#btn-auth', { timeout: 5000 });
    } catch {
      await page.waitForSelector('#screen-character.active', { timeout: 15000 });
    }
  }
  // after auth the shell always lands on the character screen
  const authed = await page.waitForSelector('#screen-character.active', { timeout: 8000 })
    .then(() => true).catch(() => false);
  if (!authed) {
    await page.click('#tab-register');
    await page.fill('#auth-user', user);
    await page.fill('#auth-pass', pass);
    await page.fill('#auth-pass2', pass);
    await page.click('#btn-auth');
    await page.waitForSelector('#screen-character.active', { timeout: 15000 });
  }
  // fresh account: create-mode shows the name field — make the first hero
  const createMode = await page.$eval('#char-name-fld', (el) => !el.classList.contains('hidden'))
    .catch(() => false);
  if (createMode) {
    await page.fill('#char-name', 'SHOTBOT');
    await page.click('#btn-char-create'); // create -> roster mode
    await page.waitForTimeout(400);
  }
  await page.click('#btn-char-create'); // "enter world with this hero" -> menu
  await page.waitForSelector('#screen-menu.active', { timeout: 15000 });
  await page.click('#btn-play');
  await page.waitForSelector('#btn-map', { timeout: 15000 });
  await page.click('#btn-map');
  await page.waitForFunction(() => window.__game?.world?.ready, null, { timeout: 420000 });
}
