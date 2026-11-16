param(
    [Alias('LocalPort')][ValidateRange(1024,65535)][int]$ClientPort = 8870,
    [string]$ClientPath = (Join-Path $PSScriptRoot '../.local/monitor-client/CatalogWeaver.MonitorClient.exe'),
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.@-]*$')][string]$SshHost,
    [ValidateRange(1,65535)][int]$RemotePort = 8770,
    [ValidateRange(1024,65535)][int]$TunnelLocalPort = 8871,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
if ($SshHost) {
    if ($ClientPort -eq $TunnelLocalPort) { throw 'Client and source tunnel need different local ports.' }
    $ssh = (Get-Command ssh.exe -ErrorAction Stop).Source
    $forward = "127.0.0.1:${TunnelLocalPort}:127.0.0.1:${RemotePort}"
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $TunnelLocalPort -ErrorAction SilentlyContinue)
    if ($listeners.Count) {
        $owners = @($listeners.OwningProcess | Select-Object -Unique)
        if ($owners.Count -ne 1) { throw 'The source tunnel port is already in use.' }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])"
        $matchesForward = $process.CommandLine -match [regex]::Escape($forward)
        $matchesHost = $process.CommandLine -match ('\s' + [regex]::Escape($SshHost) + '\s*$')
        if ($process.ExecutablePath -ne $ssh -or -not $matchesForward -or -not $matchesHost) {
            throw 'The source port belongs to another process. It was left untouched.'
        }
    } else {
        $arguments = "-N -o BatchMode=yes -o ExitOnForwardFailure=yes -o ConnectTimeout=8 -o ServerAliveInterval=15 -o ServerAliveCountMax=2 -L $forward $SshHost"
        Start-Process -FilePath $ssh -ArgumentList $arguments -WindowStyle Hidden | Out-Null
    }
}
$url = "http://127.0.0.1:$ClientPort/monitor/"
function Test-MonitorClient {
    try {
        $response = Invoke-WebRequest -Uri $url -TimeoutSec 2 -UseBasicParsing
        return ($response.StatusCode -eq 200 -and ($response.Headers['X-CatalogWeaver-Role'] -join '') -eq 'monitor-client')
    } catch { return $false }
}
if (-not (Test-MonitorClient)) {
    if (@(Get-NetTCPConnection -State Listen -LocalPort $ClientPort -ErrorAction SilentlyContinue).Count) {
        throw 'The selected client port belongs to another service. It was left untouched.'
    }
    $executable = (Resolve-Path -LiteralPath $ClientPath -ErrorAction Stop).Path
    Start-Process -FilePath $executable -ArgumentList "--port $ClientPort --no-browser" -WindowStyle Hidden | Out-Null
}
for ($attempt=0; $attempt -lt 20; $attempt++) {
    if (Test-MonitorClient) {
        if (-not $NoBrowser) { Start-Process $url }
        Write-Output $url
        return
    }
    Start-Sleep -Milliseconds 250
}
throw 'The local web client did not start. Check whether it is already running on another port.'
