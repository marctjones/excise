# Linux/GNOME Automation Evaluation

v2.23 uses the CLI and batch JSON contract as the supported Linux automation
surface. This works under GNOME, KDE, Wayland, X11, SSH, containers, and CI
without relying on `xdotool`, focus changes, or pointer injection.

The repository already ships a desktop entry for normal PDF activation:
`packaging/deb/excise.desktop`. The example `excise-automation.desktop` shows the
same MIME/open-with shape for local automation experiments.

## D-Bus Decision

A D-Bus interface is not shipped in v2.23 because the current accepted
automation workflows are document operations that do not require controlling an
already-running GUI instance. Adding D-Bus now would create a persistent local
control surface without a clear command set beyond what `excise batch` already
does.

If a future D-Bus bridge is added, keep it narrow and typed:

```text
org.excise.Application.OpenDocument(path: s) -> handle: s
org.excise.Application.CurrentPage(handle: s) -> page: i
org.excise.Application.Search(handle: s, query: s) -> matches: i
org.excise.Application.Status(handle: s) -> json: s
```

Security requirements for any future D-Bus bridge:

- Disabled by default in release builds.
- Local session bus only.
- No redaction or save operation without an explicit output path.
- No passwords in D-Bus logs or status messages.
- Tests for method signatures, authorization failure, and GUI state changes.
