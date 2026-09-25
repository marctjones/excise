# Using excise as your default PDF reader

Setup notes for macOS and Windows.

## Using excise as a PDF reader on macOS

The `.app` bundle declares itself a handler for PDF files (`CFBundleDocumentTypes`)
and opens documents passed by Finder, the Dock, or `open -a` — so it can be used
as a regular reader, not just launched empty.

```bash
# First launch: builds are deliberately not notarized (#629 — excise targets an
# audience of one, so a one-time manual accept beats maintaining an Apple
# Developer cert). Either right-click → Open once, or clear the quarantine:
xattr -dr com.apple.quarantine /Applications/excise.app

# Open a PDF in excise:
open -a excise ~/Documents/example.pdf
```

To make excise the **default** PDF app: select any `.pdf` in Finder → **⌘I** (Get
Info) → **Open with** → choose *excise* → **Change All…**. Double-clicking PDFs
then opens them in excise.

## Using excise as a PDF reader on Windows

The Windows installer registers excise as a PDF-capable app in the per-user
`Default apps` / `Open with` registry metadata. During install, select
**Associate excise with .pdf files** to add the `excise.pdf` ProgID. Windows 10/11
still require the user to choose the default handler: Settings → Apps →
Default apps → choose defaults by file type → `.pdf` → **excise**.

The portable `.zip` does not write registry entries. For portable installs, use
Explorer → right-click a PDF → **Open with** → **Choose another app** → browse to
`Excise.App.exe`; selected PDFs are passed to excise and opened on launch.
