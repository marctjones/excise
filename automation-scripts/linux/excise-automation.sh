#!/usr/bin/env bash
# Thin Linux/GNOME wrapper around the stable excise CLI automation contract.

set -euo pipefail

EXCISE="${EXCISE_CLI:-excise}"

usage() {
    cat <<'EOF'
Usage:
  excise-automation.sh info <input.pdf>
  excise-automation.sh text <input.pdf> [page]
  excise-automation.sh render <input.pdf> <output.png> [page] [dpi]
  excise-automation.sh batch <workflow.json> [report.json]

All commands write structured JSON to stdout.
EOF
}

case "${1:-}" in
    info)
        [ "$#" -eq 2 ] || { usage >&2; exit 2; }
        exec "$EXCISE" info "$2" --json
        ;;
    text)
        [ "$#" -ge 2 ] || { usage >&2; exit 2; }
        if [ "$#" -ge 3 ]; then
            exec "$EXCISE" text "$2" --page "$3" --json
        fi
        exec "$EXCISE" text "$2" --json
        ;;
    render)
        [ "$#" -ge 3 ] || { usage >&2; exit 2; }
        page="${4:-1}"
        dpi="${5:-150}"
        exec "$EXCISE" render "$2" --output "$3" --page "$page" --dpi "$dpi" --json
        ;;
    batch)
        [ "$#" -ge 2 ] || { usage >&2; exit 2; }
        if [ "$#" -ge 3 ]; then
            exec "$EXCISE" batch "$2" --json --progress --output "$3"
        fi
        exec "$EXCISE" batch "$2" --json --progress
        ;;
    -h|--help|help)
        usage
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
