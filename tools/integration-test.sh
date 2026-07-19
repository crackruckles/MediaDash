#!/usr/bin/env bash
# Full scan cycle against a disposable dockerized Jellyfin (Linux path of the release checklist).
# Builds the plugin, generates fixtures, boots jellyfin/jellyfin, creates an admin,
# adds the fixture library, runs the MediaDash scan, and asserts expected issue counts.
#
# Usage: ./integration-test.sh   (requires docker, dotnet, curl)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
PORT=8097
NAME=mediadash-itest

cleanup() { docker rm -f $NAME >/dev/null 2>&1 || true; rm -rf "$WORK"; }
trap cleanup EXIT

echo "== build plugin =="
dotnet publish "$ROOT/Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj" -c Release -o "$WORK/plugin"
mkdir -p "$WORK/config/plugins/MediaDash" "$WORK/cache"
cp "$WORK/plugin/Jellyfin.Plugin.MediaDash.dll" "$WORK/config/plugins/MediaDash/"

echo "== fixtures =="
bash "$ROOT/tools/make-fixtures.sh" "$WORK/media"

echo "== start jellyfin =="
docker run -d --name $NAME -p $PORT:8096 \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media:ro" \
  jellyfin/jellyfin:10.11.8
until curl -sf "http://localhost:$PORT/System/Info/Public" >/dev/null; do sleep 2; done

AUTH='Authorization: MediaBrowser Client="itest", Device="ci", DeviceId="ci", Version="1"'

echo "== initial setup =="
curl -sf -X POST "http://localhost:$PORT/Startup/Configuration" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -sf "http://localhost:$PORT/Startup/User" -H "$AUTH" >/dev/null
curl -sf -X POST "http://localhost:$PORT/Startup/User" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"Name":"ci","Password":"ci"}'
curl -sf -X POST "http://localhost:$PORT/Startup/Complete" -H "$AUTH"

TOKEN=$(curl -sf -X POST "http://localhost:$PORT/Users/AuthenticateByName" -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"Username":"ci","Pw":"ci"}' | sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p')
H="X-Emby-Token: $TOKEN"

echo "== configure plugin (eng-only languages) =="
curl -sf -X POST "http://localhost:$PORT/Plugins/38bdb090b7634294934bb54ade4d9d6d/Configuration" -H "$H" -H 'Content-Type: application/json' \
  -d '{"AllowedAudioLanguages":["eng"],"AllowedSubtitleLanguages":["eng"],"FirstRunDone":true,"DryRun":true}'

echo "== add library and wait for index =="
curl -sf -X POST "http://localhost:$PORT/Library/VirtualFolders?name=Fixtures&collectionType=movies&paths=%2Fmedia%2Fmovies&refreshLibrary=true" \
  -H "$H" -H 'Content-Type: application/json' -d '{"LibraryOptions":{}}'
for i in $(seq 1 60); do
  N=$(curl -sf -H "$H" "http://localhost:$PORT/Items?IncludeItemTypes=Movie&Recursive=true" | sed -n 's/.*"TotalRecordCount":\([0-9]*\).*/\1/p')
  [ "${N:-0}" -ge 5 ] && break; sleep 3
done
[ "${N:-0}" -ge 5 ] || { echo "FAIL: library indexed only ${N:-0} movies"; exit 1; }

echo "== run MediaDash scan =="
TASK=$(curl -sf -H "$H" "http://localhost:$PORT/ScheduledTasks" | tr ',' '\n' | grep -B2 MediaDashScan | sed -n 's/.*"Id":"\([a-f0-9]*\)".*/\1/p' | head -1)
curl -sf -X POST -H "$H" "http://localhost:$PORT/ScheduledTasks/Running/$TASK"
for i in $(seq 1 60); do
  STATE=$(curl -sf -H "$H" "http://localhost:$PORT/ScheduledTasks/$TASK" | sed -n 's/.*"State":"\([A-Za-z]*\)".*/\1/p')
  [ "$STATE" = "Idle" ] && break; sleep 3
done

echo "== assert issue counts =="
STATUS=$(curl -sf -H "$H" "http://localhost:$PORT/MediaDash/Status")
echo "$STATUS"
check() { # check <Type> <minCount>
  local count
  count=$(echo "$STATUS" | tr '{' '\n' | grep "\"$1\"" | sed -n 's/.*"Count":\([0-9]*\).*/\1/p')
  if [ "${count:-0}" -lt "$2" ]; then echo "FAIL: expected >=$2 $1 issues, got ${count:-0}"; exit 1; fi
  echo "OK: $1 = $count"
}
check Duplicate 1
check Quality 1
check SubtitleLanguage 1
check AudioLanguage 1

echo "PASS: integration test completed"
