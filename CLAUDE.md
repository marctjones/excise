# CLAUDE.md

Cross-platform PDF editor: **C# / .NET 10 / Avalonia (MVVM)**, on Windows, Linux and macOS.
The whole PDF stack is ours: `Excise.Core` (parser, writer, fonts, encryption, glyph-level
redaction), `Excise.Rendering` (SkiaSharp), `Excise.Ocr`, `Excise.Avalonia` (viewer control),
`Excise.App` (GUI), `Excise.Cli`. **Do not reintroduce PdfPig, PDFsharp or PDFtoImage.**
Architecture entry point: [`docs/architecture/README.md`](docs/architecture/README.md).
Everything not stated here is in the code, `LOCAL_GATES.md`, `tests/gates.tsv`, `CHANGELOG.md`
or GitHub Issues. Read those, not old prose.

## Where content goes

| Content | Home |
|---|---|
| Concepts, algorithms, file-format theory | Wiki |
| Research, ideas, lab notes | GitHub Discussions |
| Bugs, features, tasks | GitHub Issues |
| Code documentation, setup | Markdown in the repo |

Do not write educational or history prose into markdown files. No TODO comments and no
"Future Enhancements" sections: file an issue and reference it (`// See issue #25`). Create
issues proactively for bugs, debt, doc gaps and test gaps.

## Redaction is security-critical

Excise removes text, graphics and images from the PDF structure. A black box is only the
visual confirmation.

1. **Never** replace glyph removal with visual-only redaction, and never simplify the
   pipeline parse → filter → rebuild → replace → draw.
2. Engine lives in `Excise.Core/Redaction/` (`GlyphRemover`, `LetterFinder`,
   `OperationReconstructor`) and `Excise.Core/Content/` (`ContentStreamParser`,
   `ContentStreamWriter`). `Excise.App/Services/RedactionService.cs` only orchestrates.
3. The redaction gates (`verify-true-redaction.sh`, `check-redaction-oracles.sh`, the
   `Redaction` test suites) run at every tier and have no skip flag. A local build you redact
   a real document with is a binary whose failure hurts someone, silently.
4. **A tool must not be its own oracle.** `ExtractAllText` reads only the content stream and
   has passed on leaking files three times (structure-tree `/ActualText`, XMP and outlines,
   a page our extractor cannot read). Every leak test must also assert one of:

   ```csharp
   SavedPdfLeakScanner.FindTerm(savedBytes, "SECRET").Should().BeEmpty(); // decompresses streams
   MutoolTextExtractor.ExtractPage(path, page).Should().NotContain("SECRET"); // independent tool
   InkFractionIn(after, box).Should().BeLessThan(0.001);                    // independent renderer
   ```

5. **Redaction cannot remove what extraction cannot read, and it will still report success.**
   Extraction changes are redaction-security changes; `scripts/check-extraction-parity.sh`
   guards them. A green gate means "no worse than the floors", not "no blindness".
6. **Redaction has two profiles, Standard (default everywhere) and Maximum.** The engine reads
   the option flags, never `RedactionOptions.Profile` (that is a report label). Anything that
   rebuilds a carrier policy from `CarrierScrubPolicy.Default` silently undoes Maximum.
   Carriers the engine cannot scrub are reported, never silently stripped or skipped; every
   removal is reported. Reach new carriers by walking the object graph, not by listing
   known locations.
7. **A spec default is the default.** Overriding one because "no real producer relies on it"
   shipped a redaction detector that found nothing on the most-cited failed redaction in
   existence (#1617). When consuming a content stream, audit the initial value of every
   state you track against ISO 32000-2 and pin each with a fixture that omits the operator.
   Fixtures written by the people who wrote the code cannot see an assumption they share.
8. Coordinates: PDF is bottom-left, Avalonia is top-left. Convert once at the boundary through
   `Excise.Core.Document.PdfCoordinateMapper`; name variables `pdfY` / `screenY`.
9. Removing a block must not strand later text operators without their font state; keep the
   `Tf` handling in `OperationReconstructor`. Test with real documents, sequential
   redactions and special characters (`$`, parentheses, Unicode, ligatures), not only
   synthetic happy paths. PDF operations on malformed input can hang: use cancellation
   and timeouts.

Full guidance: `REDACTION_AI_GUIDELINES.md`.

## One walk, many sinks (#992)

`Excise.Core/Content/ContentStreamWalker.cs` is the ONE content-stream state machine
(tokenizer, graphics/text state, §9.4.2 line stepping, §9.4.4 advance, font decode). A new
consumer adds a **sink**; it never adds a parser or its own text state. Two parsers drifting
apart caused most of the historic redaction defects. Sinks today: `ContentStreamParser` (bounds
for redaction) and `TextExtractor`. `SkiaRenderer` is the one tracked exception; do not treat
it as a precedent.

A gate that compares excise to excise cannot see a defect excise holds consistently. Compare
against the spec or an independent tool.

## File Locations Quick Reference

The redaction engine is in `Excise.Core`, not the GUI project. Independent reference oracles
live in `Excise.Rendering/Differential/`; use them and do not build new ones. The counts are
re-derived by `scripts/check-doc-claim-freshness.sh`:

```
Excise.Rendering/Differential/
    MutoolReferenceRenderer.cs        # 435 uses in Differential tests
    GhostscriptReferenceRenderer.cs   # 116 uses in Differential tests
    PdftocairoReferenceRenderer.cs    # 83 uses in Differential tests
    PdftoppmReferenceRenderer.cs      # 18 uses in Differential tests
    PdfiumReferenceRenderer.cs        # 2 uses in Differential tests
    PdfBoxReferenceRenderer.cs        # 16 uses in Differential tests
    MutoolTextExtractor.cs            # independent text oracle (MuPDF)
    PdftotextTextExtractor.cs         # second text oracle (Poppler)
    QpdfReferenceTool.cs              # structure: --check, --show-npages
```

PDFium is never loaded in-process (native crashes cannot be caught); it runs out of process
via `Excise.RenderTools pdfium-render`. Skia-origin rasterisation differences are ACCEPTED and
registered in `tests/skia-rasterisation-register.json`; do not fix, compensate or re-triage them.

## Test Tiers

`scripts/test-tier.sh {t0|t1|full|t2|t3}`; add `--list <tier>` to see the rows without running
them. Every gate is one row of `tests/gates.tsv`; `LOCAL_GATES.md` explains it and owns the
runner internals. Pick the tier by **blast radius** (who is hurt if this is wrong), not
convenience:

| Tier | What | When |
|---|---|---|
| `t0` | build, Core/Cli/Avalonia tests, static gates | before every push (`--install-hook`) |
| `t1` | t0 + redaction suites, rendering oracles, parity ratchets, App tests (chunked, #1767) | before merging to `develop` |
| `full` | everything, resumable | weekly and before a release candidate |
| `t2` / `t3` | release smoke | release candidate / before tagging |

There is no CI gate on `develop`; running the tier is on you. Rules that keep the suite honest:

- `Excise.App.Tests` is **serial by design** (`DisableTestParallelization`): parallelism races
  SkiaSharp's process-wide font manager and crashes the host (#363). Never re-enable it, and
  run it alone: CPU contention produces false reds.
- A test step that matches zero tests is a failure. A skipped test carries its reason in code
  (`Assert.SkipWhen(cond, "why")`, `[Fact(Skip = "why")]`), never in an external allowlist. A
  reason claiming something is absent must be true: fixtures and corpora resolve only through
  `Excise.Core.Tests/TestSupport/TestRepoLayout.cs`, never a bounded upward path walk (#1527).
- Prefer `scripts/t.sh <project> [args]` over raw `dotnet test --no-build`; a `--no-build` run
  must prove the binary is fresh (`scripts/assert-fresh.sh`).
- Never commit or edit sourced scripts while a tier is running.
- **A test-only static can pin memory for the process lifetime.** `GuiInteractionRecorder
  .SeenSurfaces` was a strong `HashSet` nothing ever removed from; every window that got an
  input event stayed reachable, tile caches and all. Fixed weak (#1772): App.Tests peak RSS
  4869 → 1949 MB, wall 756 → 356 s. Sample the RSS shape before theorising about a peak — a
  sawtooth on a rising floor is retention, a flat sawtooth is GC high-water. Full record:
  `docs/performance-baselines/2026-09-21-app-testhost-memory/`.
- Test PDF corpora are gitignored under `test-pdfs/`. `scripts/check-test-prereqs.sh` reports
  what is present; fetch with `download-test-pdfs.sh`, `download-pdfjs-corpus.sh`,
  `download-pdfium-corpus.sh`. Never pin a corpus-scan result without
  `scripts/triage-corpus-nonpass.sh` first: the manifests record what excise did, including
  its bugs.

## Build and run

```bash
dotnet restore && dotnet build          # 0 warnings, 0 errors; fix warnings as they appear
cd Excise.App && dotnet run             # add -c Release for release mode
```

Publish: `dotnet publish -c Release -r {linux-x64|win-x64|osx-x64} --self-contained true -p:PublishSingleFile=true`.
Scripts: `./build.sh`, `./build.bat`. Anything over ~30 seconds goes in a script that logs
with `tee` to `logs/`, run in a separate terminal.

### Build Failures

Run `dotnet restore` and `dotnet clean` first. Use **Microsoft's official .NET 10 SDK**, not
Homebrew's `dotnet` formula: Homebrew's runtime pack produces different PDF bytes than the
official one and breaks Native AOT publishing. `dotnet --info` must show a Base Path under
`~/.dotnet`. `global.json` accepts any installed 10.0.x SDK.

## Architecture rules

- Strict MVVM: XAML in `Views/`, state and commands in `ViewModels/` (ReactiveUI), logic in
  `Services/`. **No business logic in code-behind.** UI changes edit the XAML and bind to
  ViewModel properties.
- Services orchestrate `Excise.Core`; they own no engine logic.
- SkiaSharp and HarfBuzz ship native libraries: `Excise.Rendering` and `Excise.Avalonia` are
  not "pure managed". `Excise.Core` is.
- Encryption round-trips (a document opened encrypted saves encrypted); `/P` permissions are
  enforced at the action layer, not in the engine.

## Tasks, issues, release

- GitHub Issues track ALL work. Labels: `gh label list`. Reference issues in code comments
  and commits (`Fixes #17`). Ideas start as Discussions.
- Backlog and roadmap are live data; never write an issue list or a count into this file.

### Current High-Priority Issues

```bash
gh issue list --label "priority: critical,priority: high" --state open
gh api repos/marctjones/excise/milestones --jq '.[] | "\(.number)\t\(.title)"'
```

- Before tagging or describing a release, run `scripts/check-doc-claim-freshness.sh` and
  follow `docs/RELEASE_CHECKLIST.md`. Feature, security and workflow changes update the
  implementation, tests, UI text, release notes, issues and user docs in the same pass.
