# macOS Shortcuts Example

Use Shortcuts to call the CLI contract rather than click the GUI.

1. Add **Ask for File** and restrict input to PDFs.
2. Add **Ask for Input** named `Page`, default `1`.
3. Add **Run Shell Script** with input as arguments.
4. Use this shell body:

```bash
set -euo pipefail
PDF="$1"
PAGE="${2:-1}"
OUT="${TMPDIR:-/tmp}/excise-page-${PAGE}.png"
excise render "$PDF" --output "$OUT" --page "$PAGE" --dpi 150 --json
open -R "$OUT"
```

For multi-step workflows, write a workflow JSON file and run:

```bash
excise batch workflow.json --json --progress --output report.json
```

Shortcuts receives structured JSON on stdout and does not need Accessibility
permission because no GUI input is injected.
