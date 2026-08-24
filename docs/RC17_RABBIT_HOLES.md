# RC17 rabbit holes — what NOT to build, and why (#1138)

De-redaction measurement (RC17) has a specific, provable thesis: **excise reads
the PDF's own exact glyph advances, so it narrows a redaction's residue to fewer
candidates (1–3 at ±0.5pt) than a pixels+OCR attacker (7–8 at ±2pt).** Everything
below is a tempting detour that does not serve that thesis, recorded so it is
declined on purpose rather than wandered into. From the fable design pass.

## Do not automate unredact.live into CI

It is a strictly **weaker** channel than excise's exact-metric path — pixels+OCR
is the `MutoolPositionTolerance` degradation rung (±2pt, 7–8 candidates) that
excise's exact path (±0.5pt, 1–3) already dominates. It is also non-hermetic
(network, a live third-party site, LLM nondeterminism), which violates the
offline + reproducible constraints the benchmark depends on.

**Allowed:** use it **once, manually**, on 5–10 cases, to confirm excise recovers
with fewer bits — a one-time validation of the exact-metric thesis. An optional
`--oracle unredact-live` behind an env flag is acceptable; **gating on it is not.**

## Do not build an LLM candidate generator

The dictionary + width filter **is** the measurement. An LLM guessing makes
recall un-reproducible and re-introduces "assert the answer" — precisely what the
two-mode CLI (`certain` = facts, `residue` = bits, never singularized) exists to
prevent.

## Do not read embedded font programs *for RC17* (that is #1102)

Tempting for band **B5** (embedded-program-only widths), but it is a large
subsystem. The mutool-position degradation rung gives B5 a *measured* (low)
recall without it. **Let B5 being low be the motivation to do #1102 later** — the
corpus doing its job. (#1102's embedded-TrueType rung has since landed for the
width cascade; RC17's B5 still measures the residue path's degradation, and does
not depend on it.)

## Do not build a new OCR path

`DifferentialOcrAuditor` + `PdfOcrService` already exist. The tesseract work is
**wiring**, not a new engine (see #1137 — the certain-channel wiring shipped;
the soft-context reranking half remains).

## Do not add a merged certain+residue mode

The mode separation **is** the safety property. `certain` reports text that is
actually present; `residue` reports width-admissible candidates plus residual
entropy in bits and **never singularizes, even at one candidate**. Merging them
to look more impressive destroys the epistemic line between measurement and
claim.

## Do not collect real redacted/unredacted pairs

They do not exist as a public corpus, and soliciting them is a legal minefield.
**Constructed ground truth is strictly better** because you know the answer — you
generated the redaction, so recall is exact, not estimated. (See also the
no-third-party-errands principle: generate the equivalent from what we hold.)

## Do not gate on the recall number

Recall is a **measurement, not a ratchet.** The only assertions are:

1. **anti-vacuity** — a run measured something, and at least one real band
   recovers above zero;
2. **the negative controls** — B8 (width-closed) and B6 (random string, absent
   from the dictionary) stay near zero.

Gating on *absolute* recall incentivizes making the corpus easier, which is the
exact failure the whole design guards against. "Improvement" is only: recall
rising at a **fixed** band (generation params held, manifest hash unchanged), or
a zero band (B9/B5) becoming non-zero.

## Do not over-invest in multi-word segmentation (B3) early

Single-word width discrimination (B1/B2) is where the thesis is proven. Get that
recall number first; space/`Tw` ambiguity in multi-word runs is a refinement, not
the proof.

---

**Status:** the bands, recall@N, and negative-control discipline are implemented
in `ResidueRecoveryRecallTests` (#1135); the single-anchor box channel that lifted
Bp off zero is #1140. This file is the guardrail list for anyone extending that
work.
