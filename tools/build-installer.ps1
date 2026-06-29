<#
.SYNOPSIS
  Сборка установщика GPT Grabber (WebView2-версия) одной командой.

.DESCRIPTION
  Делает всё «одним движением»:
    1) dotnet publish poc-webview2 (framework-dependent, win-x64; apphost не собирается — SAC §6.14);
    2) собирает ПОРТАТИВНЫЙ .NET runtime (Windows Desktop) в подпапку dotnet\ — копией из
       C:\Program Files\dotnet (нужны и Microsoft.NETCore.App, и Microsoft.WindowsDesktop.App);
    3) кладёт рядом Evergreen-бутстраппер WebView2 (из кэша tools\cache, иначе качает);
    4) компилирует installer\GptGrabber.iss через ISCC.exe → dist\GptGrabber-Setup-<ver>.exe.

  Запуск программы у пользователя — через вложенный подписанный Microsoft dotnet.exe + DLL.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Run
  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Version 0.2.0 -NoWebView2
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version,                  # по умолчанию берётся из package.json
    [switch]$Run,                      # запустить установщик после сборки
    [switch]$NoWebView2,               # не вкладывать бутстраппер WebView2
    [string]$IsccPath                  # переопределить путь к ISCC.exe
)

$ErrorActionPreference = 'Stop'

# --- пути ---------------------------------------------------------------------
$root    = Split-Path -Parent $PSScriptRoot                 # tools\ -> корень репозитория
$proj    = Join-Path $root 'poc-webview2\WebView2Poc.csproj'
$iss     = Join-Path $root 'installer\GptGrabber.iss'
$dist    = Join-Path $root 'dist'
$cacheDir = Join-Path $root 'tools\cache'
$appIcon = Join-Path $root 'installer\app.ico'             # опционально; если есть — попадёт в ярлыки

if (-not (Test-Path $proj)) { throw "Не найден проект: $proj" }
if (-not (Test-Path $iss))  { throw "Не найден Inno-скрипт: $iss" }

# --- версия -------------------------------------------------------------------
if (-not $Version) {
    $pkgPath = Join-Path $root 'package.json'
    if (Test-Path $pkgPath) {
        $Version = (Get-Content $pkgPath -Raw | ConvertFrom-Json).version
    }
    if (-not $Version) { $Version = '0.0.0' }
}
Write-Host "GPT Grabber installer  |  версия $Version  |  конфигурация $Configuration" -ForegroundColor Cyan

# --- ISCC ---------------------------------------------------------------------
function Find-Iscc {
    if ($IsccPath) {
        if (Test-Path $IsccPath) { return $IsccPath }
        throw "Указанный -IsccPath не найден: $IsccPath"
    }
    $cands = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles        'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }
    foreach ($hive in @('HKLM:', 'HKCU:')) {
        $k = "$hive\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
        try {
            $loc = (Get-ItemProperty -Path $k -ErrorAction Stop).InstallLocation
            if ($loc) { $p = Join-Path $loc 'ISCC.exe'; if (Test-Path $p) { return $p } }
        } catch { }
    }
    throw "ISCC.exe (Inno Setup 6) не найден. Установи Inno Setup или укажи -IsccPath."
}
$iscc = Find-Iscc
Write-Host "  ISCC: $iscc"

# --- портативный .NET runtime: ищем наивысшую общую версию 10.x -----------------
function Resolve-RuntimeVersion {
    param([string]$DotnetRoot)
    $nc = Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App'
    $wd = Join-Path $DotnetRoot 'shared\Microsoft.WindowsDesktop.App'
    if (-not (Test-Path $nc)) { throw "Не найден $nc — установлен ли .NET 10 runtime?" }
    if (-not (Test-Path $wd)) { throw "Не найден $wd — нужен Windows Desktop Runtime/.NET 10 SDK (WinForms)." }
    $ncv = Get-ChildItem $nc -Directory | ForEach-Object Name
    $wdv = Get-ChildItem $wd -Directory | ForEach-Object Name
    $common = $ncv | Where-Object { ($wdv -contains $_) -and ($_ -like '10.*') }
    if (-not $common) { throw "Нет общей версии 10.x в Microsoft.NETCore.App и Microsoft.WindowsDesktop.App." }
    # сортировка по версии (срезаем preview-суффикс вида 10.0.0-rc.x)
    return ($common | Sort-Object { [version]($_ -replace '-.*$', '') } -Descending | Select-Object -First 1)
}

# --- staging ------------------------------------------------------------------
$staging = Join-Path $env:TEMP ("gptgraber-installer-" + [guid]::NewGuid().ToString('N'))
$appDir  = Join-Path $staging 'app'

try {
    New-Item -ItemType Directory -Force -Path $appDir   | Out-Null
    New-Item -ItemType Directory -Force -Path $dist     | Out-Null

    # 1) публикация приложения (framework-dependent, без apphost) -------------
    Write-Host "[1/4] dotnet publish…" -ForegroundColor Yellow
    & dotnet publish $proj -c $Configuration -r win-x64 --self-contained false `
        -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false --nologo -o $appDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

    # подчистка лишнего: apphost (на случай если появился), pdb и XML-доки пакетов
    Get-ChildItem $appDir -Recurse -Include 'WebView2Poc.exe', '*.pdb', '*.xml' -File `
        -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path (Join-Path $appDir 'WebView2Poc.dll'))) {
        throw "После publish нет WebView2Poc.dll — сборка не удалась."
    }

    # 2) портативный runtime в app\dotnet\ -------------------------------------
    Write-Host "[2/4] сборка портативного .NET runtime…" -ForegroundColor Yellow
    $dotnetRoot = Join-Path $env:ProgramFiles 'dotnet'
    if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
        throw "Не найден $dotnetRoot\dotnet.exe"
    }
    $rtVer = Resolve-RuntimeVersion -DotnetRoot $dotnetRoot
    Write-Host "      runtime $rtVer (NETCore.App + WindowsDesktop.App)"

    $rtDst = Join-Path $appDir 'dotnet'
    New-Item -ItemType Directory -Force -Path (Join-Path $rtDst 'shared\Microsoft.NETCore.App') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $rtDst 'shared\Microsoft.WindowsDesktop.App') | Out-Null

    Copy-Item (Join-Path $dotnetRoot 'dotnet.exe') (Join-Path $rtDst 'dotnet.exe') -Force
    Copy-Item (Join-Path $dotnetRoot 'host') (Join-Path $rtDst 'host') -Recurse -Force
    Copy-Item (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$rtVer") `
              (Join-Path $rtDst "shared\Microsoft.NETCore.App\$rtVer") -Recurse -Force
    Copy-Item (Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App\$rtVer") `
              (Join-Path $rtDst "shared\Microsoft.WindowsDesktop.App\$rtVer") -Recurse -Force

    # 3) Evergreen-бутстраппер WebView2 ----------------------------------------
    $wv2 = $null
    if (-not $NoWebView2) {
        Write-Host "[3/4] WebView2 bootstrapper…" -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        $wv2 = Join-Path $cacheDir 'MicrosoftEdgeWebview2Setup.exe'
        if (-not (Test-Path $wv2)) {
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' `
                    -OutFile $wv2 -UseBasicParsing
                Write-Host "      скачан в кэш: $wv2"
            } catch {
                Write-Warning "Не удалось скачать бутстраппер WebView2 ($($_.Exception.Message)). Собираю БЕЗ него."
                $wv2 = $null
            }
        } else {
            Write-Host "      из кэша: $wv2"
        }
    } else {
        Write-Host "[3/4] WebView2 bootstrapper пропущен (-NoWebView2)" -ForegroundColor DarkGray
    }

    # 4) компиляция установщика -------------------------------------------------
    Write-Host "[4/4] ISCC компиляция…" -ForegroundColor Yellow
    $isccArgs = @("/DAppVersion=$Version", "/DStagingApp=$appDir")
    if ($wv2)                  { $isccArgs += "/DWebView2Setup=$wv2" }
    if (Test-Path $appIcon)    { $isccArgs += "/DAppIcon=$appIcon"; Write-Host "      иконка: $appIcon" }
    $isccArgs += $iss

    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }

    $outExe = Join-Path $dist "GptGrabber-Setup-$Version.exe"
    if (-not (Test-Path $outExe)) { throw "Ожидался $outExe, но его нет." }
    $sizeMb = [math]::Round((Get-Item $outExe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "ГОТОВО: $outExe  ($sizeMb МБ)" -ForegroundColor Green

    if ($Run) {
        Write-Host "Запускаю установщик…" -ForegroundColor Cyan
        Start-Process -FilePath $outExe
    }
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
