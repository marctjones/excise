#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

fail() {
  echo "doc-claim check failed: $*" >&2
  exit 1
}

require_file_text() {
  local file="$1"
  local text="$2"
  grep -Fq "$text" "$file" || fail "$file does not contain expected claim: $text"
}

require_code_text() {
  local file="$1"
  local text="$2"
  grep -Fq "$text" "$file" || fail "$file does not contain expected implementation token: $text"
}

# A ViewModel command that exists but is bound to nothing is a feature the
# README promises and the user cannot reach. Pinning only the declaration in
# Commands.cs cannot tell those apart, so pin BOTH ends: declared AND bound.
#
# require_wired_command <CommandName>
require_wired_command() {
  local cmd="$1"
  require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "$cmd"
  grep -Fq "$cmd" Excise.App/Views/MainWindow.axaml \
    || fail "$cmd is declared but bound to nothing in MainWindow.axaml — the README promises a feature the user cannot reach."
}

require_file_text README.md "page organization"
require_code_text Excise.App/Views/MainWindow.axaml "Insert Pages _Before Current"
require_code_text Excise.App/Views/MainWindow.axaml "Move Page _Later"
require_wired_command "InsertPagesBeforeCurrentCommand"
require_wired_command "MoveCurrentPageLaterCommand"
require_file_text README.md "selected pages"
require_wired_command "ExtractSelectedPagesCommand"
require_wired_command "MoveSelectedPagesLaterCommand"

require_file_text README.md "AddTextAnnotation"
require_file_text Excise.Core/README.md "AddHighlightAnnotation"
require_code_text Excise.Core/Document/PdfAnnotationAuthoring.cs "AddTextAnnotation"
require_code_text Excise.Core/Document/PdfAnnotationAuthoring.cs "AddHighlightAnnotation"
require_file_text README.md "highlight selected text"
require_wired_command "AddHighlightAnnotationFromSelectionCommand"
require_wired_command "AddStickyNoteAnnotationCommand"
require_code_text Excise.App/Views/MainWindow.axaml "Add _Highlight From Selection"

require_file_text README.md "Safe-to-share save path"
require_code_text Excise.App/Services/RedactedCopySafetyService.cs "ScrubMetadata(scrubAttachments: options.ScrubAttachments)"
require_file_text README.md "without repeating removed text"
require_code_text Excise.App/Services/RedactedCopySafetyService.cs "Removed text is not repeated"

# The README promises the summary "clearly reports remaining OS trust-chain
# validation limitations (revocation is not checked)". Pin the SENTENCE that
# delivers that, not the word "trust".
#
# #941 audit: this line used to grep for the bare substring "trust", which a
# signature-VERIFICATION formatter contains a dozen times over ("trust chain",
# "trusted signer", FormatTrustStatus…). Deleting the limitation disclosure
# outright — falsifying the README claim exactly — still printed "doc-claim
# check passed".
require_file_text README.md "OS trust-chain validation limitations"
require_code_text Excise.App/Services/SignatureVerificationSummaryFormatter.cs \
  "certificate revocation (CRL/OCSP) is not checked"

require_file_text README.md "PublicApiApprovalTests"
require_code_text Excise.Core.Tests/Authoring/PublicApiApprovalTests.cs "APPROVE_PUBLIC_API"

# #644: the release checklist names the encryption interop gate as required
# evidence — the suite (and its vacuous-run guard) must actually exist.
require_file_text docs/RELEASE_CHECKLIST.md "EncryptionInteropGateTests"
require_file_text docs/RELEASE_CHECKLIST.md "EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS"
require_code_text Excise.Rendering.Tests/Differential/EncryptionInteropGateTests.cs "EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS"
require_code_text Excise.Rendering.Tests/Differential/EncryptionInteropGateTests.cs "AtLeastOneIndependentToolIsAvailable_GateIsNotVacuous"

# #841: the release checklist names the copy-whitespace parity gate as required
# evidence with a strict tool/corpus-presence guard — the gate script must
# actually honor that env var, or the checklist promises a guarantee the code
# does not deliver (the exact drift verify-doc-claims exists to catch).
require_file_text docs/RELEASE_CHECKLIST.md "EXCISE_REQUIRE_PARITY_TOOLS"
require_code_text scripts/check-copy-whitespace-parity.sh "EXCISE_REQUIRE_PARITY_TOOLS"

echo "doc-claim check passed"
