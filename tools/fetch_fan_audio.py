#!/usr/bin/env python3
"""Pull REAL fan recordings off Freesound for the warehouse wind tunnel.

"in fans on of i need real sounds" -- the three industrial drum fans in front
of the slung Mustang. A fan is an ENGINE-shaped sound, not a gunshot-shaped
one: it starts from silence, runs forever, and winds down. That is exactly
what tools/fetch_plane_audio.py already cuts, so this tool is a manifest and
nothing else -- it borrows that tool's cutters, its licence check, its credits
file and its 16-bit mono WAV writer wholesale. Duplicating 300 lines of
crossfade and onset detection to fetch five more sounds would be silly.

    python3 tools/fetch_fan_audio.py           # fetch, cut, write
    python3 tools/fetch_fan_audio.py fan_high  # re-cut one role
    python3 tools/fetch_fan_audio.py --list

Out: public/audio/fans/{bank.json,credits.json,*.wav}, which src/audiofx.js
loads into the same keyed sample bank as the guns, the rail and the planes.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fetch_plane_audio as fp  # noqa: E402  (the path has to be set first)

# role -> freesound ids, best first. Same spec shape as the plane manifest:
# kind 'loop' | 'start' | 'stop' | 'oneshot', dur seconds, takes for one-shots.
MANIFEST = {
    # THE SPIN-UP. 667277 is literally "Fan Start and Stop" -- one recording
    # holding both events, so the same machine starts and stops; 704394 is a
    # second fan starting, so switching on twice is not the same sample.
    'fan_start': dict(ids=[667277, 704394], kind='start', dur=3.4),
    # RUNNING, LOW -- the 500 setting. A floor fan and a large fan running:
    # blade wash with the motor under it, which is what a drum fan in a shed
    # actually sounds like from a few metres away.
    'fan_low': dict(ids=[831775, 361212, 696180], kind='loop', dur=3.2),
    # RUNNING, HIGH -- the 2000 setting. NOT the low loop played louder: at
    # that speed the thing you hear is moving air rather than a motor, so this
    # is an HVAC blower, a wind-tunnel loop and the room tone of a big
    # ventilation fan. src/audiofx.js crossfades between the two banks as the
    # speed comes up, the way the aeroplane crossfades idle into run.
    'fan_high': dict(ids=[799531, 560554, 453546], kind='loop', dur=3.2),
    # THE WIND-DOWN, and it has to be a real one: the cutter in the plane tool
    # measures the shape rather than trusting the title, and it threw out both
    # of my first picks for good reasons -- 652850 ("Industrial Fan Double
    # Blade Shut Off") goes from running to silence in 0.18 s, which is an edit
    # and not a fan coasting, and 667277 is cut off while still running (its
    # last loud frame is 14.44 s into a 14.72 s file). These three are recorded
    # spin-downs: the blades slow and the air howl falls away with them.
    'fan_stop': dict(ids=[543820, 583248, 453997], kind='stop', dur=4.0),
    # THE SWITCH. A contactor is what actually closes on a site board -- a
    # relay clunk with the motor taking the load, not a light-switch tick.
    'fan_switch': dict(ids=[807384], kind='oneshot', takes=3, dur=0.9,
                       minpeak=-30, floor=0.06),
}

fp.MANIFEST = MANIFEST
fp.OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..',
                      'public', 'audio', 'fans')
fp.RAW = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'cache', 'fan_audio')

if __name__ == '__main__':
    fp.main()
