# pdfe Automation API

pdfe automation is CLI-first. AppleScript, Shortcuts, PowerShell, Power
Automate Desktop, Linux shells, GNOME launchers, CI jobs, and future OS-specific
bridges should call the stable `pdfe` command contract rather than click the
GUI.

This document covers the v2.23 automation issues #561, #564, #565, #567, #568,
and #574.

## Stable Commands

The semantic command registry is available through:

```bash
pdfe commands --json
pdfe commands render.page --json
```

The following CLI commands now have stable machine-readable output:

```bash
pdfe info input.pdf --json
pdfe text input.pdf --page 1 --json
pdfe render input.pdf --output page-1.png --page 1 --dpi 150 --json
pdfe batch workflow.json --json --progress --output report.json
```

`--password` is supported by `info`, `text`, `render`, `redact`, and batch
workflow document-open steps. Password values are accepted as inputs but are
not written to JSON reports or progress events.

## Batch Workflow Schema

Batch workflows use semantic command IDs from `pdfe commands --json`.
Relative paths resolve against the workflow JSON file location.

```json
{
  "schemaVersion": 1,
  "stopOnError": true,
  "steps": [
    {
      "id": "inspect",
      "command": "document.info",
      "input": "input.pdf"
    },
    {
      "id": "page-text",
      "command": "text.extract",
      "input": "input.pdf",
      "page": 1
    },
    {
      "id": "render-page",
      "command": "render.page",
      "input": "input.pdf",
      "output": "page-1.png",
      "page": 1,
      "dpi": 150
    }
  ]
}
```

Supported v2.23 batch command IDs:

| Command ID | Purpose |
| --- | --- |
| `document.info` | Read version, page count, encryption flag, and metadata. |
| `text.extract` | Extract one page or all pages as structured text. |
| `render.page` | Render one page to PNG. |
| `form.fillForm` | Set AcroForm values and optionally flatten. |
| `form.addField` | Add a text, checkbox, choice, or signature field. |
| `redaction.apply` | Remove matching text at the PDF content level. |
| `audit.hiddenText` | Detect hidden text from failed visual-only redactions. |

CLI aliases such as `info`, `text`, `render`, `fill-form`, `add-field`,
`redact`, and `audit` are also accepted in workflow files, but semantic IDs are
preferred for long-lived automation.

## Batch Report

`pdfe batch --json` prints the final report to stdout. `--output report.json`
writes the same report to disk.

```json
{
  "schemaVersion": 1,
  "generatedUtc": "2026-07-04T18:00:00Z",
  "overallStatus": "PASS",
  "passedCount": 3,
  "completedCount": 3,
  "steps": [
    {
      "id": "render-page",
      "command": "render.page",
      "status": "PASS",
      "exitCode": 0,
      "elapsedMs": 42,
      "result": {
        "outputPath": "/tmp/page-1.png",
        "pageNumber": 1,
        "dpi": 150,
        "width": 1275,
        "height": 1650
      }
    }
  ]
}
```

Progress is newline-delimited JSON on stderr when `--progress` is passed:

```json
{"type":"step-start","ordinal":1,"total":3,"id":"inspect","command":"document.info"}
{"type":"step-complete","ordinal":1,"total":3,"id":"inspect","command":"document.info","status":"PASS","elapsedMs":7}
```

Failure reports use the same shape and include a stable error code and
category:

```json
{
  "schemaVersion": 1,
  "overallStatus": "FAIL",
  "passedCount": 0,
  "completedCount": 1,
  "steps": [
    {
      "id": "redact",
      "command": "redaction.apply",
      "status": "FAIL",
      "exitCode": 2,
      "error": {
        "code": "DESTRUCTIVE_CONFIRMATION_REQUIRED",
        "category": "SECURITY",
        "message": "redaction.apply requires confirmDestructive: true."
      }
    }
  ]
}
```

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | All requested steps passed. |
| `1` | A document operation failed, for example an unreadable file or rendering failure. |
| `2` | Workflow contract or security refusal, for example malformed JSON, unknown command, missing required output, destructive command without confirmation, or in-place overwrite refusal. |

## Security Boundary

The default automation surface is process-local CLI execution. pdfe does not
start a background automation listener or unauthenticated GUI control service in
v2.23.

Rules enforced by the batch contract:

- Password inputs are never echoed in reports or progress events.
- Mutating commands must write to an explicit output path.
- Mutating commands refuse to overwrite their input file.
- `redaction.apply` requires `confirmDestructive: true`.
- `redaction.apply` on an encrypted source re-encrypts the output by default
  (#643): same algorithm and permissions (RC4 sources are upgraded to
  AES-256), protected by the step's `password` (or the empty password).
  `allowDecrypt: true` is the explicit opt-out that writes an unprotected
  copy instead. (Before #643 this step failed closed with
  `DECRYPT_CONFIRMATION_REQUIRED` unless `allowDecrypt: true` was supplied,
  because pdfe could not write encrypted output; that error code no longer
  occurs.)
- Document `/P` permissions are enforced (#642): `text.extract` and
  `render.page` require the document's copy/extract permission, `form.fillForm`
  requires the form fill-in permission, and `form.addField` requires the modify
  permission. A denied step fails with error code `PERMISSION_DENIED`
  (category `SECURITY`). Overrides are per step and explicit:
  `ignorePermissions: true` proceeds anyway (for document owners — pdfe cannot
  yet verify owner passwords, #324), and `forAccessibility: true` on
  `text.extract` invokes the ISO 32000-2 bit 10 extract-for-accessibility
  carve-out. `redaction.apply` is deliberately not permission-gated.
- Hidden-text audit fails the workflow when findings are present unless
  `allowFindings: true` is supplied.

Release builds exclude Roslyn GUI scripting by default unless a builder
explicitly publishes with `-p:EnableScripting=true`. The `.csx` scripts under
`automation-scripts/` remain developer/test automation, not the supported
end-user automation contract.

If a future long-lived automation service is added, it must be local-only,
disabled by default, explicitly enabled by the user, and gated by a per-session
token or equivalent capability.

## Platform Examples

- macOS AppleScript:
  `automation-scripts/macos/render-page.applescript`
- macOS Shortcuts:
  `automation-scripts/macos/shortcuts-render-page.md`
- Windows PowerShell module:
  `automation-scripts/windows/Pdfe.Automation.psm1`
- Power Automate Desktop:
  `automation-scripts/windows/power-automate-desktop.md`
- Linux/GNOME shell wrapper:
  `automation-scripts/linux/pdfe-automation.sh`
- GNOME/Wayland and D-Bus evaluation:
  `automation-scripts/linux/gnome-dbus-evaluation.md`

The repeatable release gate is:

```bash
scripts/release-smoke.sh --quick --only=automation
```

That delegates to `scripts/run-automation-smoke.sh`, runs the focused CLI tests,
then executes a real `pdfe batch` workflow against `test-pdfs/smoke/irs-w9.pdf`
and records final JSON, progress NDJSON, and a report file.
