param(
    [Parameter(Mandatory = $true)]
    [string]$FurtherAgentExecutable,

    [string]$CodexDesktopExecutable,

    [ValidateRange(1024, 65535)]
    [int]$Port = 47338
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '此启动方式用于 Windows 上的 Codex Desktop。'
}

function Resolve-Executable([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Extension -ne '.exe') {
        throw "请选择可执行文件：$Path"
    }
    return $file.FullName
}

function Start-Interactive([string]$Executable, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $Executable -Parent
    foreach ($name in $Environment.Keys) {
        if ($null -eq $Environment[$name]) { $start.EnvironmentVariables.Remove($name) }
        else { $start.EnvironmentVariables[$name] = $Environment[$name] }
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "无法启动 $Executable" }
    $process.Dispose()
}

$furtherAgent = Resolve-Executable $FurtherAgentExecutable
$agentExecutableName = [IO.Path]::GetFileName($furtherAgent)
if ($agentExecutableName -notin 'FurtherAgent.Client.exe', 'FurtherAgent.Host.exe') {
    throw 'FurtherAgentExecutable 应指向 B 上新版的 FurtherAgent.Client.exe 或 FurtherAgent.Host.exe。'
}
if (-not $CodexDesktopExecutable) {
    $package = Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw '未找到 Microsoft Store 的 Codex Desktop。请用 CodexDesktopExecutable 指定同一安装中的 ChatGPT.exe。'
    }
    $CodexDesktopExecutable = Join-Path $package.InstallLocation 'app/ChatGPT.exe'
}
$desktop = Resolve-Executable $CodexDesktopExecutable
$desktopDirectory = Split-Path $desktop -Parent
$codex = Resolve-Executable (Join-Path $desktopDirectory 'resources/codex.exe')

$session = (Get-Process -Id $PID).SessionId
$desktopName = [IO.Path]::GetFileNameWithoutExtension($desktop)
$running = Get-Process -Name $desktopName, 'FurtherAgent.Client' -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $session }
if ($running) {
    throw '请先让 Codex 完成正在执行的任务，再从托盘退出 Codex Desktop 和 FurtherAgent，然后运行本脚本。'
}

$existing = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw "本机端口 $Port 已被监听。请确认是否已有共享 Codex App Server，再决定启动方式。"
}

Add-Type -AssemblyName System.Net.Http
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromSeconds(1)
$endpoint = "ws://127.0.0.1:$Port"
$logs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FurtherAgent/CodexShared'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$logPrefix = Join-Path $logs ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
$server = $null
$desktopStarted = $false

try {
    $server = Start-Process -FilePath $codex -ArgumentList @('app-server', '--listen', $endpoint) `
        -WorkingDirectory $desktopDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$logPrefix.out.log" -RedirectStandardError "$logPrefix.error.log"
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline -and -not $server.HasExited) {
        try {
            $ready = $http.GetStringAsync("http://127.0.0.1:$Port/readyz").GetAwaiter().GetResult() -ne $null
        } catch [Net.Http.HttpRequestException] {
            $ready = $false
        } catch [OperationCanceledException] {
            $ready = $false
        }
        if ($ready) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready -or $server.HasExited) {
        throw "共享 Codex App Server 未就绪。请检查 $logPrefix.error.log。"
    }

    Start-Interactive $desktop @{
        CODEX_APP_SERVER_WS_URL = $endpoint
        CODEX_APP_SERVER_FORCE_CLI = $null
    }
    $desktopStarted = $true

    Write-Output "Codex 共享 App Server 已就绪。地址 $endpoint，进程 ID $($server.Id)，日志 $logPrefix.error.log"
    if ($agentExecutableName -eq 'FurtherAgent.Client.exe') {
        Start-Interactive $furtherAgent @{
            FURTHERAGENT_CODEX_APP_SERVER_WS_URL = $endpoint
            FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE = $null
        }
        Write-Output '请在启动后的 FurtherAgent 窗口按原方式开启共享。'
    } else {
        $savedBackend = $env:FURTHERAGENT_BACKEND
        $savedFurtherAgentUrl = $env:FURTHERAGENT_CODEX_APP_SERVER_WS_URL
        $savedTokenFile = $env:FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE
        try {
            $env:FURTHERAGENT_BACKEND = 'codex'
            $env:FURTHERAGENT_CODEX_APP_SERVER_WS_URL = $endpoint
            $env:FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE = $null
            $headless = Start-Process -FilePath $furtherAgent -WorkingDirectory (Split-Path $furtherAgent -Parent) `
                -WindowStyle Hidden -PassThru -RedirectStandardOutput "$logPrefix.host.log" -RedirectStandardError "$logPrefix.host-error.log"
            Write-Output "FurtherAgent 后台接入端进程 ID $($headless.Id)。配对码和启动状态请看 $logPrefix.host.log"
            $headless.Dispose()
        } finally {
            $env:FURTHERAGENT_BACKEND = $savedBackend
            $env:FURTHERAGENT_CODEX_APP_SERVER_WS_URL = $savedFurtherAgentUrl
            $env:FURTHERAGENT_CODEX_APP_SERVER_TOKEN_FILE = $savedTokenFile
        }
    }
} finally {
    $http.Dispose()
    if (-not $desktopStarted -and $null -ne $server -and -not $server.HasExited) {
        Stop-Process -InputObject $server
    }
    if ($null -ne $server) { $server.Dispose() }
}
