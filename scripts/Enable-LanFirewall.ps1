param([string]$ProgramPath)
$ErrorActionPreference = 'Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows requires an administrator to allow FurtherAgent LAN inbound connections.'
}
if (-not $ProgramPath) { $ProgramPath = (Get-ScheduledTask -TaskName FurtherAgent-Codex-Host).Actions[0].Execute }
$ProgramPath = (Resolve-Path -LiteralPath $ProgramPath).Path
$suffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ProgramPath))).Substring(0,12)
foreach ($item in @(@{Protocol='TCP';Port='45984-45999'},@{Protocol='UDP';Port='45371'})) {
    $rule = "FurtherAgent-LAN-$($item.Protocol)-$suffix"
    if (-not (Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name $rule -DisplayName "FurtherAgent LAN $($item.Protocol)" -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $ProgramPath -Protocol $item.Protocol -LocalPort $item.Port -RemoteAddress LocalSubnet | Out-Null
    }
}
Write-Output "FurtherAgent LAN rules enabled for $ProgramPath (local subnet only)."
