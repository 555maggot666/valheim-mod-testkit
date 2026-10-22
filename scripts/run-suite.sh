#!/usr/bin/env bash
# Runs a Valheim test suite inside a headless dedicated server and prints the report.
#
#   ./scripts/run-suite.sh --server-dir ~/valheim \
#                          --plugin-dir ~/my-mod/dist/plugins \
#                          --world testkit --port 2464
#
# The server directory must contain a Valheim dedicated server (app 896660) and BepInEx. The plugin directory
# is copied into BepInEx/plugins/testkit-run/ so a test run never mixes with the mods you actually play with.
# Exits 0 only when the report exists, lists at least one suite, has at least one passing scenario and no
# failures — "no report" and "nothing ran" are failures, never silent successes.
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
LOG="$SERVER_DIR/testkit-run.log"
rm -f "$REPORT" "$REPORT.tmp" "$LOG"

cd "$SERVER_DIR"
export LD_LIBRARY_PATH="./doorstop_libs:./linux64:${LD_LIBRARY_PATH:-}"
export LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll
export SteamAppId=892970

SERVER_PID=""
cleanup() {
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill -TERM "$SERVER_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$SERVER_PID" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

./valheim_server.x86_64 \
  -name "TestKit" -port "$PORT" -world "$WORLD" -password "$PASSWORD" -public 0 \
  -savedir "$SERVER_DIR/saves" > "$LOG" 2>&1 &
SERVER_PID=$!

json_field() {
  python3 - "$REPORT" "$1" <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get(sys.argv[2], 0))
except Exception:
    print(0)
PY
}

DEADLINE=$(( $(date +%s) + TIMEOUT ))
STARTED=$(date +%s)
REPORT_OK=""
while [[ $(date +%s) -lt $DEADLINE ]]; do
  # A second copy of the same plugin guid makes BepInEx skip one DLL — the suite then silently never runs.
  # Checked before the fail-fast below so a skipped duplicate is not misreported as "never loaded".
  if grep -q "because a duplicate of it was already loaded" "$LOG" 2>/dev/null; then
    echo "two copies of the same plugin are deployed; BepInEx skipped one:" >&2
    grep -B2 "duplicate of it was already loaded" "$LOG" >&2
    exit 1
  fi

  # Fail fast when the kit was never loaded by the chainloader. Loading the world may take minutes, so there is
  # no separate window for it: once the kit is loaded, the run lasts until --timeout.
  if (( $(date +%s) - STARTED > 90 )) && grep -q "Chainloader startup complete" "$LOG" 2>/dev/null \
     && ! grep -q "Loading \[ValheimTestKit" "$LOG" 2>/dev/null; then
    echo "the testkit plugin never loaded — is ValheimTestKit.dll in the deployed plugin dir?" >&2
    grep -a "ValheimTestKit\|Chainloader" "$LOG" | tail -10 >&2
    exit 1
  fi

  if [[ -f "$REPORT" ]]; then
    # Only trust a report that parses; the plugin writes through a temp file, so this should not race.
    if python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$REPORT" 2>/dev/null; then
      REPORT_OK=1
      break
    fi
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "server died before writing a report; last lines of $LOG:" >&2
    tail -40 "$LOG" >&2
    exit 1
  fi
  sleep 2
done

if [[ -z "$REPORT_OK" ]]; then
  echo "no usable report after ${TIMEOUT}s; last lines of $LOG:" >&2
  tail -40 "$LOG" >&2
  exit 1
fi

cat "$REPORT"
echo

SUITES=$(json_field suitesFound)
PASSED=$(json_field passed)
FAILED=$(json_field failed)

if [[ "${SUITES:-0}" == "0" ]]; then
  echo "RESULT: FAILED (no suite was found — is the suite DLL deployed and does it declare a [BepInPlugin] class?)"
  exit 1
fi

if [[ "${PASSED:-0}" == "0" ]]; then
  echo "RESULT: FAILED (no scenario passed; suites=$SUITES failed=$FAILED)"
  exit 1
fi

if [[ "${FAILED:-0}" == "0" ]]; then
  echo "RESULT: OK (suites=$SUITES passed=$PASSED failed=0)"
  exit 0
fi

echo "RESULT: FAILED (suites=$SUITES passed=$PASSED failed=$FAILED)"
exit 1
