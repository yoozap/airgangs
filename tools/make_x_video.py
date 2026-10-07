#!/usr/bin/env python3
"""Cut and encode the recorded reel for X.

    python3 tools/make_x_video.py <reel_raw.json> <out.mp4>

The raw file (tools/record_reel.mjs) is the bare canvas: no bars, no grain, no
black at the loop seam, because the film's overlay is DOM. This burns the look
back in -- 2.39:1 bars, the vignette, a little temporal grain, 0.16 s of black
either side of every seam -- at the seam times the recorder wrote, and encodes
once, straight to what X wants: H.264 High, yuv420p, AAC, faststart. Then it
checks its own work: streams, duration, audio level (a silent track is a bug,
not a quiet reel) and three frames to look at.
"""
import json, os, subprocess, sys, re

FFMPEG = '/opt/homebrew/bin/ffmpeg'
FFPROBE = '/opt/homebrew/bin/ffprobe'
W, H = 1920, 1080
BAR = round((H - W / 2.39) / 2)      # the 2.39:1 letterbox, 138 px top and bottom
FADE = 0.16                          # s of black at the seam, as cinema.js draws it
CRF = '17'

meta = json.load(open(sys.argv[1]))
out = sys.argv[2]
src = meta['file']
dur = float(meta['duration'])
wraps = [float(w) for w in meta['wraps']]

# RETIME THE TAKE. The reel is recorded a film-frame at a time (the recorder
# drives the canvas track by hand and the game runs on a fixed timestep), but
# MediaRecorder still stamps those frames with the WALL clock — and the page
# draws the city far slower than it draws the airport. So the frames are
# re-spaced evenly with setpts=N/(fps*TB): N is the frame's index, which is
# the only honest clock in the file. Everything measured in take-seconds goes
# with it, and a single loop's seam is simply the film's own length.
film = float(meta.get('film') or 0)
frames = int(meta.get('frames') or 0)
fps = 0.0
if film and frames:
    # the rate the take was CAPTURED at, which is the rate the film runs at
    fps = float(meta.get('fixedFps') or 0) or frames / (film * len(wraps))
    print(f'retiming: {frames} frames of film at {fps:.2f} fps '
          f'(captured over {dur:.1f} s of wall clock)')
    dur = film * len(wraps)
    wraps = [film * (i + 1) for i in range(len(wraps))]

# Each fade is confined to its own window with `enable`: a plain fade-out in
# ffmpeg keeps every frame after it black for the rest of the stream, so the
# first seam used to black out the other two loops.
def fade_in(st):
    return f"fade=t=in:st={st:.3f}:d={FADE}:enable='between(t,{st:.3f},{st + FADE:.3f})'"
def fade_out(st):
    return f"fade=t=out:st={st:.3f}:d={FADE}:enable='between(t,{st:.3f},{st + FADE + 0.05:.3f})'"
# Opened part-way into a loop (START in the recorder): the first frame is the
# hero, not the seam's black, so no fade-in.
fades = [] if float(meta.get('start', 0)) > 0 else [fade_in(0)]
for w in wraps[:-1]:
    fades += [fade_out(w - FADE), fade_in(w)]
fades.append(fade_out(dur - FADE))
vf = ','.join([
    *([f'setpts=N/({fps:.6f}*TB)'] if fps else []),
    f'scale={W}:{H}:flags=lanczos',
    'vignette=angle=PI/5.5',          # the overlay's radial shade, ~a third at the corners
    'noise=alls=5:allf=t+u',          # the grain, moving
    *fades,
    f'drawbox=x=0:y=0:w={W}:h={BAR}:color=black:t=fill',
    f'drawbox=x=0:y={H - BAR}:w={W}:h={BAR}:color=black:t=fill',
    'format=yuv420p',
])
cmd = [FFMPEG, '-y', '-hide_banner', '-loglevel', 'error', '-i', src, '-t', f'{dur:.3f}',
       '-vf', vf, '-r', f'{fps:.4f}' if fps else '60', '-fps_mode', 'cfr',
       '-c:v', 'libx264', '-preset', 'slow', '-crf', CRF, '-profile:v', 'high', '-level', '4.2',
       '-c:a', 'aac', '-b:a', '192k', '-ar', '48000', '-ac', '2',
       '-movflags', '+faststart', out]
print('encode:', ' '.join(cmd[-6:]))
subprocess.run(cmd, check=True)

# ---- verify ----
probe = subprocess.run([FFPROBE, '-v', 'error', '-show_entries',
                        'stream=codec_type,codec_name,width,height,avg_frame_rate,bit_rate,profile:format=duration,size',
                        '-of', 'json', out], capture_output=True, text=True, check=True)
info = json.loads(probe.stdout)
streams = {s['codec_type']: s for s in info['streams']}
fmt = info['format']
v = streams.get('video', {}); a = streams.get('audio', {})
print(f"video: {v.get('codec_name')} {v.get('profile')} {v.get('width')}x{v.get('height')} @ {v.get('avg_frame_rate')}")
print(f"audio: {a.get('codec_name')} {a.get('bit_rate')}" if a else 'audio: NONE')
print(f"duration {float(fmt['duration']):.2f} s, {int(fmt['size']) / 1048576:.1f} MB")
vol = subprocess.run([FFMPEG, '-hide_banner', '-i', out, '-af', 'volumedetect', '-vn', '-f', 'null', '-'],
                     capture_output=True, text=True)
m = re.findall(r'(mean_volume|max_volume): ([-\d.]+) dB', vol.stderr)
levels = {k: float(x) for k, x in m}
print('audio levels:', levels or 'none measured')
if not a or levels.get('max_volume', -91) < -60:
    print('WARNING: the audio track is silent or missing')
frames_dir = os.path.join(os.path.dirname(os.path.abspath(sys.argv[1])), 'x_frames')
os.makedirs(frames_dir, exist_ok=True)
for name, t in [('first', 0.0), ('draw', 1.9), ('paper', 9.3), ('mid', dur / 2), ('last', dur - 0.6)]:
    subprocess.run([FFMPEG, '-y', '-hide_banner', '-loglevel', 'error', '-ss', f'{t:.2f}', '-i', out,
                    '-frames:v', '1', os.path.join(frames_dir, f'{name}.png')], check=True)
print('frames in', frames_dir)
