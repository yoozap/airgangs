#!/usr/bin/env python3
"""Pull REAL gun and bullet-impact recordings off Freesound and cut them into
one-shot WAVs the game can fire instantly.

Why real samples: a synthesised 9 mm report is recognisably fake -- the muzzle
blast of a Makarov has a particular flat, dry crack that no oscillator chain
gets to.  These are actual recordings of the actual pistols:

    PM (Makarov)   9x18, subsonic -- a lower, flatter report
    Beretta 92FS   9x19, supersonic -- a sharper crack
    AK-47 / AKM    7.62x39 -- a deep, hard bang with a long tail

Only CC0 (public domain) and CC-BY (credit required) sounds are accepted; the
licence is read off each sound's own page and anything else is skipped.  Credits
land in public/audio/guns/credits.json and are listed in the README.

Output is 16-bit mono 44.1 kHz WAV, NOT mp3: an mp3 carries ~25 ms of encoder
padding at the front, which on a gunshot means the trigger pull and the bang no
longer line up.  A trimmed half-second one-shot is ~45 kB, so there is nothing
to save by compressing them.

    python3 tools/fetch_gun_audio.py            # fetch, cut, write
    python3 tools/fetch_gun_audio.py --list     # show what is on disk
"""
import html
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request

import numpy as np

OUT = os.path.join(os.path.dirname(__file__), '..', 'public', 'audio', 'guns')
# The downloaded mp3s stay OUT of public/: vite copies that folder into dist
# verbatim, and nothing should ship the sources the one-shots were cut from.
RAW = os.path.join(os.path.dirname(__file__), 'cache', 'gun_audio')
UA = 'Mozilla/5.0 (tbilisifly asset fetcher)'
OK_LICENCES = {
    'publicdomain/zero/1.0': 'CC0',
    'licenses/by/3.0': 'CC-BY 3.0',
    'licenses/by/4.0': 'CC-BY 4.0',
}

# role -> freesound ids, best first.  `takes` is how many separate one-shots to
# cut out of each recording: a 9 mm report needs variation or firing a magazine
# sounds like a loop, and several of these recordings hold a whole string of
# shots that can be split into individual takes.
MANIFEST = {
    # ---- the guns themselves -------------------------------------------------
    'makarov':      dict(ids=[569174], takes=4, maxlen=0.85, role='shot'),
    'beretta':      dict(ids=[151066, 157970, 707777], takes=4, maxlen=0.85, role='shot'),
    # AK-47 / AKM, 7.62x39 -- a far bigger event than either pistol: the muzzle
    # blast is longer and the report keeps going as whatever is around answers
    # it, so maxlen runs past the pistols'. Four separate recordings of four
    # different Kalashnikovs in four different places (open range, outdoors,
    # exterior Saiga, field) so a long string never sounds like one sample on
    # repeat. NOT 553848 (39 rounds full-auto: the bangs sit closer together
    # than MIN_TAKE and the splitter correctly refuses all of them), and NOT
    # 865984, which is openly a physically-synthesised AK-74 and not a
    # recording at all.
    'ak47':         dict(ids=[136399, 395572, 698984, 520934], takes=4, maxlen=0.9, role='shot'),
    # ---- what the round does when it arrives --------------------------------
    'imp_concrete': dict(ids=[423107, 150837, 150838, 150839], takes=2, maxlen=0.7, role='impact'),
    'imp_metal':    dict(ids=[182263, 351371, 399550], takes=2, maxlen=0.8, role='impact'),
    'imp_wood':     dict(ids=[237448], takes=2, maxlen=0.6, role='impact'),
    # Dirt is a quiet event by nature -- these are honest field recordings a long
    # way below full scale, so the "did anything happen here" floor has to drop
    # for them or every take is thrown away.
    'imp_dirt':     dict(ids=[319229, 319222, 319216, 570967], takes=2, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.03),
    'imp_water':    dict(ids=[675947], takes=4, maxlen=0.7, role='impact'),
    # No glass: nothing on offer is a round PUNCHING A HOLE in a pane (they are
    # all whole windows coming down), and the synthesised tick in audiofx.js is
    # closer to the real thing than a wrong recording would be.
    'ricochet':     dict(ids=[478345, 30932, 345413, 394187], takes=2, maxlen=1.0, role='impact'),
    'casing':       dict(ids=[210102, 179006], takes=3, maxlen=0.8, role='impact'),
    # ---- the reload, as three separate events -------------------------------
    # Not one recording of a whole reload: the magazine leaves the gun, the
    # fresh one goes in and the slide runs home at the moments the ANIMATION
    # does those things, and a single take would drift off it within half a
    # second.  So each event is its own one-shot, cut from a recording that is
    # predominantly that event and nothing else.
    #
    # 151067 is the same recordist and the same Beretta M9 as the 151066 the
    # game already fires, which is why the slide sounds like it belongs to the
    # gun in your hand.
    # A magazine and a slide are QUIET next to a gunshot -- these are honest
    # close recordings well below full scale, so the "did anything happen here"
    # floor has to come down for them the way it does for dirt, or every take is
    # thrown away as silence. 151067 was, first time round.
    'mag_out':      dict(ids=[171620, 456195, 842729], takes=2, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    'mag_in':       dict(ids=[171209, 171614, 842729], takes=2, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    'slide':        dict(ids=[151067, 233320, 854486, 842731], takes=2, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    # The AK does the same three things with different metal: the charging
    # handle instead of a slide, and a rock-in magazine that is a heavier,
    # duller clack than a pistol's. Same quiet-event treatment as above.
    # 851737-851748 are one recordist's complete AKM foley set, so the handle
    # and both magazine events belong to the SAME rifle. Rejected the airsoft
    # "AK" reloads (674742, 675008 -- the author says so himself) and the
    # freesound re-uploads of somebody's YouTube clip (712553, 758775).
    'ak_rack':      dict(ids=[851737, 171632, 142892, 543596], takes=2, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    # takes=1 here: a magazine coming out of an AK is one short clack and
    # nothing else, and asking for a second take out of a 0.4 s recording only
    # ever yielded a 60 ms fragment. One clean take each, four magazines.
    'ak_mag_out':   dict(ids=[851743, 851742, 851740, 851741], takes=1, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    'ak_mag_in':    dict(ids=[851748, 851746, 851744, 851745], takes=1, maxlen=0.7,
                         role='impact', minpeak=-30, floor=0.04),
    # Taking a weapon: off the ground with F, or out of the holster into the
    # hand. One bank for both -- what you hear either way is a pistol being
    # picked up and turned over, and the takes are varied enough that drawing
    # twice does not sound like a sampler.
    'draw':         dict(ids=[377145, 239959, 171617, 755986, 725400], takes=2, maxlen=0.8,
                         role='impact', minpeak=-30, floor=0.04),
    # ---- footsteps, one bank per ground surface ------------------------------
    # A step is the quietest thing in here -- quieter than a magazine -- so these
    # get the same low minpeak/floor as the reload events, or the splitter throws
    # every take away as silence. maxlen is 0.5 s because that is all a footstep
    # is: the heel lands, the sole settles, done. Anything longer drags the next
    # step of the walk cycle in with it.
    #
    # Three recordings per surface and four takes out of each, so a walk never
    # repeats a sample within a dozen steps. Picked for dry, close, separated
    # steps: foley-pit and shotgun-mic recordings where they exist, and nothing
    # with music, speech, heels or a reverberant hall.
    #
    # Rejected: 377323 (dry grass, but cicadas and birds all over it), 331167
    # ("grass" is the author stepping on a jute shopping bag), 453xxx-series
    # stereo flacs (too far off the shoe to read as the player's own feet).
    'step_concrete': dict(ids=[813622, 727367, 554380], takes=4, maxlen=0.5,
                          role='impact', minpeak=-30, floor=0.04),
    'step_grass':   dict(ids=[682128, 685286, 521587], takes=4, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.04),
    'step_dirt':    dict(ids=[353799, 682127, 521588], takes=4, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.04),
    'step_metal':   dict(ids=[834029, 698697, 463811], takes=4, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.04),
    'step_wood':    dict(ids=[543685, 426620, 682774], takes=4, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.04),
    'step_water':   dict(ids=[861369, 531566, 106395], takes=4, maxlen=0.5,
                         role='impact', minpeak=-30, floor=0.04),
}


def get(url, binary=False):
    req = urllib.request.Request(url, headers={'User-Agent': UA})
    with urllib.request.urlopen(req, timeout=40) as r:
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


def envelope(path, hop=0.005, sr=22050):
    """Peak amplitude per 5 ms frame, 0..1."""
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(sr), '-f', 's16le', '-'],
        capture_output=True).stdout
    a = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0
    h = int(sr * hop)
    n = len(a) // h
    if n < 2:
        return np.zeros(0), hop
    return np.abs(a[:n * h].reshape(n, h)).max(axis=1), hop


def shots(path, floor=0.1, jump=2.2, gap=0.07):
    """Where the BANGS are.

    silencedetect is the wrong tool for this. A gunshot's decay dips below any
    sensible gate and comes back up as the room answers, so it reports two or
    three "onsets" inside one shot; and in a recording of a string of shots it
    misses the ones that land while the previous tail is still ringing. Both
    mistakes shipped takes that held two, three, even four bangs -- so one
    trigger pull sounded like a burst.

    A shot is an ATTACK: a frame that is loud AND several times louder than the
    last 60 ms. That is what this looks for.
    """
    e, hop = envelope(path)
    if not len(e):
        return [], 0.0
    out = []
    for i in range(3, len(e)):
        prev = e[max(0, i - 12):i - 1]
        if e[i] < floor or not len(prev) or e[i] < prev.max() * jump:
            continue
        if out and (i - out[-1]) * hop < gap:
            continue
        out.append(i)
    res = [round(i * hop, 3) for i in out]
    # A file that STARTS loud has no quiet frames in front of its first bang,
    # so nothing above reads as an attack and the sound is silently dropped.
    # Treat the head of such a file as an onset in its own right.
    if e[:4].max() >= floor and (not res or res[0] > gap):
        res.insert(0, 0.0)
    return res, len(e) * hop


def peak_db(path):
    """Peak level of a file in dBFS (0 = full scale)."""
    p = subprocess.run(['ffmpeg', '-hide_banner', '-i', path, '-af', 'volumedetect',
                        '-f', 'null', '-'], capture_output=True, text=True)
    m = re.search(r'max_volume:\s*(-?[0-9.]+) dB', p.stderr)
    return float(m.group(1)) if m else -99.0


# A take has to actually contain an event. Anything peaking below this is the
# tail of the previous one -- the reverb of a shot, a casing still rolling --
# and shipping it means one trigger pull in three makes almost no noise.
MIN_PEAK_DB = -14.0
TARGET_PEAK_DB = -1.0


# The shortest take worth keeping. Below this the shot is part of a burst that
# cannot be separated, and the right thing is to throw it away rather than ship
# a clip with somebody else's bang on the end of it.
MIN_TAKE = 0.18
MIN_FINAL = 0.06   # a take shorter than this is a fragment, not a sound
# A GUNSHOT is a bigger event than an impact tick: muzzle blast plus the room
# answering runs well past a third of a second. A "shot" take that comes out
# shorter than this is not a shot -- it is the slide locking back, or a bang
# whose tail had to be cut off because another one followed too closely -- and
# firing one of those sounds like the gun did something other than go off.
MIN_SHOT = 0.30
PRE_ROLL = 0.025   # s of air kept in front of the attack, so it is not clipped


def cut(src, dst, start, length, minpeak=MIN_PEAK_DB, minlen=MIN_FINAL):
    """One take: mono 44.1 kHz, ONE bang, peak-normalised to a fixed level so
    takes never jump in volume, faded out so it cannot click.

    Returns False (and writes nothing) when the segment holds no real event, or
    when it could not be reduced to a single shot.
    """
    tmp = dst + '.tmp.wav'
    pk = -99.0
    dur = 0.0
    for attempt in range(3):
        subprocess.run(
            ['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error',
             '-ss', f'{max(0, start - PRE_ROLL):.3f}', '-t', f'{length:.3f}', '-i', src,
             '-ac', '1', '-ar', '44100',
             '-af', 'areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse',
             '-c:a', 'pcm_s16le', tmp],
            check=True, capture_output=True)
        pk = peak_db(tmp)
        raw = subprocess.run(['ffprobe', '-v', 'error', '-show_entries',
                              'format=duration', '-of', 'csv=p=0', tmp],
                             capture_output=True, text=True).stdout.strip()
        try:
            dur = float(raw)
        except ValueError:        # ffprobe says N/A for a zero-length file
            dur = 0.0
        if pk < minpeak or dur < minlen:
            os.remove(tmp)
            return False
        # VERIFY, and verify the NORMALISED take: the attack scan works off an
        # absolute level, so on a quiet clip it sees nothing and a second tink
        # sails straight through. Normalise first, then look.
        norm = dst + '.norm.wav'
        subprocess.run(['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-i', tmp,
                        '-af', f'volume={TARGET_PEAK_DB - pk:.2f}dB',
                        '-c:a', 'pcm_s16le', norm], check=True, capture_output=True)
        hits, _ = shots(norm)
        os.remove(norm)
        extra = [t for t in hits if t > PRE_ROLL + 0.05]
        if not extra:
            break
        # Anything else in here is a second event: one trigger pull must not
        # play a burst, which is exactly what shipped the first time round.
        if attempt == 2:
            os.remove(tmp)
            return False
        length = extra[0] - 0.02 + PRE_ROLL     # stop just before the next one
        if length < MIN_TAKE:
            os.remove(tmp)
            return False
    fade = min(0.06, dur * 0.3)
    st = max(0.0, dur - fade)
    subprocess.run(
        ['ffmpeg', '-y', '-hide_banner', '-loglevel', 'error', '-i', tmp,
         '-af', f'volume={TARGET_PEAK_DB - pk:.2f}dB,afade=t=out:st={st:.3f}:d={fade:.3f}',
         '-c:a', 'pcm_s16le', dst],
        check=True, capture_output=True)
    os.remove(tmp)
    return True


def main():
    os.makedirs(OUT, exist_ok=True)
    raw = RAW
    os.makedirs(raw, exist_ok=True)
    if '--list' in sys.argv:
        for f in sorted(os.listdir(OUT)):
            if f.endswith('.wav'):
                print(f, os.path.getsize(os.path.join(OUT, f)), 'bytes')
        return
    bank = {}
    credits = []
    for key, spec in MANIFEST.items():
        files = []
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
            mp3 = os.path.join(raw, f'{sid}.mp3')
            if not os.path.exists(mp3):
                with open(mp3, 'wb') as fh:
                    fh.write(get(info['url'], binary=True))
            ons, total = shots(mp3, floor=spec.get('floor', 0.1))
            made = []
            for i, t in enumerate(ons):
                # Each take stops before the NEXT bang, so it can never carry a
                # neighbour's shot. A pair closer together than MIN_TAKE is a
                # burst nobody can separate -- skip it.
                # The LAST event in a file has nothing after it to collide
                # with, so it gets the full length and ffmpeg stops at the end
                # of the file. Applying the gap rule to it threw away every
                # recording that is one short sound and nothing else -- which is
                # how the wood and gravel impacts disappeared.
                if i + 1 < len(ons):
                    room = min(spec['maxlen'], max(0.0, ons[i + 1] - t - 0.02))
                    if room < MIN_TAKE:
                        continue
                else:
                    room = spec['maxlen']
                name = f'{key}_{sid}_{len(made)}.wav'
                try:
                    if not cut(mp3, os.path.join(OUT, name), t, room,
                               spec.get('minpeak', MIN_PEAK_DB),
                               MIN_SHOT if spec.get('role') == 'shot' else MIN_FINAL):
                        continue
                except subprocess.CalledProcessError as e:
                    print(f'  !! {name}: ffmpeg {e.stderr.decode()[:120]}')
                    continue
                made.append(name)
                if len(made) >= spec['takes']:
                    break
            if not made:
                continue
            files += made
            credits.append(dict(key=key, **{k: info[k] for k in
                                            ('id', 'title', 'author', 'licence', 'page')},
                                files=made))
            print(f'  ok {key:<13} {info["licence"]:<9} "{info["title"][:44]}" by {info["author"]}'
                  f'  -> {len(made)} take(s)')
        if files:
            bank[key] = files
    with open(os.path.join(OUT, 'bank.json'), 'w') as fh:
        json.dump(bank, fh, indent=1)
    with open(os.path.join(OUT, 'credits.json'), 'w') as fh:
        json.dump(credits, fh, indent=1, ensure_ascii=False)
    total = sum(os.path.getsize(os.path.join(OUT, f))
                for fs in bank.values() for f in fs)
    print(f'\n{sum(len(v) for v in bank.values())} one-shots across {len(bank)} roles, '
          f'{total / 1024:.0f} kB total -> public/audio/guns/')


if __name__ == '__main__':
    main()
