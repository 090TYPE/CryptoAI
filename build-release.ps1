# ============================================================================
#  Crypto AI Terminal — Release Build Script
# ----------------------------------------------------------------------------
#  Собирает self-contained Windows-релиз и упаковывает в zip для отправки.
#  Запуск:   .\build-release.ps1
#  Опции:    .\build-release.ps1 -SkipClean       # без удаления bin/obj
#            .\build-release.ps1 -SkipZip         # не упаковывать в архив
#            .\build-release.ps1 -Version 1.2.0   # своя версия в имени архива
# ============================================================================

[CmdletBinding()]
param(
    [switch] $SkipClean,
    [switch] $SkipZip,
    [string] $Version = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'

# ── Paths ──────────────────────────────────────────────────────────────────
$Root         = $PSScriptRoot
$Project      = Join-Path $Root 'CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj'
$PublishDir   = Join-Path $Root 'publish\CryptoAITerminal'
$ReleaseDir   = Join-Path $Root 'release'
$ArchiveName  = "CryptoAITerminal-v$Version.zip"
$ArchivePath  = Join-Path $ReleaseDir $ArchiveName

function Write-Step($message) {
    Write-Host ''
    Write-Host '============================================================' -ForegroundColor DarkCyan
    Write-Host " $message" -ForegroundColor Cyan
    Write-Host '============================================================' -ForegroundColor DarkCyan
}

function Assert-Success($message) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[FAIL] $message (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# ── 0. Preflight ───────────────────────────────────────────────────────────
Write-Step 'Preflight checks'

if (-not (Test-Path $Project)) {
    Write-Host "[FAIL] Project not found: $Project" -ForegroundColor Red
    exit 1
}

$dotnetVersion = & dotnet --version
Assert-Success 'dotnet --version'
Write-Host "  .NET SDK:    $dotnetVersion"
Write-Host "  Solution:    $Root"
Write-Host "  Version tag: $Version"
Write-Host "  Output:      $ArchivePath"

# ── 1. Clean ───────────────────────────────────────────────────────────────
if (-not $SkipClean) {
    Write-Step 'Cleaning previous bin/obj/publish'

    Get-ChildItem -Path $Root -Recurse -Directory -Force `
        | Where-Object { $_.Name -in @('bin','obj') -and $_.FullName -notlike '*\node_modules\*' } `
        | ForEach-Object {
            Write-Host "  rm $($_.FullName.Replace($Root,'.'))"
            Remove-Item -Recurse -Force $_.FullName
        }

    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
}
else {
    Write-Step 'Skipping clean (-SkipClean)'
}

# ── 2. Restore ─────────────────────────────────────────────────────────────
Write-Step 'Restoring NuGet packages'
& dotnet restore $Project
Assert-Success 'dotnet restore'

# ── 3. Publish (self-contained) ────────────────────────────────────────────
Write-Step 'Publishing self-contained Windows release'

# self-contained — пользователю не нужен установленный .NET
# single-file отключен, потому что Avalonia загружает native-зависимости рантайма
& dotnet publish $Project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $PublishDir `
    /p:PublishSingleFile=false `
    /p:DebugType=None `
    /p:DebugSymbols=false
Assert-Success 'dotnet publish'

# Удаляем .pdb если они всё-таки сгенерировались
Get-ChildItem -Path $PublishDir -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue `
    | Remove-Item -Force -ErrorAction SilentlyContinue

# На всякий случай удаляем api-credentials.json если он попал в публикацию
$leakedCreds = Join-Path $PublishDir 'api-credentials.json'
if (Test-Path $leakedCreds) {
    Write-Host '  [warn] api-credentials.json removed from publish output' -ForegroundColor Yellow
    Remove-Item -Force $leakedCreds
}

$publishSize = (Get-ChildItem $PublishDir -Recurse | Measure-Object -Property Length -Sum).Sum
$publishMb   = [math]::Round($publishSize / 1MB, 1)
Write-Host "  publish size: $publishMb MB"

# ── 4. Package ─────────────────────────────────────────────────────────────
if (-not $SkipZip) {
    Write-Step 'Creating release archive'

    if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }
    if (Test-Path $ArchivePath)       { Remove-Item -Force $ArchivePath }

    # Compress-Archive создаст архив, внутри которого папка CryptoAITerminal\...
    Compress-Archive -Path $PublishDir -DestinationPath $ArchivePath -CompressionLevel Optimal
    Assert-Success 'Compress-Archive'

    $archiveSize = (Get-Item $ArchivePath).Length
    $archiveMb   = [math]::Round($archiveSize / 1MB, 1)
    Write-Host "  archive:      $ArchivePath"
    Write-Host "  archive size: $archiveMb MB"
}
else {
    Write-Step 'Skipping archive (-SkipZip)'
}

# ── 5. Summary ─────────────────────────────────────────────────────────────
Write-Step 'BUILD SUCCEEDED'

Write-Host ''
Write-Host '  Publish folder:' -ForegroundColor Green
Write-Host "    $PublishDir"
if (-not $SkipZip) {
    Write-Host ''
    Write-Host '  Ready to ship:' -ForegroundColor Green
    Write-Host "    $ArchivePath"
    Write-Host ''
    Write-Host '  Customer instructions:' -ForegroundColor Yellow
    Write-Host '    1. Extract the archive to any folder.'
    Write-Host '    2. Run CryptoAITerminal.TerminalUI.exe — no installer required.'
    Write-Host '    3. Configure API keys via Settings tab or env vars (see README.md).'
}
Write-Host ''
