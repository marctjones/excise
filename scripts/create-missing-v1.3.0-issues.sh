#!/bin/bash

# Script to create missing GitHub issues for v1.3.0 implementation plan
# Requires: gh CLI tool (https://cli.github.com/)

set -e

echo "Creating missing v1.3.0 implementation plan issues..."
echo ""

create_issue() {
    local title="$1"
    local body="$2"
    local labels="$3"

    echo "Creating: $title"
    gh issue create \
        --title "$title" \
        --body "$body" \
        --label "$labels"
    echo ""
}

# Issue 1: Filename Suggestion Service (Phase 1.2)
create_issue \
    "Add Filename Suggestion Service for smart redacted filename generation" \
    "$(cat <<'EOF'
## Description
Implement a service that suggests filenames for redacted PDFs following standard naming conventions.

## Requirements
- Suggest `filename_REDACTED.pdf` for original files
- Suggest `filename_pages_1-5.pdf` for page subsets
- Auto-increment if file exists: `filename_REDACTED_2.pdf`

## Implementation
Create `Excise.App/Services/FilenameSuggestionService.cs`:
- `SuggestRedactedFilename(string originalPath)` → returns `filename_REDACTED.pdf`
- `SuggestPageSubsetFilename(string originalPath, string pageRange)` → returns `filename_pages_1-5.pdf`
- `SuggestWithAutoIncrement(string path)` → returns numbered variant if exists

Register service in DI container and inject into MainWindowViewModel.

## Tests
Unit tests in `Excise.App.Tests/Unit/FilenameSuggestionServiceTests.cs`:
- Test appends `_REDACTED` suffix
- Test preserves directory and extension
- Test auto-increment when file exists
- Test page range formatting

## Related
Part of v1.3.0 implementation plan, Phase 1, Step 1.2
EOF
)" \
    "enhancement,component: file-management,priority: high,effort: small"

# Issue 2: Visual distinction for pending vs applied redactions (Phase 2.4)
create_issue \
    "Add visual distinction between pending and applied redactions" \
    "$(cat <<'EOF'
## Description
Draw different overlay styles to distinguish pending redactions (not yet applied) from applied redactions (already saved to PDF).

## Requirements
- **Pending redactions**: Red dashed border (not yet applied)
- **Applied redactions**: Black solid rectangle (already in PDF)
- Both should be visible simultaneously on the page
- Visual feedback should be immediate when marking or applying

## Implementation
- Modify rendering logic to overlay both types
- Store applied redactions separately from pending
- Update on Apply All: move pending → applied
- Clear pending list after successful apply

## User Benefit
- Clear visual feedback about redaction state
- No confusion about which redactions are saved vs marked
- Reduces "did it work?" questions

## Tests
Integration tests in `Excise.App.Tests/Integration/VisualRedactionIndicatorsTests.cs`:
- Pending redaction shows red dashed border
- Applied redaction shows black solid box
- Both types visible simultaneously

## Related
Part of v1.3.0 implementation plan, Phase 2, Step 2.4
EOF
)" \
    "enhancement,component: redaction-engine,component: ui-framework,priority: medium,effort: medium"

# Issue 3: Save Redacted Version dialog (Phase 3.2)
create_issue \
    "Create Save Redacted Version dialog for Apply All workflow" \
    "$(cat <<'EOF'
## Description
Simple dialog shown when applying redactions to an original file. Prompts user to choose filename for redacted version.

## Requirements
- Show suggested filename (from FilenameSuggestionService)
- Display count of pending redactions
- Browse button to change save location
- Confirm original file will be preserved (reassurance)
- Save and Cancel buttons

## UI Design
```
Save Redacted Version
─────────────────────
Save as: [filename_REDACTED.pdf] [Browse...]

3 areas will be redacted
✓ Original file will be preserved

                    [Cancel] [Save]
```

## Implementation
- Create `Excise.App/Views/SaveRedactedVersionDialog.axaml`
- Create `Excise.App/ViewModels/SaveRedactedVersionDialogViewModel.cs`
- Called from ApplyAllRedactionsCommand when working on original file
- Returns null if cancelled, filepath if saved

## Tests
UI tests in `Excise.App.Tests/UI/SaveRedactedVersionDialogTests.cs`:
- Dialog shows suggested filename
- Dialog shows pending count
- Browse button opens file picker
- Save button returns path
- Cancel button returns null

## Related
Part of v1.3.0 implementation plan, Phase 3, Step 3.2
Depends on Issue #37 (FilenameSuggestionService)
EOF
)" \
    "enhancement,component: ui-framework,component: file-management,priority: high,effort: medium"

# Issue 4: Dynamic Save button text based on file state (Phase 4.1)
create_issue \
    "Add context-aware Save button text (\"Save\" vs \"Save Redacted Version\")" \
    "$(cat <<'EOF'
## Description
Change Save button text dynamically based on whether user is working on original file or redacted version.

## Requirements
- **Original file + unsaved changes**: "Save Redacted Version"
- **Redacted version + unsaved changes**: "Save"
- **No changes**: "Save" (grayed out)

## User Benefit
- Clear indication of what Save will do
- Prevents confusion about file operations
- Reinforces that original is never overwritten

## Implementation
Add computed property to MainWindowViewModel:
```csharp
public string SaveButtonText
{
    get
    {
        if (FileState.IsOriginalFile && FileState.HasUnsavedChanges)
            return "Save Redacted Version";
        if (FileState.IsRedactedVersion && FileState.HasUnsavedChanges)
            return "Save";
        return "Save"; // Will be grayed out
    }
}

public bool CanSave => FileState.HasUnsavedChanges;
```

Bind in XAML:
```xml
<Button Content="{Binding SaveButtonText}"
        Command="{Binding SaveFileCommand}"
        IsEnabled="{Binding CanSave}" />
```

## Tests
Unit tests in `Excise.App.Tests/Unit/MainWindowViewModelTests.cs`:
- Original + changes → "Save Redacted Version"
- Redacted + changes → "Save"
- No changes → "Save" (disabled)

## Related
Part of v1.3.0 implementation plan, Phase 4, Step 4.1
EOF
)" \
    "enhancement,component: ui-framework,component: file-management,priority: medium,effort: small"

# Issue 5: Context-aware Save command to prevent original overwrite (Phase 4.2)
create_issue \
    "Implement context-aware Save command to prevent accidental original overwrite" \
    "$(cat <<'EOF'
## Description
Modify Save command behavior to FORCE "Save As" dialog when working on original files, preventing accidental overwrites.

## Requirements
- **Original file + Save**: MUST show Save As dialog (no direct save allowed)
- **Redacted version + Save**: Safe to save directly (update existing redacted file)
- Ctrl+S follows same logic
- Impossible to overwrite original through normal UI workflow

## Safety Goal
Zero accidental overwrites of original files. Users must explicitly choose to create a new file when redacting originals.

## Implementation
```csharp
private async Task SaveFileAsync()
{
    if (!CanSave)
        return;

    if (FileState.IsOriginalFile && FileState.HasUnsavedChanges)
    {
        // FORCE Save As dialog (prevent original overwrite)
        await ApplyAllRedactionsAsync(); // Shows dialog
    }
    else if (FileState.IsRedactedVersion && FileState.HasUnsavedChanges)
    {
        // Safe to save directly
        await ApplyAllRedactionsAsync(); // No dialog, just confirm
    }
}
```

## Tests
Integration tests in `Excise.App.Tests/Integration/ContextAwareSaveTests.cs`:
- Save on original → shows Save As dialog
- Save on original → cannot overwrite
- Save on redacted → updates directly
- Save with no changes → does nothing
- Ctrl+S on original → shows dialog

## Related
Part of v1.3.0 implementation plan, Phase 4, Step 4.2
Critical for file safety

⚠️ **SECURITY**: This prevents data loss from accidental overwrites
EOF
)" \
    "enhancement,component: file-management,priority: critical,effort: small"

# Issue 6: Text extraction after redaction verification tests (Phase 5.2)
create_issue \
    "Add comprehensive text extraction verification tests for redacted content" \
    "$(cat <<'EOF'
## Description
Verify that text extraction returns EMPTY results from redacted areas, confirming TRUE content-level redaction.

## Purpose
These tests verify the fix for "redacted text still selectable until save" issue by ensuring auto-save and reload removes text from PDF structure.

## Test Scenarios
1. **Before Apply**: Text still extractable from marked (pending) areas
2. **After Apply**: Text NOT extractable from applied areas ⭐ CRITICAL
3. **From Saved File**: No text leaks when extracting from redacted file
4. **Multiple Redactions**: All text removed from all areas

## Critical Test Example
```csharp
[Fact]
public async Task TextExtraction_AfterApply_TextNotExtractable()
{
    // Arrange: Create PDF with "CONFIDENTIAL"
    var pdfPath = CreateTestPdf("test.pdf", "CONFIDENTIAL information");
    await _viewModel.OpenFileAsync(pdfPath);

    // Mark and apply redaction
    _viewModel.CurrentRedactionArea = GetAreaContaining("CONFIDENTIAL");
    _viewModel.MarkRedactionArea();
    await _viewModel.ApplyAllRedactionsAsync(); // Auto-saves and reloads

    // Act: Try to extract text from redacted area
    var extractedText = _textExtractionService.ExtractTextFromArea(
        _viewModel.FileState.CurrentFilePath,
        0,
        GetAreaContaining("CONFIDENTIAL"));

    // Assert: Text NOT extractable (TRUE redaction!)
    Assert.DoesNotContain("CONFIDENTIAL", extractedText);
    Assert.Empty(extractedText);
}
```

## Implementation
Create `Excise.App.Tests/Integration/TextExtractionAfterRedactionTests.cs` with comprehensive scenarios.

## Related
Part of v1.3.0 implementation plan, Phase 5, Step 5.2
Verifies fix for Issue #1 from REDACTION_UX_ISSUES.md
EOF
)" \
    "enhancement,component: redaction-engine,component: text-extraction,priority: high,effort: medium"

# Issue 7: Original file protection verification tests (Phase 5.3)
create_issue \
    "Add original file protection tests to prevent accidental overwrites" \
    "$(cat <<'EOF'
## Description
Test that original files CANNOT be overwritten through normal UI workflow, preventing data loss.

## Test Scenarios
1. **Save on Original**: Cannot overwrite through normal Save
2. **Save Button State**: Disabled or shows "Save Redacted Version"
3. **Ctrl+S on Original**: Shows Save As dialog
4. **File Unchanged**: Original file timestamp not modified after redaction
5. **Two Files Exist**: Original + redacted version both exist after apply

## Critical Test Example
```csharp
[Fact]
public void CannotOverwriteOriginal_ThroughNormalSave()
{
    // Arrange
    var originalPath = CreateTestPdf("important.pdf", "Data");
    var originalTimestamp = File.GetLastWriteTime(originalPath);

    _viewModel.OpenFileAsync(originalPath).Wait();
    _viewModel.CurrentRedactionArea = new Rect(10, 10, 100, 20);
    _viewModel.MarkRedactionArea();

    // Act: Try to save (should show dialog, NOT overwrite)
    _viewModel.SaveFileCommand.Execute(null);

    // Assert: Original file timestamp unchanged
    var newTimestamp = File.GetLastWriteTime(originalPath);
    Assert.Equal(originalTimestamp, newTimestamp);
}
```

## Implementation
Create `Excise.App.Tests/Integration/OriginalFileProtectionTests.cs` with comprehensive safety checks.

## User Benefit
**Zero data loss** from accidental overwrites. Original files always preserved.

## Related
Part of v1.3.0 implementation plan, Phase 5, Step 5.3
Critical for file safety

⚠️ **SAFETY**: These tests verify data protection guarantees
EOF
)" \
    "enhancement,component: file-management,priority: high,effort: medium"

# Issue 8: Status bar updates for redaction state (Phase 6.1)
create_issue \
    "Add status bar updates to show pending redaction count and file type" \
    "$(cat <<'EOF'
## Description
Update status bar to display current redaction state and file type, providing constant feedback to user.

## Requirements
- Show pending redaction count: "3 areas marked"
- Show file type: "Ready" | "Redacted version"
- Update dynamically as user marks/applies redactions
- Clear, concise messaging

## Implementation
Add computed property to MainWindowViewModel:
```csharp
public string StatusBarText
{
    get
    {
        if (FileState.PendingRedactionsCount > 0)
            return $"{FileState.PendingRedactionsCount} areas marked";
        if (FileState.IsOriginalFile)
            return "Ready";
        if (FileState.IsRedactedVersion)
            return "Redacted version";
        return "Ready";
    }
}
```

Bind in XAML status bar.

## User Benefit
- Constant awareness of redaction state
- Visual confirmation when marking areas
- Reminder of file type being edited

## Related
Part of v1.3.0 implementation plan, Phase 6, Step 6.1
EOF
)" \
    "enhancement,component: ui-framework,priority: medium,effort: small"

# Issue 9: Manual testing checklist for v1.3.0 release (Phase 7.2)
create_issue \
    "Create and execute manual testing checklist for v1.3.0 release" \
    "$(cat <<'EOF'
## Description
Comprehensive manual testing scenarios to validate v1.3.0 UX redesign before release.

## Test Scenarios

### 1. Simple Redaction (90% use case)
- [ ] Open PDF
- [ ] Mark 3 areas for redaction
- [ ] Click "Apply All Redactions"
- [ ] Dialog shows suggested filename: `filename_REDACTED.pdf`
- [ ] Click Save
- [ ] Verify: Original exists and unchanged
- [ ] Verify: Redacted version exists with black boxes
- [ ] Verify: Cannot extract text from redacted areas
- [ ] **Success criteria: < 2 minutes to complete**

### 2. Continue Working on Redacted Version
- [ ] Open `filename_REDACTED.pdf`
- [ ] Mark 2 more areas
- [ ] Click Save (regular save, no dialog)
- [ ] Verify: File updated
- [ ] Verify: Original still unchanged

### 3. Cannot Overwrite Original
- [ ] Open `original.pdf`
- [ ] Mark area for redaction
- [ ] Press Ctrl+S
- [ ] Verify: Save As dialog appears (NOT simple save)
- [ ] Verify: Cannot overwrite original through normal workflow

### 4. Text Extraction Fix
- [ ] Open PDF
- [ ] Mark area with "SECRET" text
- [ ] Click "Apply All"
- [ ] Save as `test_REDACTED.pdf`
- [ ] Try to select/extract text from redacted area
- [ ] Verify: No text extracted (FIXED!)

### 5. Multiple Redaction Sessions
- [ ] Open `test.pdf`
- [ ] Mark 2 areas, apply, save as `test_REDACTED.pdf`
- [ ] Close and reopen `test_REDACTED.pdf`
- [ ] Mark 1 more area, apply, save
- [ ] Verify: All 3 redactions visible
- [ ] Verify: Original `test.pdf` still unchanged

## Platform Testing
- [ ] Test on Linux
- [ ] Test on Windows
- [ ] Test on macOS

## Related
Part of v1.3.0 implementation plan, Phase 7, Step 7.2
EOF
)" \
    "enhancement,priority: high,effort: large"

# Issue 10: Update documentation for v1.3.0 release (Phase 7.3)
create_issue \
    "Update documentation for v1.3.0 mark-then-apply workflow release" \
    "$(cat <<'EOF'
## Description
Update project documentation to reflect v1.3.0 UX redesign features and workflow changes.

## Files to Update

### 1. README.md
- [ ] Add v1.3.0 features section
- [ ] Update workflow description (mark-then-apply)
- [ ] Add "How to Save Redacted PDFs" section
- [ ] Update screenshots if available

### 2. CHANGELOG.md
Create comprehensive changelog entry:
```markdown
## [1.3.0] - 2025-XX-XX

### Added
- Mark-then-apply redaction workflow (batch redactions)
- Context-aware Save button (prevents original overwrite)
- Automatic filename suggestions (`_REDACTED.pdf`)
- Pending redactions panel with preview
- Visual distinction: pending (red dashed) vs applied (black solid)
- Auto-save and reload after applying redactions

### Fixed
- Redacted text no longer extractable before save (auto-save fixes this)
- Cannot accidentally overwrite original file
- Clipboard history confusion (shows pending vs applied state)

### Changed
- Redaction workflow: mark multiple areas → review → apply all
- Save behavior based on file state (original vs redacted version)
```

### 3. UX_REDESIGN_PROPOSAL.md
- [ ] Update implementation status
- [ ] Mark Phase 1 as "Completed"

## Related
Part of v1.3.0 implementation plan, Phase 7, Step 7.3
EOF
)" \
    "documentation,priority: high,effort: small"

# Issue 11: Create v1.3.0 release (Phase 7.4)
create_issue \
    "Create v1.3.0 release with binaries and release notes" \
    "$(cat <<'EOF'
## Description
Build, package, and publish v1.3.0 release with binaries for all platforms.

## Release Checklist

### Version Bump
- [ ] Update version in `.csproj` to `1.3.0`
- [ ] Update `AssemblyVersion` and `FileVersion`

### Build Release Binaries
```bash
# Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# macOS Intel
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true

# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
```

### Testing
- [ ] Run full test suite: `dotnet test -c Release`
- [ ] All 744+ tests passing
- [ ] Manual testing checklist complete (Issue #45)

### Git Operations
- [ ] Create git tag `v1.3.0`
- [ ] Push tag: `git push --tags`

### GitHub Release
- [ ] Create GitHub release with release notes
- [ ] Attach binaries for all platforms
- [ ] Publish release

## Release Highlights
- Mark-then-apply redaction workflow
- Safe file operations (original always preserved)
- Critical fix: redacted text immediately non-extractable

## Related
Part of v1.3.0 implementation plan, Phase 7, Step 7.4
Depends on Issue #45 (manual testing) and Issue #46 (documentation)
EOF
)" \
    "enhancement,priority: critical,effort: medium"

echo "✓ All missing v1.3.0 implementation issues created!"
echo ""
echo "Next steps:"
echo "1. Close duplicate issues #14-16"
echo "2. Review new issues for accuracy"
echo "3. Update IMPLEMENTATION_PLAN_V1.3.0.md status or delete it"
echo ""
