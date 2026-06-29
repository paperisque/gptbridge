# Упаковка расширения Firefox в .xpi (zip с manifest.json в КОРНЕ архива).
#
# Для ПОСТОЯННОЙ установки в Firefox Developer Edition / ESR / Nightly с отключённой
# проверкой подписи (about:config → xpinstall.signatures.required = false). На обычном
# Firefox Release неподписанный .xpi не поставить — там нужен AMO (см. npm run sign:ext).
#
# Запуск (из любой папки):
#   powershell -ExecutionPolicy Bypass -File tools\build-xpi.ps1
# Результат: dist\gptgraber-<version>.xpi

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot   # корень репозитория
$src  = Join-Path $root 'extension-firefox'
$dist = Join-Path $root 'dist'

# Версию берём из манифеста — чтобы имя .xpi совпадало с версией расширения.
# Читаем как UTF-8 (Get-Content -Raw в PS5.1 — ANSI); сам файл ниже копируется бинарно.
$manifest = [System.IO.File]::ReadAllText((Join-Path $src 'manifest.json')) | ConvertFrom-Json
$version  = $manifest.version
$xpi      = Join-Path $dist "gptgraber-$version.xpi"

New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path $xpi) { Remove-Item $xpi -Force }

# Стейджинг: кладём только рантайм-файлы расширения (без *.md — это документация).
# Сохраняем относительные пути, чтобы manifest.json оказался в корне архива.
$staging = Join-Path $env:TEMP ("gptgraber-xpi-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Get-ChildItem -Path $src -Recurse -File |
        Where-Object { $_.Extension -ne '.md' } |
        ForEach-Object {
            $rel  = $_.FullName.Substring($src.Length + 1)
            $dest = Join-Path $staging $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $dest
        }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $xpi)
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Собрано: $xpi (версия $version)"
Write-Output "Установка (Dev Edition/ESR/Nightly): about:addons -> шестерёнка -> Install Add-on From File -> выбрать этот .xpi"
