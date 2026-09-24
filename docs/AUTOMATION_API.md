# excise Automation API

excise automation is CLI-first. AppleScript, Shortcuts, PowerShell, Power
Automate Desktop, Linux shells, GNOME launchers, CI jobs, and future OS-specific
bridges should call the stable `excise` command contract rather than click the
GUI.

This document covers the v2.23 automation issues #561, #564, #565, #567, #568,
and #574.

## Stable Commands

The semantic command registry is available through:

```bash
excise commands --json
excise commands render.page --json
```

The following CLI commands now have stable machine-readable output:

```bash
excise info input.pdf --json
excise text input.pdf --page 1 --json
excise render input.pdf --output page-1.png --page 1 --dpi 150 --json
excise batch workflow.json --json --progress --output report.json
```

`--password` is supported by `info`, `text`, `render`, `redact`, and batch
workflow document-open steps.

`info --json` includes `xfaForm`: `none`, `static` (XFA data alongside usable
AcroForm fields, which excise fills), or `dynamic` (a form only an XFA engine
can display; excise shows its placeholder page). See #1547.
Password values are accepted as inputs but are not written to JSON reports
or progress events.

## Batch Workflow Schema

Batch workflows use semantic command IDs from `excise commands --json`.
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
| `redaction.markSelection` | Mark the selected text as a pending redaction (queues the area; nothing is removed until it is applied). |
| `audit.hiddenText` | Detect hidden text from failed visual-only redactions. |

CLI aliases such as `info`, `text`, `render`, `fill-form`, `add-field`,
`redact`, and `audit` are also accepted in workflow files, but semantic IDs are
preferred for long-lived automation.

## Batch Report

`excise batch --json` prints the final report to stdout. `--output report.json`
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

The default automation surface is process-local CLI execution. excise does not
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
  because excise could not write encrypted output; that error code no longer
  occurs.)
- `redaction.apply` removes every attachment from its output by default
  (#1572, decided 2026-09-17) and lists each one in the step result's
  `attachments` array (`name`, `sizeBytes`, `location`, `disposition`,
  `detail`). `keepAttachments: true` keeps them: text attachments have the
  term cut out (`KeptTermRemoved` / `KeptTermNotFound`), attached PDFs are
  redacted with the same options, and anything else is `KeptNotChecked` — it
  may still contain the term, and a `carrierNotes` line says the redaction was
  not clean. An attached PDF excise cannot open (or one with a password) fails
  the step with `ATTACHMENT_REFUSED`; a PDF portfolio fails with
  `PORTFOLIO_REFUSED` unless `keepAttachments: true` (both category
  `SECURITY`, nothing written).
- `redaction.apply` runs the **Standard output profile** by default (#1586,
  decided 2026-09-17), which also removes the hidden machinery a term scrub
  cannot make safe: all JavaScript and every `/Launch`, `/SubmitForm`,
  `/ImportData`, `/GoToR` and `/GoToE` action (internal `/GoTo` and `/Named`
  navigation is kept), `/PieceInfo`, page thumbnails, the appearance streams of
  hidden annotations, content on optional-content layers that are OFF by
  default, and the document `/Info` and XMP packet — keeping only the PDF/A and
  PDF/UA identification, so a tagged accessible document stays conformant.
  Accessibility and navigation carriers (`/TU`, `/Alt`, `/ActualText`, `/E`,
  structure titles, field names, bookmark titles, link targets) are KEPT and
  term-scrubbed. Every removal is listed in the step result's
  `profileRemovals` array, with `profile` naming which one ran.
  `profile: "maximum"` adds remove-whole on every kept carrier and strips
  bookmarks, link annotations, comments and field names and flattens forms and
  annotations; the step result then sets `accessibilityRemoved: true` — **the
  output is no longer accessible or interactive**. An unrecognised value fails
  the step with `INVALID_PROFILE` rather than falling back to the weaker
  profile: a typo that quietly gives you less redaction than you asked for is
  the failure this refuses to allow.
- Document `/P` permissions are enforced (#642): `text.extract` and
  `render.page` require the document's copy/extract permission, `form.fillForm`
  requires the form fill-in permission, and `form.addField` requires the modify
  permission. A denied step fails with error code `PERMISSION_DENIED`
  (category `SECURITY`). Overrides are per step and explicit:
  `ignorePermissions: true` proceeds anyway (for document owners — excise cannot
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

## Live Performance Metrics (#1491)

The GUI publishes live performance state through
`System.Diagnostics.Metrics`, so a development or automation session can
read render timings, cache bytes and GC state without scraping stdout or
sampling the process from outside. Both routes below are local-only and read
nothing unless enabled. There is no query verb or network endpoint; any future
inbound channel falls under the service rule in the Security Boundary section.
`footprint`/`vmmap` remain the independent OS-level check on the numbers the
app reports about itself.

Instruments (histograms are recorded per event; gauges and observable
counters are read when a listener asks):

| Meter | Instrument | Unit | Kind | Source |
| --- | --- | --- | --- | --- |
| `Excise.Viewer` | `excise.viewer.continuous.band.render.duration` | ms | histogram, tag `dpi` | each completed continuous band render the reader waited for (render-ahead excluded) |
| `Excise.Viewer` | `excise.viewer.lookahead.render.duration` | ms | histogram, tags `dpi`, `view` (`continuous`, `single_page`) | each completed render-ahead of a neighbouring page (#1564) |
| `Excise.Viewer` | `excise.viewer.continuous.composite.size` | By | histogram, tag `dpi` | each published page composite |
| `Excise.Viewer` | `excise.viewer.single_page.render.duration` | ms | histogram, tag `dpi` | each single-page render that reached the screen (cache hits excluded) |
| `Excise.Viewer` | `excise.viewer.continuous.cache.resident_bytes` | By | gauge, tag `viewer` | continuous tile LRU |
| `Excise.Viewer` | `excise.viewer.continuous.composite.resident_bytes` | By | gauge, tag `viewer` | published composites |
| `Excise.Viewer` | `excise.viewer.continuous.cache.entries` | {tile} | gauge, tag `viewer` | continuous tile LRU |
| `Excise.Viewer` | `excise.viewer.continuous.renders.in_flight` | {tile} | gauge, tag `viewer` | continuous renders in flight |
| `Excise.Viewer` | `excise.viewer.continuous.cache.hits` | {hit} | observable counter, tag `viewer` | continuous tile LRU |
| `Excise.Viewer` | `excise.viewer.single_page.cache.entries` | {bitmap} | gauge, tag `viewer` | single-page LRU |
| `Excise.Viewer` | `excise.viewer.single_page.cache.hits` / `.misses` | {hit} / {miss} | observable counter, tag `viewer` | single-page LRU |
| `Excise.App` | `excise.app.document_open.phase.duration` | ms | histogram, tag `phase` | each document open; phases match `EXCISE_RESPONSIVENESS_REPORT` workflow names |
| `Excise.App` | `excise.app.text_index.pages_indexed` / `.pages_total` | {page} | gauge | the most recently started search index |
| `Excise.App` | `excise.app.thumbnail.renders` | {render} | observable counter | thumbnail renderer invocations (disk-cache hits excluded) |

The two continuous byte gauges are mirrors that the viewer refreshes on the UI
thread whenever the tile cache or the composites change. They are refreshed only
while a listener has one of them enabled, so the first reading after attaching
reflects the next cache change.

### JSONL file

```bash
EXCISE_TRACE_VIEWER=/tmp/excise-metrics.jsonl excise   # start the GUI with the sink
tail -f /tmp/excise-metrics.jsonl
```

`EXCISE_TRACE_VIEWER=1` keeps its original meaning, which is the viewer's
free-text trace on stdout. Any other value is used as a JSONL path, but only
when it is rooted, contains a directory separator, or ends in `.jsonl`; other
values (`0`, `true`) are ignored, so they do not create a file. The two modes
are exclusive: a path does not also enable the stdout trace. The file is
appended to, and parent directories are created.
`EXCISE_METRICS_INTERVAL_MS` sets the observation/snapshot interval
(default 1000).

Every line is one JSON object with `ts` (UTC) and `kind`:

```json
{"ts":"2026-09-13T18:00:00.000Z","kind":"session-start","pid":4242,"intervalMs":1000}
{"ts":"…","kind":"measurement","meter":"Excise.Viewer","instrument":"excise.viewer.continuous.band.render.duration","unit":"ms","value":38.4,"tags":{"dpi":"144"}}
{"ts":"…","kind":"observation","meter":"Excise.Viewer","instrument":"excise.viewer.continuous.cache.resident_bytes","unit":"By","value":52428800,"tags":{"viewer":"1"}}
{"ts":"…","kind":"snapshot","lastGcHeapSizeBytes":81234567,"lastGcCommittedBytes":98765432,"liveHeapBytes":80123456,"allocatedBytes":912345678,"workingSetBytes":412345678,"cpuTotalMs":5321.5,"cpuUserMs":4100.2,"gen0Collections":41,"gen1Collections":9,"gen2Collections":2}
```

- `measurement` lines are histogram records, one per event.
- `observation` lines are gauge and observable-counter readings, taken every
  interval.
- In `snapshot` lines, `lastGcHeapSizeBytes` and `lastGcCommittedBytes`
  describe the most recent garbage collection and read 0 before the first
  one. `liveHeapBytes` and `workingSetBytes` are current.
- `snapshot` lines carry cumulative process CPU, so idle CPU over a window is
  the `cpuTotalMs` difference between two snapshots divided by their `ts`
  difference.
- Open → first page visible is the `first_page_visible` phase measurement.
- Band render p50/p99 come from the `measurement` values of
  `excise.viewer.continuous.band.render.duration`.

The file is written through a source-generated `JsonSerializerContext`, so the
sink works in the Native AOT build.

### dotnet-counters

```bash
dotnet-counters monitor -p <pid> --counters Excise.Viewer,Excise.App,System.Runtime
```

`System.Runtime` adds the runtime's own GC heap, committed memory, working set,
CPU and allocation rate. dotnet-counters attaches over EventPipe, and a Native
AOT publish omits EventSource support unless it is built with
`-p:EventSourceSupport=true`. On the AOT lane, use the JSONL file instead.

## In-app performance scenarios (#1497)

The GUI can drive a scripted interaction sequence against itself and quit,
so a live memory or CPU measurement needs nobody sitting in front of the app.

```bash
scripts/run-gui-perf-scenarios.sh --scenario altona-close --repeats 5
scripts/run-gui-perf-scenarios.sh --calibrate     # measure this machine's noise floor first
scripts/run-gui-perf-scenarios.sh --list          # the plan, runs nothing
```

Scenarios are declared in `tests/gui-perf-scenarios.json`
(`schemaVersion` 1; each carries an `id`, a mandatory `why`, and a list of
`steps`). Steps drive the view model and the viewer's public APIs — never
synthetic input and never accessibility, because an accessibility
`entire contents` query pegs the app at ~96% CPU and would perturb the numbers
being taken.

Environment variables, all read only by the outer harness's launch:

| Variable | Meaning |
| --- | --- |
| `EXCISE_PERF_SCENARIO` | path to the scenario file; **presence alone enables the runner** |
| `EXCISE_PERF_SCENARIO_ID` | which scenario to run; an id that matches nothing is an error, not "run everything" |
| `EXCISE_PERF_SCENARIO_OUT` | output directory for `steps.jsonl`, `scenario-result.json` and the step-boundary handshake files |
| `EXCISE_PERF_SCENARIO_REPEAT` | repeat number, recorded in every journal row |
| `EXCISE_PERF_SCENARIO_SAMPLE_MS` | how long a step boundary waits for the outer sampler (default 20000) |

Two instruments are added to the `Excise.App` meter, so the markers land in the
same JSONL as everything else and segment it by step:
`excise.app.perf_scenario.step.duration` (tags `scenario`, `step`, `op`) and
`excise.app.perf_scenario.sample_window.duration` (tags `scenario`, `step`).
The sample-window instrument is reported separately and never charged to the
step it follows: the harness's own cost has to be visible rather than hidden
inside a measurement.

At every step boundary the runner writes a journal row, publishes
`step.marker`, and waits for `step.ack` before continuing. That handshake
exists because `vmmap` suspends the process it inspects — a sampler polling on
a timer would sometimes land mid-scroll and silently corrupt that step's render
timings. The wait is bounded, so a run with no harness attached simply times
out at each boundary and records that the boundary has no outer sample.

**Security posture.** This is not an exception to the Security Boundary above.
The runner starts no listener and opens no port; it reads one local file whose
path the user supplied through the environment, performs the steps in it, and
quits. Without `EXCISE_PERF_SCENARIO` the code is inert — the same posture as
`EXCISE_VISUAL_TRACE_OUT`.

**What the output supports, and what it does not.** The harness reports every
number beside a floor of
`max(noise spread, runner overhead, sampler overhead)`, and prints a delta at
or below that floor as `BELOW-FLOOR` rather than as an improvement. A run with
no calibration reports the floor as `UNKNOWN`, never as zero. It cannot tell
you whether scrolling *feels* smooth (that stays human; band render p50/p99 and
blank tiles are proxies), and driving through the view model is not a user's
input path — it skips input dispatch and hit testing, and the driving-fidelity
calibration bounds that residual rather than removing it. There is no gate on
these numbers: they are absolute footprint on one machine under one load.

### Multi-document scenarios (#1551–#1554)

An optional set, never part of the default run:

```bash
scripts/run-gui-perf-scenarios.sh --set multi-document --list
scripts/run-gui-perf-scenarios.sh --set multi-document --repeats 5
scripts/run-gui-perf-scenarios.sh --scenario multi-tabs-switch
```

A scenario with a `"set"` runs only under `--set` or when named with
`--scenario`. Its `"settings"` object is written into that launch's own
`window.json`, in an isolated `HOME` under `/private/tmp/excise-gui-perf/`
(never the user's settings, and never under `~/Documents`). That is how
`multi-tabs-*` get Preferences ▸ Documents ▸ Open Documents In = `NewTab`.

| Scenario | What it checks | Estimated time per launch |
| --- | --- | --- |
| `multi-windows-open3` | Three documents in three windows: footprint after each open (marginal cost), 30 s idle with three open, then close one and read the footprint 20 s and 45 s later | ~1.5 min |
| `multi-tabs-open3` | The same three documents as tabs of one window | ~1.5 min |
| `multi-tabs-switch` | Page and scroll position must survive each tab switch | ~1.5 min |
| `multi-quit-dirty` | Quit with two unsaved documents and one clean one: the quit review must ask exactly twice | ~1 min |

The times are estimates (the idle steps plus opens and settles), not
measurements. Add the Release build on top.

The set adds four steps to the ones above. Every step acts on the active
document and on the viewer of the window that shows it:

- `openAnother` (`document`, `level`: `window` | `tab`) opens a file from the
  active document the way File ▸ Open does, so the file lands where the seeded
  preference sends it. The step fails if the file does not open in a new
  window or a new tab, as `level` says.
- `switchDocument` (`count`, default +1) shows the next or previous document.
  When the window holds several tabs, it runs the Ctrl+Tab command; otherwise
  it activates the window, as the Window menu does. A document shown before
  must come back on the same page and scroll offset (within 4 DIP). Its wall
  time runs until that position is restored.
- `expectDocuments` (`documents`, `windows`) fails unless that many documents
  are open in that many windows.
- `quitReview` (`level`: `discard`, `count`) runs the review that File ▸ Exit
  runs before quitting. Each unsaved-changes prompt is answered in process,
  through a view-model hook that only this runner sets, so no dialog appears
  and nothing is clicked. The step fails unless exactly `count` prompts were
  answered. It must be the last step, because the harness quits right after it.

Journal rows also record `openDocuments` and `documentWindows`. A scenario can
declare `"checks"`. A `drop` check compares `metric` between two labelled
boundaries. The summary prints `DROPPED` when the metric fell by more than the
floor, and `DID-NOT-DROP` when it did not fall at all. That is how "closing a
document gives its memory back" is read.

The same comparison against Preview and Acrobat is driven by keystrokes from
outside the apps, with the same `--multi` flag on both reader benches (#1543,
#1544). Each bench's docstring describes its set.

```bash
scripts/reader_bench.py --multi --list          # 4 configs: excise windows/tabs, Preview, Acrobat
scripts/reader_bench.py --multi --repeats 1     # estimated ~3 min per config
scripts/reader_speed_bench.py --multi --repeats 1   # estimated ~2 min per config
```

## Platform Examples

- macOS AppleScript:
  `automation-scripts/macos/render-page.applescript`
- macOS Shortcuts:
  `automation-scripts/macos/shortcuts-render-page.md`
- Windows PowerShell module:
  `automation-scripts/windows/Excise.Automation.psm1`
- Power Automate Desktop:
  `automation-scripts/windows/power-automate-desktop.md`
- Linux/GNOME shell wrapper:
  `automation-scripts/linux/excise-automation.sh`
- GNOME/Wayland and D-Bus evaluation:
  `automation-scripts/linux/gnome-dbus-evaluation.md`

The repeatable release gate is:

```bash
scripts/release-smoke.sh --quick --only=automation
```

That delegates to `scripts/run-automation-smoke.sh`, runs the focused CLI tests,
then executes a real `excise batch` workflow against `test-pdfs/smoke/irs-w9.pdf`
and records final JSON, progress NDJSON, and a report file.
