import { defineConfig } from 'vite';

// WHY THERE IS A CACHE PLUGIN IN HERE.
//
// Vite's default for everything it serves is `Cache-Control: no-cache`, which
// means "you may keep this, but ask me before using it again". That is the right
// rule for the heavy read-only assets -- a rebuilt model comes through because
// its ETag/mtime changed -- but it is a weak one for code, so modules are served
// `no-store`: the browser may not keep them at all and a plain reload refetches.
//
// NOTE: this plugin was written while chasing "a refresh shows yesterday's city"
// and it did NOT fix that. The culprit was a service worker left on
// localhost:5173 by another Vite project on this machine, answering our asset
// requests out of its own Cache Storage; see the comment at the top of
// index.html. Headers never reached that decision. The plugin stays because
// no-store on code is still what we want, not because it cured anything.
const KEEPABLE = /^\/(models|textures|tbilisi|audio|draco)\/|\/\.vite\/deps\//;

const freshCode = {
  name: 'tf-fresh-code',
  apply: 'serve',
  // NB: block bodies. `(s) => s.middlewares.use(stamp)` RETURNS the connect app,
  // and Vite calls a returned value as the post-hook -- which crashes the server
  // on startup with "Cannot read properties of undefined (reading 'url')".
  configureServer(s) { s.middlewares.use(stamp); },
  configurePreviewServer(s) { s.middlewares.use(stamp); },
};

// Vite sets its own Cache-Control on these responses AFTER plugin middleware has
// run (no-cache for a transformed module, immutable for a pre-bundled dep), and a
// middleware that runs later never gets the chance because the transform ends the
// response. So the header is pinned: whatever tries to set Cache-Control after
// this gets our value instead.
function stamp(req, res, next) {
  const want = KEEPABLE.test(req.url ?? '') ? 'no-cache' : 'no-store';
  const set = res.setHeader.bind(res);
  res.setHeader = (k, v) => set(k, /^cache-control$/i.test(k) ? want : v);
  set('Cache-Control', want);
  next();
}

export default defineConfig({
  plugins: [freshCode],
  server: {
    proxy: {
      // damage API (Express + Mongo, see docker-compose.yml)
      '/api': 'http://localhost:3001',
      // hero-state socket: where every personage last stood (server/index.js)
      '/ws': { target: 'ws://localhost:3001', ws: true },
    },
  },
});
