#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

exec dotnet run --project tools/Excise.Reachability/Excise.Reachability.csproj -- "$@"
