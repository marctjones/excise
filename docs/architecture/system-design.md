# Excise system design

## Purpose and boundary

Excise is a cross-platform PDF workbench whose defining security property is
content-level redaction: selected information is removed from the PDF object and
content model, not merely hidden by drawn ink. The application also provides
viewing, search, editing, document assembly, forms, annotations, OCR, and
signature inspection through shared domain libraries.

This document describes the intended architecture. Implementation status,
known gaps, and proof belong in the architecture registries rather than prose.
The current dependency graph contains no PdfPig, PDFsharp, or PDFtoImage package
dependency. Source provenance and independent-implementation assurance remain
subject to the review tracked by issue #1240.

## Principles

1. **Specification-directed domain behavior.** PDF semantics derive from the
   supported ISO 32000 contract and explicit interoperability evidence.
2. **One authoritative semantic path.** Parsing, content walking, glyph
   interpretation, mutation, and writing are shared domain capabilities, not
   reimplemented by each delivery surface.
3. **True removal for redaction.** Visual marks are secondary feedback. Saved
   bytes, independent extraction, and rendered ink establish the relevant
   security result.
4. **Explicit state ownership.** Document lifetime, mutations, render state,
   UI state, caches, and native handles have named owners and bounded lifetimes.
5. **Dependency direction is inward.** UI, CLI, platform, OCR, and verification
   layers may depend on stable domain contracts; domain code does not depend on
   delivery or operating-system policy.
6. **Conservation is a first-class property.** A mutation preserves unrelated
   document content and security properties unless an explicit policy says
   otherwise.
7. **Evidence precedes completion claims.** A path or type proves presence, not
   correctness, reachability, conformance, or completeness.
8. **Complexity follows responsibility.** Large cohesive state machines may
   remain intact; mixed ownership, duplicated policy, and parallel code paths
   are decomposition signals.

## Layers and components

Dependencies normally move from the bottom of this table toward the top-level
delivery surfaces. Stable IDs are shown in parentheses.

| Layer | Components | Responsibility |
|---|---|---|
| Foundation | Core project (`core`), PDF primitives (`core-primitives`), parsing and filters (`core-parsing`) | Represent and safely load untrusted PDF structures. |
| Domain engine | Document/mutation (`core-document`), content walking (`core-content`), fonts (`core-fonts`), text (`core-text`), redaction (`core-redaction`), writing (`core-writing`), security/validation (`core-security`) | Own PDF semantics and mutations without UI or platform policy. |
| Rendering engine | Renderer (`rendering`) | Interpret pages into raster output behind a stable API and one explicit render context. |
| Optional integration | Native OCR (`ocr-native`), OCR coordination (`ocr`) | Isolate native lifetime and add searchable-PDF behavior only when requested. |
| Presentation | Reusable Avalonia viewer (`avalonia`) | Own viewport layout, scheduling, input, selection, and accessibility presentation. |
| Delivery | CLI (`cli`), desktop app (`app`), main-window orchestration (`app-main-window`) | Compose workflows, translate user intent, and own transient delivery state. |
| Assurance | Verification (`verification`), architecture tooling (`architecture-tooling`) | Judge properties independently and detect architecture drift. |

`Excise.Core` is the domain boundary. `Excise.Rendering` may depend on it.
`Excise.Ocr` composes Core, Rendering, and its narrow native adapter.
`Excise.Avalonia`, the CLI, and the desktop app consume those layers; they do
not become alternate PDF engines.

## Primary workflows

The registry owns the detailed ordered steps and entry points. The architectural
chains are:

- **Open and view (`open-view`)**: delivery resolves input → parser loads
  untrusted bytes → document model owns lifetime → renderer rasterizes requested
  pages → viewer schedules and presents → text model supplies search/selection.
- **Redact and save (`redact-save`)**: delivery captures intent → shared text
  semantics locate candidates → redaction removes glyph/image/carrier content →
  the shared safe-copy policy applies requested scrub and audit channels → the
  delivery adapter preserves its explicit encryption and presentation contract
  while the writer produces a fresh copy → independent verification judges
  saved bytes, extraction, and ink.
- **Edit and save (`edit-save`)**: delivery owns transient intent → typed domain
  mutations apply under conservation policy → writer serializes → reopened
  rendering and focused tests judge the result.
- **Make searchable (`make-searchable`)**: renderer supplies the OCR raster →
  native adapter owns engine lifetime → OCR layer interprets results and builds
  text operations → writer persists the result.
- **Verify signature (`verify-signature`)**: parser validates structure and byte
  ranges → security layer judges CMS integrity and permissions → platform layer
  evaluates trust where supported → independent fixtures/tools corroborate.

## State and ownership rules

- `PdfDocument` owns the object graph and lifetime, but focused mutation and
  conservation services own workflow-specific policy.
- The content-stream walker is the shared text/graphics semantic path for domain
  consumers. Rendering's execution state is a deliberate separate interpreter,
  not permission to create more walkers.
- A render operation owns one context and graphics-state stack. Operator-family
  modules receive that state explicitly rather than recreating it.
- Raster consumers share `SkiaRenderer` semantics but do not share an ambient
  bitmap cache. Their keys, memory budgets, invalidation events, and ownership
  differ, so cache unification would couple unrelated lifetimes.
- The reusable viewer owns layout, input, selection, accessibility, and render
  scheduling. Its typed viewport diagnostics and scroll intents expose values,
  never template controls. The desktop app owns product workflows, automation
  scenarios, artifact paths, and dialogs.
- CLI command registration owns option parsing and dispatch. Focused handlers
  own typed delivery inputs/results, cancellation and resource lifetime, exit
  policy, and presentation; they compose Core, Rendering, and OCR engines
  without copying those engines or pushing console policy inward.
- Redacted-copy scrub and audit requests, options, statuses, and reports belong
  to `core-redaction`. Desktop dialogs, CLI notes/JSON/exit codes, file paths,
  passwords, and encryption choices are delivery adapters. Delivery surfaces
  may choose different explicit option sets, but they do not reimplement the
  underlying carrier, text, hidden-content, or raster audit policy.
- `MainWindowViewModel` is a composition surface. Feature state and workflows
  move behind focused collaborators when their ownership and conservation gates
  are established.
- Native OCR and operating-system services are accessed through narrow adapters;
  capability absence has an explicit outcome.

### Raster and bitmap lifetime ownership

| Consumer | Owner and contract | Retention and termination |
|---|---|---|
| Single-page viewer | `PdfViewerControl` schedules renders; `SinglePageRenderLifetime` owns the active generation and a six-entry `(page, DPI)` LRU. | A newer request cancels the prior generation. Document/content invalidation disposes all entries; replacement and LRU eviction dispose the displaced bitmap. Visual-tree detach cancels work but retains entries because the same control may be reattached. |
| Continuous viewer | `PdfViewerControl.Continuous` owns grid keys, scheduling, coalescing, and a 200 MiB tile LRU. | Document/content invalidation cancels the document generation, drops slot references, and disposes the cache. LRU eviction releases cache ownership without disposing a bitmap that may still be bound to a realized image; it becomes collectible after the slot releases it. Visual-tree detach cancels work while preserving reattachable state. |
| Thumbnail sidebar | `ThumbnailSidebarSession` owns demand, prefetch/prewarm cancellation, and displayed Avalonia bitmaps; `ThumbnailCacheService` owns serialized raster requests and the persistent WebP cache. | Session reset/document replacement cancels background demand and disposes displayed bitmaps. The persistent cache is content/version keyed and trims least-recently-used document directories to 500 MiB; a missing or trimmed entry is regenerated. |
| Page-image export | `DocumentImageExportWorkflowService` owns the render/encode/write transaction through the internal `IPageImageRenderer` boundary. | Export renders the current live `PdfDocument` exactly once per requested page, retains no cache, observes caller cancellation, and disposes each bitmap immediately after encoding. App code owns prompts, permission checks, paths, and progress; Rendering owns raster semantics. |

The application responsiveness report is timing evidence, not cache telemetry.
Schema version 2 reports document-open phases only. Hosts that need interactive
cache evidence call `PdfViewerControl.GetRenderDiagnostics()`, whose immutable
snapshot names the single-page and continuous owners separately.

## Cross-cutting invariants

### Security

- All PDF input is untrusted and subject to bounded size, nesting, recursion,
  decompression, time, and native-resource policies.
- Redaction never degrades to visual covering or reports located matches as
  verified removals.
- Integrity, cryptographic validity, trust, permissions, and conformance are
  separate results.
- Secrets and credentials are not stored in architecture data or source.

### Verification

- Security and fidelity claims use an independent oracle where one exists.
- Zero-test discovery, skipped gates, missing fixtures, unavailable tools, and
  clean results are distinguishable outcomes.
- Generated architecture and conformance data are deterministic and checked for
  drift.

### Performance and maintainability

- Lazy loading, streaming, caching, and pooling require explicit ownership and
  bounded retention.
- File length and complexity metrics identify review candidates; responsibility,
  coupling, duplicate semantics, and state ownership determine the refactor.
- A decomposition preserves public API, saved bytes, rendered output, security
  properties, and measured hot-path budgets relevant to the changed boundary.

## Views and status

Do not add a prose implementation matrix here. Query the architecture registry
and regenerate its checked views:

```bash
scripts/check-architecture-artifacts.sh
scripts/check-architecture-artifacts.sh --update
```
