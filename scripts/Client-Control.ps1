param([ValidateSet('Status','Shutdown','Restart')][string]$Action='Status', [string]$ExecutablePath, [switch]$AllowLegacyForce)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$allowed=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/current/builds'))+[IO.Path]::DirectorySeparatorChar
function Clients {
    @(Get-Process -Name FurtherAgent.Client -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and $_.SessionId -eq (Get-Process -Id $PID).SessionId })
}
function Command($processId,$command) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',"FurtherAgent.Control.$processId",[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(1000)
        $reader=[IO.StreamReader]::new($pipe)
        $writer=[IO.StreamWriter]::new($pipe);$writer.AutoFlush=$true
        $writer.WriteLine($command)
        $read=$reader.ReadLineAsync()
        if(!$read.Wait(6000)){throw 'Control response timeout'}
        $read.Result|ConvertFrom-Json
    } finally {$pipe.Dispose()}
}
if($Action -eq 'Status') { @(foreach($p in (Clients)){try{Command $p.Id 'status'}catch{[pscustomobject]@{success=$false;processId=$p.Id;executable=$p.Path;error='Local control unavailable'}}})|ConvertTo-Json -Depth 5;return }
$target=$null
if($Action -eq 'Restart') {
    $target=Get-Item -LiteralPath $ExecutablePath
    if(!$target.FullName.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or $target.Name -ne 'FurtherAgent.Client.exe'){throw 'Target outside managed client builds'}
}
foreach($p in (Clients)) {
    $legacy=$false
    try {$state=Command $p.Id 'status';if(!$state.success){throw 'Control status failed'}}
    catch {
        if(!$AllowLegacyForce -or [version]$p.MainModule.FileVersionInfo.FileVersion -ge [version]'0.6.4'){throw}
        $legacy=$true
    }
    if($legacy) {
        # One-time migration only: older builds have no exit API. Recheck path and start time.
        $check=Get-Process -Id $p.Id
        if($check.Path -ne $p.Path -or $check.StartTime -ne $p.StartTime){throw 'Process identity changed'}
        Stop-Process -Id $p.Id
    } else {
        $ack=Command $p.Id 'shutdown';if(!$ack.success){throw $ack.error}
    }
    if(!$p.WaitForExit(30000)){throw 'Client did not exit; no forced termination attempted'}
}
if($Action -eq 'Shutdown'){return}
Start-Process -FilePath $target.FullName -ArgumentList '--background' -WorkingDirectory $target.DirectoryName -WindowStyle Hidden | Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    foreach($p in (Clients)) {
        if($p.Path -ne $target.FullName){continue}
        try {$state=Command $p.Id 'status';if($state.success -and $state.ready){$state|ConvertTo-Json -Depth 5;return}}catch{}
    }
}while([DateTime]::UtcNow -lt $deadline)
throw 'New client did not become ready'
