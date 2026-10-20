import * as THREE from 'three';
import { PLANES, planeById, loadPlane, makeGameplayPlane } from './planes.js';
import { RACES, raceById, bodiesOf, PERSONAGES, personageById, makeCharacter } from './characters.js';
import { Game } from './game.js';
import { SettingsUI } from './settings.js';
import { HeroLink } from './net.js';

const $ = (id) => document.getElementById(id);

// WoW's flow, and nothing between: login → your personages → the world.
// There is no start menu and no map page any more ("i dont need this intro
// page") — ▶ ამ გმირით შესვლა on the personage screen loads the city, and
// the settings live on that screen too.
const screens = {
  login: $('screen-login'),
  character: $('screen-character'),
  game: $('screen-game'),
};

// The hero socket (server/index.js → /ws): every personage's own spawn place —
// where it stood, which way it looked, in the plane or out of it on foot.
const heroes = new HeroLink();
heroes.onStates = () => {
  // states landed from the server: refresh whatever screen is showing them
  if (screens.character.classList.contains('active')) {
    renderCharList();
    renderCharInfo();
  }
};

// "ბოლო ადგილი" for a hero, as the character/map screens print it.
const MODE_LABEL = { foot: '🚶 ფეხით', air: '✈ ჰაერში', parked: '🛬 გაჩერებული' };
function heroSpot(hero) {
  const st = heroes.stateOf(hero?.hid);
  // A row with no position is a LOADOUT, not a spot: what a hero that died
  // still owns. It has no "last place" to print.
  if (!Array.isArray(st?.pos)) return null;
  return {
    place: st.place || 'თბილისი',
    mode: MODE_LABEL[st.onFoot ? 'foot' : st.airborne ? 'air' : 'parked'],
    at: st.at,
  };
}

// "12 წთ წინ" — how long ago the hero was left there.
function agoKa(at) {
  if (!at) return '';
  const m = Math.max(0, Math.round((Date.now() - at) / 60000));
  if (m < 1) return 'ახლახან';
  if (m < 60) return `${m} წთ წინ`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} სთ წინ`;
  return `${Math.round(h / 24)} დღის წინ`;
}

function showScreen(name) {
  Object.values(screens).forEach((s) => s.classList.remove('active'));
  screens[name].classList.add('active');
  $('chrome').classList.toggle('hidden', name === 'game');
  $('pilot-badge').classList.toggle('hidden', !currentUser);
}

// ---- auth -------------------------------------------------------------------
// Accounts live in the local MongoDB (see server/). The session token sticks
// around in localStorage so a reload goes straight to the start menu.

const TOKEN_KEY = 'tf-token';
// WHAT LOGOUT IS ALLOWED TO TOUCH. Exactly this one key -- the session token.
// Everything else in localStorage belongs to the ACCOUNT, not to the session:
// `tf-chars-<user>` is the hero roster, `tf-heroes-<user>` every personage's
// saved spawn place, `tf-autostart` the straight-into-the-game flag, plus the
// settings and the city's damage map. Clearing storage wholesale on the way out
// would log you back in to a pilot with no heroes standing nowhere. ("if i will
// made logout dont broke login with my personage auto login".)
const LAST_USER_KEY = 'tf-last-user'; // so logging back in is one field, not two
const REMEMBER_KEY = 'tf-remember';   // the checkbox's own state, not a session

// WHERE THE SESSION LIVES. "დამიმახსოვრე" ticked puts the token in
// localStorage, so the browser comes back logged in days later; unticked puts
// it in sessionStorage, which still survives a RELOAD (that is the whole point
// -- refreshing must never throw you out) but dies with the tab.
const remembering = () => {
  try { return localStorage.getItem(REMEMBER_KEY) !== '0'; } catch { return true; }
};
const getToken = () => {
  try { return sessionStorage.getItem(TOKEN_KEY) || localStorage.getItem(TOKEN_KEY); } catch { return null; }
};
function setToken(token, remember) {
  try {
    localStorage.setItem(REMEMBER_KEY, remember ? '1' : '0');
    sessionStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(TOKEN_KEY);
    (remember ? localStorage : sessionStorage).setItem(TOKEN_KEY, token);
  } catch { /* private mode: the session simply does not outlive the page */ }
}
function clearToken() {
  try {
    sessionStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(TOKEN_KEY); // the preference above is NOT touched
  } catch { /* nothing to clear */ }
}

let currentUser = null;

async function api(path, { method = 'GET', body } = {}) {
  const headers = {};
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body) headers['Content-Type'] = 'application/json';
  try {
    const res = await fetch(`/api${path}`, {
      method,
      headers,
      body: body ? JSON.stringify(body) : undefined,
    });
    const data = await res.json().catch(() => ({}));
    return { ok: res.ok, data };
  } catch {
    return { ok: false, data: { error: 'სერვერი მიუწვდომელია — გაუშვი: npm run backend' } };
  }
}

// WoW-style hero roster: one account, many personages { name, race }.
// Lives on the Mongo profile, with a per-user localStorage copy so an
// offline backend doesn't lose it.
let roster = [];
let activeIdx = -1;
const MAX_HEROES = 9;
const charKey = () => `tf-chars-${currentUser?.username ?? ''}`;
const activeHero = () => roster[activeIdx] ?? null;

function updateBadge() {
  if (!currentUser) return;
  const hero = activeHero();
  $('pb-name').textContent = currentUser.username.toUpperCase();
  $('pb-sub').textContent = hero
    ? `${hero.name} • ${raceById(hero.race).name} • ${planeById(hero.plane).code} • LVL ${currentUser.level}`
    : `LVL ${currentUser.level} • ${currentUser.stars} ★`;
}

// A hero's own id: the roster can be renamed and reordered, but this is what
// its saved spawn place (net.js / the /ws socket) hangs on.
const newHid = () => `h${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
let hidsMinted = false; // an older hero got an id just now — persist the roster

const RACE_MOVED = { kori: 'veggie', artsivi: 'veggie', devi: 'toys' };

const validHeroes = (list) => {
  hidsMinted = false;
  return (Array.isArray(list) ? list : [])
    // THE OLD RACES. ქორი (healer) was retired long ago; არწივი and დევი are
    // now ხილ-ბოსტნეული and სათამაშოები, and each kept its own plane list, so
    // a hero crossing over keeps the machine it was flying and its body.
    .map((c) => (c ? { ...c, race: RACE_MOVED[c.race] ?? c.race } : c))
    .filter((c) => c?.name && RACES.some((r) => r.id === c.race))
    .map((c) => {
      const race = raceById(c.race);
      if (!c.hid) hidsMinted = true;
      return {
        ...c,
        // heroes from before the spawn-place socket get an id now
        hid: typeof c.hid === 'string' && c.hid ? c.hid.slice(0, 32) : newHid(),
        // older heroes have no body yet — give them the race's default one
        model: PERSONAGES.some((p) => p.id === c.model) ? c.model : race.defModel,
        // the hero IS personage + plane: older heroes get their class's lightest machine
        plane: race.planes.includes(c.plane) ? c.plane : race.planes[0],
      };
    })
    .slice(0, MAX_HEROES);
};

function saveRoster() {
  localStorage.setItem(charKey(), JSON.stringify({ list: roster, active: activeIdx }));
  if (currentUser) {
    currentUser.characters = roster;
    currentUser.activeChar = activeIdx;
    api('/auth/me', { method: 'PATCH', body: { characters: roster, activeChar: activeIdx } });
  }
  updateBadge();
}

function setUser(user) {
  currentUser = user;
  if (user && PLANES.some((p) => p.id === user.plane)) {
    chosenPlane = user.plane;
  }
  roster = validHeroes(user?.characters);
  activeIdx = roster.length ? Math.min(Math.max(0, user?.activeChar ?? 0), roster.length - 1) : -1;
  if (!roster.length && user) {
    // profile has no heroes (older backend?) — fall back to the local copy
    try {
      const saved = JSON.parse(localStorage.getItem(charKey()) ?? 'null');
      roster = validHeroes(saved?.list);
      activeIdx = roster.length ? Math.min(Math.max(0, saved?.active ?? 0), roster.length - 1) : -1;
    } catch { /* corrupt copy — start fresh */ }
  }
  // the hero's own plane wins over the account-level fallback
  const hero = activeHero();
  if (hero) chosenPlane = hero.plane;
  updateBadge();
  if (user) {
    // open the hero socket: it answers with every hero's last spawn place
    heroes.open(getToken(), user.username);
    if (hidsMinted) saveRoster(); // freshly minted ids have to stick
  }
}

// login/register mode toggle
let authMode = 'login';
function setAuthMode(mode) {
  authMode = mode;
  $('tab-login').classList.toggle('active', mode === 'login');
  $('tab-register').classList.toggle('active', mode === 'register');
  $('fld-pass2').classList.toggle('hidden', mode === 'login');
  $('auth-pass').autocomplete = mode === 'login' ? 'current-password' : 'new-password';
  $('btn-auth').textContent = mode === 'login' ? '▶ შესვლა' : '▶ რეგისტრაცია';
  authError(null);
}
$('tab-login').addEventListener('click', () => setAuthMode('login'));
$('tab-register').addEventListener('click', () => setAuthMode('register'));

function authError(msg) {
  $('auth-error').classList.toggle('hidden', !msg);
  $('auth-error').textContent = msg || '';
}

async function submitAuth() {
  const username = $('auth-user').value.trim();
  const password = $('auth-pass').value;
  if (!username || !password) return authError('შეავსე ორივე ველი');
  if (authMode === 'register' && password !== $('auth-pass2').value) {
    return authError('პაროლები არ ემთხვევა');
  }
  const btn = $('btn-auth');
  btn.disabled = true;
  const { ok, data } = await api(`/auth/${authMode}`, {
    method: 'POST',
    body: { username, password },
  });
  btn.disabled = false;
  if (!ok) return authError(data.error || 'შეცდომა — სცადე თავიდან');
  setToken(data.token, $('auth-remember').checked);
  try { localStorage.setItem(LAST_USER_KEY, data.user?.username ?? username); } catch { /* private mode */ }
  setUser(data.user);
  $('auth-pass').value = $('auth-pass2').value = '';
  openCharacter(); // WoW-style: character select comes right after login
}
$('btn-auth').addEventListener('click', submitAuth);
for (const id of ['auth-user', 'auth-pass', 'auth-pass2']) {
  $(id).addEventListener('keydown', (e) => { if (e.key === 'Enter') submitAuth(); });
}

window.addEventListener('resize', () => {
  sizeCharPreview();
});

// ---- character creation (WoW-style: race cards + live hero preview) ----
let selectedRace = RACES[0].id;
let charPrev = null;
let charPrevToken = 0;
const RACE_STAT_LABELS = ['შეტევა', 'დაცვა', 'განკურნება', 'სისწრაფე'];

async function setCharPreviewModel(raceId) {
  if (!charPrev) return;
  const token = ++charPrevToken;
  const rig = await makeCharacter(raceId);
  if (!charPrev || token !== charPrevToken) return;
  charPrev.heroHolder.clear();
  rig.play('Idle');
  charPrev.rig = rig;
  charPrev.heroHolder.add(rig.obj);
}

function sizeCharPreview() {
  if (!charPrev) return;
  const canvas = $('char-preview');
  const w = canvas.clientWidth || 800;
  const h = canvas.clientHeight || 500;
  charPrev.renderer.setSize(w, h, false);
  charPrev.camera.aspect = w / h;
  charPrev.camera.updateProjectionMatrix();
}

function startCharPreview() {
  if (charPrev) return;
  const canvas = $('char-preview');
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(38, 1.5, 0.1, 100);
  camera.position.set(0, 1.5, 4.6);
  camera.lookAt(0, 1.02, 0);

  scene.add(new THREE.HemisphereLight(0xcfe4ff, 0x54462f, 1.5));
  const sun = new THREE.DirectionalLight(0xffffff, 2.4);
  sun.position.set(3, 6, 5);
  scene.add(sun);
  const rim = new THREE.DirectionalLight(0x3fd6ff, 1.2); // cyan holo rim light
  rim.position.set(-4, 3, -5);
  scene.add(rim);

  const holder = new THREE.Group();
  holder.rotation.y = Math.PI; // the rig faces -Z — start face-on to the camera
  scene.add(holder);
  const heroHolder = new THREE.Group();
  holder.add(heroHolder);

  wireCharDrag(canvas);

  charPrev = { renderer, camera, holder, heroHolder, rig: null, clock: new THREE.Clock(), running: true };
  sizeCharPreview();
  const animate = () => {
    if (!charPrev?.running) return;
    charPrev.rig?.update(charPrev.clock.getDelta());
    renderer.render(scene, camera);
    requestAnimationFrame(animate);
  };
  animate();
  setCharPreviewModel(selectedRace);
}

// Drag to look around, PUBG-lobby style: the mouse turns the hero. Wired
// once — the same canvas survives screen open/close cycles.
let charDragWired = false;
function wireCharDrag(canvas) {
  if (charDragWired) return;
  charDragWired = true;
  let dragging = false;
  let lastX = 0;
  canvas.style.cursor = 'grab';
  canvas.addEventListener('pointerdown', (e) => {
    dragging = true;
    lastX = e.clientX;
    canvas.style.cursor = 'grabbing';
    canvas.setPointerCapture(e.pointerId);
  });
  canvas.addEventListener('pointermove', (e) => {
    if (!dragging || !charPrev) return;
    const dx = e.clientX - lastX;
    lastX = e.clientX;
    charPrev.holder.rotation.y += dx * 0.012;
  });
  const endDrag = () => {
    dragging = false;
    canvas.style.cursor = 'grab';
  };
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);
}

function stopCharPreview() {
  if (charPrev) {
    charPrev.running = false;
    charPrev.renderer.dispose();
    charPrev = null;
  }
}

// Two modes on one screen, like the WoW character flow: the ROSTER (your
// heroes, pick the active one) and CREATE (race cards + personage body from
// the whole model folder + name → new hero).
let charMode = 'roster';
let selectedHero = 0; // roster mode selection
let selectedModel = raceById(selectedRace).defModel; // create mode: which body the hero wears
let starterPlane = raceById(selectedRace).planes[0]; // create mode: the role's starter machine, shown but not chosen

// Pick a body straight off its tile: the tile knows which race it belongs to,
// so choosing a banana chooses ხილ-ბოსტნეული with it. (The old ◄ ► cycler is
// gone — the WoW screen shows every body at once.)
function selectBody(raceId, bodyId) {
  const changedRace = raceId !== selectedRace;
  selectedRace = raceId;
  selectedModel = bodyId;
  if (changedRace) starterPlane = raceById(raceId).planes[0];
  renderCharList();
  renderCharInfo();
  setCharPreviewModel(selectedModel);
  // (the preview is the hero alone — no plane behind them in either mode;
  //  the plane is named in the panel and on the bar under the hero)
}

// NO MACHINE PICKING ON THIS SCREEN. The ◄ ► cycler that used to sit under
// the stats is gone ("when u are choose personage, u cant choose plane,
// remove this arrows"): a personage owns its role's machine, the panel and
// the hero card just NAME it, and planes are taken in the world — the hall by
// the warehouse is full of them.

// One race down a column: its banner, then every body it owns as a tile.
function renderRaceColumn(el, race) {
  el.innerHTML = '';
  const head = document.createElement('div');
  head.className = 'race-head' + (race.id === selectedRace ? ' on' : '');
  head.innerHTML = `
    <div class="rh-top"><span class="rh-icon">${race.icon}</span><span class="rh-name">${race.name}</span><span class="rh-latin">${race.latin}</span></div>
    <div class="rh-role">${race.roleKa}</div>
    <div class="rh-desc">${race.desc}</div>`;
  head.addEventListener('click', () => selectBody(race.id, race.defModel));
  el.appendChild(head);
  const grid = document.createElement('div');
  grid.className = 'body-grid';
  for (const id of bodiesOf(race.id)) {
    const body = personageById(id);
    const card = document.createElement('div');
    card.className = 'body-card'
      + (race.id === selectedRace && id === selectedModel ? ' selected' : '');
    card.innerHTML = `<div class="bc-icon">${body.icon}</div><div class="bc-name">${body.name}</div>`;
    card.addEventListener('click', () => selectBody(race.id, id));
    grid.appendChild(card);
  }
  el.appendChild(grid);
}

// CREATE: the two races take the two side columns, the hero stands between
// them and the name goes under it. ROSTER: the old heroes list on the left.
// The name field and the big button MOVE between the create bar and the info
// panel rather than being duplicated — one of each, whatever the mode.
function applyCharLayout() {
  const create = charMode === 'create';
  const info = document.querySelector('.hangar-info');
  const bar = $('create-bar');
  info.classList.toggle('hidden', create);
  bar.classList.toggle('hidden', !create);
  $('race-list-2').classList.toggle('hidden', !create);
  (create ? bar : info).append($('char-name-fld'), $('btn-char-create'));
}

function renderCharList() {
  const list = $('race-list');
  applyCharLayout();
  list.innerHTML = '';
  if (charMode === 'create') {
    renderRaceColumn(list, RACES[0]);
    renderRaceColumn($('race-list-2'), RACES[1] ?? RACES[0]);
    return;
  }
  roster.forEach((hero, i) => {
    const def = raceById(hero.race);
    const body = personageById(hero.model);
    const plane = planeById(hero.plane);
    const item = document.createElement('div');
    item.className = 'pl-item' + (i === selectedHero ? ' selected' : '');
    const spot = heroSpot(hero);
    item.innerHTML = `
      <div class="pl-row1"><span class="pl-name"><span class="race-icon">${def.icon}</span>${hero.name}</span><span class="pl-code">${body.icon} ✈ ${plane.code}</span></div>
      <div class="pl-row2"><span class="pl-cls">${def.roleKa}</span>${i === activeIdx ? '<span class="pl-tag equipped">ACTIVE</span>' : ''}</div>`
      + (spot ? `<div class="pl-row3">📍 ${spot.place} · ${spot.mode}</div>` : '');
    item.addEventListener('click', () => selectHero(i));
    list.appendChild(item);
  });
  if (roster.length < MAX_HEROES) {
    const add = document.createElement('div');
    add.className = 'pl-item new-hero';
    add.textContent = '+ ახალი გმირი';
    add.addEventListener('click', () => enterCreateMode());
    list.appendChild(add);
  }
}

function renderCharInfo() {
  const hero = charMode === 'roster' ? roster[selectedHero] : null;
  const def = raceById(hero ? hero.race : selectedRace);
  const plane = planeById(hero ? hero.plane : starterPlane);
  $('ci-code').textContent = `RACE // ${def.latin} // ${plane.code}`;
  $('ci-name').textContent = hero ? hero.name : `${def.icon} ${def.name}`;
  $('ci-role').textContent = hero
    ? `${def.icon} ${def.name} — ${def.roleKa} · ✈ ${plane.name}`
    : `${def.roleKa} · ✈ ${plane.name}`;
  $('ci-desc').textContent = def.desc;
  // Where this hero will wake up — its own spot, not the city's spawn.
  const spot = hero ? heroSpot(hero) : null;
  $('ci-spot').classList.toggle('hidden', !spot);
  if (spot) {
    $('ci-spot').innerHTML =
      `📍 ბოლო ადგილი: <b>${spot.place}</b> · ${spot.mode}<br>${agoKa(spot.at)} — აქ გამოჩნდები`;
  }
  const stats = $('ci-stats');
  stats.innerHTML = '';
  def.stats.forEach((v, i) => {
    const row = document.createElement('div');
    row.innerHTML = `
      <div class="hs-row"><span class="hs-label">${RACE_STAT_LABELS[i]}</span><span class="hs-val">${String(v).padStart(2, '0')}</span></div>
      <div class="hs-track"><div class="hs-fill" style="width:${v}%"></div></div>`;
    stats.appendChild(row);
  });
  if (charMode === 'create') {
    const body = personageById(selectedModel);
    $('cb-line').innerHTML = `${def.icon} <b>${def.name}</b> · ${body.icon} ${body.name}`
      + `<br>${def.roleKa} · ✈ <b>${planeById(starterPlane).name}</b>`;
  }
  $('char-name-fld').classList.toggle('hidden', charMode !== 'create');
  $('btn-char-delete').classList.toggle('hidden', charMode !== 'roster' || !hero);
  // ◄ უკან only means "never mind, back to my heroes" — there is no screen
  // behind this one, and a pilot with no hero yet has no roster to go back to.
  $('btn-char-back').classList.toggle('hidden', charMode !== 'create' || !roster.length);
  const btn = $('btn-char-create');
  if (charMode === 'create') {
    btn.textContent = 'შექმნა';
    btn.classList.remove('equipped');
  } else {
    btn.textContent = '▶ ამ გმირით შესვლა'; // WoW's "Enter World" — straight in
    btn.classList.remove('equipped');
  }
}

function selectHero(i) {
  selectedHero = i;
  renderCharList();
  renderCharInfo();
  if (roster[i]) {
    setCharPreviewModel(roster[i].model);
  }
}

function enterCreateMode() {
  charMode = 'create';
  $('char-sub').textContent = 'ახალი გმირი — აირჩიე მხარე და სხეული, მერე დაარქვი სახელი';
  $('char-name').value = '';
  const race = raceById(selectedRace);
  selectedModel = race.defModel;
  starterPlane = race.planes[0];
  renderCharList();
  renderCharInfo();
  setCharPreviewModel(selectedModel);
}

// Back out of creation onto the roster (the head's ◄ უკან).
function enterRosterMode() {
  if (!roster.length) return;
  charMode = 'roster';
  selectedHero = activeIdx >= 0 ? activeIdx : 0;
  $('char-sub').textContent = 'შენი გმირები — აირჩიე ან შექმენი ახალი';
  renderCharList();
  renderCharInfo();
  setCharPreviewModel(roster[selectedHero].model);
}

function openCharacter() {
  if (roster.length) {
    charMode = 'roster';
    selectedHero = activeIdx >= 0 ? activeIdx : 0;
    $('char-sub').textContent = 'შენი გმირები — აირჩიე ან შექმენი ახალი';
  } else {
    charMode = 'create';
    $('char-name').value = '';
    const race = raceById(selectedRace);
    selectedModel = race.defModel;
    starterPlane = race.planes[0];
    $('char-sub').textContent = 'შექმენი შენი პირველი გმირი — აირჩიე მხარე და სხეული';
  }
  renderCharList();
  renderCharInfo();
  showScreen('character');
  startCharPreview();
  sizeCharPreview(); // canvas has real dimensions only after the screen is visible
  setCharPreviewModel(charMode === 'roster' ? roster[selectedHero].model : selectedModel);
}

$('btn-char-create').addEventListener('click', () => {
  if (charMode === 'create') {
    const name = $('char-name').value.trim();
    if (!name) {
      $('char-name').focus();
      $('char-sub').textContent = '⚠ დაარქვი გმირს სახელი!';
      return;
    }
    roster.push({
      hid: newHid(),
      name,
      race: selectedRace,
      model: selectedModel,
      plane: raceById(selectedRace).planes[0], // the role's lightest machine
    });
    activeIdx = roster.length - 1; // a newborn hero becomes the active one
    selectedHero = activeIdx;
    chosenPlane = roster[activeIdx].plane; // the hero brings their own machine
    charMode = 'roster';
    $('char-sub').textContent = 'შენი გმირები — აირჩიე ან შექმენი ახალი';
    saveRoster();
    renderCharList();
    renderCharInfo(); // stay on the roster, WoW-style — შესვლა is the next click
    return;
  }
  if (!roster[selectedHero]) return;
  activeIdx = selectedHero;
  chosenPlane = roster[selectedHero].plane; // switching hero switches the plane
  saveRoster();
  renderCharList();
  renderCharInfo();
  stopCharPreview();
  startFlight(); // the personage screen IS the door into the city
});

$('btn-char-delete').addEventListener('click', () => {
  if (charMode !== 'roster' || !roster[selectedHero]) return;
  heroes.save(roster[selectedHero].hid, null); // the hero's spot goes with it
  roster.splice(selectedHero, 1);
  if (activeIdx === selectedHero) activeIdx = roster.length ? 0 : -1;
  else if (activeIdx > selectedHero) activeIdx--;
  selectedHero = Math.max(0, Math.min(selectedHero, roster.length - 1));
  saveRoster();
  if (activeHero()) chosenPlane = activeHero().plane;
  if (!roster.length) enterCreateMode();
  else {
    renderCharList();
    renderCharInfo();
    setCharPreviewModel(roster[selectedHero].model);
  }
});

$('char-name').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') $('btn-char-create').click();
});

$('btn-char-back').addEventListener('click', enterRosterMode);

// ---- equipped plane (no hangar page: the plane belongs to the personage) ----
let chosenPlane = 'spitfire'; // what the active hero flies

// Weight class, WoW-style: the active hero's race decides which planes are
// flyable — the toys only fit the heavies, the fruit takes the fighters.
const planeAllowed = (id) => {
  const hero = activeHero();
  return !hero || raceById(hero.race).planes.includes(id);
};
const firstAllowedPlane = () =>
  (PLANES.find((p) => planeAllowed(p.id)) ?? PLANES[0]).id;

// ---- settings: a chip on the personage screen, not a menu page ----
const settings = new SettingsUI(null); // shared instance; the Game attaches to it
$('btn-char-settings').addEventListener('click', () => settings.open());

// The only city there is. The map page that used to pick one is gone — the
// others were locked cards anyway, and a second city belongs on the personage
// screen when it unlocks.
let selectedCity = 'tbilisi';

// Spawn points: city-center avenues (the game snaps to the nearest road).
const SPAWN_CITIES = {
  // TBS airport, on the parallel taxiway beside the warehouse, 70 m from it
  // and facing the 31L threshold so the roll starts with a taxi out.
  tbilisi: { lat: 41.660283, lon: 44.964831, yaw: 0.775 },
  // the same airport, lined up ON runway 31L ~150 m from the threshold, on the
  // real 316.6° heading toward the city (yaw = atan2(-fx,-fz) convention)
  tbilisiRunway: { lat: 41.659247, lon: 44.963588, yaw: -2.385 },
  // დედაენის ბაღი — Dedaena Park, left bank of the Mtkvari (the old Madatov
  // island). Point and heading are the longest tree-free lane through the real
  // OSM outline (public/tbilisi/parks.json): ~114 m of open lawn in a 22 m
  // corridor, the most the park's tree rows leave anywhere. Only ~114 m of
  // roll, so an F-15C (rotates at 277 km/h) will not get off the ground here.
  tbilisiPark: { lat: 41.700494, lon: 44.803848, yaw: 3.107 },
  batumi: { lat: 41.646, lon: 41.636 },
  kutaisi: { lat: 42.2679, lon: 42.6946 },
  gori: { lat: 41.986, lon: 44.1105 },
  rustavi: { lat: 41.5495, lon: 45.011 },
  kazbegi: { lat: 42.657, lon: 44.641 },
};

let game = null;

async function startFlight() {
  const hero = activeHero();
  if (hero) chosenPlane = hero.plane; // you fly what your personage owns
  if (!planeAllowed(chosenPlane)) chosenPlane = firstAllowedPlane();
  showScreen('game');
  $('loading-overlay').classList.remove('hidden');
  // This hero's own spawn place, straight off the socket — position, heading,
  // camera, and whether it was standing outside the plane.
  const resume = heroes.stateOf(hero?.hid);
  $('loading-text').textContent = Array.isArray(resume?.pos)
    ? 'ბრუნდები იქ, სადაც გაჩერდი…'
    : 'იტვირთება თბილისი…';

  const def = planeById(chosenPlane);
  const model = await loadPlane(def.id);
  const plane = makeGameplayPlane(model, def);

  window.__THREE = THREE; // dev/tooling handle: the app's ONE three instance
  window.__game = game = new Game({
    container: $('game-container'),
    plane,
    def,
    character: hero, // { name, race } — Enter climbs out on foot
    // per-hero spawn place: restore it now, and keep streaming it back.
    // Read LAZILY -- the city takes ~13 s to build and the socket often
    // answers inside that window, so the Game asks again when it is ready.
    resume: () => heroes.stateOf(hero?.hid) ?? resume,
    cityId: selectedCity,
    onSave: (state) => heroes.save(hero?.hid, state),

    maxSpeed: def.maxSpeed,
    spawnLatLon: SPAWN_CITIES[selectedCity] ?? SPAWN_CITIES.tbilisi,
    settingsUI: settings,
    ui: {
      speed: $('hud-speed'),
      alt: $('hud-alt'),
      healthFill: $('healthbar-fill'),
      healthNum: $('healthbar-num'),
      loading: $('loading-overlay'),
      loadingText: $('loading-text'),
      crash: $('crash-overlay'),
      error: $('error-overlay'),
      errorText: $('error-text'),
      hud: $('hud'),
      hint: $('hud-controls'),
      compassDial: $('compass-dial'),
      compassDeg: $('compass-deg'),
      attribution: $('attribution'),
    },
  });
  // Closing the tab or hitting reload flushes the hero socket. Let it ask the
  // running game for a snapshot at that exact moment, instead of shipping
  // whatever the 2.5 s autosave last caught -- a reload mid-run used to put
  // the personage back where it was up to 26 m ago.
  // A game that has not started yet has no spot to save (and snapshot() would
  // read as "dead" and wipe the stored one), so it is left alone.
  heroes.provider = () => {
    if (game?.started && !game.crashed) game.saveState(true, 'leaving the page');
  };
}

$('btn-restart').addEventListener('click', () => game?.resetPlane());

// 🎬 — the looping cinematics. Wired once, on the live game: the Game object is
// rebuilt on every flight, so the listener asks for whichever one is running.
// The chip opens a LIST of scenes (index.html #cinema-menu, one row per entry
// in cinema.js SCENES); while a reel is running it is a stop button, because
// that is what a player reaching for it then wants.
{
  const chip = $('btn-cinema');
  const menu = $('cinema-menu');
  const rows = [...menu.querySelectorAll('.cm-row')];
  const close = () => {
    menu.classList.add('hidden');
    chip.classList.remove('open');
  };
  const mark = () => rows.forEach((r) => r.classList.toggle('on',
    !!game?.cinema?.active && game.cinema.scene === +r.dataset.scene));
  chip.addEventListener('click', (e) => {
    e.stopPropagation();
    if (game?.cinema?.active) { game.cinema.stop(); close(); return; }
    menu.classList.toggle('hidden');
    chip.classList.toggle('open', !menu.classList.contains('hidden'));
    mark();
  });
  for (const row of rows) {
    row.addEventListener('click', (e) => {
      e.stopPropagation();
      close();
      game?.cinema?.toggle(+row.dataset.scene);
    });
  }
  // anywhere else -- including into the game -- puts it away
  document.addEventListener('pointerdown', (e) => {
    if (!menu.classList.contains('hidden') && !menu.contains(e.target) && e.target !== chip) close();
  });
}
$('btn-airport').addEventListener('click', () => game?.goAirport());

// Put the game down and push the hero's final snapshot. The ORDER is the whole
// point: dispose first (that is what takes the snapshot), flush second.
function leaveGame() {
  // dispose() SAVES FIRST and frees the GPU after, so a throw half way through
  // costs some memory but never the hero's spot -- and the shell must come back
  // either way, or a bad frame in the teardown traps you on the game screen.
  try {
    game?.dispose(); // pushes the final snapshot: this is where the hero waits
  } catch (e) {
    console.error('teardown threw:', e);
  }
  heroes.flush();
  heroes.provider = null;
  game = null;
  settings.game = null;
  $('error-overlay').classList.add('hidden');
  $('hud').classList.add('hidden');
}

$('btn-back').addEventListener('click', () => {
  leaveGame();
  openCharacter(); // WoW's "exit to character select"
});

// ---- logout ----------------------------------------------------------------
// Reachable from the pilot badge on the personage screen (that is where a
// login lands, and there was no way out of it without flying first) and from
// the 🚪 chip in the game.
//
// It ends the SESSION and nothing else: the hero, its spawn place, its kit and
// the autostart flag all stay exactly as they are, so logging back in puts the
// same personage back in the same spot.
const lastUser = () => {
  try { return localStorage.getItem(LAST_USER_KEY) || ''; } catch { return ''; }
};

function doLogout() {
  if (game) leaveGame();   // mid-flight: the snapshot goes up BEFORE the socket closes
  stopCharPreview();       // the character screen's holo loop, if it is running
  settings.close?.();
  api('/auth/logout', { method: 'POST' }); // fire and forget
  heroes.close();          // flushes the last spawn place, then drops the socket
  // Remember the callsign, never forget it: a logout with no user somehow in
  // hand must not blank the name the login form opens on.
  if (currentUser?.username) {
    try { localStorage.setItem(LAST_USER_KEY, currentUser.username); } catch { /* private mode */ }
  }
  clearToken(); // the one thing logout owns
  currentUser = null;
  roster = [];
  activeIdx = -1;
  setAuthMode('login');
  $('auth-user').value = lastUser();
  $('auth-pass').value = $('auth-pass2').value = '';
  showScreen('login');
}
for (const id of ['btn-logout-top', 'btn-logout-game']) {
  $(id).addEventListener('click', doLogout);
}

// ---- ?auto=1: skip the screens and drop straight into the active hero ------
// FOR TESTING ONLY, and ONLY while it is in the URL. It used to be remembered
// in localStorage, so one `?auto=1` meant every later reload went straight into
// the game -- and the stored token meant a plain load walked past the login
// screen as well. Both are gone: a normal load always asks who you are.
function autostartWanted() {
  const q = new URLSearchParams(location.search).get('auto');
  return q !== null && q !== '0' && q !== 'false';
}

// ---- boot: a live session goes back to its personages, ?auto=1 straight in --
//
// A RELOAD MUST NEVER LOG YOU OUT. The token is read from wherever the tick
// box put it (localStorage = for keeps, sessionStorage = this tab), and it
// lands you on the character screen exactly as it did before you refreshed.
// `?auto=1` is still the only thing that skips the screens entirely and drops
// you into the game on the active hero -- and it is NOT remembered, so the
// next plain load is a normal one.
(async () => {
  $('auth-remember').checked = remembering();
  if (getToken()) {
    const { ok, data } = await api('/auth/me');
    if (ok) {
      setUser(data.user);
      if (autostartWanted() && activeHero()) {
        console.log(`?auto=1: straight in on ${activeHero().name}`);
        startFlight();
        return;
      }
      openCharacter(); // WoW-style: land on your personages first
      return;
    }
    clearToken(); // stale or revoked — ask again
  }
  if (!$('auth-user').value) $('auth-user').value = lastUser();
  showScreen('login');
})();
