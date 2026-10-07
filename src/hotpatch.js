// LIVE PATCHING, for a game that takes half a minute to get back to where you
// were standing.
//
// An ordinary hot update is almost useless here. Vite swaps the module, but the
// hero on screen was built from the OLD class and keeps the OLD methods, so
// nothing changes -- and the fallback, a full reload, means logging in again and
// rebuilding the entire city to look at a number that moved by 0.05.
//
// So don't replace the objects: replace the METHODS UNDER them. Copying the new
// class's prototype onto the live class's prototype means every personage already
// in the scene runs the new code on its very next frame, with all of its state
// intact -- where it is standing, what it is holding, which way it is facing, the
// roll it is halfway through. The new methods close over the NEW module's scope,
// so retuned constants come with them.
//
// What this cannot do: a method you DELETE stays on the live prototype until a
// reload, and a change to a module's top-level data (a catalogue, a lookup table
// that something already copied) only reaches code that reads it afresh. Both are
// loud in the console rather than silent.
export function patchClass(Live, Next, label = '') {
  if (!Live || !Next || Live === Next) return false;
  const copy = (to, from, skip) => {
    for (const k of Object.getOwnPropertyNames(from)) {
      if (skip.includes(k)) continue;
      const d = Object.getOwnPropertyDescriptor(from, k);
      if (d) try { Object.defineProperty(to, k, d); } catch { /* non-writable: let it be */ }
    }
  };
  copy(Live.prototype, Next.prototype, ['constructor']);
  copy(Live, Next, ['length', 'name', 'prototype']);
  if (label) console.log(`[hmr] ${label}: live objects patched, scene kept`);
  return true;
}

// Same idea for a module that exports plain functions rather than a class. The
// importers cannot see a new binding, so they call through a holder object whose
// identity never changes, and the update pours the new functions into it.
export function patchFns(live, next, label = '') {
  if (!live || !next) return false;
  Object.assign(live, next);
  if (label) console.log(`[hmr] ${label}: swapped live`);
  return true;
}
