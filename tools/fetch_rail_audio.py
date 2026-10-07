#!/usr/bin/env python3
"""Pull REAL recordings for the target carrier on the shooting range and cut
them into a seamless motor LOOP plus the end-stop clunk.

Why this is a separate tool from fetch_gun_audio.py: a gunshot is a one-shot,
and that tool is built to find the bang and cut around it.  A carrier motor is
the opposite -- a sound with no event in it at all, that has to run for as long
as the finger is on the button and come round to its own start without a seam.
Finding the steadiest stretch of a recording and crossfading its own tail onto
its head is a different job, so it lives here.  The clunk IS a one-shot, so
that half is handed straight to the gun tool's cutter.

WHAT A TARGET CARRIER ACTUALLY IS: a small electric motor dragging a trolley
along an overhead rail on a cable.  That is, mechanically and acoustically, a
garage door opener -- same motor, same rail, same trolley, same load -- and a
linear actuator under load is the same again at close quarters.  Nobody has
published a recording of a range retriever, so those are what this uses, and
they are the real thing rather than an oscillator pretending.

Only CC0 and CC-BY are accepted; the licence is read off each sound's own page
and anything else is skipped.  Credits land in public/audio/rail/credits.json.

    python3 tools/fetch_rail_audio.py            # fetch, cut, write
    python3 tools/fetch_rail_audio.py --list     # show what is on disk
"""
import json
import os
import subprocess
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fetch_gun_audio as gun   # noqa: E402  -- sound_info/get/shots/cut, same pipeline

OUT = os.path.join(os.path.dirname(__file__), '..', 'public', 'audio', 'rail')
RAW = os.path.join(os.path.dirname(__file__), 'cache', 'rail_audio')
SR = 44100

# role -> freesound ids, best first.
MANIFEST = {
    # The motor, as a loop. Several takes so two trips down the lane are not
    # bit-for-bit the same machine.
    'rail_motor': dict(ids=[683806, 683807, 741372, 717554], kind='loop',
                       seconds=1.5, takes=1),
    # The hanger arriving at its end stop: metal reaching the end of a rail and
    # being stopped by it.
    'rail_stop':  dict(ids=[865928, 717554, 545024], kind='oneshot',
                       takes=2, maxlen=0.7, minpeak=-26, floor=0.06),
}

XFADE = 0.18        # s of crossfade at the loop join
TARGET_RMS_DB = -20.0   # loops are level-matched by RMS, not by peak: a peak
CEILING_DB = -1.5       # match leaves one noisy take twice as loud as the rest
MIN_RMS_DB = -46.0      # below this the "steady part" is just room tone


def decode(path):
    """The whole file as mono float32 at 44.1 kHz."""
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(SR), '-f', 's16le', '-'],
        capture_output=True).stdout
    return np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0


def steadiest(a, n, hop=0.005):
    """Start sample of the most CONSTANT window of `n` samples.

    A motor recording is a start-up thump, a steady run and a stop. Only the
    middle is loopable, and "steady" is exactly what picks it out: the window
    whose level wanders least, as a fraction of how loud it is. Dividing by the
    mean is what stops it choosing a quiet patch of nothing -- silence is very
    steady indeed.
    """
    h = int(SR * hop)
    e = np.abs(a[:len(a) // h * h].reshape(-1, h)).max(axis=1)
    w = max(2, n // h)
    if len(e) <= w:
        return 0, -99.0
    # rolling mean and rolling std, via cumulative sums
    c1 = np.concatenate([[0.0], np.cumsum(e)])
    c2 = np.concatenate([[0.0], np.cumsum(e * e)])
    k = len(e) - w + 1          # that many windows fit, not len(e) - w
    mean = (c1[w:w + k] - c1[:k]) / w
    var = np.maximum(0.0, (c2[w:w + k] - c2[:k]) / w - mean ** 2)
    quiet = mean < np.percentile(mean, 55)      # ignore the quiet half outright
    score = np.where(quiet, np.inf, np.sqrt(var) / np.maximum(mean, 1e-6))
    i = int(np.argmin(score))
    seg = a[i * h:i * h + n]
    rms = 20 * np.log10(max(1e-9, float(np.sqrt(np.mean(seg ** 2)))))
    return i * h, rms


def loop_cut(src, dst, seconds):
    """Cut a seamless loop out of the steadiest stretch of `src`.

    The join is a crossfade of the loop's own continuation onto its own head,
    so the sample that plays after the last one is the sample that followed it
    in the recording. Equal-power (cos/sin), because motor noise either side of
    the seam is uncorrelated and a linear blend dips in the middle of it.
    """
    a = decode(src)
    n = int(SR * seconds)
    x = int(SR * XFADE)
    if len(a) < n + 2 * x:
        return None
    start, rms = steadiest(a[:len(a) - x], n)
    if rms < MIN_RMS_DB:
        return None
    seg = a[start:start + n + x]
    if len(seg) < n + x:
        return None
    body = seg[x:x + n].copy()
    t = np.linspace(0, np.pi / 2, x, dtype=np.float32)
    body[n - x:] = seg[n:n + x] * np.cos(t) + seg[0:x] * np.sin(t)
    # level: RMS-matched, then pulled down if that would clip
    cur = 20 * np.log10(max(1e-9, float(np.sqrt(np.mean(body ** 2)))))
    body *= 10 ** ((TARGET_RMS_DB - cur) / 20)
    pk = float(np.max(np.abs(body)))
    if pk > 10 ** (CEILING_DB / 20):
        body *= 10 ** (CEILING_DB / 20) / pk
    with wave.open(dst, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(body, -1, 1) * 32767).astype('<i2').tobytes())
    # DOES IT CLICK? Not "how far apart are the two end samples" -- on a noisy
    # signal two unrelated samples sit about 1.4 RMS apart and that number says
    # nothing. A click is a sample-to-sample STEP bigger than the waveform
    # normally takes, so the join's step is measured against the loop's own
    # distribution of steps. Under 1.0 the seam is not doing anything the
    # signal does not already do every millisecond, and nothing is audible.
    d = np.abs(np.diff(body))
    click = abs(float(body[0]) - float(body[-1])) / max(1e-9, float(np.percentile(d, 99.9)))
    cen = spectral_centroid(body)
    return dict(seconds=len(body) / SR, click=click, centroid=cen)


def spectral_centroid(a):
    """Where the energy sits, in Hz -- a motor should be down in the hundreds,
    not up in the thousands where hiss and handling noise live."""
    w = np.hanning(len(a)).astype(np.float32)
    m = np.abs(np.fft.rfft(a * w))
    f = np.fft.rfftfreq(len(a), 1 / SR)
    return float((f * m).sum() / max(1e-9, m.sum()))


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(RAW, exist_ok=True)
    if '--list' in sys.argv:
        for f in sorted(os.listdir(OUT)):
            if f.endswith('.wav'):
                print(f, os.path.getsize(os.path.join(OUT, f)), 'bytes')
        return
    bank, credits = {}, []
    for key, spec in MANIFEST.items():
        files = []
        for sid in spec['ids']:
            try:
                info = gun.sound_info(sid)
            except Exception as e:                       # noqa: BLE001
                print(f'  !! {key}/{sid}: {e}')
                continue
            if not info:
                print(f'  !! {key}/{sid}: no preview on the page')
                continue
            if not info['licence']:
                print(f'  -- {key}/{sid} "{info["title"]}": not CC0/CC-BY, skipped')
                continue
            mp3 = os.path.join(RAW, f'{sid}.mp3')
            if not os.path.exists(mp3):
                with open(mp3, 'wb') as fh:
                    fh.write(gun.get(info['url'], binary=True))
            made = []
            if spec['kind'] == 'loop':
                name = f'{key}_{sid}.wav'
                r = loop_cut(mp3, os.path.join(OUT, name), spec['seconds'])
                if r:
                    made.append(name)
                    print(f'  ok {key:<11} {info["licence"]:<9} "{info["title"][:38]:<38}" '
                          f'by {info["author"][:16]:<16} loop {r["seconds"]:.2f}s '
                          f'click {r["click"]:.2f} centroid {r["centroid"]:.0f} Hz')
                else:
                    print(f'  -- {key}/{sid}: no steady stretch long enough')
            else:
                ons, _ = gun.shots(mp3, floor=spec.get('floor', 0.1))
                for i, t in enumerate(ons):
                    room = (min(spec['maxlen'], max(0.0, ons[i + 1] - t - 0.02))
                            if i + 1 < len(ons) else spec['maxlen'])
                    if room < gun.MIN_TAKE:
                        continue
                    name = f'{key}_{sid}_{len(made)}.wav'
                    try:
                        if not gun.cut(mp3, os.path.join(OUT, name), t, room,
                                       spec.get('minpeak', gun.MIN_PEAK_DB)):
                            continue
                    except subprocess.CalledProcessError as e:
                        print(f'  !! {name}: ffmpeg {e.stderr.decode()[:110]}')
                        continue
                    made.append(name)
                    if len(made) >= spec['takes']:
                        break
                if made:
                    print(f'  ok {key:<11} {info["licence"]:<9} "{info["title"][:38]:<38}" '
                          f'by {info["author"][:16]:<16} {len(made)} take(s)')
            if not made:
                continue
            files += made
            credits.append(dict(key=key, **{k: info[k] for k in
                                            ('id', 'title', 'author', 'licence', 'page')},
                                files=made))
        if files:
            bank[key] = files
    with open(os.path.join(OUT, 'bank.json'), 'w') as fh:
        json.dump(bank, fh, indent=1)
    with open(os.path.join(OUT, 'credits.json'), 'w') as fh:
        json.dump(credits, fh, indent=1, ensure_ascii=False)
    total = sum(os.path.getsize(os.path.join(OUT, f)) for fs in bank.values() for f in fs)
    print(f'\n{sum(len(v) for v in bank.values())} clips across {len(bank)} roles, '
          f'{total / 1024:.0f} kB total -> public/audio/rail/')


if __name__ == '__main__':
    main()
