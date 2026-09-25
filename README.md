<p align="center">
  <img src="Excise.App/Assets/excise_logo.svg" alt="excise logo" width="128" height="128">
</p>

# excise

A cross-platform PDF editor for macOS, Windows and Linux, written in C# on .NET 10 with Avalonia. It removes redacted content from the PDF itself instead of drawing a black box over it. The parser, writer, renderer and redaction engine are all in this repository, with no third-party PDF library.

[![Release](https://img.shields.io/github/v/release/marctjones/excise)](https://github.com/marctjones/excise/releases)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## What it does

- **Redaction that removes content.** Text, images and vector graphics are cut out of the PDF's content streams. Metadata, attachments, JavaScript, thumbnails and hidden layers are scrubbed by default, and every removal is reported. Results are checked with independent tools (mutool, pdftotext), never with excise itself.
- **Read and navigate.** Skia rendering, search, text selection and copy, thumbnails, outlines, and several documents at once.
- **Fill forms.** Fill and flatten AcroForm fields, create new fields, and view dynamic XFA forms. FormCalc calculations run in excise's own interpreter; JavaScript never runs.
- **Annotate.** Highlight, underline, strike-out and squiggly markup, sticky notes, shapes, stamps (including image stamps for signatures), ink, lines and polygons. Typewriter text can be placed on flat PDFs.
- **Organize.** Reorder, rotate, extract, remove and merge pages; reduce file size; Bates numbering.
- **Security.** Read and write AES-128 and AES-256 encryption; inspect digital signatures against the OS trust store.
- **Audit.** `excise unredact` tests whether a PDF that claims to be redacted still leaks its text, and `excise audit` finds text hidden under opaque overlays.
- **Automate.** A command-line tool with stable JSON output and batch workflows.

See [docs/FEATURES.md](docs/FEATURES.md) for the full list and [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md) for what excise does not do.

## Install

Download a package from [GitHub Releases](https://github.com/marctjones/excise/releases). Packages are self-contained and need no .NET install.

| Platform | Asset | Install |
|---|---|---|
| macOS (Apple Silicon) | `excise-<version>-macos-arm64.zip` | Unzip and move `excise.app` to Applications. There is no Intel build. |
| Windows 10/11 (x64) | `excise-<version>-win-x64-setup.exe`, or the portable `excise-<version>-win-x64.zip` | Run the installer, or unzip and run `Excise.App.exe`. |
| Ubuntu / Debian | `excise_<version>_amd64.deb`, `excise_<version>_arm64.deb` | `sudo apt install ./excise_<version>_<arch>.deb` |
| Any Linux | `excise-<version>-linux-x64.tar.gz`, `excise-<version>-linux-arm64.tar.gz` | Extract and run `Excise.App` (GUI) or `excise` (CLI). |

The builds are not signed or notarized, and there is no auto-update. On macOS the first launch is refused: right-click the app and choose Open, or run `xattr -dr com.apple.quarantine /Applications/excise.app`. On Windows, SmartScreen warns: choose More info, then Run anyway. Every asset has a `.sha256` file; verify it with `shasum -a 256 -c <asset>.sha256` (macOS) or `sha256sum -c <asset>.sha256` (Linux).

Printing works on macOS and Windows; there is no Linux printing yet. OCR uses the system `tesseract` (the `.deb` recommends it).

To use excise as your default PDF reader, see [docs/DEFAULT_PDF_READER.md](docs/DEFAULT_PDF_READER.md).

## Command line

```bash
excise info       form.pdf
excise text       report.pdf
excise render     report.pdf -o page1.png --page 1 --dpi 150
excise redact     input.pdf redacted.pdf "Jane Doe"
excise unredact   suspicious.pdf
excise fill-form  form.pdf filled.pdf --field Name="Jane Doe" --flatten
```

All commands, options and exit codes: [docs/CLI.md](docs/CLI.md). Automation contract: [docs/AUTOMATION_API.md](docs/AUTOMATION_API.md).

## Libraries

| Package | What it is |
|---|---|
| `Excise.Core` | PDF parser, writer, encryption, fonts and the redaction engine. Managed code, no native dependency. |
| `Excise.Rendering` | Page rasterization to `SKBitmap` or PNG with SkiaSharp and HarfBuzz. |
| `Excise.Avalonia` | `PdfViewerControl`, a PDF viewer for Avalonia apps. |
| `Excise.Native` | A C ABI over `Excise.Core` for other languages. See [docs/native-api.md](docs/native-api.md). |

```csharp
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;

using var doc = PdfDocument.Open("input.pdf");
doc.RedactText("Jane Doe");
doc.Save("redacted.pdf");
```

The published libraries follow semantic versioning ([docs/API_STABILITY.md](docs/API_STABILITY.md)).

## Build from source

```bash
git clone https://github.com/marctjones/excise.git
cd excise
dotnet run --project Excise.App
```

You need the .NET 10 SDK from Microsoft's installer (not Homebrew's `dotnet`, which produces different output and breaks NativeAOT). Packaging, installers and the release process are in [docs/BUILDING.md](docs/BUILDING.md); tests and gates are in [LOCAL_GATES.md](LOCAL_GATES.md).

## Documentation

- [CHANGELOG.md](CHANGELOG.md): release notes
- [docs/USAGE.md](docs/USAGE.md): desktop workflows and keyboard shortcuts
- [docs/RENDERER_COVERAGE.md](docs/RENDERER_COVERAGE.md): rendering validation and PDF 2.0 conformance
- [docs/architecture/README.md](docs/architecture/README.md): system design and decisions
- [GitHub Wiki](https://github.com/marctjones/excise/wiki): redaction internals and PDF specification notes
- [CLAUDE.md](CLAUDE.md) and [REDACTION_AI_GUIDELINES.md](REDACTION_AI_GUIDELINES.md): contributor guidelines, including for AI-assisted changes

## Contributing

Work is tracked in [GitHub Issues](https://github.com/marctjones/excise/issues) and [milestones](https://github.com/marctjones/excise/milestones), not in this file. Start from an issue whose acceptance criteria name the behavior and the checks that prove it.

## License

MIT. See [LICENSE](LICENSE); the dependency inventory is in [LICENSES.md](LICENSES.md).
