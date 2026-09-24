#!/usr/bin/env bash
# Real-world dynamic XFA forms for layout stress tests (#1547, #1579).
#
# Four public forms, fetched as the original bytes from archived copies (the same copies pdf.js
# tests against) and pinned by SHA-256, so a moved or changed file fails instead of silently
# changing the corpus (#1727). 262 to 452 fields each; two are encrypted (IRCC, AES, empty user
# password), one is French, one has repeating rows. Output: test-pdfs/xfa-real/ (gitignored).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/test-pdfs/xfa-real"
mkdir -p "$DEST"

fetch() { # name url sha256
  local out="$DEST/$1"
  if [[ -f "$out" ]] && [[ "$(shasum -a 256 "$out" | cut -d" " -f1)" == "$3" ]]; then echo "ok      $1"; return; fi
  curl -sfL --retry 3 -m 180 "$2" -o "$out.part"
  local got; got="$(shasum -a 256 "$out.part" | cut -d" " -f1)"
  if [[ "$got" != "$3" ]]; then rm -f "$out.part"; echo "CHECKSUM MISMATCH for $1: got $got, expected $3" >&2; exit 1; fi
  mv "$out.part" "$out"; echo "fetched $1"
}

fetch imm5257e.pdf "https://web.archive.org/web/20210521163640id_/https://www.canada.ca/content/dam/ircc/migration/ircc/english/pdf/kits/forms/imm5257e.pdf" d512ef649902eadc400c667cf6ad41bba0d905e2a54245a339be6c25a2d80f11
fetch imm1295e.pdf "https://web.archive.org/web/20210506174920id_/https://www.canada.ca/content/dam/ircc/migration/ircc/english/pdf/kits/forms/imm1295e.pdf" cb8a29f740d56d822c42bccc3e71ac6ca8372c7abb482a47a6d0b84f2d36b24d
fetch hsbc-cloture-compte.pdf "https://web.archive.org/web/20210610145227id_/https://www.hsbc.fr/content/dam/hsbc/fr/docs/pib/Cloture-Compte.pdf" 0b2609c7a5910934756cdb88429d2a35e80da525c853c02deacca354b6765260
fetch ohio-expense-report.pdf "https://web.archive.org/web/20210509141350id_/https://www.sos.state.oh.us/globalassets/elections/directives/2020/dir2020-25_annualexpensereport.pdf" 4780f4556c14a395d977f16ccee58f59863abdb962f993f8d35f0a7d548e9fb3
