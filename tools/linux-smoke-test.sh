#!/usr/bin/env bash
# Smoke test of the Linux build on a headless machine (CI or a container):
# PulseAudio with a virtual sink, then the ALSA fallback, then the GUI under Xvfb.
# Usage: tools/linux-smoke-test.sh path/to/KeepSoundAlive [screenshot.png]
set -euo pipefail

BIN=$(realpath "$1")
SHOT=${2:-}
chmod +x "$BIN"
export HOME=${HOME:-/root}
export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/tmp/xdg-runtime}
mkdir -p "$XDG_RUNTIME_DIR" && chmod 700 "$XDG_RUNTIME_DIR"

echo "== PulseAudio with a virtual sink"
pulseaudio --daemonize=yes --exit-idle-time=-1 --log-target=stderr 2>/dev/null || true
for _ in $(seq 20); do pactl info >/dev/null 2>&1 && break; sleep 0.5; done
pactl load-module module-null-sink sink_name=testsink sink_properties=device.description=TestSink >/dev/null
pactl set-default-sink testsink

"$BIN" --list-devices | tee /tmp/devices.txt
grep -q "Audio backend: PulseAudio" /tmp/devices.txt
grep -q "TestSink" /tmp/devices.txt

echo "== Headless playback"
"$BIN" --headless > /tmp/headless.log 2>&1 &
APP=$!
sleep 3
cat /tmp/headless.log
pactl list short sink-inputs | tee /tmp/inputs.txt
test -s /tmp/inputs.txt

# Record one second from the sink's monitor and measure the peak level.
timeout 2 parec -d testsink.monitor --format=float32le --channels=1 --rate=48000 --raw > /tmp/rec.raw || true
python3 - <<'EOF'
import array, math
data = array.array('f')
with open('/tmp/rec.raw', 'rb') as f:
    raw = f.read()
data.frombytes(raw[: len(raw) // 4 * 4])
peak = max((abs(x) for x in data), default=0.0)
db = 20 * math.log10(peak) if peak > 0 else float('-inf')
print(f"recorded {len(data)} samples, peak {peak:.5f} ({db:.1f} dBFS)")
assert len(data) > 10000, "nothing recorded"
assert -45 < db < -35, "unexpected signal level (expected about -40 dBFS)"
EOF

kill -TERM $APP
wait $APP && echo "headless exited cleanly"

echo "== ALSA fallback (PulseAudio stopped)"
pulseaudio --kill || true
sleep 1
"$BIN" --list-devices | tee /tmp/alsa.txt || true
grep -q "Audio backend: ALSA" /tmp/alsa.txt

echo "== Applications menu entry"
"$BIN" --add-to-menu
grep -q "^Exec=\"$BIN\"" "$HOME/.local/share/applications/keepsoundalive.desktop"
test -s "$HOME/.local/share/icons/hicolor/256x256/apps/keepsoundalive.png"

echo "== GUI under Xvfb"
pulseaudio --daemonize=yes --exit-idle-time=-1 2>/dev/null || true
sleep 1
Xvfb :99 -screen 0 1280x1024x24 >/dev/null 2>&1 &
XVFB=$!
sleep 1
DISPLAY=:99 "$BIN" > /tmp/gui.log 2>&1 &
GUI=$!
sleep 8
if ! kill -0 $GUI 2>/dev/null; then
    echo "GUI process exited early:"; cat /tmp/gui.log; exit 1
fi
if [ -n "$SHOT" ] && command -v import >/dev/null; then
    DISPLAY=:99 import -window root "$SHOT" && echo "screenshot: $SHOT"
fi
kill $GUI $XVFB || true
echo "== All Linux smoke tests passed"
