param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectDir "publish\$Runtime"
$distDir = Join-Path $projectDir "dist"

if (-not $Version) {
    [xml]$csproj = Get-Content (Join-Path $projectDir "VibeLauncher.csproj")
    $Version = $csproj.Project.PropertyGroup.Version
}
if (-not $Version) { throw "Не удалось определить версию. Укажите -Version." }

Write-Host "Vibe Launcher $Version ($Runtime)" -ForegroundColor Cyan

# Закрываем запущенный лаунчер, иначе publish не сможет перезаписать exe.
Get-Process VibeLauncher -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host "Публикация self-contained..." -ForegroundColor Yellow
    dotnet publish (Join-Path $projectDir "VibeLauncher.csproj") `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с ошибкой." }
}

# WiX ставится как global tool: dotnet tool install --global wix --version 5.0.2
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) { throw "WiX не найден. Установите: dotnet tool install --global wix --version 5.0.2" }

$extensions = Join-Path $projectDir ".wix\extensions\WixToolset.UI.wixext"
if (-not (Test-Path $extensions)) {
    Write-Host "Добавление расширения WiX UI..." -ForegroundColor Yellow
    Push-Location $projectDir
    try { wix extension add WixToolset.UI.wixext/5.0.2 } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "Не удалось добавить WixToolset.UI.wixext." }
}

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
$msi = Join-Path $distDir "VibeLauncher-Setup-$Version-$Runtime.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }

Write-Host "Сборка MSI..." -ForegroundColor Yellow
wix build (Join-Path $PSScriptRoot "VibeLauncher.wxs") `
    -ext WixToolset.UI.wixext `
    -d "Version=$Version" `
    -d "ProjectDir=$projectDir" `
    -d "PublishDir=$publishDir" `
    -culture ru-RU `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build завершился с ошибкой." }

$hash = Get-FileHash $msi -Algorithm SHA256
$hashLine = "$($hash.Hash.ToLower())  $(Split-Path -Leaf $msi)"
[IO.File]::WriteAllText((Join-Path $distDir "SHA256SUMS.txt"), $hashLine + "`n", (New-Object System.Text.UTF8Encoding($false)))

$sizeMb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Готово: $msi ($sizeMb МБ)" -ForegroundColor Green
Write-Host "SHA256: $($hash.Hash)" -ForegroundColor DarkGray
