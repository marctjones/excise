# Architecture decisions

These are durable system-level decisions. They record intent and rationale, not
implementation status. A decision may be superseded only by adding a new entry
that names the old ID, updates the architecture registries, and changes the
relevant enforcement and tests.

## AD-001 — Domain semantics live in Excise.Core

**Status:** accepted

PDF object, parsing, content, font, text, mutation, writing, redaction, security,
and validation behavior belongs in `Excise.Core`. Delivery projects translate
user or automation intent and must not create substitute PDF engines.

## AD-002 — Content streams and glyph semantics are first-class

**Status:** accepted

Callers that need text or graphics semantics use the shared content model and
walker. Redaction, extraction, search geometry, and conservation must not drift
into independent operator interpretations. Rendering retains a separate
graphics interpreter because rasterization has different state/output needs;
that exception is explicit and measured.

## AD-003 — Redaction is verified content removal

**Status:** accepted

The security result is removal from saved content and relevant carriers.
Painting a rectangle is presentation only. Shipping redaction paths use the
same fail-secure engine and are judged by independent byte/extraction/rendering
evidence appropriate to the claim.

## AD-004 — Mutation is explicit and conservative

**Status:** accepted

Document changes occur through typed operations and require an explicit save.
The original is preserved by default. Each mutation defines the unrelated
content and security properties it must conserve, plus intentional removals.

## AD-005 — Rendering is isolated behind one explicit context

**Status:** accepted

`Excise.Rendering` depends on Core and owns SkiaSharp rasterization. Operator
families may be separated for maintainability, but they share one render
context, graphics-state stack, resource policy, and cancellation/budget model.

## AD-006 — Delivery and platform policy stay outside the domain

**Status:** accepted

The Avalonia viewer owns reusable presentation mechanics. The desktop app and
CLI own composition and interaction contracts. Native services are exposed as
capability-discovered adapters; Core and Rendering do not depend on OS/UI
implementations.

## AD-007 — No process-wide mutable PDF state

**Status:** accepted

Documents, render contexts, caches, mutation services, and native handles have
explicit owners and lifetimes. Static immutable lookup data is permitted;
mutable singleton workflow or document state is not.

## AD-008 — Architecture status is structured and evidence-backed

**Status:** accepted

Prose explains the desired design. Machine-readable registries own observed
inventory, current/target assessment, evidence, gaps, and issue links.
Generated diagrams are derived artifacts. A directory or type name alone cannot
establish an `implemented` claim.

## AD-009 — Broad legacy intake; narrow, capability-based output

**Status:** accepted

Excise is a daily-driver viewer, editor, and security-sensitive redactor, not a
generic implementation of every PDF feature. It accepts PDF 1.0 through PDF
2.0 within explicit resource and security limits, and interprets shared
semantics using ISO 32000-2:2020 with errata as the current reference. PDF 1.7
remains a first-class compatibility contract for legacy structures. A writer
preserves the input's declared version where that is safe; it emits PDF 2.0
when the chosen workflow requires it or PDFE cannot safely author the
earlier-version form. It must never silently introduce a PDF 2.0-only feature
or a deprecated PDF 2.0 feature.

Product scope is capability-based, not version-badge-based. Core reading,
rendering, text/search, true redaction, page operations, mainstream forms and
annotations, metadata/attachments, encryption, and signature inspection have
priority. Complex media, 3D, geospatial, print-production, and profile
conformance are preserve-only or deferred unless a specific product workflow
and independent evidence justify them. JavaScript and Launch execution remain
blocked. The product makes no blanket PDF 2.0 conformance claim; its published
capability profile and formal, independently validated output modes are the
only conformance statements. Every exception is represented in the PDF
specification capability registry with its reason, mode-specific status, and
evidence.

## AD-010 — Public document facades do not reverse engine ownership

**Status:** accepted

`PdfPage` and `PdfDocument` remain the stable public document contracts. Their
established content, text, and save convenience members remain on those types
for source and binary compatibility, but their declarations and implementation
live in partial files owned by `core-content`, `core-text`, and `core-writing`.
The canonical `{TypeName}.cs` declaration owns the public type; each partial
member's declaration file owns the dependency it makes. A cross-component
partial type without exactly one canonical declaration fails architecture
analysis.

These facades must call or contain the one authoritative engine path; they do
not authorize duplicate parsers, extractors, or writers. New workflow policy
belongs in focused engine services rather than expanding the document model.
Removing or relocating the compiled public members requires an explicitly
versioned API decision and public-API approval change.

## AD-011 — Encryption state sits below document validation

**Status:** accepted

`core-encryption` owns the standard security handler, writer-side encryptor,
password processing, permission value contracts, and legacy cryptographic
transforms. It depends only on PDF primitives and parsing diagnostics. The
document model and writer may depend on that lower layer to preserve established
public security contracts and encryption state.

`core-security` owns document-dependent structural and conformance validation.
It may inspect the authoritative document and content models, but encryption
code does not depend on validation and validation does not move into the
document model. The public `Excise.Core.Security` namespace remains stable;
source-root and component ownership establish dependency direction. There is
one standard security handler and one writer encryption path.

## AD-012 — Font interpretation owns shared glyph decoding facts

**Status:** accepted

`core-fonts` owns CMap parsing, registered CMap lookup, glyph-name tables,
standard Macintosh glyph order, and the one glyph-to-Unicode cascade used by
content walking and text extraction. The decoder receives an object-resolution
function rather than owning a document so it remains below both consumers.

`core-content` owns the neutral typed sink and the single stateful walk.
`core-text` implements the extraction sink and owns reading-order and selection
policy. Reachability may follow interface dispatch from the content contract to
the text implementation, but that synthesized runtime edge is not a reverse
source dependency. Adding a second CMap parser, glyph table, or extraction walk
requires an explicit superseding decision and provenance review.

## AD-013 — PDF numeric tokens have one foundation formatter

**Status:** accepted

`core-primitives` owns the canonical real-number spelling used in PDF syntax:
invariant culture, no exponent notation, at most six decimal places, trimmed
zeros, and normalized negative zero. Content serialization, object writing,
graphics, form flattening, annotation appearances, and signature appearances
all consume that authority without applying independent near-integer or
precision thresholds.

Format-specific serialization remains distinct when it is not PDF syntax.
In particular, XFDF is XML and retains an explicit XML-number contract. A
different PDF quantization rule requires a named typed policy and evidence that
the precision loss is part of the file-format behavior rather than an
accidental local formatter.

## AD-014 — Redaction has a physical owner without breaking API namespaces

**Status:** accepted

`core-redaction` owns the production files under `Excise.Core/Redaction` for
glyph and image removal, content reconstruction, structure and interactive
scrubbing, document-carrier sanitation, audits, reports, and safe-copy policy.
It consumes the authoritative content, document, font, text, filter, primitive,
and writing contracts; it must not copy those engines or introduce a second
redaction pipeline.

The compiled `Excise.Core.Text.Segmentation` and `Excise.Core.Operations`
namespaces remain stable for source and binary compatibility. Namespace names
do not override source-root ownership. Relocations preserve Git history and do
not change code provenance; a future public namespace redesign requires an
explicit versioned API decision and approval-baseline change.

## AD-015 — One object store owns document identity and parser lifetime

**Status:** accepted

Each `PdfDocument` contains exactly one internal `PdfDocumentObjectStore`. That
store owns the source-stream disposition, parser and lexer lifetime, xref,
parsed-object identity cache, reentrant parse lock, decompressor, object-stream
cache, recursion guards, and security handler used while materializing objects.
Object allocation, replacement, removal, reachability, writer enumeration, and
disposal all consume that same state.

`PdfDocument` remains the stable public compatibility facade and document-level
policy surface. Its `GetObject`, `Resolve`, mutation, and writer hooks delegate
to the single store; they do not maintain shadow caches or another graph. The
existing `PdfParser`, `XRefParser`, `PdfDocumentWriter`, and redaction engine
remain authoritative. The document-open pipeline constructs this store, and a
writing-owned save-session boundary consumes it rather than introducing
another reader, document graph, or serializer.

## AD-016 — Supported mutations invalidate derived state by source scope

**Status:** accepted

`PdfDocument` has one internal flags-based invalidation contract for parsed or
projected views derived from the page tree, catalog actions and name trees,
structure/tagging, optional content, attachments, metadata, and page labels.
Each cache documents its authoritative PDF dictionary source and scope.
Supported mutation paths invalidate their affected scopes after changing those
dictionaries; they do not publish a public dirty-state API or a general event
bus.

Page-tree edits retain the stable `PageCollection` and authoritative indirect
object identities. They rebuild the collection's page wrappers and invalidate
only projections that embed page positions, including named destinations,
tagged-text page lookup, and parsed structure state. Unrelated caches retain
their identity until their own source scope changes. Raw dictionary edits made
directly by external callers cannot be intercepted without a breaking API
change and therefore remain outside this supported-mutation contract.

## AD-017 — One open pipeline assembles document identity

**Status:** accepted

All six public `PdfDocument.Open` overloads delegate through one internal
document-open pipeline. The pipeline owns header/version reading, xref revision
traversal, hybrid-reference merge precedence, reconstruction when the assembled
catalog is unreachable, trailer and catalog validation, Standard security
handler negotiation, creation of the single `PdfDocumentObjectStore`, and
owned-stream cleanup if any stage fails. Its typed result transfers the object
store, trailer, catalog, optional Info dictionary, version, and permissions to
the stable public document facade.

The pipeline is orchestration, not another reader. `XRefParser`, `PdfParser`,
the existing filters, and `PdfStandardSecurityHandler` remain the authoritative
implementations, and `CreateNew` continues to pass its minimal in-memory PDF
through the same open path. The parsing foundation does not depend on the
document component. A borrowed stream remains caller-owned on success and
failure; an owned stream transfers to the object store on success and is
disposed by the pipeline or store on failure.

## AD-018 — One save lifecycle owns finalization and writer graph views

**Status:** accepted

Every `PdfDocument.Save` overload and direct `PdfDocumentWriter.Write` enters
one writing-owned save lifecycle. Constructing a writer and making document
queries do not enter that lifecycle. At the start of each write, every
registered idempotent finalizer runs exactly once: embedded-font subsetting,
tagged-structure emission, and PDF/A policy therefore precede serialization
through the same contract rather than independent save paths.

After finalization, one `PdfDocumentSaveSession` exposes the existing object
store to the writer. It captures catalog, Info, file-ID, version, and temporary
encryption-number policy, and lazily computes one catalog-and-Info-reachable
object snapshot. A compressed-format fallback reuses that snapshot instead of
walking a potentially different graph. Stale `/Prev` and source `/Encrypt`
branches remain outside the fresh-write roots; a new `/Encrypt` dictionary is
write-local and never mutates persistent document identity.

`PdfDocumentWriter` remains the sole fresh-write serializer. The public Save,
writer-constructor, and `GetReEncryptionOptions` surfaces remain compatibility
facades with the established plaintext default. The lifecycle composes the
single `PdfDocumentObjectStore`; it is not another object graph, parser,
incremental writer, or encryption implementation.
