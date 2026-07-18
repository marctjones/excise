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

require_file_text README.md "page organization"
require_code_text Excise.App/Views/MainWindow.axaml "Insert Pages _Before Current"
require_code_text Excise.App/Views/MainWindow.axaml "Move Page _Later"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "InsertPagesBeforeCurrentCommand"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "MoveCurrentPageLaterCommand"
require_file_text README.md "selected pages"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "ExtractSelectedPagesCommand"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "MoveSelectedPagesLaterCommand"

require_file_text README.md "AddTextAnnotation"
require_file_text Excise.Core/README.md "AddHighlightAnnotation"
require_code_text Excise.Core/Document/PdfAnnotationAuthoring.cs "AddTextAnnotation"
require_code_text Excise.Core/Document/PdfAnnotationAuthoring.cs "AddHighlightAnnotation"
require_file_text README.md "highlight selected text"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "AddHighlightAnnotationFromSelectionCommand"
require_code_text Excise.App/ViewModels/MainWindowViewModel.Commands.cs "AddStickyNoteAnnotationCommand"
require_code_text Excise.App/Views/MainWindow.axaml "Add _Highlight From Selection"

require_file_text README.md "Safe-to-share save path"
require_code_text Excise.App/Services/RedactedCopySafetyService.cs "ScrubMetadata(scrubAttachments: options.ScrubAttachments)"
require_file_text README.md "without repeating removed text"
require_code_text Excise.App/Services/RedactedCopySafetyService.cs "Removed text is not repeated"

require_file_text README.md "OS trust-chain validation limitations"
require_code_text Excise.App/Services/SignatureVerificationSummaryFormatter.cs "trust"

require_file_text README.md "PublicApiApprovalTests"
require_code_text Excise.Core.Tests/Authoring/PublicApiApprovalTests.cs "APPROVE_PUBLIC_API"

# #644: the release checklist names the encryption interop gate as required
# evidence — the suite (and its vacuous-run guard) must actually exist.
require_file_text docs/RELEASE_CHECKLIST.md "EncryptionInteropGateTests"
require_file_text docs/RELEASE_CHECKLIST.md "EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS"
require_code_text Excise.Rendering.Tests/Differential/EncryptionInteropGateTests.cs "EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS"
require_code_text Excise.Rendering.Tests/Differential/EncryptionInteropGateTests.cs "AtLeastOneIndependentToolIsAvailable_GateIsNotVacuous"

echo "doc-claim check passed"
