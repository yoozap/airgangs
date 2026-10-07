#!/usr/bin/env python3
"""Build the X cut from deterministically rendered frames + the live take's audio.

    python3 tools/assemble_reel.py <frames.json> <audio reel_raw.json> <out.mp4>

tools/render_reel_frames.mjs draws every frame off a fixed clock, so the motion
is exact -- but it has no sound, because the game's audio only exists in real
time. tools/record_reel.mjs has the sound and the judder. This takes the picture
from one and the track from the other.

The two runs do not cover the same span: the live take ran the film at whatever
pace it could manage (its sidecar says how long in WALL seconds it took to reach
the loop's wrap), while the render covers exactly `loop - start` FILM seconds.
The track is stretched by that ratio with atempo so a shot still lands on its
muzzle flash; a few percent is inaudible on gunfire.

The film's look (bars, vignette, grain, the black at the seam) is burned in by
tools/make_x_video.py, which this hands the muxed file to.
"""
import json
import os
import subprocess
import sys

FFMPEG = '/opt/homebrew/bin/ffmpeg'

frames_meta = json.load(open(sys.argv[1]))
audio_meta = json.load(open(sys.argv[2]))
out = sys.argv[3]
work = os.path.dirname(os.path.abspath(sys.argv[1]))

film = frames_meta['duration']              # exact, by construction
wall = float(audio_meta['duration'])        # what the live take took
tempo = wall / film                         # >1 speeds the track up to fit
print(f'picture {film:.3f} s ({frames_meta["frames"]} frames @ {frames_meta["fps"]}), '
      f'track {wall:.3f} s -> atempo {tempo:.4f}')

muxed = os.path.join(work, 'reel_smooth.mp4')
af = f'atempo={tempo:.6f}' if 0.5 <= tempo <= 2.0 else None
cmd = [FFMPEG, '-y', '-hide_banner', '-loglevel', 'error',
       '-framerate', str(frames_meta['fps']), '-i', os.path.join(frames_meta['dir'], 'f%05d.jpg'),
       '-i', audio_meta['file'],
       '-map', '0:v:0', '-map', '1:a:0',
       *(['-af', af] if af else []),
       '-t', f'{film:.3f}',
       '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '12', '-pix_fmt', 'yuv420p',
       '-c:a', 'aac', '-b:a', '192k', muxed]
subprocess.run(cmd, check=True)

# hand it to the look pass exactly as if it had been captured
side = os.path.join(work, 'reel_smooth.json')
json.dump({'file': muxed, 'duration': film, 'wraps': [film],
           'start': frames_meta['start'], 'fps': frames_meta['fps']}, open(side, 'w'), indent=1)
here = os.path.dirname(os.path.abspath(__file__))
subprocess.run([sys.executable, os.path.join(here, 'make_x_video.py'), side, out], check=True)
