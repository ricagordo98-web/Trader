$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework

$url = 'http://localhost:5256'
$serverExe = Join-Path $PSScriptRoot 'Server\Trader.Web.exe'
$appExe = Join-Path $PSScriptRoot 'App\Trader.exe'

function Test-Server {
    try {
        Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null
        return $true
    }
    catch {
        return [bool]$_.Exception.Response
    }
}

$serverProcess = $null

try {
    if (-not (Test-Server)) {
        $env:ASPNETCORE_URLS = $url

        $serverProcess = Start-Process $serverExe -WorkingDirectory (Split-Path $serverExe) -WindowStyle Normal -PassThru

        for ($i = 0; $i -lt 60 -and -not (Test-Server); $i++) {
            Start-Sleep -Milliseconds 500
        }

        if (-not (Test-Server)) {
            throw "O servidor não arrancou em $url."
        }
    }

    Start-Process $appExe -Wait
}
catch {
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'Trader', 'OK', 'Error') | Out-Null
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        & taskkill /PID $serverProcess.Id /T /F | Out-Null
    }
}
