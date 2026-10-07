#!/usr/bin/env python3
"""Pull REAL aircraft recordings off Freesound and cut them into the loops and
one-shots the plane engine needs.

Why this is a separate tool from fetch_gun_audio.py: a GUNSHOT is a one-shot
and an ENGINE is a loop, and almost nothing about cutting the two is the same.
A gunshot take is found by looking for an attack and is thrown away if a second
bang sneaks in; an engine take is found by looking for the STEADIEST stretch of
a long recording and then has to be made to join onto itself without a click.
So this tool has three cutters the gun tool does not:

    loop   the calmest `dur` seconds of the file, crossfaded head-to-tail so it
           can run forever (an engine loop with a seam is the single most
           recognisably-fake sound a flight game can make)
    start  the ignition: the first place the recording comes up out of near
           silence, taken from a beat BEFORE it so the starter's first turn is
           not clipped off
    stop   the shutdown: the last place it goes back down to near silence

Real engines, not oscillators. src/sound.js synthesises a prop out of three
detuned saws, and it sounds like three detuned saws: a Merlin starting is a
specific sequence of events (the starter whines, blades turn over dry, one
cylinder catches, it stumbles, the rest come in, the exhaust settles) and no
oscillator chain gets there. These are recordings of real aeroplanes:

    C-47 / DC-4      twin and four-engine radials, INTERIOR — you are the pilot
    WWII fighter     a Merlin-class V-12 being revved
    P-51 Mustang     the radial-engine recording for the hot end
    Cessna / Piper   light-plane idles, for the small airframes
    F-16 / F-22      turbine start, idle and full power
    Jet canopy       a fighter canopy actually closing, for the hood

Only CC0 and CC-BY are accepted; the licence is read off each sound's own page
and anything else is skipped. Credits land in public/audio/planes/credits.json.

16-bit mono 44.1 kHz WAV, like the gun bank: an mp3's encoder padding is both a
timing error on a one-shot and a guaranteed click in a loop.

    python3 tools/fetch_plane_audio.py            # fetch, cut, write
    python3 tools/fetch_plane_audio.py --list     # show what is on disk
"""
import html
import json
import os
import re
import struct
import subprocess
import sys
import urllib.parse
import urllib.request
import wave

import numpy as np

OUT = os.path.join(os.path.dirname(__file__), '..', 'public', 'audio', 'planes')
# The downloaded mp3s stay OUT of public/: vite copies that folder into dist
# verbatim and nothing should ship the sources the takes were cut from.
RAW = os.path.join(os.path.dirname(__file__), 'cache', 'plane_audio')
UA = 'Mozilla/5.0 (tbilisifly asset fetcher)'
SR = 44100
OK_LICENCES = {
    'publicdomain/zero/1.0': 'CC0',
    'licenses/by/3.0': 'CC-BY 3.0',
    'licenses/by/4.0': 'CC-BY 4.0',
}

# role -> freesound ids, best first.
#
#   kind   'loop' | 'start' | 'stop' | 'oneshot'
#   dur    seconds of audio wanted out of each source
#   takes  how many separate cuts (oneshot only)
#
# The loops come in PAIRS per engine type — a low one and a high one — because
# that is how the engine is voiced: src/sound.js crossfades idle into run as the
# RPM comes up and only trims the pitch the rest of the way. One loop stretched
# across the whole range by playbackRate alone is a chipmunk at full throttle.
MANIFEST = {
    # ---- piston / propeller ---------------------------------------------------
    # G06-16 is a C-47 starting and idling from INSIDE the aeroplane, which is
    # exactly where the player is sitting: both radials turning over, catching
    # and settling, with the airframe around the microphone. G07-17 is the same
    # event on a four-engine DC-4, as a second take so starting twice in a row
    # is not the same sample.
    'prop_start': dict(ids=[437835, 437861], kind='start', dur=5.0, sr=32000),
    # Idle: a light aircraft ticking over. Three different aeroplanes, so a
    # taxiing Spitfire and a taxiing Cessna are not the same recording.
    'prop_idle':  dict(ids=[437757, 437764, 437758], kind='loop', dur=3.2, sr=32000),
    # Power: a WWII fighter's V-12 being revved, a Mustang, and a general prop
    # plane under load. This is the loop you hear at full throttle.
    'prop_run':   dict(ids=[437792, 614743, 547068], kind='loop', dur=3.2, sr=32000),
    # Shutdown: the mixture is cut and the blades wind down to a stop. Both of
    # these recordings end in exactly that.
    'prop_stop':  dict(ids=[437834, 478218], kind='stop', dur=4.5, sr=32000),
    # ---- turbines -------------------------------------------------------------
    # An F-16 starting from cold is a 20-second event and the recording holds
    # all of it; SamsterBirdies' startup is the second take.
    'jet_start':  dict(ids=[322178, 491407], kind='start', dur=6.0),
    'jet_idle':   dict(ids=[187734, 437890], kind='loop', dur=3.2),
    'jet_run':    dict(ids=[152509, 437922], kind='loop', dur=3.2),
    # Afterburner, for the supersonic types at full throttle — a different
    # sound from a turbine at military power, not a louder one.
    'jet_ab':     dict(ids=[104883, 829068], kind='loop', dur=2.6, sr=32000),
    # NO jet_stop: nothing on offer is a turbine running down, and a spool-down
    # is one thing synthesis genuinely does right — it IS the idle loop with the
    # rate falling away, which is what src/sound.js does with it.
    # ---- the cockpit itself ---------------------------------------------------
    # R02-48 is a fighter canopy closing, 31 s of it, so the latch, the slide
    # and the thump all come out as separate takes.
    'canopy':     dict(ids=[479492], kind='oneshot', takes=4, dur=2.2,
                       minpeak=-26, floor=0.05),
    # Strapping in. A car belt is the same mechanism as a lap strap and the
    # same click; a five-point harness recording does not exist on Freesound.
    'belt':       dict(ids=[384406, 538704, 830198], kind='oneshot', takes=2, dur=1.2,
                       minpeak=-28, floor=0.05),
    # Gear coming up and going down.
    'gear':       dict(ids=[425273, 763119], kind='oneshot', takes=2, dur=2.6,
                       minpeak=-26, floor=0.05),
    # Wheels arriving: a light aeroplane actually touching down on a runway.
    'touchdown':  dict(ids=[437756], kind='oneshot', takes=3, dur=1.4,
                       minpeak=-24, floor=0.07),
}


def get(url, binary=False):
    req = urllib.request.Request(url, headers={'User-Agent': UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read()
    return data if binary else data.decode('utf8', 'replace')


def sound_info(sid):
    """Resolve a freesound id to {title, author, licence, preview}."""
    page = get(f'https://freesound.org/s/{sid}/')
    title = re.search(r'<title>\s*Freesound\s*-\s*(.*?)\s*by\s*(.*?)\s*</title>', page, re.S)
    lic = None
    for frag, name in OK_LICENCES.items():
        if frag in page:
            lic = name
            break
    prev = re.search(r'previews/(\d+)/(\d+)_(\d+)-hq\.mp3', page)
    if not prev:
        return None
    return dict(
        id=sid,
        title=html.unescape(title.group(1).strip()) if title else f'sound {sid}',
        author=html.unescape(title.group(2).strip()) if title else '?',
        licence=lic,
        url=f'https://cdn.freesound.org/previews/{prev.group(1)}/{prev.group(2)}_{prev.group(3)}-hq.mp3',
        page=f'https://freesound.org/s/{sid}/',
    )


# ---- the audio itself -------------------------------------------------------
# Everything from here down works on a float32 mono array at SR. ffmpeg only
# decodes; the cutting, the loop seam and the levels are all done here, because
# a crossfaded loop seam is not something ffmpeg's filter graph expresses
# without writing the fade curves out by hand anyway.

def decode(path):
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(SR), '-f', 's16le', '-'],
        capture_output=True).stdout
    return np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0


def write_wav(path, a, sr=SR):
    """16-bit mono. `sr` below SR resamples on the way out.

    Measured before choosing: a WWII fighter at power has nothing above 11 kHz
    worth 34 dB of headroom, and an afterburner is 35 dB down up there -- so
    those roles ship at 32 kHz and nobody can tell. A TURBINE is the exception,
    its hiss sits only 9 dB under the whole signal, so the jet loops keep the
    full 44.1 kHz. Dropping them all would have hollowed out the one engine
    that actually lives up there.
    """
    a = np.clip(a, -1.0, 1.0)
    if sr != SR:
        n = int(round(len(a) * sr / SR))
        a = np.interp(np.linspace(0, len(a) - 1, n), np.arange(len(a)), a).astype(np.float32)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((a * 32767.0).astype('<i2').tobytes())


HOP = 0.02  # s — envelope resolution


def rms_env(a, hop=HOP):
    h = int(SR * hop)
    n = len(a) // h
    if n < 2:
        return np.zeros(0)
    f = a[:n * h].reshape(n, h)
    return np.sqrt((f * f).mean(axis=1))


TARGET_PEAK = 0.891  # -1 dBFS, the level the gun bank is normalised to


def normalise(a, target=TARGET_PEAK):
    pk = np.abs(a).max()
    return a * (target / pk) if pk > 1e-5 else a


def peak_db(a):
    pk = float(np.abs(a).max())
    return 20 * np.log10(pk) if pk > 1e-9 else -99.0


def steadiest(a, dur, xf):
    """Start sample of the calmest `dur + xf` seconds of `a`.

    An engine recording is a minute of somebody walking around an aeroplane:
    there is a car door, a voice, a gust, the mic being bumped. The loop has to
    come out of the stretch where NONE of that happens, or it repeats every
    three seconds with a cough in it. "Calmest" = loud on average (so it is the
    engine and not a gap) with the least variation in level across the window.
    """
    need = int(SR * (dur + xf))
    if len(a) < need + SR // 2:
        return 0
    e = rms_env(a)
    w = max(2, int((dur + xf) / HOP))
    best, bestScore = 0, -1e9
    # 50 ms steps through the file; anything finer is below the resolution the
    # envelope has anyway.
    step = max(1, int(0.05 / HOP))
    for i in range(0, len(e) - w, step):
        win = e[i:i + w]
        m = win.mean()
        if m < 1e-4:
            continue
        # relative spread, so a quiet idle is judged on the same scale as a
        # fighter at full chat
        spread = win.std() / m
        # a frame that is a tenth of the window's level is a hole in it — the
        # loop would breathe once a cycle
        holes = float((win < m * 0.25).mean())
        score = m * 0.6 - spread * 0.9 - holes * 3.0
        if score > bestScore:
            best, bestScore = i, score
    s = int(best * HOP * SR)
    return min(s, len(a) - need)


def cut_loop(a, dur, xf=0.25):
    """`dur` seconds that join onto themselves without a click.

    Take dur + xf seconds, then fold the extra xf back over the head with
    complementary (equal-power) fades. The result is exactly dur long and its
    last sample flows into its first, because the two are the same audio
    crossfaded — which is the only way to loop a recording that was never
    recorded as a loop.
    """
    s = steadiest(a, dur, xf)
    n = int(SR * dur)
    m = int(SR * xf)
    seg = a[s:s + n + m]
    if len(seg) < n + m:
        return None
    body = seg[:n].copy()
    tail = seg[n:n + m]
    t = np.linspace(0, 1, m, dtype=np.float32)
    # equal-power (sin/cos) rather than linear: a linear crossfade of two
    # uncorrelated noise-like signals dips ~3 dB in the middle, and an engine
    # loop that ducks once per cycle is a throb nobody can un-hear
    body[:m] = body[:m] * np.sin(t * np.pi / 2) + tail * np.cos(t * np.pi / 2)
    return normalise(body)


def onset(a, floor_rel=0.22, hold=0.35):
    """Where the recording FIRST comes up and stays up.

    Not an attack detector: a starter motor comes in over a second or two and
    an attack detector looks straight past it. This looks for the first frame
    above a fraction of the file's own typical level that then STAYS there for
    `hold` seconds, which is what distinguishes an engine starting from a door
    slamming in the quiet before it.
    """
    e = rms_env(a)
    if not len(e):
        return 0.0
    ref = np.percentile(e, 70)
    if ref < 1e-5:
        return 0.0
    thr = ref * floor_rel
    w = max(1, int(hold / HOP))
    for i in range(len(e) - w):
        if e[i] >= thr and (e[i:i + w] >= thr * 0.6).all():
            return i * HOP
    return 0.0


def shutdown_window(a, dur):
    """Find the WIND-DOWN: where the engine falls from running to silence.

    The first version looked for "the last place it goes back down" and took
    `dur` seconds ENDING there. On a recording that is engine noise from end to
    end -- which both of the ones on offer are, because the shutdown is a few
    seconds at the end of a 71 s take -- that picks four and a half seconds of
    an engine AT FULL CHAT and calls it a shutdown. Climbing out of the
    aeroplane then played a Merlin running hard for four seconds, which is the
    exact opposite of what the sound is for.

    A shutdown is not a position in the file, it is a SHAPE: loud, then a
    sustained fall, then quiet. Find the last moment the engine is properly
    loud, find the first moment after that when it has gone quiet and stays
    quiet, and take the span between. No such span means the recording does
    not contain a shutdown, and it has to be rejected rather than guessed at.
    """
    e = rms_env(a)
    if len(e) < 10:
        return None
    ref = np.percentile(e, 70)
    if ref < 1e-5:
        return None
    loud = ref * 0.55
    quiet = ref * 0.10
    hi = None
    for i in range(len(e) - 1, -1, -1):
        if e[i] >= loud:
            hi = i
            break
    if hi is None:
        return None
    hold = max(2, int(0.3 / HOP))
    lo = None
    for i in range(hi + 1, len(e) - hold):
        if (e[i:i + hold] < quiet).all():
            lo = i
            break
    if lo is None:
        return None            # cut off while still running
    fall = (lo - hi) * HOP
    if fall < 0.5 or fall > dur * 3:
        return None
    return max(0.0, hi * HOP - 0.5), min(len(a) / SR, lo * HOP + 0.4)


def decays(seg, by_db=8.0):
    """Does the take actually get quieter? The acceptance test for a wind-down."""
    e = rms_env(seg)
    if len(e) < 6:
        return False
    k = max(1, len(e) // 5)
    head = float(e[:k].mean())
    tail = float(e[-k:].mean())
    if head < 1e-6:
        return False
    return 20 * np.log10(max(tail, 1e-6) / head) <= -by_db


def fade(a, fin=0.02, fout=0.08):
    a = a.copy()
    ni, no = int(SR * fin), int(SR * fout)
    if ni and len(a) > ni:
        a[:ni] *= np.linspace(0, 1, ni, dtype=np.float32)
    if no and len(a) > no:
        a[-no:] *= np.linspace(1, 0, no, dtype=np.float32)
    return a


def cut_start(a, dur, pre=0.4):
    """The ignition, with `pre` seconds of air in front of it so the starter's
    first turn is not clipped off."""
    s = max(0, int(SR * (onset(a) - pre)))
    seg = a[s:s + int(SR * dur)]
    if len(seg) < SR * 0.6:
        return None
    return fade(normalise(seg), 0.01, 0.12)


def cut_stop(a, dur):
    """The shutdown: the span over which the engine actually winds down.

    VERIFIED, not assumed: a take that does not end at least 8 dB below where
    it started is not a shutdown, and shipping one means climbing out of the
    aeroplane plays a running engine.
    """
    win = shutdown_window(a, dur)
    if win is None:
        return None
    s0, s1 = win
    seg = a[int(s0 * SR):int(s1 * SR)]
    if len(seg) < SR * 0.8:
        return None
    if len(seg) > SR * dur:          # keep the END of an over-long fall
        seg = seg[-int(SR * dur):]
    if not decays(seg):
        return None
    return fade(normalise(seg), 0.03, 0.25)


def attacks(a, floor=0.1, jump=2.2, gap=0.12):
    """Where the EVENTS are, for the one-shots (canopy, belt, gear, wheels).
    Same rule as the gun tool: a frame that is loud and several times louder
    than the last little while."""
    e = rms_env(a, 0.005)
    out = []
    for i in range(3, len(e)):
        prev = e[max(0, i - 12):i - 1]
        if e[i] < floor or not len(prev) or e[i] < prev.max() * jump:
            continue
        if out and (i - out[-1]) * 0.005 < gap:
            continue
        out.append(i)
    res = [round(i * 0.005, 3) for i in out]
    if len(e) > 4 and e[:4].max() >= floor and (not res or res[0] > gap):
        res.insert(0, 0.0)
    return res


def cut_oneshot(a, t, dur, minpeak):
    s = max(0, int(SR * (t - 0.02)))
    seg = a[s:s + int(SR * dur)]
    if len(seg) < SR * 0.08 or peak_db(seg) < minpeak:
        return None
    # trim the tail back to where it actually goes quiet, so a 2 s window
    # around a half-second latch does not ship 1.5 s of room
    e = rms_env(seg, 0.01)
    if len(e):
        thr = e.max() * 0.08
        live = np.nonzero(e > thr)[0]
        if len(live):
            seg = seg[:min(len(seg), int((live[-1] + 4) * 0.01 * SR))]
    if len(seg) < SR * 0.08:
        return None
    return fade(normalise(seg), 0.004, 0.05)


def main():
    out = os.path.abspath(OUT)
    os.makedirs(out, exist_ok=True)
    os.makedirs(RAW, exist_ok=True)
    if '--list' in sys.argv:
        for f in sorted(os.listdir(out)):
            print(f, os.path.getsize(os.path.join(out, f)), 'bytes')
        return
    only = [a for a in sys.argv[1:] if not a.startswith('-')]

    bank, credits = {}, []
    if only:  # keep what is already there when re-cutting one role
        for f in ('bank.json', 'credits.json'):
            p = os.path.join(out, f)
            if os.path.exists(p):
                d = json.load(open(p))
                if f == 'bank.json':
                    bank = d
                else:
                    credits = d

    for key, spec in MANIFEST.items():
        if only and key not in only:
            continue
        files = []
        credits = [c for c in credits if c.get('key') != key]
        for sid in spec['ids']:
            try:
                info = sound_info(sid)
            except Exception as e:                      # noqa: BLE001
                print(f'  !! {key}/{sid}: {e}')
                continue
            if not info:
                print(f'  !! {key}/{sid}: no preview on the page')
                continue
            if not info['licence']:
                print(f'  -- {key}/{sid} "{info["title"]}": licence not CC0/CC-BY, skipped')
                continue
            mp3 = os.path.join(RAW, f'{sid}.mp3')
            if not os.path.exists(mp3):
                try:
                    with open(mp3, 'wb') as fh:
                        fh.write(get(info['url'], binary=True))
                except Exception as e:                  # noqa: BLE001
                    print(f'  !! {key}/{sid}: download failed: {e}')
                    continue
            a = decode(mp3)
            if len(a) < SR * 0.5:
                print(f'  !! {key}/{sid}: decoded to nothing')
                continue

            made = []
            if spec['kind'] == 'oneshot':
                ons = attacks(a, floor=spec.get('floor', 0.1))
                for t in ons:
                    seg = cut_oneshot(a, t, spec['dur'], spec.get('minpeak', -20))
                    if seg is None:
                        continue
                    name = f'{key}_{sid}_{len(made)}.wav'
                    write_wav(os.path.join(out, name), seg, spec.get('sr', SR))
                    made.append(name)
                    if len(made) >= spec.get('takes', 2):
                        break
            else:
                seg = {'loop': lambda: cut_loop(a, spec['dur']),
                       'start': lambda: cut_start(a, spec['dur']),
                       'stop': lambda: cut_stop(a, spec['dur'])}[spec['kind']]()
                if seg is not None:
                    name = f'{key}_{sid}.wav'
                    write_wav(os.path.join(out, name), seg, spec.get('sr', SR))
                    made.append(name)

            if not made:
                print(f'  -- {key}/{sid} "{info["title"][:40]}": nothing usable came out')
                continue
            files += made
            credits.append(dict(key=key, **{k: info[k] for k in
                                            ('id', 'title', 'author', 'licence', 'page')},
                                files=made))
            print(f'  ok {key:<11} {info["licence"]:<9} "{info["title"][:42]}" by {info["author"]}'
                  f'  -> {len(made)} {spec["kind"]}(s)')
        if files:
            bank[key] = (bank.get(key, []) if only else []) + files
            bank[key] = sorted(set(bank[key]))

    # Drop bank entries whose file is gone, so a re-cut never leaves a 404 in
    # the manifest (a missing take is a silent role, and silence is the one
    # failure mode nobody notices until they are in the air).
    for k in list(bank):
        bank[k] = [f for f in bank[k] if os.path.exists(os.path.join(out, f))]
        if not bank[k]:
            del bank[k]
    with open(os.path.join(out, 'bank.json'), 'w') as fh:
        json.dump(bank, fh, indent=1)
    with open(os.path.join(out, 'credits.json'), 'w') as fh:
        json.dump(credits, fh, indent=1, ensure_ascii=False)
    total = sum(os.path.getsize(os.path.join(out, f)) for fs in bank.values() for f in fs)
    print(f'\n{sum(len(v) for v in bank.values())} takes across {len(bank)} roles, '
          f'{total / 1024:.0f} kB -> public/audio/planes/')


if __name__ == '__main__':
    main()
