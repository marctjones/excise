# Building and releasing

Building binaries, installers and the NativeAOT release lane. Release steps are in [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md).

## Plain self-contained binaries

```bash
# Linux
dotnet publish Excise.App -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Windows
dotnet publish Excise.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# macOS Intel / Apple Silicon
dotnet publish Excise.App -c Release -r osx-x64    --self-contained true -p:PublishSingleFile=true
dotnet publish Excise.App -c Release -r osx-arm64  --self-contained true -p:PublishSingleFile=true
```

Published binaries land in `bin/Release/net10.0/<runtime>/publish/`.

Release builds exclude the Roslyn scripting engine by default to keep shipped
packages lean and AOT/trim-friendlier. To produce a developer build with the
GUI scripting service included, add `-p:EnableScripting=true` to the publish
command.

Repo-local `tessdata/*.traineddata` files are also excluded from app packages by
default; excise uses the system `tesseract` installation when differential OCR is
requested. To bundle local language data for an offline/developer package, add
`-p:IncludeTessdataInApp=true`.

## Installers

```bash
# Ubuntu / Debian .deb (requires dpkg-deb; preinstalled on Ubuntu)
scripts/build-deb.sh                          # → dist/excise_<version>_amd64.deb
scripts/build-deb.sh --arch arm64             # arm64 variant
scripts/build-deb.sh --version 2.1.0-rc8      # explicit version

# Windows .exe (requires Inno Setup 6: choco install innosetup)
pwsh scripts/build-windows-installer.ps1      # → dist/excise-<version>-win-x64-setup.exe

# macOS .app bundle (Apple Silicon by default; Intel via --rid osx-x64)
scripts/build-macos-app.sh --version <version>            # → dist/excise-<version>-macos-arm64.zip
scripts/build-macos-app.sh --version <version> --rid osx-x64
```

## Native AOT release lane

Native AOT is the macOS/Linux release package lane. The macOS GUI is
**validated on `osx-arm64`**: it AOT-compiles with a zero warning budget
(first-party `Excise.*` code is AOT-clean — source-generated JSON, no
reflection-based ReactiveUI `WhenAnyValue`; the residual IL2104/IL3053 roll-ups
are from third-party GUI frameworks, suppressed and tracked in #593), and it
passes the packaged GUI smoke. A CI job (**Native AOT (macOS)**) keeps the
macOS AOT publish from regressing. The Linux release workflow builds the AOT
`.deb` and smokes the CLI `version`/`info`/`text`/`render` paths to cover native
asset loading on a headless runner. Other RIDs await per-platform probes (#595).
The local gate publishes/packages the AOT app, captures IL/AOT warning output,
asserts that the payload has `0` managed `.dll` sidecars, separates debug
symbols from the user-facing artifact, and writes JSON/markdown evidence:

```bash
scripts/release-smoke.sh --quick --only=aot
scripts/run-aot-smoke.sh --version <version> --rid osx-arm64
scripts/build-macos-app.sh --version <version> --rid osx-arm64 --aot
```

Use `scripts/run-aot-smoke.sh --gui-smoke` on an interactive macOS runner when
validating the AOT app against packaged GUI launch/open/render evidence.


## Release automation

`.github/workflows/release.yml` builds installers/app bundles and attaches them
to a **draft** GitHub Release whenever a `v*` tag is pushed (publishing stays a
manual click; a manual `workflow_dispatch` is a dry run that uploads run
artifacts and never touches a release):

1. `preflight` job: the version matches the tree, the changelog has notes for it, doc claims and the license manifest are current
2. `linux` job (ubuntu-latest and ubuntu-24.04-arm) → `excise_<version>_{amd64,arm64}.deb` + portable `.tar.gz`
3. `windows` job (windows-latest) → `excise-<version>-win-x64-setup.exe` + portable `.zip`
4. `macos` job (macos-latest) → arm64 `.app` bundle `.zip`
5. `release` job checks every asset against its `.sha256` and creates the draft; tags containing `-rc`/`-beta`/`-alpha` are flagged as pre-releases.

All four packages are Native AOT, and each job unpacks what it ships and fails
if it is not (`scripts/check-aot-payload.py`: no managed assemblies, no runtime,
no single-file bundle marker).

Before tagging, run the release checklist in
[`docs/RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md). The repeatable local
gate is:

```bash
scripts/release-smoke.sh --visual --package --packaged-gui --aot --version <version>
```

The release-smoke script does not create tags or upload artifacts. It runs the
documentation, build, redaction, signature-verification, UI workflow,
benchmark, Native AOT, full-test, local visual-regression, packaging,
packaged-GUI evidence, packaged first-page responsiveness timing, and diff-cleanliness gates
and writes logs under `logs/release-smoke_*`. Passing `--package` implies AOT
unless `--no-aot` is used for a package-only investigation.

```bash
# Cut a new release
git tag -a v2.27.1 -m "excise v2.27.1"
git push origin v2.27.1          # workflow runs, attaches release artifacts
# Or via the GitHub UI: Releases → Draft a new release → choose tag
```
