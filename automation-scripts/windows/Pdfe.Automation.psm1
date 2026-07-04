Set-StrictMode -Version Latest

$script:PdfePath = if ($env:PDFE_CLI) { $env:PDFE_CLI } else { "pdfe" }

function Set-PdfePath {
    param([Parameter(Mandatory)][string]$Path)
    $script:PdfePath = $Path
}

function Invoke-PdfeProcess {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [switch]$AllowNonZeroExit
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $script:PdfePath
    foreach ($arg in $ArgumentList) {
        [void]$psi.ArgumentList.Add($arg)
    }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0 -and -not $AllowNonZeroExit) {
        throw "pdfe exited with $($process.ExitCode): $stderr $stdout"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Invoke-PdfeJson {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [switch]$AllowNonZeroExit
    )

    $result = Invoke-PdfeProcess -ArgumentList $ArgumentList -AllowNonZeroExit:$AllowNonZeroExit
    if ([string]::IsNullOrWhiteSpace($result.StdOut)) {
        return $result
    }

    $json = $result.StdOut | ConvertFrom-Json
    if ($result.ExitCode -ne 0) {
        $json | Add-Member -NotePropertyName ExitCode -NotePropertyValue $result.ExitCode -Force
    }
    return $json
}

function Get-PdfeInfo {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Password
    )

    $args = @("info", $Path, "--json")
    if ($Password) { $args += @("--password", $Password) }
    Invoke-PdfeJson -ArgumentList $args
}

function Get-PdfeText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Page = 0,
        [string]$Password
    )

    $args = @("text", $Path, "--json")
    if ($Page -gt 0) { $args += @("--page", [string]$Page) }
    if ($Password) { $args += @("--password", $Password) }
    Invoke-PdfeJson -ArgumentList $args
}

function Export-PdfePageImage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Output,
        [int]$Page = 1,
        [int]$Dpi = 150,
        [string]$Password
    )

    $args = @("render", $Path, "--output", $Output, "--page", [string]$Page, "--dpi", [string]$Dpi, "--json")
    if ($Password) { $args += @("--password", $Password) }
    Invoke-PdfeJson -ArgumentList $args
}

function Invoke-PdfeBatch {
    param(
        [Parameter(Mandatory)][string]$Workflow,
        [string]$Output
    )

    $args = @("batch", $Workflow, "--json", "--progress")
    if ($Output) { $args += @("--output", $Output) }
    Invoke-PdfeJson -ArgumentList $args -AllowNonZeroExit
}

function Invoke-PdfeRedaction {
    param(
        [Parameter(Mandatory)][string]$Input,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][string]$Text,
        [switch]$CaseSensitive
    )

    if ([System.IO.Path]::GetFullPath($Input) -eq [System.IO.Path]::GetFullPath($Output)) {
        throw "Redaction output must be different from input."
    }

    $workflow = @{
        schemaVersion = 1
        steps = @(@{
            id = "redact"
            command = "redaction.apply"
            input = $Input
            output = $Output
            text = $Text
            caseSensitive = [bool]$CaseSensitive
            confirmDestructive = $true
        })
    }
    $temp = [System.IO.Path]::GetTempFileName()
    try {
        $workflow | ConvertTo-Json -Depth 8 | Set-Content -Path $temp -Encoding UTF8
        Invoke-PdfeBatch -Workflow $temp
    }
    finally {
        Remove-Item -Path $temp -ErrorAction SilentlyContinue
    }
}

function Set-PdfeFormField {
    param(
        [Parameter(Mandatory)][string]$Input,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][hashtable]$Fields,
        [switch]$Flatten
    )

    $workflow = @{
        schemaVersion = 1
        steps = @(@{
            id = "fill-form"
            command = "form.fillForm"
            input = $Input
            output = $Output
            fields = $Fields
            flatten = [bool]$Flatten
        })
    }
    $temp = [System.IO.Path]::GetTempFileName()
    try {
        $workflow | ConvertTo-Json -Depth 8 | Set-Content -Path $temp -Encoding UTF8
        Invoke-PdfeBatch -Workflow $temp
    }
    finally {
        Remove-Item -Path $temp -ErrorAction SilentlyContinue
    }
}

function Test-PdfeHiddenText {
    param([Parameter(Mandatory)][string]$Path)
    Invoke-PdfeJson -ArgumentList @("audit", $Path, "--json") -AllowNonZeroExit
}

Export-ModuleMember -Function Set-PdfePath,Get-PdfeInfo,Get-PdfeText,Export-PdfePageImage,Invoke-PdfeBatch,Invoke-PdfeRedaction,Set-PdfeFormField,Test-PdfeHiddenText
