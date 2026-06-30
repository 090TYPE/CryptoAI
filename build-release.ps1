# ============================================================================
#  Crypto AI Terminal — Release Build Script
# ----------------------------------------------------------------------------
#  Собирает self-contained Windows-релиз и упаковывает в zip для отправки.
#  Запуск:   .\build-release.ps1
#  Опции:    .\build-release.ps1 -SkipClean       # без удаления bin/obj
#            .\build-release.ps1 -SkipPack        # не упаковывать через Velopack
#            .\build-release.ps1 -Version 1.2.0   # своя версия в имени архива
# ============================================================================

[CmdletBinding()]
param(
    [switch] $SkipClean,
    [switch] $SkipPack,
    [string] $Version = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $PublishToGithub
)

$ErrorActionPreference = 'Stop'

# Velopack CLI — installs once if absent.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing Velopack CLI (vpk)...' -ForegroundColor Yellow
    & dotnet tool install -g vpk
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

# vpk requires a SemVer version. Warn if the (date-default) version isn't SemVer.
if ($Version -notmatch '^\d+\.\d+\.\d+') {
    Write-Host "[warn] -Version '$Version' is not SemVer; vpk needs e.g. 1.6.0. Pass -Version explicitly for releases." -ForegroundColor Yellow
}

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

# ── 4. Pack (Velopack) ─────────────────────────────────────────────────────
if (-not $SkipPack) {
    Write-Step 'Packing release with Velopack (vpk)'

    if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory -Path $ReleaseDir | Out-Null }

    & vpk pack `
        --packId        CryptoAITerminal `
        --packVersion   $Version `
        --packDir       $PublishDir `
        --mainExe       CryptoAITerminal.TerminalUI.exe `
        --packTitle     'CryptoAI Terminal' `
        --outputDir     $ReleaseDir
    Assert-Success 'vpk pack'

    Write-Host "  Velopack output: $ReleaseDir" -ForegroundColor Green

    if ($PublishToGithub) {
        Write-Step 'Uploading release to GitHub (vpk upload github)'
        if (-not $env:GITHUB_TOKEN) {
            Write-Host '[FAIL] GITHUB_TOKEN env var required for -PublishToGithub' -ForegroundColor Red
            exit 1
        }
        & vpk upload github `
            --repoUrl     "https://github.com/090TYPE/CryptoAI" `
            --token       $env:GITHUB_TOKEN `
            --outputDir   $ReleaseDir `
            --tag         "v$Version" `
            --releaseName "v$Version"
        Assert-Success 'vpk upload github'
    }
}
else {
    Write-Step 'Skipping pack (-SkipPack)'
}

# ── 5. Summary ─────────────────────────────────────────────────────────────
Write-Step 'BUILD SUCCEEDED'

Write-Host ''
Write-Host '  Publish folder:' -ForegroundColor Green
Write-Host "    $PublishDir"
if (-not $SkipPack) {
    Write-Host ''
    Write-Host '  Ready to ship:' -ForegroundColor Green
    Write-Host "    $ReleaseDir"
    Write-Host ''
    Write-Host '  Customer instructions:' -ForegroundColor Yellow
    Write-Host '    1. Run Setup.exe from the release folder (installs to %LocalAppData%).'
    Write-Host '    2. The app updates itself from then on — no manual downloads.'
    Write-Host '    3. Configure API keys via Settings tab or env vars (see README.md).'
}
Write-Host ''
