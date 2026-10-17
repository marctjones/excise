# Release readiness of the 3.18.0 candidate on macOS arm64, 2026-10-17

Candidate: develop @ 7f5175e5, built locally exactly as the release workflow builds the macOS package
(`scripts/build-macos-app.sh --version 3.18.0 --rid osx-arm64 --aot`). Last release tag: v3.17.0 (258 commits earlier).
The pipeline already ships Native AOT for the GUI on every platform and the CLI where the installer carries it
(`.github/workflows/release.yml`, `docs/RELEASE_CHECKLIST.md` AOT matrix); what changed in this cycle is the macOS bundle's Info.plist
(`LSEnvironment` `MallocSpaceEfficient=1`, #2072).

## Evidence (this machine)
| check | result |
|---|---|
| zip built; unzipped `excise.app` through `scripts/check-aot-payload.py` | PASS: 10 files, no managed assemblies, no runtime, no single-file marker |
| version | `CFBundleShortVersionString` and `CFBundleVersion` 3.18.0; the allocator setting present |
| unzipped package through the in-app scenarios (w9 launch/idle, irs search then redact, irs redact and save, 455-page book open, scan scroll/close/idle) | 0 step failures in all five; focus never taken |
| `scripts/check-aot-cli-parity.sh` (JIT against NativeAOT CLI output) | PASS (20 s) |
| reference-performance gate against the committed baseline (JIT CLI) | PASS (#1761 closed) |
| `scripts/check-doc-claim-freshness.sh` | passes |
| pre-push t0 on every push this cycle, including the redaction gates | PASS |
| Core tests 7,822 pass, 1 skipped; CLI redact/search/text 201 pass; 30 cached reference redactions byte-identical to the pre-change engine | PASS |
| budgets (`check-perf-budgets.sh`, allocation) | redaction-save 2.14x its budget and search 8.3x: red, already tracked (#1626); not a change in this cycle's user-visible behaviour |

## Not verified, and whether it matters
- The release workflow's `workflow_dispatch` dry run (Windows and Linux packages, installers, `.deb` installs) was not run: it builds on hosted runners and is the remote step in the checklist. Windows and Linux were not measured here for memory or latency.
- OCR from the NativeAOT GUI bundle (no language data is bundled by default). The NativeAOT CLI's OCR gives byte-identical text to the JIT CLI.
- t1 and full were not run for this candidate; the targeted redaction, Core, CLI and App tests above were.
- Interactive Apply All was measured with a real dispatcher in a headless test (the dispatcher kept turning while a 60-mark apply ran on the 126-page book), not in a window.
- Minor, observed: the idle memory trim runs a blocking collection on the UI thread; a 45 s idle shows a ~200 ms UI stall when it fires. Harmless at rest, a hitch if a key arrives at that instant.

## Judgement
Nothing found that a typical user would notice as a regression on macOS arm64 against 3.17.0: the changes this cycle are lower memory after idle/close, shorter UI freezes on open and redaction, and fewer allocations in redaction, with output unchanged. A release still needs the workflow dry run before tagging.
