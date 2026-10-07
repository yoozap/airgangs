// TbilisiFly damage API — stores per-building hit points in MongoDB so the
// broken city survives reloads on any browser (localStorage stays as an
// offline fallback in the client).
import crypto from 'node:crypto';
import express from 'express';
import cors from 'cors';
import mongoose from 'mongoose';
import bcrypt from 'bcryptjs';
import { WebSocketServer } from 'ws';

const PORT = process.env.PORT || 3001;
const MONGO_URL = process.env.MONGO_URL || 'mongodb://localhost:27017/tbilisifly';

const Building = mongoose.model('Building', new mongoose.Schema({
  key: { type: String, required: true, unique: true }, // chunk bi or landmark/instance key
  hp: { type: Number, required: true },
  // Blast holes torn out of the facade: [[x, y, z, radius], ...] in world
  // meters, in the building's pristine frame. The city is shared, so a wall
  // one pilot opened up stays open for everyone.
  holes: { type: [[Number]], default: undefined },
  // what got hit, in plain Georgian street terms
  addr: { type: String },
  x: { type: Number },
  z: { type: Number },
}, { timestamps: true }));

// Every building's street address, imported from the OSM extract
// (tools/import_addresses.mjs → addresses.json.gz). Keyed by rounded world
// position, which is how the client matches a hit building to its address.
const Address = mongoose.model('Address', new mongoose.Schema({
  key: { type: String, required: true, unique: true }, // "x_z" at 1 m resolution
  addr: { type: String, required: true },
  x: { type: Number, required: true },
  z: { type: Number, required: true },
  lat: { type: Number },
  lon: { type: Number },
}));

// One personage of a roster. `hid` is a stable id minted by the client when
// the hero is born: names can repeat and roster slots shift when a hero is
// deleted, but the hid is what a hero's saved spawn place hangs on.
const CharacterSchema = new mongoose.Schema({
  hid: { type: String },
  name: { type: String },
  race: { type: String },
  model: { type: String },
  plane: { type: String },
}, { _id: false });

const User = mongoose.model('User', new mongoose.Schema({
  username: { type: String, required: true, unique: true },
  passHash: { type: String, required: true },
  token: { type: String, index: true }, // current session token
  plane: { type: String, default: 'spitfire' }, // equipped plane id
  level: { type: Number, default: 1 },
  stars: { type: Number, default: 0 },
  // WoW-style hero roster: one account, many personages; activeChar indexes it
  characters: { type: [CharacterSchema], default: [] },
  activeChar: { type: Number, default: -1 },
}, { timestamps: true }));

// Where each personage stood when its player last left the world: position,
// heading, velocity, camera, and whether the hero was standing OUTSIDE the
// plane. One row per (account, hero) — every hero owns its own spawn place,
// WoW-style, and logging back in drops you exactly where you stopped.
const HeroState = mongoose.model('HeroState', new mongoose.Schema({
  key: { type: String, required: true, unique: true }, // `${userId}|${hid}`
  user: { type: mongoose.Schema.Types.ObjectId, index: true },
  hid: { type: String, required: true },
  state: { type: mongoose.Schema.Types.Mixed },
}, { timestamps: true }));

const heroKey = (user, hid) => `${user._id}|${hid}`;

// The snapshot is written by the game client, so it is clamped here: a few
// fixed-length number tuples plus a couple of short strings, nothing else is
// stored. Returns null for anything that is not a usable spawn place.
function cleanState(s) {
  if (!s || typeof s !== 'object') return null;
  const nums = (v, n) =>
    (Array.isArray(v) && v.length >= n && v.slice(0, n).every(Number.isFinite)
      ? v.slice(0, n).map(Number)
      : null);
  const str = (v, n) => (typeof v === 'string' && v ? v.slice(0, n) : undefined);
  const at = Number.isFinite(s.at) ? Math.min(s.at, Date.now()) : Date.now();
  // WHAT THE PERSONAGE OWNS — the weapons in its slots and the gear on its
  // body. This is kept apart from where it stood because the two are lost in
  // different ways: a hero dies and loses its spot, but it does not lose the
  // rifle it is carrying.
  const guns = Array.isArray(s.guns)
    ? s.guns
      .filter((g) => g && Number.isInteger(g.s) && g.s >= 0 && g.s < 16
        && typeof g.id === 'string' && g.id)
      .slice(0, 16)
      .map((g) => ({ s: g.s, id: g.id.slice(0, 24) }))
    : undefined;
  const gear = Array.isArray(s.gear)
    ? s.gear.filter((id) => typeof id === 'string' && id).slice(0, 8).map((id) => id.slice(0, 24))
    : undefined;
  const pos = nums(s.pos, 3);
  const foot = nums(s.foot, 3);
  // No position: not a spawn place, but still a LOADOUT worth keeping — this
  // is what a dead hero writes, and it is why its guns are waiting for it at
  // the next login instead of the row being deleted with its corpse.
  if (!pos) {
    return guns?.length || gear?.length
      ? { guns, gear, city: str(s.city, 24) ?? 'tbilisi', at }
      : null;
  }
  // The client says the hero was on foot but the coordinates did not survive.
  // Storing `onFoot: false` here would silently demote a walking hero to a
  // seated one and -- because this row then wins the merge below -- destroy the
  // real spot for good. Reject the snapshot instead and keep the previous row.
  if (s.onFoot && !foot) return null;
  return {
    pos,
    quat: nums(s.quat, 4) ?? [0, 0, 0, 1],
    vel: nums(s.vel, 3) ?? [0, 0, 0],
    cam: nums(s.cam, 3) ?? undefined,
    airborne: !!s.airborne,
    speed: Number.isFinite(s.speed) ? s.speed : 0,
    health: Number.isFinite(s.health) ? s.health : undefined,
    // on foot: the hero climbed out and was walking when the session ended
    onFoot: !!(s.onFoot && foot),
    foot: foot ?? undefined,
    footYaw: Number.isFinite(s.footYaw) ? s.footYaw : 0,
    plane: str(s.plane, 24),
    city: str(s.city, 24) ?? 'tbilisi',
    place: str(s.place, 60), // the district name, for the character screen
    guns, // the weapons it owns: [{ s: slotIndex, id: 'makarov' }, ...]
    // What it is WEARING — ids out of GEAR_DEFS, the belt and whatever joins
    // it. This was missing from the whitelist, so every write to the hero DB
    // quietly threw the gear away and the stored row (which wins the merge on
    // an equal timestamp) handed the browser a hero with no belt back.
    gear,
    // KEEP THE CLIENT'S TIMESTAMP. Stamping Date.now() here made every stored
    // row strictly newer than the browser's own mirror, so `_merge` in net.js
    // ("newest `at` wins") always picked the server copy -- a stale or demoted
    // row could never be beaten by the correct local one. Clamp it so a client
    // with a fast clock cannot pin itself permanently in the future.
    at,
  };
}

const app = express();
app.use(cors());
app.use(express.json({ limit: '20mb' }));

// ---- auth -----------------------------------------------------------------

const publicUser = (u) => ({
  username: u.username, plane: u.plane, level: u.level, stars: u.stars,
  characters: u.characters ?? [], activeChar: u.activeChar ?? -1,
});

async function authed(req, res) {
  const token = (req.headers.authorization || '').replace(/^Bearer\s+/i, '');
  const user = token ? await User.findOne({ token }) : null;
  if (!user) res.status(401).json({ error: 'not logged in' });
  return user;
}

app.post('/api/auth/register', async (req, res) => {
  const { username, password } = req.body || {};
  if (!username || !/^[\w.-]{3,20}$/.test(username)) {
    return res.status(400).json({ error: 'მომხმარებლის სახელი: 3–20 სიმბოლო (ასოები, ციფრები, _ . -)' });
  }
  if (!password || password.length < 4) {
    return res.status(400).json({ error: 'პაროლი უნდა იყოს მინიმუმ 4 სიმბოლო' });
  }
  try {
    const token = crypto.randomBytes(24).toString('hex');
    const user = await User.create({
      username: username.toLowerCase(),
      passHash: await bcrypt.hash(password, 10),
      token,
    });
    res.json({ token, user: publicUser(user) });
  } catch (e) {
    if (e.code === 11000) return res.status(409).json({ error: 'ეს სახელი უკვე დაკავებულია' });
    res.status(500).json({ error: e.message });
  }
});

app.post('/api/auth/login', async (req, res) => {
  const { username, password } = req.body || {};
  try {
    const user = await User.findOne({ username: (username || '').toLowerCase() });
    if (!user || !(await bcrypt.compare(password || '', user.passHash))) {
      return res.status(401).json({ error: 'არასწორი სახელი ან პაროლი' });
    }
    user.token = crypto.randomBytes(24).toString('hex');
    await user.save();
    res.json({ token: user.token, user: publicUser(user) });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

app.get('/api/auth/me', async (req, res) => {
  const user = await authed(req, res);
  if (user) res.json({ user: publicUser(user) });
});

// Persist profile bits (equipped plane, progression).
app.patch('/api/auth/me', async (req, res) => {
  const user = await authed(req, res);
  if (!user) return;
  const { plane, level, stars, characters, activeChar } = req.body || {};
  if (typeof plane === 'string') user.plane = plane;
  if (Number.isFinite(level)) user.level = level;
  if (Number.isFinite(stars)) user.stars = stars;
  if (Array.isArray(characters)) {
    user.characters = characters
      .filter((c) => c && typeof c.name === 'string' && typeof c.race === 'string')
      .slice(0, 9)
      .map((c) => ({
        // the client mints the hid; keeping it is what lets a hero find its
        // saved spawn place again after any roster edit
        hid: typeof c.hid === 'string' ? c.hid.slice(0, 32) : undefined,
        name: c.name.slice(0, 20),
        race: c.race.slice(0, 20),
        model: typeof c.model === 'string' ? c.model.slice(0, 20) : undefined,
        plane: typeof c.plane === 'string' ? c.plane.slice(0, 20) : undefined,
      }));
  }
  if (Number.isFinite(activeChar)) {
    user.activeChar = Math.min(Math.max(-1, Math.round(activeChar)), user.characters.length - 1);
  }
  await user.save();
  res.json({ user: publicUser(user) });
});

// Last-gasp hero spot, for the moment the page goes away.
//
// The /ws socket is the normal road for snapshots, but a WebSocket frame sent
// during `pagehide`/`beforeunload` is routinely dropped -- the renderer is torn
// down before the frame leaves. `navigator.sendBeacon` is the one request the
// browser guarantees to deliver, so the FINAL position lands in the hero DB
// instead of only in this browser's localStorage.
//
// sendBeacon cannot set headers, so the token rides in the body.
app.post('/api/hero-state', async (req, res) => {
  const { token, hid, state } = req.body || {};
  if (typeof token !== 'string' || typeof hid !== 'string' || !hid) {
    return res.status(400).json({ error: 'bad beacon' });
  }
  const user = await User.findOne({ token });
  if (!user) return res.status(401).json({ error: 'not logged in' });
  const clean = state === null ? null : cleanState(state);
  const key = heroKey(user, hid.slice(0, 32));
  if (clean) {
    await HeroState.updateOne({ key },
      { $set: { user: user._id, hid: hid.slice(0, 32), state: clean } }, { upsert: true });
  } else {
    await HeroState.deleteOne({ key });
  }
  res.json({ ok: true });
});

app.post('/api/auth/logout', async (req, res) => {
  const user = await authed(req, res);
  if (!user) return;
  user.token = undefined;
  await user.save();
  res.json({ ok: true });
});

app.get('/api/health', (_req, res) => {
  res.json({ ok: true, db: mongoose.connection.readyState === 1 });
});

// Full damage map: { "<key>": { hp, holes } , ... }
app.get('/api/damage', async (_req, res) => {
  try {
    const docs = await Building.find({}, { _id: 0, key: 1, hp: 1, holes: 1 }).lean();
    const map = {};
    for (const d of docs) map[d.key] = d.holes?.length ? { hp: d.hp, holes: d.holes } : { hp: d.hp };
    res.json(map);
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

// Upsert a batch of { "<key>": { hp, holes, addr, x, z } } — the client sends
// only keys that changed. A bare number is still accepted (older clients).
app.put('/api/damage', async (req, res) => {
  const damage = req.body?.damage;
  if (!damage || typeof damage !== 'object') {
    return res.status(400).json({ error: 'body must be { damage: { key: { hp, holes } } }' });
  }
  const ops = Object.entries(damage)
    .map(([key, v]) => {
      const row = typeof v === 'number' ? { hp: v } : (v || {});
      if (!Number.isFinite(row.hp)) return null;
      const set = { hp: row.hp };
      if (Array.isArray(row.holes)) {
        // sanity-clamp: 4 finite numbers per hole, at most 40 holes
        const holes = row.holes
          .filter((h) => Array.isArray(h) && h.length >= 4 && h.every(Number.isFinite))
          .slice(-40)
          .map((h) => h.slice(0, 4));
        if (holes.length) set.holes = holes;
      }
      if (typeof row.addr === 'string' && row.addr) set.addr = row.addr.slice(0, 120);
      if (Number.isFinite(row.x)) set.x = row.x;
      if (Number.isFinite(row.z)) set.z = row.z;
      return { updateOne: { filter: { key }, update: { $set: set }, upsert: true } };
    })
    .filter(Boolean);
  if (!ops.length) return res.json({ updated: 0 });
  try {
    const r = await Building.bulkWrite(ops, { ordered: false });
    res.json({ updated: r.upsertedCount + r.modifiedCount + r.matchedCount });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

// ---- addresses -------------------------------------------------------------
// The whole city's street addresses live here, not just the damaged ones, so
// any client or tool can name a building without shipping the OSM extract.

// Bulk import: { items: [[x, z, "ვაჟა-ფშაველას 12", lat?, lon?], ...] }
app.post('/api/addresses/import', async (req, res) => {
  const items = req.body?.items;
  if (!Array.isArray(items)) {
    return res.status(400).json({ error: 'body must be { items: [[x, z, addr], ...] }' });
  }
  const ops = items
    .filter((it) => Array.isArray(it) && Number.isFinite(it[0]) && Number.isFinite(it[1]) && it[2])
    .map(([x, z, addr, lat, lon]) => {
      const set = { addr: String(addr).slice(0, 120), x, z };
      if (Number.isFinite(lat)) set.lat = lat;
      if (Number.isFinite(lon)) set.lon = lon;
      return {
        updateOne: {
          filter: { key: `${Math.round(x)}_${Math.round(z)}` },
          update: { $set: set },
          upsert: true,
        },
      };
    });
  if (!ops.length) return res.json({ imported: 0 });
  try {
    const r = await Address.bulkWrite(ops, { ordered: false });
    res.json({ imported: r.upsertedCount + r.modifiedCount + r.matchedCount });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

// Addresses near a point: /api/addresses?x=1200&z=-450&r=200
app.get('/api/addresses', async (req, res) => {
  const x = Number(req.query.x);
  const z = Number(req.query.z);
  const r = Math.min(2000, Number(req.query.r) || 200);
  try {
    if (!Number.isFinite(x) || !Number.isFinite(z)) {
      const count = await Address.estimatedDocumentCount();
      return res.json({ count });
    }
    const docs = await Address.find({
      x: { $gte: x - r, $lte: x + r },
      z: { $gte: z - r, $lte: z + r },
    }, { _id: 0, addr: 1, x: 1, z: 1 }).limit(500).lean();
    res.json(docs);
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

// Repair the whole city.
app.delete('/api/damage', async (_req, res) => {
  try {
    const r = await Building.deleteMany({});
    res.json({ deleted: r.deletedCount });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

mongoose.connect(MONGO_URL).then(() => {
  console.log(`connected to ${MONGO_URL}`);
}).catch((e) => {
  console.error('mongo connection failed:', e.message);
});

const server = app.listen(PORT, () =>
  console.log(`damage API on http://localhost:${PORT} (hero socket on /ws)`));

// ---- hero-state socket (/ws) ------------------------------------------------
// The game holds one socket open for the whole session. It streams the active
// personage's snapshot in every couple of seconds and, on connect, the server
// hands back the last snapshot of EVERY hero on the account — which is what
// makes a hero start exactly where it stopped, plane or no plane.
//
//   client → { t: 'save', hid, state }   state === null clears (dead hero)
//   client → { t: 'load' }               ask for the states again
//   client → { t: 'ping' }
//   server → { t: 'hello', user, states: { [hid]: state } }
//   server → { t: 'states', states }  |  { t: 'saved', hid, at }  |  { t: 'pong' }
const wss = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', async (ws, req) => {
  let user = null;
  try {
    const token = new URL(req.url, 'http://x').searchParams.get('token') || '';
    user = token ? await User.findOne({ token }) : null;
  } catch { /* malformed URL — treated as no token */ }
  if (!user) {
    ws.close(4001, 'not logged in');
    return;
  }
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });

  const send = (msg) => {
    if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(msg));
  };

  const allStates = async () => {
    const rows = await HeroState.find({ user: user._id }, { _id: 0, hid: 1, state: 1 }).lean();
    const states = {};
    for (const r of rows) if (r.state) states[r.hid] = r.state;
    return states;
  };

  try {
    send({ t: 'hello', user: user.username, states: await allStates() });
  } catch (e) {
    console.warn('hero states read failed:', e.message);
    send({ t: 'hello', user: user.username, states: {} });
  }

  // Snapshots arrive every few seconds; Mongo sees at most one write per hero
  // per flush window, and the last snapshot always wins.
  const pending = new Map();
  let flushT = null;
  const flush = async () => {
    clearTimeout(flushT);
    flushT = null;
    if (!pending.size) return;
    const rows = [...pending.entries()];
    pending.clear();
    try {
      await HeroState.bulkWrite(rows.map(([hid, state]) => (state
        ? {
          updateOne: {
            filter: { key: heroKey(user, hid) },
            update: { $set: { user: user._id, hid, state } },
            upsert: true,
          },
        }
        : { deleteOne: { filter: { key: heroKey(user, hid) } } })), { ordered: false });
    } catch (e) {
      console.warn('hero state write failed:', e.message);
    }
  };

  ws.on('message', async (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }
    if (msg?.t === 'ping') return send({ t: 'pong' });
    if (msg?.t === 'load') {
      try {
        return send({ t: 'states', states: await allStates() });
      } catch {
        return;
      }
    }
    if (msg?.t !== 'save') return;
    const hid = typeof msg.hid === 'string' ? msg.hid.slice(0, 32) : '';
    if (!hid) return;
    const state = msg.state === null ? null : cleanState(msg.state);
    if (msg.state !== null && !state) return; // garbage snapshot — drop it
    pending.set(hid, state);
    send({ t: 'saved', hid, at: Date.now() });
    if (!flushT) flushT = setTimeout(flush, 2000);
  });

  ws.on('close', flush);
  ws.on('error', () => {});
});

// Drop sockets that stopped answering (closed laptop, dead wifi).
const heartbeat = setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) {
      ws.terminate();
      continue;
    }
    ws.isAlive = false;
    ws.ping();
  }
}, 30000);
heartbeat.unref?.();
