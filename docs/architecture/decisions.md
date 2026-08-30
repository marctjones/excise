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
