#!/usr/bin/env bash
# Scan-only Linux gate. Each invocation owns its container and temporary files.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
for command in docker dotnet curl python3 ffmpeg; do
  command -v "$command" >/dev/null || { echo "Missing required tool: $command" >&2; exit 2; }
done
WORK="$(mktemp -d)"
PORT=8097
NAME="mediadash-itest-$(basename "$WORK" | tr '[:upper:]' '[:lower:]')"
cleanup() {
  if [ -s "$WORK/container.id" ]; then docker rm -f "$(cat "$WORK/container.id")" >/dev/null 2>&1 || true; fi
  rm -rf -- "$WORK"
}
trap cleanup EXIT

dotnet publish "$ROOT/Jellyfin.Plugin.MediaDash/Jellyfin.Plugin.MediaDash.csproj" -c Release -o "$WORK/plugin"
mkdir -p "$WORK/config/plugins/MediaDash" "$WORK/cache"
cp "$WORK/plugin/"*.dll "$WORK/plugin/"*.json "$WORK/config/plugins/MediaDash/"
bash "$ROOT/tools/make-fixtures.sh" "$WORK/media"
docker run -d --cidfile "$WORK/container.id" --name "$NAME" -p "127.0.0.1:$PORT:8096" \
  -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media:ro" \
  jellyfin/jellyfin:10.11.8
ready=0
for i in $(seq 1 45); do
  if curl --noproxy '*' --max-time 3 -sf "http://127.0.0.1:$PORT/System/Info/Public" >/dev/null; then ready=1; break; fi
  sleep 2
done
if [ "$ready" != 1 ]; then docker logs "$NAME"; echo 'FAIL: startup timeout' >&2; exit 1; fi
python3 "$ROOT/tools/integration_probe.py" --port "$PORT"
echo 'PASS: disposable-container scan integration'
