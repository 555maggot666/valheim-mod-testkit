#!/usr/bin/env bash
# Runs a Valheim test suite inside a real game client on a headless X display.
#
#   ./scripts/run-client-suite.sh --client-dir ~/valheim-client \
#                                 --plugin-dir ./out/plugins \
#                                 --connect 127.0.0.1:2464 [--password <pw>] --timeout 600
#
# Auto-join: a) `+connect <ip:port>` is the evidenced auto-join path (FejdStartup.HandleStartupJoin reads it).
# b) A password join without a character (`-joinserverwithcharacter <host> <char>`) would be the only fully
# automatic path, but it is UNVERIFIED here as long as no Steam client is running. `--password` is passed as
# `-password <pw>`.
#
# Requires: a Steam client logged in as the account that owns the game (Valheim's ZNet talks Steam
# networking — without Steamworks the client boots but can never join or host a world), Xvfb and Mesa's
# software Vulkan (lavapipe) if you have no GPU.
#
# Suites with RequiresClient => true are what this is for. Exits 0 only on a report with >= 1 passing
# scenario and 0 failures.
set -euo pipefail

CLIENT_DIR=""
PLUGIN_DIR=""
CONNECT=""
DISPLAY_NUM=":99"
WIDTH="1280"
HEIGHT="720"
TIMEOUT="600"
PASSWORD=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --client-dir) CLIENT_DIR="$2"; shift 2 ;;
    --plugin-dir) PLUGIN_DIR="$2"; shift 2 ;;
    --connect) CONNECT="$2"; shift 2 ;;
    --display) DISPLAY_NUM="$2"; shift 2 ;;
    --width) WIDTH="$2"; shift 2 ;;
    --height) HEIGHT="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$CLIENT_DIR" ]] || { echo "--client-dir is required" >&2; exit 2; }
[[ -x "$CLIENT_DIR/valheim.x86_64" ]] || { echo "$CLIENT_DIR has no valheim.x86_64" >&2; exit 2; }
[[ -d "$CLIENT_DIR/BepInEx/core" ]] || { echo "$CLIENT_DIR has no BepInEx; deploy it first (see docs/setup.md)" >&2; exit 2; }
if ! pgrep -f "steam" > /dev/null; then
  echo "warning: no Steam process found — Valheim's ZNet needs Steamworks, so the client will boot but" >&2
  echo "         will not be able to join or host a world. Start Steam first (or run this on your desktop)." >&2
fi

SLOT="$CLIENT_DIR/BepInEx/plugins/testkit-run"
rm -rf "$SLOT"
mkdir -p "$SLOT"
if [[ -n "$PLUGIN_DIR" ]]; then
  cp -a "$PLUGIN_DIR"/. "$SLOT"/
fi

REPORT="$CLIENT_DIR/BepInEx/config/valheim-testkit/report.json"
LOG="$CLIENT_DIR/client-run.log"
rm -f "$REPORT" "$REPORT.tmp" "$LOG"

XVFB_PID=""
if ! xdpyinfo -display "$DISPLAY_NUM" > /dev/null 2>&1; then
  Xvfb "$DISPLAY_NUM" -screen 0 "${WIDTH}x${HEIGHT}x24" > /dev/null 2>&1 &
  XVFB_PID=$!
  sleep 2
fi

CLIENT_PID=""
cleanup() {
  if [[ -n "$CLIENT_PID" ]] && kill -0 "$CLIENT_PID" 2>/dev/null; then
    kill -TERM "$CLIENT_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$CLIENT_PID" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$CLIENT_PID" 2>/dev/null || true
    wait "$CLIENT_PID" 2>/dev/null || true
  fi
  if [[ -n "$XVFB_PID" ]]; then
    kill "$XVFB_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

cd "$CLIENT_DIR"
export DISPLAY="$DISPLAY_NUM"
export SteamAppId=892970
export LD_LIBRARY_PATH="./doorstop_libs:./linux64:${LD_LIBRARY_PATH:-}"
export LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll

ARGS=(-screen-width "$WIDTH" -screen-height "$HEIGHT" -screen-fullscreen 0 -console)
if [[ -n "$CONNECT" ]]; then
  # FejdStartup.HandleStartupJoin reads "+connect <addr>" as two separate argv elements.
  ARGS+=("+connect" "$CONNECT")
fi
if [[ -n "$PASSWORD" ]]; then
  ARGS+=("-password" "$PASSWORD")
fi

./valheim.x86_64 "${ARGS[@]}" > "$LOG" 2>&1 &
CLIENT_PID=$!

DEADLINE=$(( $(date +%s) + TIMEOUT ))
while [[ $(date +%s) -lt $DEADLINE ]]; do
  if [[ -f "$REPORT" ]] && python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$REPORT" 2>/dev/null; then
    break
  fi
  if ! kill -0 "$CLIENT_PID" 2>/dev/null; then
    echo "client exited before writing a report; last lines of $LOG:" >&2
    tail -40 "$LOG" >&2
    exit 1
  fi
  sleep 2
done

if [[ ! -f "$REPORT" ]]; then
  echo "no report after ${TIMEOUT}s; last lines of $LOG:" >&2
  tail -40 "$LOG" >&2
  exit 1
fi

cat "$REPORT"
echo

SUITES=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('suitesFound',0))" "$REPORT")
PASSED=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('passed',0))" "$REPORT")
FAILED=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1])).get('failed',0))" "$REPORT")

if [[ "$SUITES" == "0" || "$PASSED" == "0" ]]; then
  echo "RESULT: FAILED (suites=$SUITES passed=$PASSED — nothing client-side ran)"
  exit 1
fi

if [[ "$FAILED" == "0" ]]; then
  echo "RESULT: OK (suites=$SUITES passed=$PASSED failed=0)"
  exit 0
fi

echo "RESULT: FAILED (suites=$SUITES passed=$PASSED failed=$FAILED)"
exit 1
