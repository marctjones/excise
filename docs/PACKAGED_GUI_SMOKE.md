# Packaged GUI Smoke

`scripts/run-packaged-gui-smoke.sh` is the release-candidate check for issues
#558, #571, #577, #581, and #582. It tests the installed/package shape of the
Avalonia app instead of the in-process Avalonia.Headless test harness.

## What It Proves

- The macOS `.app` bundle contains an executable GUI.
- Launch Services or the packaged executable can start the app with a real PDF.
- The packaged process remains alive after startup stabilization instead of
  crashing immediately after Launch Services reports success.
- The smoke writes durable evidence: JSON report, markdown summary, app/launch
  logs, native-input log, app responsiveness report, and a screenshot path.
- The report records timing evidence with PASS/WARN/FAIL budgets for packaged
  launch, PDF open, startup screenshot, and app-internal document-open phases.
- When `--allow-focus-input` is explicitly enabled on a dedicated runner, the
  report also records native search typing, page navigation, zoom, and
  redaction-preview interaction latency rows.
- The report references the headless coverage from #560 instead of duplicating
  every workflow in a brittle OS automation script.

## Background-Safe Default

The default mode uses macOS `open -g` and does not inject keyboard or mouse
events. That keeps the check suitable for normal local work where the user may
still be using the machine. The script sets a temporary
`PDFE_RESPONSIVENESS_REPORT` Launch Services environment value so the packaged
app can write `app-responsiveness.json`, also writes a one-shot request file for
Launch Services runs that do not inherit environment values, then unsets and
removes that state during cleanup. Missing app-internal timing is a smoke
failure because it means the package did not prove first-page display.

Use direct executable launch when investigating app stdout/stderr or when
Launch Services environment inheritance is unavailable:

```bash
scripts/run-packaged-gui-smoke.sh --mode direct-exec
scripts/release-smoke.sh --quick --package --packaged-gui-direct-exec --version <version>
```

Native key/mouse delivery through `System Events` is available only with:

```bash
scripts/run-packaged-gui-smoke.sh --allow-focus-input
```

That mode takes foreground focus and requires Accessibility permission for the
terminal or CI runner. Use it only on a dedicated runner or during an explicit
manual release-candidate pass.

## Release Usage

After building a local package:

```bash
scripts/release-smoke.sh --quick --package --packaged-gui --version <version>
```

For the focus-taking native input path:

```bash
scripts/release-smoke.sh --quick --package --packaged-gui-focus-input --version <version>
```

Reports are written under the current `logs/release-smoke_*` directory when run
through `release-smoke.sh`, or under `logs/packaged-gui-smoke_*` when run
directly.

## Timing Budgets

Timing status is separate from rendering fidelity. A WARN is visible in the
JSON/markdown report but does not fail the smoke; a FAIL exits non-zero.

| Workflow | PASS | WARN |
| --- | ---: | ---: |
| Packaged process appears | 3s | 8s |
| Packaged process survives startup stabilization | 5s | 15s |
| PDF argument open observed externally | 5s | 15s |
| Startup screenshot captured | 6s | 18s |
| App document instances loaded | 2s | 8s |
| App first page visible | 4s | 15s |
| App background work started | 6s | 20s |
| App document load complete | 8s | 25s |
| Native search typing, page navigation, zoom | 1s | 3s |
| Native redaction preview | 1.5s | 5s |
