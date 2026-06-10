# Agent Instructions

This is a cross-platform PDF editor (C# / .NET 10 / Avalonia, MVVM). Full guidance
for AI assistants lives in [`CLAUDE.md`](CLAUDE.md); read it first.

## Task tracking

This project tracks all work in **GitHub Issues** — bugs, features, tech debt, and
research questions. Use the `gh` CLI:

```bash
gh issue list
gh issue create --title "..." --body "..." --label "bug,component: redaction-engine"
```

Reference issues from code and commits (`// See issue #25`, `Fixes #17`) instead of
leaving `// TODO:` comments or creating ad-hoc tracking files.

## Knowledge organization

Four tiers (see CLAUDE.md for the decision matrix):

- **Wiki** — concepts, algorithms, PDF-format theory (timeless reference)
- **Discussions** — research, ideas, lab notes (unstructured exploration)
- **Issues** — actionable bugs/features/tasks (clear completion criteria)
- **Markdown files** — code/setup documentation (version-controlled)

## Redaction is security-critical

Never replace glyph-level removal with visual-only redaction. Always run
`dotnet test --filter "FullyQualifiedName~Redaction"` after touching redaction code.
See [`REDACTION_AI_GUIDELINES.md`](REDACTION_AI_GUIDELINES.md).
