param(
    [string]$Release = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Trader-release')
)

$ErrorActionPreference = 'Stop'

Copy-Item (Join-Path $PSScriptRoot 'Iniciar-Trader.ps1') $Release -Force
Copy-Item (Join-Path $PSScriptRoot 'Iniciar-Trader.bat') $Release -Force

$launcherPath = Join-Path $Release 'Trader-Iniciar.exe'
$iconPath = Join-Path $Release 'App\appicon.ico'
$csc = Join-Path ([System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
& $csc /nologo /target:winexe /out:$launcherPath /win32icon:$iconPath /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'IniciarTrader.cs')
if ($LASTEXITCODE -ne 0) {
    throw 'Falhou: executável de arranque'
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Trader.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $launcherPath
$shortcut.WorkingDirectory = $Release
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Save()

Write-Host "Atalho criado: $shortcutPath"
Write-Host "Pasta: $Release"
