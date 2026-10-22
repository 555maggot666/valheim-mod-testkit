#!/usr/bin/env bash
# Runs a Valheim test suite inside a headless dedicated server and prints the report.
#
#   ./scripts/run-suite.sh --server-dir ~/steamcmd/vh \
#                          --plugin-dir ~/workspace/mymod/dist/plugins \
#                          --world testkit --port 2464
#
# The server directory must contain a Valheim dedicated server (app 896660) and BepInEx. The plugin directory
# is copied into BepInEx/plugins/testkit-run/ (old contents removed) so a test run never mixes with the mods you
# actually play with. Exits 0 when the report has zero failures.
set -euo pipefail

SERVER_DIR=""
PLUGIN_DIR=""
WORLD="testkit"
PORT="2464"
PASSWORD="testkit123"
TIMEOUT="420"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --server-dir) SERVER_DIR="$2"; shift 2 ;;
    --plugin-dir) PLUGIN_DIR="$2"; shift 2 ;;
    --world) WORLD="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$SERVER_DIR" ]] || { echo "--server-dir is required" >&2; exit 2; }
[[ -x "$SERVER_DIR/valheim_server.x86_64" ]] || { echo "$SERVER_DIR has no valheim_server.x86_64" >&2; exit 2; }
[[ -d "$SERVER_DIR/BepInEx/core" ]] || { echo "$SERVER_DIR has no BepInEx; deploy it first (see docs/setup.md)" >&2; exit 2; }

SLOT="$SERVER_DIR/BepInEx/plugins/testkit-run"
rm -rf "$SLOT"
mkdir -p "$SLOT"
if [[ -n "$PLUGIN_DIR" ]]; then
  cp -a "$PLUGIN_DIR"/. "$SLOT"/
fi

REPORT="$SERVER_DIR/BepInEx/config/valheim-testkit/report.json"
rm -f "$REPORT"
LOG="$SERVER_DIR/testkit-run.log"
rm -f "$LOG"

cd "$SERVER_DIR"
export LD_LIBRARY_PATH="./doorstop_libs:./linux64:${LD_LIBRARY_PATH:-}"
export LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll
export SteamAppId=892970

./valheim_server.x86_64 \
  -name "TestKit" -port "$PORT" -world "$WORLD" -password "$PASSWORD" -public 0 \
  -savedir "$SERVER_DIR/saves" > "$LOG" 2>&1 &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true' EXIT

DEADLINE=$(( $(date +%s) + TIMEOUT ))
while [[ $(date +%s) -lt $DEADLINE ]]; do
  if [[ -f "$REPORT" ]]; then
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "server died before writing a report; last lines:" >&2
    tail -40 "$LOG" >&2
    exit 1
  fi
  sleep 2
done

if [[ ! -f "$REPORT" ]]; then
  echo "no report after ${TIMEOUT}s; last lines:" >&2
  tail -40 "$LOG" >&2
  exit 1
fi

cat "$REPORT"
FAILED=$(python3 - "$REPORT" <<'PY'
import json, sys
print(json.load(open(sys.argv[1])).get("failed", 0))
PY
)
echo
if [[ "$FAILED" == "0" ]]; then
  echo "RESULT: OK (0 failed)"
  exit 0
fi

echo "RESULT: FAILED ($FAILED)"
exit 1
