---
name: excise-release
description: Plan and ship an Excise release with focused regression checks, cross-platform package validation, and clear stop conditions. Use for release readiness, tagging, packaging, publishing, or installing Excise releases.
---

# Excise Release

Ship once the candidate has no **major regression a typical user would notice** versus the last released version. Use the smallest trustworthy evidence that answers that question. Do not turn the repository's full validation inventory into a mandatory release checklist.

## Workflow

1. **Set the scope once.** Read `CLAUDE.md`, `docs/RELEASE_CHECKLIST.md`, and the current changelog entry. Check `git status`, candidate SHA, last release tag, and version. Identify only critical user workflows affected by the candidate. Reuse passing evidence only when it names the exact candidate commit and has a complete, trustworthy report.
2. **Choose focused checks.** Prefer existing candidate `t1` evidence when available. Otherwise run the smallest targeted tests covering the affected critical workflows, plus the pre-push `t0` gate. Run the full suite only when a concrete major-risk question cannot be answered by focused checks or the user explicitly asks for it. Redaction suites are required for redaction changes; they are not general release gates when redaction behavior is unchanged.
3. **Judge impact, not volume.** Block on reproducible crashes, hangs, app unresponsiveness, broken core workflows, data loss/corruption, security regressions in changed behavior, version mismatch, or invalid packages. A benchmark slowdown by itself is acceptable while the app remains responsive. Minor UI polish, unrelated issues, baseline disagreements, missing optional evidence, and infrastructure/telemetry defects do not block; record them once and continue with valid focused evidence.
4. **Stop investigating at the decision boundary.** For each concern ask: could resolving it change the major-regression decision? If no, stop, keep it in its existing issue or note the evidence limitation, and proceed. Do not repeatedly diagnose a host/tooling failure. Do not restart a completed or interrupted full run unless that result is necessary to answer a concrete blocker question.
5. **Build and release.** Verify version and changelog; run `scripts/check-doc-claim-freshness.sh`. Push the candidate to `develop` (the pre-push gate runs `t0`). Run the release workflow's `workflow_dispatch` dry run for the exact candidate SHA and version; require all package/AOT/checksum legs to pass. Then create and push an annotated `vX.Y.Z` tag, let the tag workflow build the release artifacts, inspect the release assets and checksums, fast-forward `main` to the tag, and publish the draft release. Follow existing authorization boundaries for external writes and installations; a skill does not itself grant permission.
6. **Install only when requested.** Verify the downloaded platform asset against its published checksum, preserve the current installation recoverably, install, and confirm the reported version and successful launch. Do not interrupt foreground user work without authorization.

## Excise-specific boundaries

- Read `docs/RELEASE_CHECKLIST.md` for exact tag, workflow, asset, and `main` mechanics; this skill narrows test selection and stop conditions, not release integrity checks.
- Do not edit product code merely to make unrelated gates green during release. If a new code change is proposed as a blocker, explain the reproducible major user impact and let the user review that change before treating it as required.
- Report what passed, what remains unverified, and whether any limitation changes the release decision. Never describe interrupted or incomplete evidence as a pass.
