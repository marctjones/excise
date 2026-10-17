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
- Retracted: an earlier version of this note called the ~200 ms "stalls" seen in idle steps of a backgrounded run the idle trim's blocking collection. It is not: the trim's collections took 19-33 ms (`heap_reclaim.duration`), the stalls repeat (88 and 183 hitches over 100 ms in one 45 s idle), and the same scenario run in the foreground shows none (largest stall 1 ms in every idle step), while the backgrounded run showed 100 ms with 130 hitches in one idle step. A backgrounded idle app has its 50 ms heartbeat timer coalesced by macOS (App Nap), which the UI-stall monitor reads as a stall; that is a measurement artifact (#2078), not a UI block.

## Judgement
Nothing found that a typical user would notice as a regression on macOS arm64 against 3.17.0: the changes this cycle are lower memory after idle/close, shorter UI freezes on open and redaction, and fewer allocations in redaction, with output unchanged. A release still needs the workflow dry run before tagging.

## Outcome (2026-10-17): v3.18.0 tagged on 25e43022, verified, published
- Dry run (workflow run 39042877503) and the tag's release run (39052305991): every job green (preflight, macOS, Windows installer and zip, Linux amd64 and arm64 `.deb` and `.tar.gz`).
- Draft assets checked: 7 packages plus `.sha256` files and `SHA256SUMS`; the macOS zip's hash matches the `.sha256` file and the `SHA256SUMS` line.
- The downloaded macOS app: payload is Native AOT (no managed assemblies), version 3.18.0, allocator setting present; the six in-app scenarios (w9 launch/idle, irs search then redact, irs redact and save, 455-page book, scan scroll/close, Altona zoom) 0 step failures; real-key page turns, jumps, scroll and zoom answered (irs and scan, 0 unanswered turns).
- Independent redaction check on the NativeAOT CLI built from the tag (mutool, pdftotext, and a raw plus inflated-stream byte scan): the redacted term is gone in all four documents under mutool and in the byte scan. pdftotext still reads 18 "immunity" in one document (rc=3): exactly the 18 occurrences the report lists as "NOT REMOVED (hyphen-wrapped)" (a word split across a line break, rejoined by pdftotext's dehyphenation), the reported limitation of #1372 and unchanged by this release (output byte-identical to the pre-change engine on the cached references).
- Published as Latest; `main` fast-forwarded to the tag.
