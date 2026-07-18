Set-StrictMode -Version Latest

$script:ExcisePath = if ($env:EXCISE_CLI) { $env:EXCISE_CLI } else { "excise" }

function Set-ExcisePath {
    param([Parameter(Mandatory)][string]$Path)
    $script:ExcisePath = $Path
}

function Invoke-ExciseProcess {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [switch]$AllowNonZeroExit
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $script:ExcisePath
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
        throw "excise exited with $($process.ExitCode): $stderr $stdout"
    }

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Invoke-ExciseJson {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [switch]$AllowNonZeroExit
    )

    $result = Invoke-ExciseProcess -ArgumentList $ArgumentList -AllowNonZeroExit:$AllowNonZeroExit
    if ([string]::IsNullOrWhiteSpace($result.StdOut)) {
        return $result
    }

    $json = $result.StdOut | ConvertFrom-Json
    if ($result.ExitCode -ne 0) {
        $json | Add-Member -NotePropertyName ExitCode -NotePropertyValue $result.ExitCode -Force
    }
    return $json
}

function Get-ExciseInfo {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$Password
    )

    $args = @("info", $Path, "--json")
    if ($Password) { $args += @("--password", $Password) }
    Invoke-ExciseJson -ArgumentList $args
}

function Get-ExciseText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Page = 0,
        [string]$Password
    )

    $args = @("text", $Path, "--json")
    if ($Page -gt 0) { $args += @("--page", [string]$Page) }
    if ($Password) { $args += @("--password", $Password) }
    Invoke-ExciseJson -ArgumentList $args
}

function Export-ExcisePageImage {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Output,
        [int]$Page = 1,
        [int]$Dpi = 150,
        [string]$Password
    )

    $args = @("render", $Path, "--output", $Output, "--page", [string]$Page, "--dpi", [string]$Dpi, "--json")
    if ($Password) { $args += @("--password", $Password) }
    Invoke-ExciseJson -ArgumentList $args
}

function Invoke-ExciseBatch {
    param(
        [Parameter(Mandatory)][string]$Workflow,
        [string]$Output
    )

    $args = @("batch", $Workflow, "--json", "--progress")
    if ($Output) { $args += @("--output", $Output) }
    Invoke-ExciseJson -ArgumentList $args -AllowNonZeroExit
}

function Invoke-ExciseRedaction {
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
        Invoke-ExciseBatch -Workflow $temp
    }
    finally {
        Remove-Item -Path $temp -ErrorAction SilentlyContinue
    }
}

function Set-ExciseFormField {
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
        Invoke-ExciseBatch -Workflow $temp
    }
    finally {
        Remove-Item -Path $temp -ErrorAction SilentlyContinue
    }
}

function Test-ExciseHiddenText {
    param([Parameter(Mandatory)][string]$Path)
    Invoke-ExciseJson -ArgumentList @("audit", $Path, "--json") -AllowNonZeroExit
}

Export-ModuleMember -Function Set-ExcisePath,Get-ExciseInfo,Get-ExciseText,Export-ExcisePageImage,Invoke-ExciseBatch,Invoke-ExciseRedaction,Set-ExciseFormField,Test-ExciseHiddenText
