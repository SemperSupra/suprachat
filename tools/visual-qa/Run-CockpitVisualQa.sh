#!/usr/bin/env bash
# Public-safe disposable Ubuntu/Xvfb visual qualification, not logged in.
set -Eeuo pipefail
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -qq
sudo apt-get install -y -qq --no-install-recommends xvfb xdotool scrot openbox imagemagick libgtk-3-0 libnss3 libgbm1 >/dev/null
OUT="$PWD/qa-evidence/visual/192-20261108"
mkdir -p "$OUT"
export HOME="$RUNNER_TEMP/suprachat-visual-qa-home"
export XDG_CONFIG_HOME="$HOME/.config"
export XDG_DATA_HOME="$HOME/.local/share"
export XDG_CACHE_HOME="$HOME/.cache"
mkdir -p "$HOME" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"
export DISPLAY=:99
Xvfb "$DISPLAY" -screen 0 1920x1080x24 -nolisten tcp -ac >"$RUNNER_TEMP/xvfb.log" 2>&1 &
XVFB_PID=$!
sleep 2
openbox >"$RUNNER_TEMP/openbox.log" 2>&1 &
WM_PID=$!
sleep 2
dotnet build SupraChat/SupraChat.csproj -c Release --nologo > "$RUNNER_TEMP/build.log" 2>&1
echo "build=PASS" > "$OUT/manifest.txt"
export SUPRACHAT_STARTUP_LOG="$RUNNER_TEMP/suprachat-startup.log"
APP_DIR="$PWD/SupraChat/bin/Release/net10.0"
(cd "$APP_DIR" && dotnet SupraChat.dll) >"$RUNNER_TEMP/app-stdout.log" 2>&1 &
APP_PID=$!
echo "app_pid=$APP_PID" >>"$OUT/manifest.txt"
WINDOW=""
for i in $(seq 1 35); do
  WINDOW="$(xdotool search --onlyvisible --name 'SupraChat' 2>/dev/null | head -n 1 || true)"
  if [ -n "$WINDOW" ]; then break; fi
  if ! kill -0 "$APP_PID" 2>/dev/null; then break; fi
  sleep 1
done
if [ -z "$WINDOW" ]; then
  echo "launch=NO_VISIBLE_WINDOW" >>"$OUT/manifest.txt"
  import -display "$DISPLAY" -window root "$OUT/00-launch-failure.png" || true
  tail -n 30 "$RUNNER_TEMP/app-stdout.log" | sed -E 's/(Bearer|sk-)[^ ]+/[redacted]/g' >"$OUT/sanitized-launch-diagnostic.txt"
else
  echo "launch=VISIBLE_WINDOW" >>"$OUT/manifest.txt"
  echo "window_id=$WINDOW" >>"$OUT/manifest.txt"
  focus() { xdotool windowactivate --sync "$WINDOW" || true; sleep 0.6; }
  resize() {
    local w="$1" h="$2"
    xdotool windowsize "$WINDOW" "$w" "$h" || true
    xdotool windowmove "$WINDOW" 45 45 || true
    sleep 1
  }
  capture() {
    local name="$1"
    focus
    sleep 1
    import -display "$DISPLAY" -window "$WINDOW" "$OUT/$name.png"
    identify -format '%f %wx%h\n' "$OUT/$name.png" >> "$OUT/manifest.txt"
  }
  resize 1280 900
  capture 01-start-chatgpt-1280
  for n in 2 3 4; do
    focus
    xdotool key --clearmodifiers --window "$WINDOW" "ctrl+$n" || true
    sleep 2
    capture "0$((n))-$n-shortcut-1280"
  done
  focus
  xdotool mousemove --window "$WINDOW" 550 82 click 1
  sleep 2
  capture 05-sessions-target-1280
  resize 900 650
  capture 06-target-900x650
  resize 1600 1000
  capture 07-target-1600x1000
  kill "$APP_PID" 2>/dev/null || true
  wait "$APP_PID" 2>/dev/null || true
  export SUPRACHAT_INITIAL_TAB=Sessions
  (cd "$APP_DIR" && dotnet SupraChat.dll) >"$RUNNER_TEMP/app-second-stdout.log" 2>&1 &
  APP2_PID=$!
  WIN2=""
  for i in $(seq 1 30); do
    WIN2="$(xdotool search --onlyvisible --name 'SupraChat' 2>/dev/null | head -n 1 || true)"
    if [ -n "$WIN2" ]; then break; fi
    if ! kill -0 "$APP2_PID" 2>/dev/null; then break; fi
    sleep 1
  done
  if [ -n "$WIN2" ]; then
    xdotool windowactivate --sync "$WIN2" || true
    xdotool windowsize "$WIN2" 1280 900 || true
    sleep 2
    import -display "$DISPLAY" -window "$WIN2" "$OUT/08-initial-tab-sessions-1280.png"
    identify -format '%f %wx%h\n' "$OUT/08-initial-tab-sessions-1280.png" >> "$OUT/manifest.txt"
    echo "sessions_env_launch=VISIBLE_WINDOW" >> "$OUT/manifest.txt"
  else
    echo "sessions_env_launch=NO_VISIBLE_WINDOW" >> "$OUT/manifest.txt"
  fi
  kill "$APP2_PID" 2>/dev/null || true
  wait "$APP2_PID" 2>/dev/null || true
fi
kill "$APP_PID" "$WM_PID" "$XVFB_PID" 2>/dev/null || true
wait "$APP_PID" 2>/dev/null || true
echo "complete=YES" >>"$OUT/manifest.txt"
