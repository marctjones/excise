# Adopted code-health baseline f2550b79 (#1945, epic #1938)

**Adopted 2026-10-08 by Marc Jones** (`reviewer` is recorded in `snapshot.json`'s source attestation).

- Source: clean checkout of commit `f2550b793a3280b863be69add12003c409d43ed7` (`head` in `capture.json`).
- Two consecutive captures from that commit were byte-identical (`diff -r`), as the adoption checklist in
  [../../review-policy.md](../../review-policy.md) requires. The earlier unadopted candidate,
  [candidate-4421d66a](../candidate-4421d66a/), is kept.
- Reviewed before adoption: the ranked `summary.json` (largest methods by cognitive complexity, cyclomatic
  complexity, executable lines, nesting and parameter count; 20 duplicate-code groups, 5 with shipping code;
  7 component edges not in the architecture registry). These are rankings and review candidates only.
- Use: informational. After a release commit exists, capture it and run `review` against this baseline; a review
  request means a person reads the symbol. It is not a score, a verdict, a merge gate or a release blocker.
- The raw reports (about 150 MB) are not committed. `snapshot.json` pins their hashes; recreate them with

```sh
python3 scripts/code-health.py capture --output DIR --reviewer "Marc Jones" --as-of 2026-10-08
```

  from commit `f2550b79` and the recorded SDK, then compare the hashes. The output directory must be outside the
  checkout.
