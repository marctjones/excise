#!/usr/bin/env bash
#
# Find public API that nothing calls.
#
# WHY THIS, AND WHY IT IS CHEAP
#
# The hard part — a complete, machine-readable inventory of the public surface —
# already exists and is already gated: PublicApi/*.approved.txt, maintained by
# PublicApiApprovalTests, 1400+ members for Excise.Core alone. This script does
# the other half: cross-reference each declared member against call sites.
#
# The motivating example is real. RedactionService.RedactWithOptions bundles
# redaction with the metadata scrub — fully implemented, tested, and with ZERO
# production callers. #896 shipped a leak through the CLI precisely because the
# safe API existed and nothing used it. That was found by reading code. This
# finds that shape mechanically.
#
# WHAT IT IS NOT
#
# NOT a dead-code prover. It is a CANDIDATE LIST for a human. Three reasons a
# zero-reference member can be perfectly correct:
#
#   1. Excise.Core is a LIBRARY. Public API exists for external consumers, so
#      "unused in this repo" is not "unused". The signal is far stronger for
#      Excise.App (an application) than for Excise.Core.
#   2. Interface implementations and overrides are invoked polymorphically — the
#      name may never appear at a call site and still run constantly.
#   3. XAML bindings and DI resolve by name at runtime. .axaml files are indexed
#      for that reason; reflection is not detectable here at all.
#
# Short and common identifiers (Value, Text, Name, Id…) collide with everything,
# so a minimum length applies. That trades recall for a usable signal-to-noise
# ratio; --min-length lowers it if you want the noisier view.
#
# Implemented in Python rather than bash because this machine ships bash 3.2,
# which has no `mapfile` — and the first draft of this script died on exactly
# that.
#
# Usage:
#   scripts/check-unwired-api.sh [--min-length N] [--assembly NAME] [--quiet]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
exec python3 "$ROOT/scripts/check_unwired_api.py" "$@"
