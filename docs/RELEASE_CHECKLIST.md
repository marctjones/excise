# Release Checklist

Use this checklist before tagging any `v*` release.

## Documentation Accuracy

- Run `scripts/verify-doc-claims.sh`.
- Confirm README feature bullets match implemented commands, menu items, CLI commands, and public APIs.
- Confirm `Pdfe.Core/README.md`, `Pdfe.Rendering/README.md`, and `Pdfe.Avalonia/README.md` describe the current library APIs.
- Confirm release notes do not imply future issue scope is already shipped.
- If a behavior change touches redaction, signatures, metadata, attachments, or forms, update implementation, UI text, tests, and docs in the same change.

## Validation

- Run `scripts/release-smoke.sh --visual --package --version <version>` before tagging a release candidate.
- Run `dotnet build pdfe.sln --no-restore`.
- Run `dotnet test --no-build --filter "FullyQualifiedName~Redaction"` after any redaction-adjacent change.
- Run the signature verification and UI workflow gates in `scripts/release-smoke.sh`.
- Run the focused tests for the changed area.
- Run `dotnet test pdfe.sln --no-build --logger "console;verbosity=minimal"` before tagging.
- Run `scripts/run-visual-regression-local.sh --release` before tagging a release candidate.
- Run `git diff --check`.

`scripts/release-smoke.sh` is the repeatable wrapper for these gates. It does
not tag, push, create a GitHub Release, or upload artifacts. Its build gate
restores packages so it is reliable after configuration-changing package
builds. Its build and test gates use the same Debug configuration as CI because
Release excludes the developer scripting surface by default; `--package` is the
Release artifact check. Use `--quick` for a short documentation/build/redaction
check, and use `--visual --package` for the full local release-candidate pass.

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
- Verify `.sha256` files are present for each release artifact.
