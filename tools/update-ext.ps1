# Быстрый dev-цикл обновления ПОСТОЯННО установленного расширения Gptgraber.
#
# Одна команда: пересобрать .xpi с авто-версией и открыть его в Firefox на установку.
# Постоянная установка нужна, потому что временное дополнение (about:debugging)
# слетает при перезапуске браузера, а нам надо тестировать сценарий §6.16
# («сервер сам поднимает закрытый Firefox»). См. CLAUDE.md §6.16.
#
# Стабильный ID в manifest (browser_specific_settings.gecko.id = gptgraber@local)
# => новый .xpi встаёт ПОВЕРХ старого, а не вторым экземпляром. Разрешения/настройки
# сохраняются. Условие установки: в профиле Firefox выключена проверка подписи
# (about:config -> xpinstall.signatures.required = false) — это уже сделано при первой
# постоянной установке (Dev Edition/ESR/Nightly).
#
# Запуск (из любой папки):
#   powershell -ExecutionPolicy Bypass -File tools\update-ext.ps1
# Опции:
#   -FirefoxPath "C:\...\firefox.exe"   явный путь к firefox.exe (если не нашёлся сам)
#   -NoLaunch                            только собрать .xpi, не открывать Firefox
#
# Что делает:
#   1. Версию для сборки генерирует САМ: база из manifest.json + секунды от 2024-01-01.
#      Строго растёт по времени — Firefox примет .xpi как ОБНОВЛЕНИЕ (версия должна быть
#      выше установленной). Исходный manifest.json НЕ меняем (версия подставляется только
#      в копию для сборки) — нет мусора в git.
#   2. Пакует рантайм-файлы расширения (без *.md) в dist\gptgraber-dev.xpi (перезапись).
#   3. Открывает .xpi в Firefox (через Start-Process — предпочтёт УЖЕ запущенный
#      экземпляр: та же сборка и профиль, где расширение и стоит). Firefox покажет «Add».
#
# Дальше — один клик «Add»/«Добавить». Таб ChatGPT перезагрузится сам (см. background.js,
# обработчик runtime.onInstalled), вручную Ctrl+R не нужен.

[CmdletBinding()]
param(
    [string]$FirefoxPath,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot   # корень репозитория
$src  = Join-Path $root 'extension-firefox'
$dist = Join-Path $root 'dist'
$xpi  = Join-Path $dist 'gptgraber-dev.xpi'   # стабильное имя: версия живёт ВНУТРИ архива

# --- Версия сборки: база из манифеста + монотонный суффикс (секунды от 2024-01-01) ---
$manifestPath = Join-Path $src 'manifest.json'
$manifestRaw  = Get-Content $manifestPath -Raw
$verMatch = [regex]::Match($manifestRaw, '"version"\s*:\s*"([^"]+)"')
if (-not $verMatch.Success) { throw "В manifest.json не найдено поле version." }
$base = $verMatch.Groups[1].Value             # напр. "0.1.0" (база должна быть <= 3 частей)

$epoch2024 = [DateTimeOffset]::new(2024, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$build = [int][math]::Floor(([DateTimeOffset]::UtcNow - $epoch2024).TotalSeconds)
$devVersion = "$base.$build"                  # напр. "0.1.0.76123456" — строго растёт по времени

# Подменяем версию только в тексте манифеста для сборки (исходный файл не трогаем).
$stagedManifest = [regex]::Replace($manifestRaw, '("version"\s*:\s*")[^"]+(")', "`${1}$devVersion`${2}")

# --- Стейджинг и упаковка (manifest.json в КОРНЕ архива, как требует .xpi) ---
$staging = Join-Path $env:TEMP ("gptgraber-xpi-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Get-ChildItem -Path $src -Recurse -File |
        Where-Object { $_.Extension -ne '.md' } |
        ForEach-Object {
            $rel  = $_.FullName.Substring($src.Length + 1)
            $dest = Join-Path $staging $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            if ($rel -ieq 'manifest.json') {
                # UTF-8 без BOM: BOM может сломать парсер манифеста, а в description есть кириллица.
                [System.IO.File]::WriteAllText($dest, $stagedManifest, (New-Object System.Text.UTF8Encoding $false))
            } else {
                Copy-Item -LiteralPath $_.FullName -Destination $dest
            }
        }

    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    if (Test-Path $xpi) { Remove-Item $xpi -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $xpi)
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Собрано: $xpi  (версия $devVersion)" -ForegroundColor Green

if ($NoLaunch) {
    Write-Host "-NoLaunch: установить вручную (about:addons -> шестерёнка -> Install Add-on From File -> выбрать .xpi)." -ForegroundColor Yellow
    return
}

# --- Поиск firefox.exe ---
function Find-Firefox {
    param([string]$Override)

    if ($Override) {
        if (Test-Path $Override) { return $Override }
        throw "Указанный -FirefoxPath не найден: $Override"
    }

    # 1) УЖЕ запущенный firefox — самый надёжный: гарантированно та сборка и профиль,
    #    где расширение установлено (важно, если стоят и Dev Edition, и обычный Firefox).
    try {
        $p = Get-Process -Name firefox -ErrorAction Stop |
             Where-Object { $_.Path } | Select-Object -First 1 -ExpandProperty Path
        if ($p -and (Test-Path $p)) { return $p }
    } catch { }

    # 2) Стандартные пути установки (Dev Edition в приоритете).
    $bases = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($b in $bases) {
        foreach ($edition in 'Firefox Developer Edition', 'Firefox Nightly', 'Mozilla Firefox') {
            $c = Join-Path $b (Join-Path $edition 'firefox.exe')
            if (Test-Path $c) { return $c }
        }
    }

    # 3) Реестр App Paths (HKLM/HKCU).
    foreach ($hive in 'HKLM:', 'HKCU:') {
        $k = Join-Path $hive 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe'
        try {
            $v = (Get-ItemProperty -Path $k -ErrorAction Stop).'(default)'
            if ($v -and (Test-Path $v)) { return $v }
        } catch { }
    }

    return $null
}

$ff = Find-Firefox -Override $FirefoxPath
if (-not $ff) {
    Write-Host "firefox.exe не найден. Установи вручную: about:addons -> Install Add-on From File -> $xpi" -ForegroundColor Yellow
    Write-Host "Или укажи путь: -FirefoxPath 'C:\Program Files\Firefox Developer Edition\firefox.exe'" -ForegroundColor Yellow
    return
}

# Start-Process не блокирует скрипт (в отличие от & ): если Firefox запущен, команда
# уйдёт в существующий экземпляр и откроет окно установки в нём.
Start-Process -FilePath $ff -ArgumentList "`"$xpi`""

Write-Host ""
Write-Host "Firefox: $ff" -ForegroundColor Cyan
Write-Host "Открыл .xpi на установку — нажми «Add»/«Добавить» в окне Firefox." -ForegroundColor Cyan
Write-Host "Таб ChatGPT перезагрузится сам (Ctrl+R вручную не нужен)." -ForegroundColor Cyan
