# Accessibility Release Checklist

Issue scope: #562, #566, #569, #570, #572, #573.

## Automated Gate

Run the background-safe accessibility gate:

```bash
scripts/run-accessibility-smoke.sh --config Debug
```

This gate verifies:

- shared semantic command metadata in `Excise.Core.Automation`;
- CLI command metadata through `excise commands`;
- GUI controls with command IDs expose accessible names, descriptions,
  shortcuts, unavailable reasons, and matching tooltips;
- search controls, status text, operation status, current mode, and the PDF
  viewer expose stable accessible names/status;
- representative keyboard-only workflows for search, page navigation, text
  selection mode, and redaction mode.

Release smoke runs the same gate with:

```bash
scripts/release-smoke.sh --quick --only=accessibility
```

The report is written as `accessibility-smoke.json` and includes a platform
accessibility-tree probe status.

## Platform Tree Review

The automated gate does not take keyboard or mouse focus. Platform
accessibility-tree checks may require a dedicated interactive runner:

- **macOS AX / VoiceOver:** grant Accessibility permission to the terminal or
  runner app, launch excise with a representative PDF, and inspect menus, toolbar
  buttons, search controls, status text, dialogs, and the PDF viewer through
  VoiceOver or System Events. Set `EXCISE_ACCESSIBILITY_ALLOW_PLATFORM_PROBE=1`
  only on a dedicated runner.
- **Windows UI Automation:** use Inspect.exe or an equivalent UIA client on a
  Windows desktop runner. Verify names, roles, enabled states, shortcut text,
  focus movement, dialogs, and status updates.
- **Linux / GNOME AT-SPI:** run inside a desktop session with user D-Bus and
  AT-SPI services. Verify names, roles, enabled states, focus movement, and
  status text with an AT-SPI inspector.

Any missing platform name, role, state, focus behavior, or announcement should
be filed as a GitHub issue linked to #566 before release.
