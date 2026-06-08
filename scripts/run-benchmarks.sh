#!/bin/bash
# Run the pdfe performance benchmarks (#344/#357). BenchmarkDotNet.
# Examples:
#   scripts/run-benchmarks.sh                  # all benchmarks
#   scripts/run-benchmarks.sh --filter '*Render*'
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
dotnet run -c Release --project Pdfe.Benchmarks/Pdfe.Benchmarks.csproj -- "$@"
