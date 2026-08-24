# Priority order (2026-08-24)

Reordered across all open milestones for **quick wins first** and **code-base
quality that makes later milestones cheaper**. Ordered by leverage — what each
item unblocks or what friction it removes — not by milestone number.

This supersedes the sequencing in `FIX_ORDER.md` (2026-08-10) where they
conflict; that file's root-cause *cluster analysis* still holds, this file's
*order* is current.

> Feature freeze (2026-08-10) still stands: fix errors only. The measurement
> and CLI work (RC13/17/18) is sanctioned as error-finding infrastructure by
> direct request. In-place editing (RC6) and unvalidated features (RC7) stay
> frozen.

---

## Tier 0 — Quick-win quality: clear friction before more work piles on

All small. Each removes a coupling, a lie, or a drift risk that gets more
expensive the longer it sits. Do these first; they make every later tier
cheaper.

| # | milestone | why now |
|---|---|---|
| **#1142** | RC10 | `SavedPdfLeakScanner` reached across test projects — 3 consumers already, growing. Extract before more depend on it. |
| **#1143** | RC10 | standard-14 widths duplicated (Core vs corpus JSON); a drift silently corrupts the recall benchmark. Add the guard now, before more font work. |
| **#1144** | RC10 | `check-skip-budget --update` re-runs the whole suite and whole-file-rewrites — the mechanism by which a CI-needed entry gets dropped. |
| **#1105** | RC12 | `excise letters -p` ignores its page arg — a diagnostic that lies cost a real measurement this session. |
| **#1096** | RC12 | resolve the #1042/#1050 doc contradiction about where #1040 leaked. |
| **#1097** | RC12 | REDACTION_AI_GUIDELINES claims an ObjStm limitation §7.5.7 forbids — a doc that misleads planning. |
| **#1083** | RC10 | audit in-suite subprocesses for the `WaitForExit`-bounds-the-process-not-the-streams bug (it hung the suite 3× this session's predecessor). |
| **#1084** | RC10 | default long-suite scripts to `--blame-hang-timeout` so a host death names its test. |

## Tier 1 — Security quick wins: real leaks the benchmark found, all small

The redaction benchmark surfaced these; each is a term surviving in a carrier
the redaction misses. Small, high value.

| # | why |
|---|---|
| **#1129** | XMP scrub covers only the catalog's `/Metadata`; page/XObject packets keep the term. Reproduced on a real CDC PDF. **Highest of this tier.** |
| **#1130** | AcroForm `/T` field names and `/TU` tooltips are unscrubbed carriers (found on a passport form). |
| **#1131** | white-on-black text is missed by BOTH excise audit and x-ray — a shared blind spot. |

## Tier 2 — The font-metrics root cause (foundational cluster)

**#1102 is the keystone of the whole session's findings.** The width cascade
never consults the embedded font program, then silently guesses 600. Fixing it
unblocks the accuracy of everything geometry-dependent: extraction, redaction
match, the residue engine, the advance-parity gate. Do #1102 first, then the
cluster it enables.

1. **#1102** — read embedded font-program widths (hmtx/CFF) instead of guessing.
2. **#1104** (RC13) — per-glyph advance parity vs mutool. The general instrument that proves #1102 and guards the rest.
3. **#1101** — `PdfPage.Letters` duplicate glyphs (count 36 vs 9). Likely same geometry family.
4. **#1103** — Type3 `/FontMatrix` applied by renderer, not the walker.
5. **#1106** — standard-14 widths above code 126 (encoding-aware glyph names).

> "Font work is redaction security" — these are silent leaks, not display
> polish. #1100 (fixed this session) was the first of the cluster.

## Tier 3 — Redaction correctness remainder (RC12)

After the font cluster, the carrier/collateral defects:
**#1098** (`/AP` appearance rewrite), **#1099** (remaining collateral),
**#999** (sub-3-char floor), then the operand-granularity mechanism work
(**#1092 → #1091 → #1093 → #1044 → #1045**) which is larger and lower-urgency
now that #1090 removed the destructive paths.

## Tier 4 — Measurement + CLI (mostly built; extend)

RC13 (benchmark), RC17 (measure de-redaction), RC18 (the unredact CLI). The
engine and benchmark are built. Remaining high-value: **#1141** (wire the
ink-differential as a first-class leak axis), then the CLI surface
**#1132 → #1146 → #1147**, then the tesseract channel **#1137/#1139**.

## Tier 5 — Later

RC14/RC16 corpus scripts (fetch when a defect needs the corpus, not on
speculation — each cites its trigger), RC11 assembly conservation, RC5 text
extraction, RC3 encryption (#1128 R=5), and the frozen RC6/RC7.

---

## The rule for picking the next thing

Prefer, in order: (1) a Tier-0/1 small fix that removes friction or a real
leak, (2) #1102 and its cluster, (3) whatever a failing gate or the benchmark
newly surfaces. Root cause before symptom; quick quality before new surface.
