#requires -Version 5.1
<#
.SYNOPSIS
    Собирает установщик (MSI) приложения CheckMail с помощью WiX Toolset v5.

.DESCRIPTION
    Скрипт компилирует файл Product.wxs в MSI-установщик CheckMail.
    Исходные файлы приложения берутся из папки ..\Programm.
    Файл settings.json (без паролей) берётся из текущей папки Installer.

.PARAMETER SourceDir
    Папка с собранными файлами приложения. По умолчанию ..\Programm
    относительно папки скрипта.

.PARAMETER OutputDir
    Папка, куда будет записан MSI. По умолчанию .\bin

.PARAMETER Version
    Версия устанавливаемого приложения (мажор.минор.патч).
    По умолчанию 1.0.0.

.PARAMETER NoUI
    Если указан, стандартный интерфейс установки не добавляется.
#>
[CmdletBinding()]
param(
    [string]$SourceDir,
    [string]$OutputDir,
    [string]$Version = "1.0.0",
    [switch]$NoUI
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SourceDir) {
    $SourceDir = Join-Path (Split-Path -Parent $ScriptRoot) "Programm"
}
$SourceDir = (Resolve-Path $SourceDir).Path

if (-not $OutputDir) {
    $OutputDir = Join-Path $ScriptRoot "bin"
}
$OutputDir = (New-Item -ItemType Directory -Path $OutputDir -Force).FullName

$SettingsFile = Join-Path $ScriptRoot "settings.json"
$ProductWxs   = Join-Path $ScriptRoot "Product.wxs"

if (-not (Test-Path $ProductWxs)) {
    throw "Файл Product.wxs не найден: $ProductWxs"
}

# Убедиться, что инструмент wix (WiX v5) доступен
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    $dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
    if (-not (Test-Path (Join-Path $dotnetTools "wix.exe"))) {
        throw "WiX Toolset v5 (команда wix) не найден. Установите: dotnet tool install --global wix --version 5.0.2"
    }
    $env:PATH += ";$dotnetTools"
}

$OutputMsi = Join-Path $OutputDir "CheckMail-$Version.msi"

# Расширение стандартного интерфейса установки (WixUI_InstallDir).
# Должно лежать в .\tools\WixToolset.UI.wixext.dll (см. README).
$ExtensionPath = Join-Path $ScriptRoot "tools\WixToolset.UI.wixext.dll"

if (-not (Test-Path $ExtensionPath)) {
    throw "Не найден файл расширения UI: $ExtensionPath`nСкачайте пакет WixToolset.UI.wixext (v5.0.2) из NuGet и положите его DLL в папку tools."
}

$Args = @(
    $ProductWxs
    "-out", $OutputMsi
    "-d", "var.SourceDir=$SourceDir"
    "-d", "var.SettingsFile=$SettingsFile"
    "-d", "var.Version=$Version"
    "-arch", "x64"
)

$Args += @("-ext", $ExtensionPath)

Write-Host "Сборка MSI -> $OutputMsi" -ForegroundColor Cyan
& wix build @Args

if ($LASTEXITCODE -ne 0) {
    throw "Сборка WiX завершилась с ошибкой (код $LASTEXITCODE)."
}

Write-Host "Готово: $OutputMsi" -ForegroundColor Green
