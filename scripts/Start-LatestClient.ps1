$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$currentRoot = Join-Path $repoRoot 'artifacts/current'
try {
    $manifest = Get-Content -LiteralPath (Join-Path $currentRoot 'client.json') -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath((Join-Path $currentRoot $manifest.relativeExe))
    $allowed = [IO.Path]::GetFullPath((Join-Path $currentRoot 'builds')) + [IO.Path]::DirectorySeparatorChar
    if (!$target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $target -PathType Leaf)) {
        throw 'The current client build is missing or invalid. Run scripts/Publish.ps1 again.'
    }
    # An explicit desktop shortcut opens the interactive client; do not hide its window.
    Start-Process -FilePath $target -WorkingDirectory (Split-Path $target -Parent)
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'FurtherAgent') | Out-Null
    exit 1
}
