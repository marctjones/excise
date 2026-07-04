# Packaged GUI Smoke

`scripts/run-packaged-gui-smoke.sh` is the release-candidate check for issues
#558 and #571. It tests the installed/package shape of the Avalonia app instead
of the in-process Avalonia.Headless test harness.

## What It Proves

- The macOS `.app` bundle contains an executable GUI.
- Launch Services or the packaged executable can start the app with a real PDF.
- The smoke writes durable evidence: JSON report, markdown summary, app/launch
  logs, native-input log, and a screenshot path.
- The report references the headless coverage from #560 instead of duplicating
  every workflow in a brittle OS automation script.

## Background-Safe Default

The default mode uses macOS `open -g` and does not inject keyboard or mouse
events. That keeps the check suitable for normal local work where the user may
still be using the machine.

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
