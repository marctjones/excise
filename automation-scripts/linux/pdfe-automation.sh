#!/usr/bin/env bash
# Thin Linux/GNOME wrapper around the stable pdfe CLI automation contract.

set -euo pipefail

PDFE="${PDFE_CLI:-pdfe}"

usage() {
    cat <<'EOF'
Usage:
  pdfe-automation.sh info <input.pdf>
  pdfe-automation.sh text <input.pdf> [page]
  pdfe-automation.sh render <input.pdf> <output.png> [page] [dpi]
  pdfe-automation.sh batch <workflow.json> [report.json]

All commands write structured JSON to stdout.
EOF
}

case "${1:-}" in
    info)
        [ "$#" -eq 2 ] || { usage >&2; exit 2; }
        exec "$PDFE" info "$2" --json
        ;;
    text)
        [ "$#" -ge 2 ] || { usage >&2; exit 2; }
        if [ "$#" -ge 3 ]; then
            exec "$PDFE" text "$2" --page "$3" --json
        fi
        exec "$PDFE" text "$2" --json
        ;;
    render)
        [ "$#" -ge 3 ] || { usage >&2; exit 2; }
        page="${4:-1}"
        dpi="${5:-150}"
        exec "$PDFE" render "$2" --output "$3" --page "$page" --dpi "$dpi" --json
        ;;
    batch)
        [ "$#" -ge 2 ] || { usage >&2; exit 2; }
        if [ "$#" -ge 3 ]; then
            exec "$PDFE" batch "$2" --json --progress --output "$3"
        fi
        exec "$PDFE" batch "$2" --json --progress
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
