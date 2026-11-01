#!/bin/sh
# Pull a C# probe's own KEY=value lines out of a Unity batch-mode log.
#
# Extracting with a regex like "^[A-Z][A-Z0-9_]*=" looks right and silently
# drops real output: a probe that prints `WheelH_gear=1.7` has a lowercase
# letter in its key, so 40 of 47 lines vanished and the comparison "failed"
# on lines that were there all along. Unity's own log lines are noise we must
# still exclude, so the keys are taken FROM THE JS SIDE: anything the
# counterpart emits is signal, anything else is not.
#
#   tools/probe_extract.sh <unity.log> <js-output.txt>
log="$1"; js="$2"
keys=$(sed -n 's/^\([A-Za-z_][A-Za-z0-9_]*\).*/\1/p' "$js" | sort -u | paste -sd'|' -)
[ -z "$keys" ] && exit 1
grep -E "^($keys)( |=)" "$log"
