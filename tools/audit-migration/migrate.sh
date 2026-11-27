#!/usr/bin/env bash
# Current schema gate: execute the production migrations on disposable databases.
# Historical *.db/*.sql files here are archived evidence, not a migration engine.
set -euo pipefail
if [ "$#" -ne 0 ]; then
  echo 'Direct database migration is retired. Run this script without arguments for isolated production-schema tests.' >&2
  exit 2
fi
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
dotnet test "$ROOT/Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj" \
  --configuration Release --filter 'FullyQualifiedName~SchemaUpgradeTests' --verbosity minimal
