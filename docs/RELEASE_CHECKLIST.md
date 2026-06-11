# Release Checklist

Use this checklist before tagging any `v*` release.

## Documentation Accuracy

- Run `scripts/verify-doc-claims.sh`.
- Confirm README feature bullets match implemented commands, menu items, CLI commands, and public APIs.
- Confirm `Pdfe.Core/README.md`, `Pdfe.Rendering/README.md`, and `Pdfe.Avalonia/README.md` describe the current library APIs.
- Confirm release notes do not imply future issue scope is already shipped.
- If a behavior change touches redaction, signatures, metadata, attachments, or forms, update implementation, UI text, tests, and docs in the same change.

## Validation

- Run `dotnet build pdfe.sln --no-restore`.
- Run `dotnet test --no-build --filter "FullyQualifiedName~Redaction"` after any redaction-adjacent change.
- Run the focused tests for the changed area.
- Run `dotnet test pdfe.sln --no-build --logger "console;verbosity=minimal"` before tagging.
- Run `git diff --check`.

## Issue Hygiene

- Every shipped issue has a completion comment with validation evidence.
- Remaining work stays in GitHub Issues, not TODO comments or roadmap prose.
- Broad epics stay open until all acceptance criteria are done; patch-release issues close when their concrete gate is implemented.

## Release

- Commit with a scoped message.
- Tag with an annotated `v*` tag.
- Push the commit to `origin/main`.
- Push the tag.
- Create or verify the GitHub Release.
