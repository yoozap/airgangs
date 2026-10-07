// Hero-state link — the live socket to server/index.js (/ws).
//
// Every personage keeps its own spawn place: where the plane stood, which way
// it pointed, where the camera was looking, and whether the hero was standing
// OUTSIDE the machine on foot. The game streams a snapshot in every couple of
// seconds; on the next login the socket hands the snapshots of the whole
// roster back, so picking a hero drops you exactly where that hero stopped —
// WoW-style, one saved spot per character.
//
// localStorage mirrors everything, so a dead backend only costs cross-device
// sync (same deal as the damage map in citymap.js).

const LS_KEY = (user) => `tf-heroes-${user ?? ''}`;

export class HeroLink {
  constructor() {
    this.states = {}; // hid → snapshot
    this.username = null;
    this.token = null;
    this.ws = null;
    this.connected = false;
    this.onStates = null; // fired when the server's snapshots land
    this._queue = new Map(); // hid → snapshot waiting for an open socket
    this._retry = 0;
    this._retryT = null;
    this._wanted = false;
    this.provider = null; // the live game: hands back a FRESH snapshot on demand
    // Leaving the page (tab closed, reload, alt-tab away on mobile) is the one
    // moment we cannot miss: take a snapshot right now and flush it.
    this._onHide = () => this.flush({ leaving: true });
    window.addEventListener('pagehide', this._onHide);
    window.addEventListener('beforeunload', this._onHide);
    // Safari/iOS often skip beforeunload entirely; going hidden is the signal
    // that actually fires there, and it fires on a plain reload too.
    this._onVis = () => {
      if (document.visibilityState === 'hidden') this.flush({ leaving: true });
    };
    document.addEventListener('visibilitychange', this._onVis);
  }

  // Sign in: read the local mirror right away (so the character screen can
  // show where each hero is even before the socket answers), then connect.
  open(token, username) {
    // switching account: the old socket belongs to the old token. Its handlers
    // go first — a late onclose would otherwise null out the new socket.
    if (this.ws && this.token !== token) {
      const old = this.ws;
      old.onopen = old.onmessage = old.onclose = old.onerror = null;
      old.close();
      this.ws = null;
      this._queue.clear();
    }
    this.token = token;
    this.username = username;
    this.states = this._readLocal();
    this._wanted = true;
    this._connect();
  }

  close() {
    this._wanted = false;
    clearTimeout(this._retryT);
    this.flush();
    this.provider = null;
    this.ws?.close();
    this.ws = null;
    this.connected = false;
    this.states = {};
  }

  stateOf(hid) {
    return (hid && this.states[hid]) || null;
  }

  // The game hands over a snapshot (or null: the hero died, so its spawn place
  // goes back to the city default). Local first, socket second.
  save(hid, state) {
    if (!hid) return;
    if (state) {
      // AN EMPTY KIT NEVER OVERWRITES A STORED ONE. There is no way in the
      // game to end up owning nothing once you own something -- swapping a
      // weapon always leaves one in the slot, and dying keeps what you carry
      // -- so a snapshot with no weapons in it is a snapshot taken before the
      // hero's own were back (the models are downloads), not a hero who lost
      // them. Letting one of those through is exactly how a personage turned
      // up at the warehouse with empty slots.
      const prev = this.states[hid];
      if (prev) {
        if (!state.guns?.length && prev.guns?.length) state.guns = prev.guns;
        if (!state.gear?.length && prev.gear?.length) state.gear = prev.gear;
      }
      state.at = Date.now();
      this.states[hid] = state;
    } else {
      delete this.states[hid];
    }
    this._writeLocal();
    this._queue.set(hid, state ?? null);
    this._send();
  }

  // Push everything pending right now (back to the menu, page unload).
  //
  // The snapshot comes FIRST. Without it a reload only ever stored whatever
  // the 2.5 s autosave happened to catch, so refreshing while running threw
  // away up to ~26 m of movement -- enough to walk into the warehouse, reload,
  // and wake up back outside it.
  flush({ leaving = false } = {}) {
    // pagehide, beforeunload AND visibilitychange all fire on a single close,
    // and each one would take a snapshot and post its own beacon. One is
    // enough; a tab-switch that comes back and keeps playing is past this
    // window long before the next one.
    if (leaving) {
      const now = Date.now();
      if (now - (this._leftAt ?? 0) < 1000) return;
      this._leftAt = now;
    }
    try {
      this.provider?.();
    } catch (e) {
      console.warn('snapshot on unload failed:', e);
    }
    if (leaving) this._beacon();
    this._send();
  }

  // The page is going away. A WebSocket frame sent now is routinely dropped --
  // the renderer is torn down before it leaves -- so the socket alone meant the
  // last position only ever reached localStorage. sendBeacon is the one request
  // the browser promises to deliver, so the final spot lands in the hero DB.
  _beacon() {
    if (!this._queue.size || !this.token || !navigator.sendBeacon) return;
    for (const [hid, state] of this._queue) {
      try {
        const body = new Blob([JSON.stringify({ token: this.token, hid, state })],
          { type: 'application/json' });
        if (navigator.sendBeacon('/api/hero-state', body)) this._queue.delete(hid);
      } catch { /* the socket below is still worth a try */ }
    }
  }

  // ---- socket ----

  _connect() {
    if (!this._wanted || !this.token || this.ws) return;
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    let ws;
    try {
      ws = new WebSocket(`${proto}://${location.host}/ws?token=${encodeURIComponent(this.token)}`);
    } catch {
      return this._scheduleRetry();
    }
    this.ws = ws;
    ws.onopen = () => {
      this.connected = true;
      this._retry = 0;
      this._send();
    };
    ws.onmessage = (e) => {
      let msg;
      try {
        msg = JSON.parse(e.data);
      } catch {
        return;
      }
      if (msg.t === 'hello' || msg.t === 'states') this._merge(msg.states);
    };
    ws.onclose = (e) => {
      this.connected = false;
      this.ws = null;
      // 4001: the token is stale — reconnecting would just fail again.
      if (e.code !== 4001) this._scheduleRetry();
    };
    ws.onerror = () => { /* onclose does the retrying */ };
  }

  _scheduleRetry() {
    if (!this._wanted) return;
    clearTimeout(this._retryT);
    const wait = Math.min(30000, 1000 * 2 ** this._retry++);
    this._retryT = setTimeout(() => this._connect(), wait);
  }

  _send() {
    if (!this._queue.size) return;
    if (this.ws?.readyState !== WebSocket.OPEN) return; // keep it queued
    for (const [hid, state] of this._queue) {
      this.ws.send(JSON.stringify({ t: 'save', hid, state }));
    }
    this._queue.clear();
  }

  // The server is the shared truth, but a snapshot this browser wrote while
  // the backend was down is newer — newest `at` wins, per hero.
  _merge(remote) {
    if (!remote || typeof remote !== 'object') return;
    for (const [hid, state] of Object.entries(remote)) {
      const mine = this.states[hid];
      if (!mine || (state?.at ?? 0) >= (mine.at ?? 0)) this.states[hid] = state;
      else this._queue.set(hid, mine); // ours is newer — push it up
    }
    // spots this browser saved while the backend was down are new to it
    for (const [hid, state] of Object.entries(this.states)) {
      if (!(hid in remote)) this._queue.set(hid, state);
    }
    this._writeLocal();
    this._send();
    this.onStates?.(this.states);
  }

  _readLocal() {
    try {
      return JSON.parse(localStorage.getItem(LS_KEY(this.username))) || {};
    } catch {
      return {};
    }
  }

  _writeLocal() {
    try {
      localStorage.setItem(LS_KEY(this.username), JSON.stringify(this.states));
    } catch { /* storage full/blocked — the socket still has it */ }
  }
}
