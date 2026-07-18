#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

mkdir -p coverage

dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
  /p:CollectCoverage=true \
  /p:CoverletOutput=../coverage/coverage \
  /p:CoverletOutputFormat=lcov \
  /p:Threshold=80 \
  /p:ThresholdType=line

echo "Coverage report written to coverage/coverage.info"
