# Power Automate Desktop Example

Use Power Automate Desktop to run PowerShell, not GUI clicks.

1. Add **Run PowerShell script**.
2. Import the module from this repository or from the installed examples path.
3. Call the command and parse the returned object.

```powershell
Import-Module "C:\path\to\Excise.Automation.psm1" -Force

$result = Export-ExcisePageImage `
  -Path "C:\Docs\input.pdf" `
  -Output "$env:TEMP\excise-page-1.png" `
  -Page 1 `
  -Dpi 150

$result | ConvertTo-Json -Depth 8
```

For redaction:

```powershell
Import-Module "C:\path\to\Excise.Automation.psm1" -Force

Invoke-ExciseRedaction `
  -Input "C:\Docs\input.pdf" `
  -Output "C:\Docs\input.redacted.pdf" `
  -Text "SECRET"
```

The PowerShell layer preserves the CLI JSON contract and refuses redaction when
input and output paths are the same. No desktop focus or mouse/keyboard
injection is required.
