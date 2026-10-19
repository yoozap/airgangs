# WHICH ENGINE TAKES HAVE PEOPLE TALKING IN THEM — take 2.
#
# Take 1 scored amplitude modulation and simply ranked the SHORT one-shots
# first: a canopy latch is one bang, which is pure 2-8 Hz modulation and no
# speech at all. The discriminator has to use the one thing an engine really
# is and a voice really isn't: STATIONARY. So take the median spectrum of the
# whole take as the engine's own signature, subtract it, and whatever is left
# standing in the 300-3400 Hz voice band — in runs of a few hundred ms, which
# is a syllable — is something that was not the engine.
import glob, os, subprocess
import numpy as np

SR = 16000
N = 512
HOP = 160          # 10 ms

def load(path):
    raw = subprocess.run(['ffmpeg', '-v', 'quiet', '-i', path, '-ac', '1',
                          '-ar', str(SR), '-f', 'f32le', '-'],
                         capture_output=True).stdout
    return np.frombuffer(raw, dtype=np.float32)

def spec(x):
    w = np.hanning(N)
    frames = 1 + max(0, (len(x) - N) // HOP)
    S = np.empty((frames, N // 2 + 1))
    for i in range(frames):
        S[i] = np.abs(np.fft.rfft(x[i * HOP:i * HOP + N] * w))
    return S

def analyse(path):
    x = load(path)
    if len(x) < SR // 2:
        return None
    S = spec(x)
    f = np.fft.rfftfreq(N, 1 / SR)
    # the engine: what this take sounds like MOST of the time
    M = np.median(S, axis=0)
    R = np.clip(S - 2.2 * M, 0, None)        # 2.2x over the steady floor
    band = (f >= 300) & (f <= 3400)
    res = R[:, band].sum(axis=1)
    ref = S[:, band].sum(axis=1).mean() + 1e-12
    r = res / ref                            # per-frame excess, 0 = pure engine
    # syllables: runs of >=120 ms where the excess holds up
    hot = r > 0.35
    runs, n = [], 0
    for h in hot:
        if h: n += 1
        else:
            if n >= 12: runs.append(n)
            n = 0
    if n >= 12: runs.append(n)
    return dict(dur=len(x) / SR, peak=r.max(), frac=hot.mean(),
                syls=len(runs), longest=(max(runs) * 10 if runs else 0), r=r)

PROPS = ['prop_idle', 'prop_run', 'prop_start']
files = sorted(p for p in glob.glob('public/audio/planes/*.wav')
               if any(os.path.basename(p).startswith(k) for k in PROPS))
rows = []
for p in files:
    a = analyse(p)
    if a: rows.append((a['syls'], a['longest'], os.path.basename(p), a))
rows.sort(key=lambda r: (r[0], r[1]), reverse=True)
print(f'{"syls":>4} {"longest":>8} {"peak":>6} {"hot%":>6}  file')
for syls, longest, name, a in rows:
    print(f'{syls:4d} {longest:7d}ms {a["peak"]:6.2f} {a["frac"]*100:5.1f}%  {name}')
print('\n--- where, per 100 ms (X loud non-engine, x some, . engine only)')
for syls, longest, name, a in rows:
    r = a['r']
    buckets = [r[i:i+10].max() for i in range(0, len(r) - 9, 10)]
    bar = ''.join('X' if v > 0.8 else 'x' if v > 0.35 else '.' for v in buckets)
    print(f'{name:26} {bar}')
