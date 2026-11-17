#!/usr/bin/env sh
# LinuxServer custom-cont-init hook. Runs before Jellyfin, after web files exist.
set -eu
index="${SERIES_AUDIO_WEB_INDEX:-/usr/share/jellyfin/web/index.html}"
if [ ! -f "$index" ]; then
    echo "Series Audio: Jellyfin Web index.html not found: $index" >&2
    exit 1
fi
if grep -q 'id="series-audio-script"' "$index"; then
    exit 0
fi
if ! grep -q '</body>' "$index"; then
    echo "Series Audio: unsupported index.html; no changes made." >&2
    exit 1
fi
temporary=$(mktemp)
trap 'rm -f "$temporary"' EXIT HUP INT TERM
awk '{ sub(/<\/body>/, "<script id=\"series-audio-script\" defer src=\"../SeriesAudio/web.js\"></script></body>"); print }' "$index" > "$temporary"
cat "$temporary" > "$index"
echo "Series Audio: browser controls enabled."
