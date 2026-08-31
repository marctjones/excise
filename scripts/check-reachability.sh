#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# A failed NuGet audit can be cached in the no-op restore state and replayed by
# Roslyn's out-of-process MSBuild host even when the workspace sets
# NuGetAudit=false. Refresh that generated state explicitly: architecture
# analysis is offline/deterministic, while the repository's network-capable
# vulnerability audit remains independently owned by #1238.
dotnet restore excise.sln \
  -p:NuGetAudit=false \
  --force-evaluate \
  --disable-build-servers \
  --verbosity quiet

exec dotnet run \
  --no-restore \
  --project tools/Excise.Reachability/Excise.Reachability.csproj \
  -- "$@"
