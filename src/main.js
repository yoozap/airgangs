import * as THREE from 'three';
import { PLANES, planeById, loadPlane, makeGameplayPlane } from './planes.js';
import { RACES, raceById, PERSONAGES, personageById, makeCharacter } from './characters.js';
import { Game } from './game.js';
import { SettingsUI } from './settings.js';
import { HeroLink } from './net.js';

const $ = (id) => document.getElementById(id);

const screens = {
  login: $('screen-login'),
  menu: $('screen-menu'),
  character: $('screen-character'),
  map: $('screen-map'),
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
  if (screens.map.classList.contains('active')) renderMapSpot();
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
  if (name === 'map') renderMapSpot();
  $('chrome').classList.toggle('hidden', name === 'game');
  $('pilot-badge').classList.toggle('hidden', !currentUser);
}

// ---- auth -------------------------------------------------------------------
// Accounts live in the local MongoDB (see server/). The session token sticks
// around in localStorage so a reload goes straight to the start menu.

const TOKEN_KEY = 'tf-token';
let currentUser = null;

async function api(path, { method = 'GET', body } = {}) {
  const headers = {};
  const token = localStorage.getItem(TOKEN_KEY);
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

const validHeroes = (list) => {
  hidsMinted = false;
  return (Array.isArray(list) ? list : [])
    // the HEALER class is retired — old ქორი heroes fly on as DPS
    .map((c) => (c?.race === 'kori' ? { ...c, race: 'artsivi' } : c))
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
    heroes.open(localStorage.getItem(TOKEN_KEY), user.username);
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
  localStorage.setItem(TOKEN_KEY, data.token);
  setUser(data.user);
  $('auth-pass').value = $('auth-pass2').value = '';
  openCharacter('menu'); // WoW-style: character select comes right after login
}
$('btn-auth').addEventListener('click', submitAuth);
for (const id of ['auth-user', 'auth-pass', 'auth-pass2']) {
  $(id).addEventListener('keydown', (e) => { if (e.key === 'Enter') submitAuth(); });
}

$('btn-logout').addEventListener('click', async () => {
  api('/auth/logout', { method: 'POST' }); // fire and forget
  heroes.close(); // flush the last spawn place, then drop the socket
  localStorage.removeItem(TOKEN_KEY);
  currentUser = null;
  showScreen('login');
});

window.addEventListener('resize', () => {
  sizeCharPreview();
});

// ---- character creation (WoW-style: race cards + live hero preview) ----
let selectedRace = 'artsivi';
let charPrev = null;
let charPrevToken = 0;
// Where picking a hero leads next: 'menu' (WoW-style entry after login),
// 'map' (came from ▶ PLAY without a hero), or null (browsing from the menu).
let charNext = null;

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

// The hero's machine hovers behind them in the holo scan — personage = body + plane.
let charPlaneToken = 0;
async function setCharPreviewPlane(planeId) {
  if (!charPrev) return;
  const token = ++charPlaneToken;
  const model = await loadPlane(planeId);
  if (!charPrev || token !== charPlaneToken) return;
  const g = charPrev.planeHolder;
  g.clear();
  const box = new THREE.Box3().setFromObject(model);
  model.position.sub(box.getCenter(new THREE.Vector3()));
  const size = box.getSize(new THREE.Vector3());
  const wrap = new THREE.Group();
  wrap.add(model);
  // same frame for an 8.5 m racer and a 52 m C-17
  wrap.scale.setScalar(3.4 / Math.max(size.x, size.y, size.z));
  g.add(wrap);
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
  // The plane sits above the hero on its own pivot — PUBG-lobby style, no spin.
  const planeHolder = new THREE.Group();
  planeHolder.position.set(0, 2.45, -2.2);
  planeHolder.rotation.y = 0.55; // static 3/4 view
  scene.add(planeHolder);

  wireCharDrag(canvas);

  charPrev = { renderer, camera, holder, heroHolder, planeHolder, rig: null, clock: new THREE.Clock(), running: true };
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

// Drag to look around, PUBG-lobby style: the mouse turns hero and plane
// together. Wired once — the same canvas survives screen open/close cycles.
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
    charPrev.planeHolder.rotation.y += dx * 0.012;
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
let selectedModel = 'astronaut'; // create mode: which body the hero wears
let selectedCreatePlane = raceById(selectedRace).planes[0]; // create mode: the hero's plane

function renderModelRow() {
  const row = $('char-model-row');
  row.classList.toggle('hidden', charMode !== 'create');
  const def = personageById(selectedModel);
  $('cm-label').textContent = `${def.icon} ${def.name}`;
}

function cycleModel(dir) {
  const i = PERSONAGES.findIndex((p) => p.id === selectedModel);
  const next = PERSONAGES[(i + dir + PERSONAGES.length) % PERSONAGES.length];
  selectedModel = next.id;
  renderModelRow();
  setCharPreviewModel(selectedModel);
}

$('cm-prev').addEventListener('click', () => cycleModel(-1));
$('cm-next').addEventListener('click', () => cycleModel(1));

// Plane cycler — only the machines the chosen role (race) can fly.
function renderPlaneRow() {
  $('char-plane-row').classList.toggle('hidden', charMode !== 'create');
  $('cp-label').textContent = `✈ ${planeById(selectedCreatePlane).name}`;
}

function cyclePlane(dir) {
  const allowed = raceById(selectedRace).planes;
  const i = Math.max(0, allowed.indexOf(selectedCreatePlane));
  selectedCreatePlane = allowed[(i + dir + allowed.length) % allowed.length];
  renderCharInfo(); // banner + code line show the plane too
  setCharPreviewPlane(selectedCreatePlane);
}

$('cp-prev').addEventListener('click', () => cyclePlane(-1));
$('cp-next').addEventListener('click', () => cyclePlane(1));

function renderCharList() {
  const list = $('race-list');
  list.innerHTML = '';
  if (charMode === 'create') {
    for (const def of RACES) {
      const item = document.createElement('div');
      item.className = 'pl-item' + (def.id === selectedRace ? ' selected' : '');
      item.innerHTML = `
        <div class="pl-row1"><span class="pl-name"><span class="race-icon">${def.icon}</span>${def.name}</span><span class="pl-code">${def.latin}</span></div>
        <div class="pl-row2"><span class="pl-cls">${def.roleKa}</span><span class="pl-tag">${def.role}</span></div>`;
      item.addEventListener('click', () => selectRace(def.id));
      list.appendChild(item);
    }
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
      <div class="pl-row2"><span class="pl-cls">${def.roleKa}</span><span class="pl-tag${i === activeIdx ? ' equipped' : ''}">${i === activeIdx ? 'ACTIVE' : def.role}</span></div>`
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
  const plane = planeById(hero ? hero.plane : selectedCreatePlane);
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
  renderModelRow();
  renderPlaneRow();
  $('char-name-fld').classList.toggle('hidden', charMode !== 'create');
  $('btn-char-delete').classList.toggle('hidden', charMode !== 'roster' || !hero);
  // Entry flow (after login) has nowhere to go "back" to until a hero exists.
  $('btn-char-back').classList.toggle('hidden', !activeHero() && charNext !== null);
  const btn = $('btn-char-create');
  if (charMode === 'create') {
    btn.textContent = 'შექმნა';
    btn.classList.remove('equipped');
  } else if (charNext) {
    btn.textContent = '▶ ამ გმირით შესვლა'; // WoW's "Enter World"
    btn.classList.remove('equipped');
  } else {
    const isActive = selectedHero === activeIdx && hero;
    btn.textContent = isActive ? '✓ აქტიური გმირი' : 'ამ გმირით თამაში';
    btn.classList.toggle('equipped', !!isActive);
  }
}

function selectRace(id) {
  selectedRace = id;
  const race = raceById(id);
  selectedModel = race.defModel; // race default body; ◄ ► cycles all
  selectedCreatePlane = race.planes[0]; // role switch resets to its lightest machine
  renderCharList();
  renderCharInfo();
  setCharPreviewModel(selectedModel);
  setCharPreviewPlane(selectedCreatePlane);
}

function selectHero(i) {
  selectedHero = i;
  renderCharList();
  renderCharInfo();
  if (roster[i]) {
    setCharPreviewModel(roster[i].model);
    setCharPreviewPlane(roster[i].plane);
  }
}

function enterCreateMode() {
  charMode = 'create';
  $('char-sub').textContent = 'ახალი გმირი — როლი, თვითმფრინავი, პერსონაჟი (◄ ►) და სახელი';
  $('char-name').value = '';
  const race = raceById(selectedRace);
  selectedModel = race.defModel;
  selectedCreatePlane = race.planes[0];
  renderCharList();
  renderCharInfo();
  setCharPreviewModel(selectedModel);
  setCharPreviewPlane(selectedCreatePlane);
}

function openCharacter(next = null) {
  charNext = next;
  if (roster.length) {
    charMode = 'roster';
    selectedHero = activeIdx >= 0 ? activeIdx : 0;
    $('char-sub').textContent =
      next === 'map' ? '⚠ აირჩიე გმირი ფრენამდე' : 'შენი გმირები — აირჩიე ან შექმენი ახალი';
  } else {
    charMode = 'create';
    $('char-name').value = '';
    const race = raceById(selectedRace);
    selectedModel = race.defModel;
    selectedCreatePlane = race.planes[0];
    $('char-sub').textContent = 'შექმენი შენი პირველი გმირი — როლი, თვითმფრინავი, პერსონაჟი და სახელი';
  }
  renderCharList();
  renderCharInfo();
  showScreen('character');
  startCharPreview();
  sizeCharPreview(); // canvas has real dimensions only after the screen is visible
  setCharPreviewModel(charMode === 'roster' ? roster[selectedHero].model : selectedModel);
  setCharPreviewPlane(charMode === 'roster' ? roster[selectedHero].plane : selectedCreatePlane);
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
      plane: selectedCreatePlane,
    });
    activeIdx = roster.length - 1; // a newborn hero becomes the active one
    selectedHero = activeIdx;
    chosenPlane = selectedCreatePlane; // the hero brings their own machine
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
  if (charNext) {
    const dest = charNext === 'map' ? 'map' : 'menu';
    charNext = null;
    stopCharPreview();
    showScreen(dest);
  }
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
    setCharPreviewPlane(roster[selectedHero].plane);
  }
});

$('char-name').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') $('btn-char-create').click();
});

$('btn-char-back').addEventListener('click', () => {
  stopCharPreview();
  showScreen('menu');
});

// ---- equipped plane (no hangar page: the plane belongs to the personage) ----
let chosenPlane = 'spitfire'; // what the active hero flies

// Weight class, WoW-style: the active hero's race decides which planes are
// flyable — a devi only fits the heavies, an artsivi takes the fighters.
const planeAllowed = (id) => {
  const hero = activeHero();
  return !hero || raceById(hero.race).planes.includes(id);
};
const firstAllowedPlane = () =>
  (PLANES.find((p) => planeAllowed(p.id)) ?? PLANES[0]).id;

// ---- start menu ----
const settings = new SettingsUI(null); // shared instance; the Game attaches to it

$('btn-play').addEventListener('click', () => {
  if (!activeHero()) openCharacter('map'); // no hero yet — create one first
  else showScreen('map');
});
$('btn-character').addEventListener('click', () => openCharacter(null));
$('btn-menu-settings').addEventListener('click', () => settings.open());
$('btn-map-back').addEventListener('click', () => showScreen('menu'));

// City cards: only Tbilisi is unlocked for now; locked ones shake.
let selectedCity = 'tbilisi';
document.querySelectorAll('.city-card').forEach((card) => {
  card.addEventListener('click', () => {
    if (card.classList.contains('locked')) {
      card.classList.remove('shake');
      void card.offsetWidth; // restart the animation
      card.classList.add('shake');
      return;
    }
    document.querySelectorAll('.city-card').forEach((c) => c.classList.remove('selected'));
    card.classList.add('selected');
    selectedCity = card.dataset.city;
  });
});

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

// The map screen tells you up front that ▶ ფრენა continues the hero's session
// instead of starting over at the airport.
function renderMapSpot() {
  const el = $('map-spot');
  if (!el) return;
  const spot = heroSpot(activeHero());
  el.classList.toggle('hidden', !spot);
  if (spot) {
    el.innerHTML = `📍 ${activeHero().name} გაჩერდა: <b>${spot.place}</b> · ${spot.mode} (${agoKa(spot.at)})`
      + '<small>გაგრძელდება იქიდან, სადაც გაჩერდი</small>';
  }
  $('btn-map').textContent = spot ? 'გაგრძელება ✈' : 'ფრენა ✈';
}

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

$('btn-map').addEventListener('click', startFlight);

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

$('btn-back').addEventListener('click', () => {
  game?.dispose(); // pushes the final snapshot: this is where the hero waits
  heroes.flush();
  heroes.provider = null;
  game = null;
  settings.game = null;
  $('error-overlay').classList.add('hidden');
  $('hud').classList.add('hidden');
  showScreen('menu');
});

// ---- autostart: skip the menus and drop straight into the active hero ------
// For testing. `?auto=1` turns it on and REMEMBERS it, so every plain reload
// goes straight back in on the hero you were playing, exactly where the
// snapshot left it; `?auto=0` turns it off again. Without a hero to resume
// it falls through to the normal character screen.
const AUTOSTART_KEY = 'tf-autostart';
function autostartWanted() {
  const q = new URLSearchParams(location.search).get('auto');
  if (q !== null) {
    const on = q !== '0' && q !== 'false';
    try { localStorage.setItem(AUTOSTART_KEY, on ? '1' : ''); } catch { /* private mode */ }
    return on;
  }
  try { return localStorage.getItem(AUTOSTART_KEY) === '1'; } catch { return false; }
}

// ---- boot: token → straight to the menu, otherwise login ----
(async () => {
  if (localStorage.getItem(TOKEN_KEY)) {
    const { ok, data } = await api('/auth/me');
    if (ok) {
      setUser(data.user);
      if (autostartWanted() && activeHero()) {
        console.log(`autostart: ${activeHero().name} — add ?auto=0 to stop`);
        startFlight();
        return;
      }
      openCharacter('menu'); // WoW-style: land on your personages first
      return;
    }
    localStorage.removeItem(TOKEN_KEY);
  }
  showScreen('login');
})();
