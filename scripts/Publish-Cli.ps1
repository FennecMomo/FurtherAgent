param([ValidateSet('win-x64','linux-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
$releaseDir = Join-Path $repoRoot "artifacts/release/FurtherAgent-Cli-$version-$Runtime"
dotnet publish (Join-Path $repoRoot 'src/FurtherAgent.Cli') -c Release -r $Runtime --self-contained true -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/CLI.md'), (Join-Path $repoRoot 'docs/Feature-Feasibility.md'), (Join-Path $repoRoot 'docs/File-Transfer.md'), (Join-Path $repoRoot 'docs/Features-0.5.0-Validation.md'), (Join-Path $repoRoot 'LICENSE') -Destination $releaseDir -Force
if ($Runtime -eq 'win-x64') {
    $archive = "$releaseDir.zip"
    Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $archive -Force
} else {
    $archive = "$releaseDir.tar.gz"
    tar -czf $archive -C $releaseDir .
    if ($LASTEXITCODE -ne 0) { throw 'CLI archive failed.' }
}
Get-FileHash -LiteralPath $archive -Algorithm SHA256
